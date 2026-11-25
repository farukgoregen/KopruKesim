using System;
using System.Linq;

namespace KopruKesim.Core
{
    /// <summary>
    /// Kamera modelinin (lens + konum) tablada kalan hatasini seritle olculen tablaya gore duzelten donusum. Iki parca:
    ///
    /// 1. Kalan lens bukulmesi: tabla duzleminde kameranin optik ekseninin tablayi kestigi nokta (Center) etrafinda
    ///    p' = c + d (1 + E |d|^2), d = p - c. Kamera tablaya yukaridan baktigi icin bukulmesi giderilmis goruntudeki
    ///    radyal hata tabla duzleminde de radyaldir. E kalibrasyonda tabla kenarlarinin duz ve paspaslarin olcusunde
    ///    cikmasindan bulunur.
    /// 2. Kamera konumu hatasi: radyal duzeltmeden sonra dort tabla kosesi seritle olculen kenar ve kosegenden kurulan
    ///    gercek koselere homografi ile tam oturtulur.
    ///
    /// 3. Kenar egrileri (istege bagli, L kose kalibrasyonu): 1-2'den sonra tablanin kenarlari fotografta bulunan
    ///    kenarlara (L kollari, dis citalarin uzun kenari) gore hala egri kalabilir; her kenar icin kenara dik sapma
    ///    f(s) = s (1 - s) (a + b s) (s kenar boyunca 0..1, koselerde sifir, uclarda egimi L kolunun yonu) oturtulur.
    ///    Tablanin ici dort kenar egrisinin Coons harmanlamasiyla duzeltilir: D(u,v) = (1-v) f_alt(u) n_alt +
    ///    v f_ust(u) n_ust + (1-u) f_sol(v) n_sol + u f_sag(v) n_sag; dogru nokta = t - D. Koseler degismez.
    ///
    /// Kose sirasi LensCalibration.PaperCorners ile ayni: sol ust, sag ust, sol alt, sag alt.
    /// Kenarlar (EdgeEnds): 0 alt (sol alt -> sag alt), 1 sag (sag alt -> sag ust), 2 ust (sol ust -> sag ust), 3 sol.
    /// </summary>
    public sealed class TableCorrection
    {
        /// <summary>Modelin olctugu tabla koseleri (duzeltmesiz, mm).</summary>
        public readonly Point2[] Measured;
        /// <summary>Gercek tabla koseleri (mm).</summary>
        public readonly Point2[] True;
        public readonly Point2 Center;
        /// <summary>Radyal katsayi (1/mm^2).</summary>
        public readonly double E;
        /// <summary>Kenar egrileri: kenar basina (a, b), EdgeEnds sirasiyla (alt, sag, ust, sol); yoksa null.</summary>
        public readonly double[] EdgeCurve;
        private readonly Homography _h, _hInv;
        /// <summary>Kenarlarin disari bakan normalleri (EdgeEnds sirasi), kenar egrisi varsa bir kez hesaplanir.</summary>
        private readonly Point2[] _normals;

        public TableCorrection(Point2[] measured, Point2[] trueCorners, Point2 center, double e)
            : this(measured, trueCorners, center, e, null) { }

        public TableCorrection(Point2[] measured, Point2[] trueCorners, Point2 center, double e, double[] edgeCurve)
        {
            if (edgeCurve != null && (edgeCurve.Length != 8 || edgeCurve.Any(x => double.IsNaN(x) || double.IsInfinity(x))))
                throw new ArgumentException("Kenar egrisi 8 sonlu deger olmali.");
            EdgeCurve = edgeCurve == null || edgeCurve.All(x => x == 0) ? null : (double[])edgeCurve.Clone();
            // Ilk dort nokta tabla koseleri (sol ust, sag ust, sol alt, sag alt); fazla noktada ardindan orta
            // paspaslarin tabla kenarindaki dis koseleri gelir. Dortten fazla noktada homografi en kucuk karelerle.
            if (measured == null || measured.Length < 4 || trueCorners == null || trueCorners.Length != measured.Length)
                throw new ArgumentException("En az dort nokta gerekli.");
            Measured = (Point2[])measured.Clone();
            True = (Point2[])trueCorners.Clone();
            Center = center; E = e;
            _h = Homography.FromPoints(Measured.Select(Radial).ToList(), True.ToList());
            _hInv = _h.Inverse();
            if (EdgeCurve != null) _normals = Enumerable.Range(0, 4).Select(ComputeNormal).ToArray();
        }

