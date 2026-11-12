using System;
using System.Collections.Generic;
using System.Linq;
using KopruKesim.Core;
using OpenCvSharp;

namespace KopruKesim.Vision
{
    /// <summary>Kenar tabanli dortgen bulucunun ayarlari.</summary>
    public sealed class EdgeRectOptions
    {
        /// <summary>Bundan kisa kenarli parcalar aranmaz (mm).</summary>
        public double MinSideMm = 50;
        /// <summary>Tabla kenarindan bu kadar iceride kalmayan cizgiler tabla yapisidir (mm).</summary>
        public double BorderMm = 25;
        /// <summary>En fazla bu kadar parca aranir.</summary>
        public int MaxObjects = 6;
        /// <summary>Karsi kenar profil tepesi, profilin ortancasinin en az bu kati olmali.</summary>
        public double PeakRatio = 4;
        /// <summary>Kenar boyunca ortanca gecis gucu en az bu olmali (Sobel, 0-255 goruntu).</summary>
        public double MinStrength = 10;
        /// <summary>Dortgenin ici ile disi arasindaki en az renk farki (Lab). Altindaysa tabla deseni sayilir.</summary>
        public double MinContrast = 6;
        /// <summary>Her kenarin en az bu orani gercekten kenar olmali (gecis gucu MinStrength ustu).</summary>
        public double MinEdgeCoverage = 0.6;
        /// <summary>Kenar kosenin otesinde en fazla bu oranda devam edebilir (tabla cizgisi ayiklama).</summary>
        public double MaxBeyondCoverage = 0.5;
    }

    /// <summary>
    /// Cismi renginden degil DUZ KENARLARINDAN bulur (beyaz tas / beyaz tabla gibi renk farki
    /// zayif durumlar icin). Ustten (duzeltilmis) goruntude calisir:
    ///  1) uzun duz cizgi parcalari bulunur (LSD); damar, leke gibi kivrimli izler kisa kalir,
    ///  2) her uzun parcanin gercek uclari kenar boyunca yurunerek bulunur; tabla boyunca uzanan
    ///     cizgiler (tahta araliklari) ve tabla kenarina degenler tabla yapisi sayilip atilir,
    ///  3) paralel karsi kenar, kenar boyunca ortalama gecis gucu profilinin tepesiyle aranir
    ///     (zayif kenar uzunluk boyunca toplaninca belirginlesir),
    ///  4) kisa kenarlar uclarin yakininda dik profil tepesiyle bulunur,
    ///  5) dort dogru kesistirilip dortgen kurulur; kenar destegi ve acilarla sinanir.
    /// Yalnizca dortgen (kare, dikdortgen, paralelkenar benzeri) parcalar icindir.
    /// </summary>
    public static class EdgeRectFinder
    {
        private static readonly bool Debug = Environment.GetEnvironmentVariable("KK_DEBUG") == "1";

