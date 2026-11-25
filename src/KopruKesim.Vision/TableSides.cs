using System;
using KopruKesim.Core;

namespace KopruKesim.Vision
{
    /// <summary>
    /// Tablanin seritle olculen dort kenari (L kose parcalarinin dis koseleri arasi, mm) ve sol alt -> sag ust kosegen
    /// (bilinmiyorsa NaN: sol alt kose dik kabul edilir).
    /// </summary>
    public sealed class TableSides
    {
        public double Bottom, Top, Left, Right;
        public double Diagonal = double.NaN;

        /// <summary>Tabla koseleri (mm): sol ust, sag ust, sol alt, sag alt. Orijin sol alt, X sag alta dogru.</summary>
        public Point2[] Corners(out string error)
        {
            error = null;
            var bl = new Point2(0, 0);
            var br = new Point2(Bottom, 0);
            Point2 tl, tr;
            if (!double.IsNaN(Diagonal) && Diagonal > 0)
            {
                // Sag ust: sol alttan kosegen, sag alttan sag kenar uzakliginda (yukarida)
                if (!Circles(bl, Diagonal, br, Right, p => p.Y > 0, out tr)) { error = "Köşegen, alt ve sağ kenarla bir üçgen oluşturmuyor."; return null; }
                // Sol ust: sol alttan sol kenar, sag ustten ust kenar uzakliginda (kosegenin solunda)
                if (!Circles(bl, Left, tr, Top, p => Cross(tr - bl, p - bl) > 0, out tl)) { error = "Köşegen, sol ve üst kenarla bir üçgen oluşturmuyor."; return null; }
            }
            else
            {
                tl = new Point2(0, Left);
                if (!Circles(tl, Top, br, Right, p => Cross(br - tl, p - tl) > 0, out tr)) { error = "Üst ve sağ kenar uzunlukları tabla köşesini kapatmıyor."; return null; }
            }
            return new[] { tl, tr, bl, br };
        }

        static double Cross(Point2 a, Point2 b) => a.X * b.Y - a.Y * b.X;

        /// <summary>a merkezli ra ve b merkezli rb yaricapli cemberlerin kesisimlerinden kosulu saglayani.</summary>
        static bool Circles(Point2 a, double ra, Point2 b, double rb, Func<Point2, bool> pick, out Point2 p)
        {
            p = default(Point2);
            Point2 d = b - a;
            double dist = d.Length;
            if (dist < 1e-9) return false;
            double x = (ra * ra - rb * rb + dist * dist) / (2 * dist);
            double h2 = ra * ra - x * x;
            if (h2 < 0) return false;
            double h = Math.Sqrt(h2);
            Point2 u = d * (1 / dist), n = new Point2(-u.Y, u.X);
            Point2 p1 = a + u * x + n * h, p2 = a + u * x - n * h;
            if (pick(p1)) { p = p1; return true; }
            if (pick(p2)) { p = p2; return true; }
            return false;
        }
    }
}
