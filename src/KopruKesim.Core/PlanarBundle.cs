using System;
using System.Collections.Generic;
using System.Linq;

namespace KopruKesim.Core
{
    /// <summary>Bir damanin tabla uzerindeki yeri: desen merkezi (mm) ve uzun kenarinin X eksenine gore acisi (derece).</summary>
    public sealed class BoardPlacement
    {
        public double X, Y, RotationDeg;
        public BoardPlacement() { }
        public BoardPlacement(double x, double y, double rotationDeg) { X = x; Y = y; RotationDeg = rotationDeg; }
        public Point2 Center => new Point2(X, Y);

        /// <summary>Damanin kendi eksenindeki bir noktanin (merkeze gore, mm) tabladaki yeri.</summary>
        public Point2 Apply(Point2 local)
        {
            double a = RotationDeg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
            return new Point2(X + c * local.X - s * local.Y, Y + s * local.X + c * local.Y);
        }
    }

    /// <summary>
    /// Tek fotograftaki birden cok duz dama icin lens, kamera konumu ve her damanin tabla duzlemindeki yeri (x, y, aci)
    /// birlikte cozulur (Levenberg-Marquardt). Damalarin tabladaki yeri varsayilmaz: olcek yalniz kare boyundan, duzlem
    /// tum damalarin ayni duzlemde olmasindan gelir. Boylece bir kagit birkac santim yanlis yere konmus olsa da lens
    /// modeli bozulmaz (2026-11-20: orta etiketin 49 mm kaymis olmasi tum modeli egip karsilikli kenarlari 12 mm farkli
    /// olcturuyordu).
    ///
    /// Model OpenCV ile aynidir: optik merkez sabit, kare piksel, radyal k1 k2 k3, tegetsel bukulme yok.
    /// Koordinatlar cozum sirasinda bir damaya (Fixed) gore tanimlidir; tablanin kendi cercevesi sonradan kurulur.
    /// </summary>
    public sealed class PlanarBundle
    {
        public sealed class Board
        {
            /// <summary>Ic koselerin damanin kendi eksenindeki yeri (merkeze gore, mm).</summary>
            public Point2[] Local;
            /// <summary>Ayni koselerin fotograftaki yeri (piksel).</summary>
            public Point2[] Pixels;
            public double X, Y, Theta;   // Theta radyan
            /// <summary>Cozumde yeri sabit tutulan dama (cerceveyi belirler).</summary>
            public bool Fixed;
        }

        public double F, Cx, Cy, K1, K2, K3;
        public double[] Rvec = new double[3], Tvec = new double[3];
        public readonly List<Board> Boards = new List<Board>();
        /// <summary>
        /// Lens sabit: odak ve k1 k2 k3 cozumde degismez, yalniz kamera konumu ve damalarin yeri cozulur. Lens daha once
        /// koselerin de gorundugu bir kalibrasyondan biliniyorsa (mavi paspasli kalibrasyon).
        /// </summary>
        public bool FixIntrinsics;
        /// <summary>Damalarin yeniden izdusum hatasi (piksel, nokta basina rms).</summary>
        public double RmsPx = double.NaN;
        public int Iterations;

        private double[] Pack()
        {
            var p = new List<double> { F, K1, K2, K3, Rvec[0], Rvec[1], Rvec[2], Tvec[0], Tvec[1], Tvec[2] };
            foreach (Board b in Boards) if (!b.Fixed) { p.Add(b.X); p.Add(b.Y); p.Add(b.Theta); }
            return p.ToArray();
        }

        private void Unpack(double[] p)
        {
            F = p[0]; K1 = p[1]; K2 = p[2]; K3 = p[3];
            Rvec = new[] { p[4], p[5], p[6] }; Tvec = new[] { p[7], p[8], p[9] };
            int k = 10;
            foreach (Board b in Boards) if (!b.Fixed) { b.X = p[k++]; b.Y = p[k++]; b.Theta = p[k++]; }
        }

        /// <summary>Duzlem noktasinin (Z = 0) goruntudeki yeri.</summary>
        public static Point2 Project(double f, double cx, double cy, double k1, double k2, double k3, double[] r, double[] t, double x, double y)
        {
            double xc = r[0] * x + r[1] * y + t[0], yc = r[3] * x + r[4] * y + t[1], zc = r[6] * x + r[7] * y + t[2];
            double u = xc / zc, v = yc / zc, r2 = u * u + v * v;
            double rad = 1 + r2 * (k1 + r2 * (k2 + r2 * k3));
            return new Point2(f * u * rad + cx, f * v * rad + cy);
        }