        public static void EdgeEnds(Point2[] c, int edge, out Point2 a, out Point2 b)
        {
            switch (edge)
            {
                case 0: a = c[2]; b = c[3]; break;
                case 1: a = c[3]; b = c[1]; break;
                case 2: a = c[0]; b = c[1]; break;
                default: a = c[2]; b = c[0]; break;
            }
        }

        public Point2 Radial(Point2 p)
        {
            Point2 d = p - Center;
            return Center + d * (1 + E * (d.X * d.X + d.Y * d.Y));
        }

        Point2 RadialInverse(Point2 q)
        {
            Point2 d = q - Center;
            double rq = d.Length;
            if (rq < 1e-9 || E == 0) return q;
            // r (1 + E r^2) = rq, Newton
            double r = rq;
            for (int i = 0; i < 20; i++)
            {
                double f = r * (1 + E * r * r) - rq, df = 1 + 3 * E * r * r;
                double step = f / df; r -= step;
                if (Math.Abs(step) < 1e-10) break;
            }
            return Center + d * (r / rq);
        }

        /// <summary>Modelin verdigi nokta (mm) -> gercek tabla mm.</summary>
        public Point2 Apply(Point2 model)
        {
            Point2 t = _h.Transform(Radial(model));
            return EdgeCurve == null ? t : t - Displacement(t);
        }

        /// <summary>Gercek tabla mm -> modelin kullandigi nokta (mm).</summary>
        public Point2 Invert(Point2 table)
        {
            Point2 t = table;
            // t - D(t) = table: D yumusak ve kucuk (birkac on mm), sabit nokta yinelemesi hizla yakinsar
            if (EdgeCurve != null)
                for (int i = 0; i < 20; i++)
                {
                    Point2 next = table + Displacement(t);
                    double dd = Point2.Distance(next, t);
                    t = next;
                    if (dd < 1e-6) break;
                }
            return RadialInverse(_hInv.Transform(t));
        }

        /// <summary>Kenar egrisi s (0..1) noktasinda kenara dik sapma (mm, disari +).</summary>
        public static double CurveValue(double a, double b, double s) => s * (1 - s) * (a + b * s);

        /// <summary>
        /// Gercek tabla dortgeninde (True koseleri) noktanin (u, v) parametresi: P = sol alt (1-u)(1-v) + sag alt u(1-v)
        /// + sag ust u v + sol ust (1-u) v (ters cift dogrusal, Newton).
        /// </summary>
        public void Parameters(Point2 t, out double u, out double v)
        {
            Point2 tl = True[0], tr = True[1], bl = True[2], br = True[3];
            Point2 ex = br - bl, ey = tl - bl;
            u = Point2.Dot(t - bl, ex) / Point2.Dot(ex, ex);
            v = Point2.Dot(t - bl, ey) / Point2.Dot(ey, ey);
            for (int it = 0; it < 8; it++)
            {
                Point2 p = bl * ((1 - u) * (1 - v)) + br * (u * (1 - v)) + tr * (u * v) + tl * ((1 - u) * v);
                Point2 du = (br - bl) * (1 - v) + (tr - tl) * v, dv = (tl - bl) * (1 - u) + (tr - br) * u;
                Point2 r = t - p;
                double det = du.X * dv.Y - du.Y * dv.X;
                if (Math.Abs(det) < 1e-12) break;
                double su = (r.X * dv.Y - r.Y * dv.X) / det, sv = (du.X * r.Y - du.Y * r.X) / det;
                u += su; v += sv;
                if (Math.Abs(su) + Math.Abs(sv) < 1e-12) break;
            }
        }

