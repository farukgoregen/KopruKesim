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
            // Ilk dort nokta tabla koseleri (sol ust, sag ust, sol alt, sag alt); 6 paspasli kalibrasyonda ardindan orta
            // paspaslarin tabla kenarindaki dis koseleri gelir. Dortten fazla noktada homografi en kucuk karelerle.
            if (measured == null || measured.Length < 4 || trueCorners == null || trueCorners.Length != measured.Length)
                throw new ArgumentException("En az dort nokta gerekli.");
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