        private double[] Residuals(double[] p)
        {
            double f = p[0], k1 = p[1], k2 = p[2], k3 = p[3];
            double[] r = Rotations.FromRvec(new[] { p[4], p[5], p[6] }), t = { p[7], p[8], p[9] };
            var res = new List<double>();
            int k = 10;
            foreach (Board b in Boards)
            {
                double bx = b.X, by = b.Y, th = b.Theta;
                if (!b.Fixed) { bx = p[k++]; by = p[k++]; th = p[k++]; }
                double c = Math.Cos(th), s = Math.Sin(th);
                for (int i = 0; i < b.Local.Length; i++)
                {
                    Point2 l = b.Local[i];
                    Point2 q = Project(f, Cx, Cy, k1, k2, k3, r, t, bx + c * l.X - s * l.Y, by + s * l.X + c * l.Y);
                    res.Add(q.X - b.Pixels[i].X);
                    res.Add(q.Y - b.Pixels[i].Y);
                }
            }
            return res.ToArray();
        }

        /// <summary>n1.p = d1 ve n2.p = d2 dogrularinin kesisimi; paralelse NaN.</summary>
        public static Point2 Intersect(Point2 n1, double d1, Point2 n2, double d2)
        {
            double det = n1.X * n2.Y - n1.Y * n2.X;
            if (Math.Abs(det) < 1e-9) return new Point2(double.NaN, double.NaN);
            return new Point2((d1 * n2.Y - d2 * n1.Y) / det, (n1.X * d2 - n2.X * d1) / det);
        }

        /// <summary>
        /// Noktalara dik uzaklik toplami en kucuk dogru (toplam en kucuk kareler): birim normal n ve n.p = d. Donen dizi
        /// her noktanin dogruya isaretli uzakligi.
        /// </summary>
        public static double[] FitLine(Point2[] pts, out Point2 normal, out double offset)
        {
            double mx = 0, my = 0;
            foreach (Point2 p in pts) { mx += p.X; my += p.Y; }
            mx /= pts.Length; my /= pts.Length;
            double sxx = 0, syy = 0, sxy = 0;
            foreach (Point2 p in pts) { double dx = p.X - mx, dy = p.Y - my; sxx += dx * dx; syy += dy * dy; sxy += dx * dy; }
            // Dogrultu en buyuk ozdegerin ozvektoru; normal ona dik
            double ang = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
            normal = new Point2(-Math.Sin(ang), Math.Cos(ang));
            offset = normal.X * mx + normal.Y * my;
            var res = new double[pts.Length];
            for (int i = 0; i < pts.Length; i++) res[i] = normal.X * pts[i].X + normal.Y * pts[i].Y - offset;
            return res;
        }

        /// <summary>Goruntu noktasinin tabla duzlemindeki (Z = 0) yeri: radyal bukulme Newton ile giderilir, isin duzlemle kesisimi.</summary>
        public static Point2 BackProject(double f, double cx, double cy, double k1, double k2, double k3, double[] r, double[] t, Point2 pixel)
        {
            double xd = (pixel.X - cx) / f, yd = (pixel.Y - cy) / f;
            double rd = Math.Sqrt(xd * xd + yd * yd), ru = rd;
            for (int it = 0; it < 30 && rd > 1e-12; it++)
            {
                double r2 = ru * ru, g = ru * (1 + r2 * (k1 + r2 * (k2 + r2 * k3))) - rd;
                double dg = 1 + r2 * (3 * k1 + r2 * (5 * k2 + r2 * 7 * k3));
                if (Math.Abs(dg) < 1e-12) break;
                double step = g / dg;
                ru -= step;
                if (Math.Abs(step) < 1e-15) break;
            }
            double sc = rd > 1e-12 ? ru / rd : 1, x = xd * sc, y = yd * sc;
            // Kamera merkezi C = -R^T t, isin d = R^T (x, y, 1)
            double ccx = -(r[0] * t[0] + r[3] * t[1] + r[6] * t[2]);
            double ccy = -(r[1] * t[0] + r[4] * t[1] + r[7] * t[2]);
            double ccz = -(r[2] * t[0] + r[5] * t[1] + r[8] * t[2]);
            double dx = r[0] * x + r[3] * y + r[6], dy = r[1] * x + r[4] * y + r[7], dz = r[2] * x + r[5] * y + r[8];
            double s = Math.Abs(dz) < 1e-12 ? double.NaN : -ccz / dz;
            return new Point2(ccx + s * dx, ccy + s * dy);
        }

        /// <summary>Fotograf noktalarinin su anki cozumle tabla duzlemindeki yeri (cozum cercevesinde, mm).</summary>
        public Point2[] LineOnTable(Point2[] pixels)
        {
            double[] r = Rotations.FromRvec(Rvec);
            return pixels.Select(q => BackProject(F, Cx, Cy, K1, K2, K3, r, Tvec, q)).ToArray();
        }

