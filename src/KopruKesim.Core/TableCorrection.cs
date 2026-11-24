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
        private readonly Homography _h, _hInv;

        public TableCorrection(Point2[] measured, Point2[] trueCorners, Point2 center, double e)
        {
            if (measured == null || measured.Length != 4 || trueCorners == null || trueCorners.Length != 4)
                throw new ArgumentException("Dort kose gerekli.");
            Measured = (Point2[])measured.Clone();
            True = (Point2[])trueCorners.Clone();
            Center = center; E = e;
            _h = Homography.FromPoints(Measured.Select(Radial).ToList(), True.ToList());
            _hInv = _h.Inverse();
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
        public Point2 Apply(Point2 model) => _h.Transform(Radial(model));

        /// <summary>Gercek tabla mm -> modelin kullandigi nokta (mm).</summary>
        public Point2 Invert(Point2 table) => RadialInverse(_hInv.Transform(table));

        /// <summary>Tablanin icinde en buyuk kayma (mm), bilgi icin.</summary>
        public double MaxShiftMm()
        {
            double max = 0;
            for (int i = 0; i <= 20; i++)
                for (int j = 0; j <= 20; j++)
                {
                    double u = i / 20.0, v = j / 20.0;
                    Point2 p = Measured[2] * ((1 - u) * (1 - v)) + Measured[3] * (u * (1 - v)) + Measured[1] * (u * v) + Measured[0] * ((1 - u) * v);
                    max = Math.Max(max, Point2.Distance(Apply(p), p));
                }
            return max;
        }

        /// <summary>Tabla kenarindan bir nokta (model mm); Mat: paspasin dis kenarindan (ahsap kenarindan birkac mm kayik olabilir).</summary>
        public struct EdgeSample
        {
            public Point2 P; public bool Mat;
            public EdgeSample(Point2 p, bool mat) { P = p; Mat = mat; }
        }

        public sealed class FitReport
        {
            public double E;
            /// <summary>Kenarlarin duzlukten sapmasi (mm, rms): alt, sag, ust, sol; once / sonra.</summary>
            public double[] StraightBefore = new double[4], StraightAfter = new double[4];
            /// <summary>Paspas kenarlarinin olculen uzunluklari (paspas basina 4 kenar) ve beklenen; once / sonra.</summary>
            public double[][] MatBefore, MatAfter, MatExpected;
        }

        /// <summary>
        /// E'yi bulur: radyal duzeltme + kose homografisinden sonra tabla kenarlari duz, paspaslar olcusunde olmali.
        /// mats: her paspas icin (modelin olctugu 4 kose, gercek 4 kose) ayni sirada.
        /// </summary>
        public static TableCorrection Fit(Point2[] measured, Point2[] truth, Point2 center, System.Collections.Generic.IList<EdgeSample>[] edges,
            System.Collections.Generic.IList<Tuple<Point2[], Point2[]>> mats, out FitReport report)
        {
            var rep = new FitReport();
            Func<double, double> cost = e =>
            {
                var tc = new TableCorrection(measured, truth, center, e);
                double s = 0; int n = 0;
                for (int k = 0; k < 4; k++) { double r = tc.Straightness(k, edges[k]); if (!double.IsNaN(r)) { s += r * r; n++; } }
                double straight = n > 0 ? s / n : 0;
                double m = 0; int nm = 0;
                if (mats != null)
                    foreach (var mt in mats)
                        for (int i = 0; i < 4; i++)
                        {
                            double got = Point2.Distance(tc.Apply(mt.Item1[i]), tc.Apply(mt.Item1[(i + 1) % 4]));
                            double want = Point2.Distance(mt.Item2[i], mt.Item2[(i + 1) % 4]);
                            m += (got - want) * (got - want); nm++;
                        }
                return straight + (nm > 0 ? m / nm : 0);
            };
            // Kaba tarama, sonra altin oran
            double best = 0, bestC = double.MaxValue;
            for (int i = -60; i <= 60; i++) { double e = i * 0.5e-9, c = cost(e); if (c < bestC) { bestC = c; best = e; } }
            double lo = best - 0.5e-9, hi = best + 0.5e-9, g = (Math.Sqrt(5) - 1) / 2;
            double x1 = hi - g * (hi - lo), x2 = lo + g * (hi - lo), f1 = cost(x1), f2 = cost(x2);
            for (int it = 0; it < 40; it++)
            {
                if (f1 < f2) { hi = x2; x2 = x1; f2 = f1; x1 = hi - g * (hi - lo); f1 = cost(x1); }
                else { lo = x1; x1 = x2; f1 = f2; x2 = lo + g * (hi - lo); f2 = cost(x2); }
            }
            double eBest = (lo + hi) / 2;
            var none = new TableCorrection(measured, truth, center, 0);
            var res = new TableCorrection(measured, truth, center, eBest);
            rep.E = eBest;
            for (int k = 0; k < 4; k++) { rep.StraightBefore[k] = none.Straightness(k, edges[k]); rep.StraightAfter[k] = res.Straightness(k, edges[k]); }
            if (mats != null)
            {
                rep.MatBefore = mats.Select(mt => Enumerable.Range(0, 4).Select(i => Point2.Distance(mt.Item1[i], mt.Item1[(i + 1) % 4])).ToArray()).ToArray();
                rep.MatAfter = mats.Select(mt => Enumerable.Range(0, 4).Select(i => Point2.Distance(res.Apply(mt.Item1[i]), res.Apply(mt.Item1[(i + 1) % 4]))).ToArray()).ToArray();
                rep.MatExpected = mats.Select(mt => Enumerable.Range(0, 4).Select(i => Point2.Distance(mt.Item2[i], mt.Item2[(i + 1) % 4])).ToArray()).ToArray();
            }
            report = rep;
            return res;
        }

        /// <summary>
        /// Kenar noktalarinin radyal duzeltmeden sonra duz cizgiden sapmasi (mm, rms; uc noktalar 15 mm'de kesilir). Kirise
        /// gore sapma s = a + b t + m [paspas] ile oturtulur (kayma, egim ve paspas tasmasi duzluk sayilmaz). Nokta azsa NaN.
        /// </summary>
        public double Straightness(int edge, System.Collections.Generic.IList<EdgeSample> pts)
        {
            if (pts == null || pts.Count(p => !p.Mat) < 10) return double.NaN;
            EdgeEnds(Measured, edge, out Point2 a0, out Point2 b0);
            Point2 a = Radial(a0), b = Radial(b0), d = b - a; double len = d.Length; d = d * (1 / len);
            var n = new Point2(-d.Y, d.X);
            bool haveMat = pts.Count(p => p.Mat) >= 4;
            int np = haveMat ? 3 : 2;
            var t = new double[pts.Count]; var s = new double[pts.Count];
            for (int i = 0; i < pts.Count; i++) { Point2 q = Radial(pts[i].P) - a; t[i] = Point2.Dot(q, d) / len; s[i] = Point2.Dot(q, n); }
            var w = Enumerable.Repeat(1.0, pts.Count).ToArray();
            double[] x = new double[np];
            for (int it = 0; it < 5; it++)
            {
                var A = new double[np, np]; var B = new double[np];
                for (int i = 0; i < pts.Count; i++)
                {
                    double[] f = haveMat ? new[] { 1, t[i], pts[i].Mat ? 1 : 0 } : new[] { 1, t[i] };
                    for (int r = 0; r < np; r++) { B[r] += w[i] * f[r] * s[i]; for (int c = 0; c < np; c++) A[r, c] += w[i] * f[r] * f[c]; }
                }
                x = SolveSmall(A, B) ?? x;
                for (int i = 0; i < pts.Count; i++)
                {
                    double res = s[i] - x[0] - x[1] * t[i] - (haveMat && pts[i].Mat ? x[2] : 0);
                    w[i] = Math.Abs(res) <= 3 ? 1 : 3 / Math.Abs(res);
                }
            }
            double sq = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                double res = s[i] - x[0] - x[1] * t[i] - (haveMat && pts[i].Mat ? x[2] : 0);
                sq += Math.Min(res * res, 225);
            }
            return Math.Sqrt(sq / pts.Count);
        }

        static double[] SolveSmall(double[,] A, double[] b)
        {
            int n = b.Length; var m = (double[,])A.Clone(); var r = (double[])b.Clone();
            for (int c = 0; c < n; c++)
            {
                int p = c;
                for (int i = c + 1; i < n; i++) if (Math.Abs(m[i, c]) > Math.Abs(m[p, c])) p = i;
                if (Math.Abs(m[p, c]) < 1e-12) return null;
                if (p != c) { for (int k = 0; k < n; k++) { double tmp = m[c, k]; m[c, k] = m[p, k]; m[p, k] = tmp; } double tr = r[c]; r[c] = r[p]; r[p] = tr; }
                for (int i = c + 1; i < n; i++) { double f = m[i, c] / m[c, c]; for (int k = c; k < n; k++) m[i, k] -= f * m[c, k]; r[i] -= f * r[c]; }
            }
            var x = new double[n];
            for (int i = n - 1; i >= 0; i--) { double s = r[i]; for (int k = i + 1; k < n; k++) s -= m[i, k] * x[k]; x[i] = s / m[i, i]; }
            return x;
        }

        /// <summary>Kayit icin 19 deger: olculen koseler (8), gercek koseler (8), merkez (2), E.</summary>
        public double[] ToValues() =>
            Measured.SelectMany(p => new[] { p.X, p.Y }).Concat(True.SelectMany(p => new[] { p.X, p.Y }))
                .Concat(new[] { Center.X, Center.Y, E }).ToArray();

        public static TableCorrection FromValues(double[] v)
        {
            if (v == null || v.Length != 19 || v.Any(x => double.IsNaN(x) || double.IsInfinity(x)))
                throw new FormatException("Tabla duzeltmesi 19 sonlu deger olmali.");
            Point2[] m = Enumerable.Range(0, 4).Select(i => new Point2(v[2 * i], v[2 * i + 1])).ToArray();
            Point2[] t = Enumerable.Range(0, 4).Select(i => new Point2(v[8 + 2 * i], v[8 + 2 * i + 1])).ToArray();
            return new TableCorrection(m, t, new Point2(v[16], v[17]), v[18]);
        }
    }
}