        /// <param name="rectified">Ustten goruntu (BGR), olcek scale piksel/mm, orijin sol alt.</param>
        /// <returns>Dortgenler, tabla mm (siluet; kalinlik duzeltmesi yapilmamis).</returns>
        public static List<Point2[]> Find(Mat rectified, double scale, double lengthMm, double widthMm, EdgeRectOptions opt)
        {
            opt = opt ?? new EdgeRectOptions();
            var result = new List<Point2[]>();
            var field = new Field(rectified);
            double s = scale;
            double border = opt.BorderMm * s, minSide = opt.MinSideMm * s;

            Vec4f[] lines;
            using (var gray = new Mat())
            using (LineSegmentDetector lsd = LineSegmentDetector.Create(LineSegmentDetectorModes.RefineStd))
            {
                Cv2.CvtColor(rectified, gray, ColorConversionCodes.BGR2GRAY);
                Cv2.GaussianBlur(gray, gray, new Size(3, 3), 0);
                double[] w, p, n;
                lsd.Detect(gray, out lines, out w, out p, out n);
            }
            var segs = new List<Seg>();
            foreach (Vec4f l in lines)
            {
                var a = new Point2(l.Item0, l.Item1); var b = new Point2(l.Item2, l.Item3);
                if (Point2.Distance(a, b) < minSide) continue;
                if (!field.InsideBy(a, border) || !field.InsideBy(b, border)) continue;
                segs.Add(new Seg(a, b));
            }
            segs.Sort((x, y) => y.Length.CompareTo(x.Length));
            if (Debug) Console.WriteLine("  [kenar] uzun duz parca: {0}", segs.Count);

            // Once butun adaylari topla (her uzun parca bir aday uretebilir)
            var candidates = new List<Point2[]>();
            foreach (Seg seg in segs.Take(60))
            {
                Point2[] quad = BuildQuad(field, seg, s, lengthMm, widthMm, border, minSide, opt);
                if (quad == null) continue;
                // Ayni dortgen baska bir parcadan da bulunmus olabilir
                if (candidates.Any(c => c.Zip(quad, (a, b) => Point2.Distance(a, b)).Max() < 8 * s)) continue;
                double contrast = InsideOutsideContrast(rectified, quad, s);
                if (Debug) Console.WriteLine("  [kenar aday] {0}  ic/dis farki {1:F1}", string.Join(" ", quad.Select(p => new Point2(p.X / s, widthMm - p.Y / s).ToString())), contrast);
                // Damar/leke cizgilerinin olusturdugu dortgenin ici tablanin aynisidir
                if (contrast < opt.MinContrast) continue;
                candidates.Add(quad);
            }
            // Buyukten kucuge sec: bir cismin icindeki etiket/desen cizgileri disaridaki dortgene yenilir
            var taken = new List<Point2[]>();
            foreach (Point2[] quad in candidates.OrderByDescending(q => Math.Abs(PolygonMath.SignedArea(q))))
            {
                if (result.Count >= opt.MaxObjects) break;
                Point2[] big = Inflate(quad, 3 * s);
                if (taken.Any(t => PolygonMath.Contains(t, Centroid(quad)) || PolygonMath.Contains(big, Centroid(t)))) continue;
                taken.Add(big);
                result.Add(quad.Select(p => new Point2(p.X / s, widthMm - p.Y / s)).ToArray());
                if (Debug) Console.WriteLine("  [kenar] secildi: {0}", string.Join(" ", result[result.Count - 1].Select(p => p.ToString())));
            }
            return result;
        }

        /// <summary>
        /// Dortgenin ici (kenardan 5 mm iceride) ile hemen disinin (5-20 mm disarida bir serit) ortalama
        /// renk farki (Lab, parlaklik yari agirlik). Gercek bir cisimde belirgin, tabla deseninde kucuk.
        /// </summary>
        private static double InsideOutsideContrast(Mat bgr, Point2[] quad, double s)
        {
            using (var lab = new Mat())
            using (var inner = new Mat(bgr.Size(), MatType.CV_8UC1, Scalar.All(0)))
            using (var ring = new Mat(bgr.Size(), MatType.CV_8UC1, Scalar.All(0)))
            using (var hole = new Mat(bgr.Size(), MatType.CV_8UC1, Scalar.All(0)))
            {
                Cv2.CvtColor(bgr, lab, ColorConversionCodes.BGR2Lab);
                Func<Point2[], Point[]> P = q => q.Select(p => new Point((int)Math.Round(p.X), (int)Math.Round(p.Y))).ToArray();
                Cv2.FillConvexPoly(inner, P(Inflate(quad, -5 * s)), Scalar.All(255));
                Cv2.FillConvexPoly(ring, P(Inflate(quad, 20 * s)), Scalar.All(255));
                Cv2.FillConvexPoly(hole, P(Inflate(quad, 5 * s)), Scalar.All(255));
                ring.SetTo(Scalar.All(0), hole);
                if (Cv2.CountNonZero(inner) < 50 || Cv2.CountNonZero(ring) < 50) return 0;
                Scalar a = Cv2.Mean(lab, inner), b = Cv2.Mean(lab, ring);
                return Math.Sqrt(Math.Pow(0.5 * (a.Val0 - b.Val0), 2) + Math.Pow(a.Val1 - b.Val1, 2) + Math.Pow(a.Val2 - b.Val2, 2));
            }
        }

