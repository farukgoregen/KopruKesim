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