        private static double Cost(double[] r) { double s = 0; foreach (double v in r) s += v * v; return s; }

        /// <summary>Baslangic degerlerinden cozer. Basarili ise true; RmsPx piksel cinsinden yeniden izdusum hatasidir.</summary>
        public bool Solve(int maxIterations = 200)
        {
            if (Boards.Count == 0 || !Boards.Any(b => b.Fixed)) throw new InvalidOperationException("En az bir dama sabit olmali.");
            double[] p = Pack();
            int n = p.Length;
            double[] r = Residuals(p);
            double cost = Cost(r), lambda = -1;
            for (Iterations = 0; Iterations < maxIterations; Iterations++)
            {
                // Sayisal Jacobian (merkezi fark)
                int m = r.Length;
                var J = new double[m, n];
                for (int j = 0; j < n; j++)
                {
                    if (FixIntrinsics && j < 4) continue;   // sutun sifir kalir
                    double h = j == 0 ? 1e-4 : j < 4 ? 1e-7 : j < 7 ? 1e-8 : j < 10 ? 1e-4 : (j - 10) % 3 == 2 ? 1e-8 : 1e-4;
                    double keep = p[j];
                    p[j] = keep + h; double[] rp = Residuals(p);
                    p[j] = keep - h; double[] rm = Residuals(p);
                    p[j] = keep;
                    for (int i = 0; i < m; i++) J[i, j] = (rp[i] - rm[i]) / (2 * h);
                }
                var A = new double[n, n];
                var g = new double[n];
                for (int a = 0; a < n; a++)
                {
                    for (int i = 0; i < m; i++) g[a] += J[i, a] * r[i];
                    for (int b = a; b < n; b++)
                    {
                        double s = 0;
                        for (int i = 0; i < m; i++) s += J[i, a] * J[i, b];
                        A[a, b] = A[b, a] = s;
                    }
                }
                if (lambda < 0) lambda = 1e-3;
                bool improved = false, converged = false;
                double[] step = null;
                for (int tries = 0; tries < 12; tries++)
                {
                    var M = (double[,])A.Clone();
                    for (int a = 0; a < n; a++) M[a, a] += FixIntrinsics && a < 4 ? 1 : lambda * Math.Max(A[a, a], 1e-12);   // sabit lens: adim 0
                    step = SolveLinear(M, g.Select(v => -v).ToArray());
                    if (step == null) { lambda *= 10; continue; }
                    double[] pn = p.Zip(step, (x, d) => x + d).ToArray();
                    double[] rn = Residuals(pn);
                    double cn = Cost(rn);
                    if (!double.IsNaN(cn) && cn < cost)
                    {
                        double rel = (cost - cn) / Math.Max(cost, 1e-30);
                        p = pn; r = rn; cost = cn; lambda = Math.Max(lambda / 3, 1e-12); improved = true;
                        converged = rel < 1e-12;
                        break;
                    }
                    lambda *= 4;
                }
                if (!improved || converged) break;
            }
            Unpack(p);
            RmsPx = Math.Sqrt(cost / (r.Length / 2.0));   // OpenCV ile ayni tanim: nokta basina piksel
            return !double.IsNaN(RmsPx) && !double.IsInfinity(RmsPx);
        }

        /// <summary>Kismi pivotlu Gauss eleme. Tekil ise null.</summary>
        public static double[] SolveLinear(double[,] a, double[] b)
        {
            int n = b.Length;
            var m = (double[,])a.Clone();
            var x = (double[])b.Clone();
            for (int c = 0; c < n; c++)
            {
                int piv = c; double best = Math.Abs(m[c, c]);
                for (int i = c + 1; i < n; i++) if (Math.Abs(m[i, c]) > best) { best = Math.Abs(m[i, c]); piv = i; }
                if (best < 1e-300) return null;
                if (piv != c)
                {
                    for (int j = 0; j < n; j++) { double tmp = m[c, j]; m[c, j] = m[piv, j]; m[piv, j] = tmp; }
                    double tb = x[c]; x[c] = x[piv]; x[piv] = tb;
                }
                for (int i = c + 1; i < n; i++)
                {
                    double f = m[i, c] / m[c, c];
                    if (f == 0) continue;
                    for (int j = c; j < n; j++) m[i, j] -= f * m[c, j];
                    x[i] -= f * x[c];
                }
            }
            for (int i = n - 1; i >= 0; i--)
            {
                double s = x[i];
                for (int j = i + 1; j < n; j++) s -= m[i, j] * x[j];
                x[i] = s / m[i, i];
            }
            return x.Any(v => double.IsNaN(v) || double.IsInfinity(v)) ? null : x;
        }