        private static Point2[] BuildQuad(Field f, Seg seg, double s, double lengthMm, double widthMm, double border, double minSide, EdgeRectOptions opt)
        {
            // 1) A kenari: dogruyu gecis noktalarina oturt, uclarini yuru
            double[] lineA = f.FitLine(seg.A, seg.B, 3);
            if (lineA == null) return null;
            Point2 d = Unit(seg.B - seg.A), n = new Point2(-d.Y, d.X);
            Point2 midA = Project(lineA, seg.Mid);
            double thrA = 0.35 * f.MedianStrength(Project(lineA, seg.A), Project(lineA, seg.B), n);
            if (thrA * 2.857 < opt.MinStrength) return null;
            double tA0, tA1;
            f.Walk(midA, d, n, thrA, out tA0, out tA1);
            Point2 a0 = midA + d * tA0, a1 = midA + d * tA1;
            double spanA = tA1 - tA0;
            if (spanA < minSide) return null;
            // Tabla boyunca uzanan ya da tabla kenarina degen cizgi: tabla yapisi (tahta araligi vb.)
            if (!f.InsideBy(a0, border) || !f.InsideBy(a1, border)) return null;
            if (spanA > 0.9 * Math.Max(lengthMm, widthMm) * s) return null;

            // 2) Karsi kenar: A boyunca ortalama gecis gucu profili, iki yonde
            double maxOff = Math.Min(lengthMm, widthMm) * s;
            double bestOff = 0, bestVal = 0, bestMed = 0;
            foreach (int sign in new[] { 1, -1 })
            {
                var prof = new List<KeyValuePair<double, double>>();
                for (double o = minSide; o <= maxOff; o += 1)
                {
                    Point2 off = n * (sign * o);
                    if (!f.InsideBy(a0 + off, border * 0.5) || !f.InsideBy(a1 + off, border * 0.5)) break;
                    prof.Add(new KeyValuePair<double, double>(sign * o, f.MedianStrength(a0 + off, a1 + off, n, 0.1, 0.9)));
                }
                if (prof.Count < 5) continue;
                double med = Median(prof.Select(p => p.Value));
                foreach (var p in prof)
                    if (p.Value > bestVal && p.Value >= opt.PeakRatio * Math.Max(1, med) && p.Value >= opt.MinStrength)
                    { bestVal = p.Value; bestOff = p.Key; bestMed = med; }
            }
            if (bestVal <= 0) return null;
            double[] lineB = f.FitLine(a0 + n * bestOff, a1 + n * bestOff, 3);
            if (lineB == null || !Parallel(lineA, lineB, 3)) return null;

            // 3) Uclar: A ve B'nin yurunmus uclari birlestirilir (zayif kenar kisa yurunebilir)
            Point2 midB = Project(lineB, midA + n * bestOff);
            double thrB = 0.35 * f.MedianStrength(Project(lineB, a0), Project(lineB, a1), n);
            double tB0, tB1;
            f.Walk(midB, d, n, Math.Max(thrB, opt.MinStrength * 0.35), out tB0, out tB1);
            double e0 = Math.Min(tA0, tB0), e1 = Math.Max(tA1, tB1);   // en genis kapsama

            // 4) Kisa kenarlar: tahmini ucun +-25 mm cevresinde dik profil tepesi
            double[] lineC = ShortEdge(f, midA, d, n, bestOff, e0, s, opt);
            double[] lineD = ShortEdge(f, midA, d, n, bestOff, e1, s, opt);
            if (lineC == null || lineD == null) return null;

            // 5) Kesistir ve sina
            var q = new[] { Cross(lineA, lineC), Cross(lineC, lineB), Cross(lineB, lineD), Cross(lineD, lineA) };
            if (q.Any(x => Math.Abs(x[2]) < 1e-9)) return null;
            Point2[] quad = q.Select(x => new Point2(x[0] / x[2], x[1] / x[2])).ToArray();
            for (int i = 0; i < 4; i++)
            {
                Point2 u = quad[(i + 3) % 4] - quad[i], v = quad[(i + 1) % 4] - quad[i];
                double ang = Math.Acos(Math.Max(-1, Math.Min(1, Point2.Dot(u, v) / (u.Length * v.Length)))) * 180 / Math.PI;
                if (ang < 70 || ang > 110) return null;
                if (Point2.Distance(quad[i], quad[(i + 1) % 4]) < minSide * 0.6) return null;
                if (!f.InsideBy(quad[i], 0)) return null;
            }
            // Her kenar gercekten kenar mi (kisa kenarlar daha zayif olabilir)
            for (int i = 0; i < 4; i++)
            {
                Point2 p = quad[i], q2 = quad[(i + 1) % 4], nrm = Normal(p, q2), dir = Unit(q2 - p);
                // Esikler kenarin kendi gucune gore goreli (doku/damar sabit esigi kolayca asar)
                double edgeMed = f.MedianStrength(p, q2, nrm, 0.15, 0.85);
                if (edgeMed < opt.MinStrength) return null;
                if (f.Coverage(p, q2, nrm, 0.4 * edgeMed) < opt.MinEdgeCoverage) return null;
                // Gercek cismin kenari kosede biter; tabla cizgisi (tahta araligi) kosenin otesine devam eder
                double ext = Math.Min(80 * s, 0.3 * Point2.Distance(p, q2));
                Point2 b0 = p - dir * (5 * s), b1 = q2 + dir * (5 * s);
                double c0 = f.InsideBy(b0 - dir * ext, 1) ? f.Coverage(b0 - dir * ext, b0, nrm, 0.5 * edgeMed) : 0;
                double c1 = f.InsideBy(b1 + dir * ext, 1) ? f.Coverage(b1, b1 + dir * ext, nrm, 0.5 * edgeMed) : 0;
                if (Debug && Math.Max(c0, c1) > opt.MaxBeyondCoverage)
                    Console.WriteLine("    red: kenar {0} kosenin otesine devam ediyor ({1:F2} / {2:F2}), A {3:F0} mm", i, c0, c1, spanA / s);
                if (Math.Max(c0, c1) > opt.MaxBeyondCoverage) return null;
            }
            if (Debug) Console.WriteLine("    A {0:F0} mm, karsi kenar {1:F0} mm uzakta (tepe {2:F1}, ortanca {3:F1})", spanA / s, Math.Abs(bestOff) / s, bestVal, bestMed);
            return quad;
        }