        /// <summary>Kenarin disari bakan birim normali (EdgeEnds sirasi).</summary>
        public Point2 OutwardNormal(int edge) => _normals != null ? _normals[edge] : ComputeNormal(edge);

        Point2 ComputeNormal(int edge)
        {
            EdgeEnds(True, edge, out Point2 a, out Point2 b);
            Point2 d = b - a, n = new Point2(-d.Y, d.X) * (1 / d.Length);
            Point2 mid = (True[0] + True[1] + True[2] + True[3]) * 0.25;
            return Point2.Dot(n, (a + b) * 0.5 - mid) < 0 ? n * -1 : n;
        }

        // Sapma izgarasi: tablanin cevresi (200 mm pay) 5 mm arayla, ilk kullanimda bir kez hesaplanir. Sapma kubik ve
        // yumusak; cift dogrusal okumanin hatasi ~1e-4 mm. Ustten gorunum milyonlarca noktada Invert cagirir.
        const double GridStep = 5, GridPad = 200;
        private float[] _gx, _gy;
        private double _gx0, _gy0;
        private int _gw, _gh;
        private readonly object _gridLock = new object();

        void BuildGrid()
        {
            double minX = True.Min(p => p.X) - GridPad, minY = True.Min(p => p.Y) - GridPad;
            double maxX = True.Max(p => p.X) + GridPad, maxY = True.Max(p => p.Y) + GridPad;
            int w = (int)Math.Ceiling((maxX - minX) / GridStep) + 1, h = (int)Math.Ceiling((maxY - minY) / GridStep) + 1;
            var gx = new float[w * h]; var gy = new float[w * h];
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    Point2 d = ExactDisplacement(new Point2(minX + i * GridStep, minY + j * GridStep));
                    gx[j * w + i] = (float)d.X; gy[j * w + i] = (float)d.Y;
                }
            _gx0 = minX; _gy0 = minY; _gw = w; _gh = h; _gy = gy; _gx = gx;
        }

        /// <summary>Kenar egrilerinin Coons harmanlamasi: dortgendeki t noktasindaki sapma (mm), izgaradan.</summary>
        public Point2 Displacement(Point2 t)
        {
            if (EdgeCurve == null) return new Point2(0, 0);
            if (_gx == null) lock (_gridLock) if (_gx == null) BuildGrid();
            double fx = (t.X - _gx0) / GridStep, fy = (t.Y - _gy0) / GridStep;
            if (double.IsNaN(fx) || double.IsNaN(fy) || fx < 0 || fy < 0 || fx >= _gw - 1 || fy >= _gh - 1) return ExactDisplacement(t);
            int i = (int)fx, j = (int)fy;
            double ax = fx - i, ay = fy - j;
            int k = j * _gw + i;
            double dx = (_gx[k] * (1 - ax) + _gx[k + 1] * ax) * (1 - ay) + (_gx[k + _gw] * (1 - ax) + _gx[k + _gw + 1] * ax) * ay;
            double dy = (_gy[k] * (1 - ax) + _gy[k + 1] * ax) * (1 - ay) + (_gy[k + _gw] * (1 - ax) + _gy[k + _gw + 1] * ax) * ay;
            return new Point2(dx, dy);
        }

        /// <summary>Kenar egrilerinin Coons harmanlamasi, dogrudan hesap.</summary>
        public Point2 ExactDisplacement(Point2 t)
        {
            if (EdgeCurve == null) return new Point2(0, 0);
            Parameters(t, out double u, out double v);
            double fb = CurveValue(EdgeCurve[0], EdgeCurve[1], u), fr = CurveValue(EdgeCurve[2], EdgeCurve[3], v);
            double ft = CurveValue(EdgeCurve[4], EdgeCurve[5], u), fl = CurveValue(EdgeCurve[6], EdgeCurve[7], v);
            Point2[] nm = _normals;
            return nm[0] * ((1 - v) * fb) + nm[1] * (u * fr) + nm[2] * (v * ft) + nm[3] * ((1 - u) * fl);
        }