        /// <summary>
        /// Cozum cercevesini tabla cercevesine tasir: tabla noktasi = R(phi) * cozum noktasi + shift. Kamera konumu ve
        /// damalarin yeri buna gore guncellenir; lens degismez.
        /// </summary>
        public void Reframe(double phi, Point2 shift)
        {
            double c = Math.Cos(phi), s = Math.Sin(phi);
            // Kamera: x_kam = R p_cozum + t ve p_cozum = Rz(phi)^T (p_tabla - shift)  =>  R' = R Rz^T, t' = t - R' shift
            double[] R = Rotations.FromRvec(Rvec);
            double[] RzT = { c, s, 0, -s, c, 0, 0, 0, 1 };
            double[] Rn = Rotations.Multiply(R, RzT);
            double[] tn =
            {
                Tvec[0] - (Rn[0] * shift.X + Rn[1] * shift.Y),
                Tvec[1] - (Rn[3] * shift.X + Rn[4] * shift.Y),
                Tvec[2] - (Rn[6] * shift.X + Rn[7] * shift.Y)
            };
            Rvec = Rotations.ToRvec(Rn); Tvec = tn;
            foreach (Board b in Boards)
            {
                double x = c * b.X - s * b.Y + shift.X, y = s * b.X + c * b.Y + shift.Y;
                b.X = x; b.Y = y; b.Theta += phi;
            }
        }

        /// <summary>
        /// Iki nokta takimini ust uste getiren rijit donusum (olceksiz): to ~ R(phi) * from + shift (en kucuk kareler).
        /// </summary>
        public static void RigidFit(IList<Point2> from, IList<Point2> to, out double phi, out Point2 shift)
        {
            int n = from.Count;
            double fx = from.Average(p => p.X), fy = from.Average(p => p.Y), tx = to.Average(p => p.X), ty = to.Average(p => p.Y);
            double sxx = 0, sxy = 0;
            for (int i = 0; i < n; i++)
            {
                double ax = from[i].X - fx, ay = from[i].Y - fy, bx = to[i].X - tx, by = to[i].Y - ty;
                sxx += ax * bx + ay * by; sxy += ax * by - ay * bx;
            }
            phi = Math.Atan2(sxy, sxx);
            double c = Math.Cos(phi), s = Math.Sin(phi);
            shift = new Point2(tx - (c * fx - s * fy), ty - (s * fx + c * fy));
        }
    }

    /// <summary>Rodrigues donusumleri (satir sirali 3x3).</summary>
    public static class Rotations
    {
        public static double[] FromRvec(double[] rv)
        {
            double rx = rv[0], ry = rv[1], rz = rv[2];
            double th = Math.Sqrt(rx * rx + ry * ry + rz * rz);
            if (th < 1e-12) return new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
            double kx = rx / th, ky = ry / th, kz = rz / th, c = Math.Cos(th), s = Math.Sin(th), v = 1 - c;
            return new[]
            {
                c + kx * kx * v, kx * ky * v - kz * s, kx * kz * v + ky * s,
                ky * kx * v + kz * s, c + ky * ky * v, ky * kz * v - kx * s,
                kz * kx * v - ky * s, kz * ky * v + kx * s, c + kz * kz * v
            };
        }

        public static double[] ToRvec(double[] R)
        {
            double tr = R[0] + R[4] + R[8];
            double cos = Math.Max(-1, Math.Min(1, (tr - 1) / 2));
            double th = Math.Acos(cos);
            if (th < 1e-9) return new double[] { 0, 0, 0 };
            if (Math.PI - th > 1e-4)
            {
                double k = th / (2 * Math.Sin(th));
                return new[] { k * (R[7] - R[5]), k * (R[2] - R[6]), k * (R[3] - R[1]) };
            }
            // ~180 derece: eksen kosegenden
            double x = Math.Sqrt(Math.Max(0, (R[0] + 1) / 2)), y = Math.Sqrt(Math.Max(0, (R[4] + 1) / 2)), z = Math.Sqrt(Math.Max(0, (R[8] + 1) / 2));
            if (x >= y && x >= z) { y = Math.Sign(R[1] + R[3]) * y; z = Math.Sign(R[2] + R[6]) * z; }
            else if (y >= z) { x = Math.Sign(R[1] + R[3]) * x; z = Math.Sign(R[5] + R[7]) * z; }
            else { x = Math.Sign(R[2] + R[6]) * x; y = Math.Sign(R[5] + R[7]) * y; }
            double nrm = Math.Sqrt(x * x + y * y + z * z);
            return new[] { th * x / nrm, th * y / nrm, th * z / nrm };
        }

        public static double[] Multiply(double[] a, double[] b)
        {
            var m = new double[9];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    m[3 * i + j] = a[3 * i] * b[j] + a[3 * i + 1] * b[3 + j] + a[3 * i + 2] * b[6 + j];
            return m;
        }
    }
}