        /// <summary>A ile karsi kenar arasinda, d dogrultusunda t=e civarindaki dik kenari bulur.</summary>
        private static double[] ShortEdge(Field f, Point2 midA, Point2 d, Point2 n, double off, double e, double s, EdgeRectOptions opt)
        {
            double best = -1, bestT = e;
            for (double t = e - 25 * s; t <= e + 25 * s; t += 0.5)
            {
                Point2 p = midA + d * t, q = p + n * off;
                double v = f.MedianStrength(p, q, d, 0.15, 0.85);
                if (v > best) { best = v; bestT = t; }
            }
            Point2 p0 = midA + d * bestT;
            return f.FitLine(p0, p0 + n * off, 3) ?? LineThrough(p0, p0 + n * off);
        }

        // ---------- yardimcilar ----------

        private sealed class Seg
        {
            public readonly Point2 A, B;
            public Seg(Point2 a, Point2 b) { A = a; B = b; }
            public double Length { get { return Point2.Distance(A, B); } }
            public Point2 Mid { get { return (A + B) * 0.5; } }
        }

        /// <summary>Gradyan alani (ustten goruntu).</summary>
        private sealed class Field
        {
            private readonly float[] _gx, _gy;
            private readonly int _w, _h;

            public Field(Mat bgr)
            {
                _w = bgr.Cols; _h = bgr.Rows;
                using (var g = new Mat())
                using (var gx = new Mat())
                using (var gy = new Mat())
                {
                    Cv2.CvtColor(bgr, g, ColorConversionCodes.BGR2GRAY);
                    Cv2.GaussianBlur(g, g, new Size(3, 3), 0);
                    Cv2.Sobel(g, gx, MatType.CV_32F, 1, 0);
                    Cv2.Sobel(g, gy, MatType.CV_32F, 0, 1);
                    gx.GetArray(out _gx);
                    gy.GetArray(out _gy);
                }
            }

            public bool InsideBy(Point2 p, double m) { return p.X >= m && p.Y >= m && p.X < _w - m && p.Y < _h - m; }

            private double Raw(double x, double y, Point2 n)
            {
                int xi = (int)Math.Round(x), yi = (int)Math.Round(y);
                if (xi < 0 || yi < 0 || xi >= _w || yi >= _h) return 0;
                int i = yi * _w + xi;
                return Math.Abs(_gx[i] * n.X + _gy[i] * n.Y);
            }

            /// <summary>p civarinda (+-2 px dik) n yonundeki en guclu gecis.</summary>
            public double Strength(Point2 p, Point2 n)
            {
                double best = 0;
                for (int o = -2; o <= 2; o++) best = Math.Max(best, Raw(p.X + n.X * o, p.Y + n.Y * o, n));
                return best;
            }

            /// <summary>p-q parcasi boyunca (t0..t1 arasi) n yonundeki gecis gucunun ortancasi.</summary>
            public double MedianStrength(Point2 p, Point2 q, Point2 n, double t0 = 0.05, double t1 = 0.95)
            {
                int k = Math.Max(10, (int)(Point2.Distance(p, q) / 2));
                var v = new double[k];
                for (int i = 0; i < k; i++) { double t = t0 + (t1 - t0) * i / (k - 1); v[i] = Strength(p + (q - p) * t, n); }
                Array.Sort(v);
                return v[k / 2];
            }

            /// <summary>p-q boyunca (uclarin %10'u haric) gecis gucu esigi asan orneklerin orani.</summary>
            public double Coverage(Point2 p, Point2 q, Point2 n, double thr)
            {
                int k = Math.Max(10, (int)(Point2.Distance(p, q) / 2)), ok = 0;
                for (int i = 0; i < k; i++) if (Strength(p + (q - p) * (0.1 + 0.8 * i / (k - 1)), n) >= thr) ok++;
                return (double)ok / k;
            }