        /// <summary>Tablanin icinde en buyuk kayma (mm), bilgi icin.</summary>
        public double MaxShiftMm()
        {
            double max = 0;
            for (int i = 0; i <= 20; i++)
                for (int j = 0; j <= 20; j++)
                {
                    double u = i / 20.0, v = j / 20.0;
                    Point2 p = Measured[2] * ((1 - u) * (1 - v)) + Measured[3] * (u * (1 - v)) + Measured[1] * (u * v) + Measured[0] * ((1 - u) * v);                    max = Math.Max(max, Point2.Distance(Apply(p), p));
                }
            return max;
        }

        /// <summary>
        /// E'yi noktalardan bulur: her E icin homografi en kucuk karelerle kurulur, noktalarin gercek yerden en kucuk
        /// kareler sapmasi en az olan E secilir (altin oran arama). Dort noktada E belirlenemez (homografi her E'de tam
        /// oturur); kenar ortasindaki noktalar kenarin egriligini, yani E'yi belirler.
        /// </summary>
        public static TableCorrection Fit(Point2[] measured, Point2[] trueCorners, Point2 center, out double rmsMm)
        {
            Func<double, double> cost = e =>
            {
                var c = new TableCorrection(measured, trueCorners, center, e);
                double sq = 0;
                for (int i = 0; i < measured.Length; i++) { double d = Point2.Distance(c.Apply(measured[i]), trueCorners[i]); sq += d * d; }
                return Math.Sqrt(sq / measured.Length);
            };
            // |E| < 2e-8: tablanin kosesinde (r ~ 2000 mm) %8 kayma; gercek degerler 1e-9 mertebesinde
            double lo = -2e-8, hi = 2e-8, g = (Math.Sqrt(5) - 1) / 2;
            double x1 = hi - g * (hi - lo), x2 = lo + g * (hi - lo), f1 = cost(x1), f2 = cost(x2);
            for (int i = 0; i < 80; i++)
            {
                if (f1 < f2) { hi = x2; x2 = x1; f2 = f1; x1 = hi - g * (hi - lo); f1 = cost(x1); }
                else { lo = x1; x1 = x2; f1 = f2; x2 = lo + g * (hi - lo); f2 = cost(x2); }
            }
            double best = (lo + hi) / 2;
            rmsMm = cost(best);
            return new TableCorrection(measured, trueCorners, center, best);
        }

        /// <summary>Kayit icin 4n+3 deger: olculen noktalar (2n), gercek noktalar (2n), merkez (2), E. Dort kosede 19.</summary>
        public double[] ToValues() =>
            Measured.SelectMany(p => new[] { p.X, p.Y }).Concat(True.SelectMany(p => new[] { p.X, p.Y }))
                .Concat(new[] { Center.X, Center.Y, E }).ToArray();

        public static TableCorrection FromValues(double[] v)
        {
            if (v == null || v.Length < 19 || (v.Length - 3) % 4 != 0 || v.Any(x => double.IsNaN(x) || double.IsInfinity(x)))
                throw new FormatException("Tabla duzeltmesi 4n+3 (n >= 4) sonlu deger olmali.");
            int n = (v.Length - 3) / 4;
            Point2[] m = Enumerable.Range(0, n).Select(i => new Point2(v[2 * i], v[2 * i + 1])).ToArray();
            Point2[] t = Enumerable.Range(0, n).Select(i => new Point2(v[2 * n + 2 * i], v[2 * n + 2 * i + 1])).ToArray();
            return new TableCorrection(m, t, new Point2(v[4 * n], v[4 * n + 1]), v[4 * n + 2]);
        }
    }
}