            /// <summary>Dogru boyunca ortadan iki yone yurur; gecisin surekli kesildigi yerde durur.</summary>
            public void Walk(Point2 mid, Point2 d, Point2 n, double thr, out double t0, out double t1)
            {
                const int window = 12;   // 12 px boyunca %80 zayifsa kenar bitti
                double max = Math.Max(_w, _h);
                Func<int, double> go = dir =>
                {
                    for (double t = 0; t < max; t += 1)
                    {
                        if (!InsideBy(mid + d * (dir * t), 1)) return t;
                        int low = 0;
                        for (int j = 0; j < window; j++) if (Strength(mid + d * (dir * (t + j)), n) < thr) low++;
                        if (low >= 0.8 * window) return t;
                    }
                    return max;
                };
                t0 = -go(-1); t1 = go(1);
            }

            /// <summary>p-q yakinindaki (+-win px) en guclu gecis noktalarina dogru oturtur (yinelemeli ayiklama).</summary>
            public double[] FitLine(Point2 p, Point2 q, int win)
            {
                double len = Point2.Distance(p, q);
                if (len < 5) return null;
                Point2 d = (q - p) * (1 / len), n = new Point2(-d.Y, d.X);
                var pts = new List<Point2f>();
                int k = Math.Max(20, (int)(len / 2));
                for (int i = 0; i < k; i++)
                {
                    Point2 c = p + d * (len * (0.05 + 0.9 * i / (k - 1)));
                    double best = 0; Point2 at = c;
                    for (int o = -win; o <= win; o++)
                    {
                        double v = Raw(c.X + n.X * o, c.Y + n.Y * o, n);
                        if (v > best) { best = v; at = c + n * o; }
                    }
                    if (best > 0) pts.Add(new Point2f((float)at.X, (float)at.Y));
                }
                if (pts.Count < 10) return null;
                double[] line = null;
                for (int it = 0; it < 3; it++)
                {
                    Line2D l = Cv2.FitLine(pts, DistanceTypes.Huber, 0, 0.01, 0.01);
                    line = new[] { l.Vy, -l.Vx, l.Vx * l.Y1 - l.Vy * l.X1 };
                    double nn = Math.Sqrt(line[0] * line[0] + line[1] * line[1]);
                    var keep = pts.Where(t => Math.Abs(line[0] * t.X + line[1] * t.Y + line[2]) / nn <= 1.5).ToList();
                    if (keep.Count < 10 || keep.Count == pts.Count) break;
                    pts = keep;
                }
                return line;
            }
        }

        private static Point2 Unit(Point2 v) { double l = v.Length; return l < 1e-12 ? v : v * (1 / l); }
        private static Point2 Normal(Point2 a, Point2 b) { Point2 d = Unit(b - a); return new Point2(-d.Y, d.X); }
        private static double[] LineThrough(Point2 p, Point2 q) { return Cross(new[] { p.X, p.Y, 1.0 }, new[] { q.X, q.Y, 1.0 }); }
        private static double[] Cross(double[] u, double[] v)
        {
            return new[] { u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0] };
        }
        private static Point2 Project(double[] l, Point2 p)
        {
            double nn = l[0] * l[0] + l[1] * l[1];
            double t = (l[0] * p.X + l[1] * p.Y + l[2]) / nn;
            return new Point2(p.X - l[0] * t, p.Y - l[1] * t);
        }
        private static bool Parallel(double[] a, double[] b, double maxDeg)
        {
            double c = Math.Abs(a[0] * b[1] - a[1] * b[0]) / Math.Sqrt((a[0] * a[0] + a[1] * a[1]) * (b[0] * b[0] + b[1] * b[1]));
            return Math.Asin(Math.Min(1, c)) * 180 / Math.PI <= maxDeg;
        }
        private static double Median(IEnumerable<double> v) { var a = v.ToArray(); Array.Sort(a); return a.Length == 0 ? 0 : a[a.Length / 2]; }
        private static Point2 Centroid(Point2[] q) { return new Point2(q.Average(p => p.X), q.Average(p => p.Y)); }
        private static Point2[] Inflate(Point2[] q, double by)
        {
            Point2 c = Centroid(q);
            return q.Select(p => { Point2 v = p - c; double l = v.Length; return l < 1e-9 ? p : c + v * ((l + by) / l); }).ToArray();
        }
    }
}
