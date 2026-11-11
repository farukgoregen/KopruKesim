using System;
using System.Collections.Generic;

namespace KopruKesim.Core
{
    /// <summary>Kose listesi ile verilen cokgenler icin temel hesaplar.</summary>
    public static class PolygonMath
    {
        /// <summary>Isaretli alan: koseler saat yonunun tersine siraliysa pozitif.</summary>
        public static double SignedArea(IList<Point2> p)
        {
            double s = 0;
            for (int i = 0; i < p.Count; i++) s += Point2.Cross(p[i], p[(i + 1) % p.Count]);
            return s / 2;
        }

        public static double Perimeter(IList<Point2> p)
        {
            double s = 0;
            for (int i = 0; i < p.Count; i++) s += Point2.Distance(p[i], p[(i + 1) % p.Count]);
            return s;
        }

        /// <summary>Agirlik merkezi.</summary>
        public static Point2 Centroid(IList<Point2> p)
        {
            double a = 0, cx = 0, cy = 0;
            for (int i = 0; i < p.Count; i++)
            {
                Point2 u = p[i], v = p[(i + 1) % p.Count];
                double c = Point2.Cross(u, v);
                a += c; cx += (u.X + v.X) * c; cy += (u.Y + v.Y) * c;
            }
            if (Math.Abs(a) < 1e-12) return p[0];
            return new Point2(cx / (3 * a), cy / (3 * a));
        }

        /// <summary>Nokta cokgenin icinde mi (isin atma yontemi). Kesim hatti kontrolu icin.</summary>
        public static bool Contains(IList<Point2> p, Point2 q)
        {
            bool inside = false;
            for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
            {
                if ((p[i].Y > q.Y) != (p[j].Y > q.Y) &&
                    q.X < (p[j].X - p[i].X) * (q.Y - p[i].Y) / (p[j].Y - p[i].Y) + p[i].X)
                    inside = !inside;
            }
            return inside;
        }

        public static bool IsConvex(IList<Point2> p)
        {
            int sign = 0;
            for (int i = 0; i < p.Count; i++)
            {
                double c = Point2.Cross(p[(i + 1) % p.Count] - p[i], p[(i + 2) % p.Count] - p[(i + 1) % p.Count]);
                if (Math.Abs(c) < 1e-9) continue;
                int s = c > 0 ? 1 : -1;
                if (sign == 0) sign = s; else if (s != sign) return false;
            }
            return true;
        }

        /// <summary>Her kosedeki ic aci (derece). Koseler saat yonunun tersine sirali olmali.</summary>
        public static double[] InteriorAngles(IList<Point2> p)
        {
            int n = p.Count;
            var r = new double[n];
            for (int i = 0; i < n; i++)
            {
                Point2 a = p[(i + n - 1) % n] - p[i], b = p[(i + 1) % n] - p[i];
                double ang = Math.Atan2(Point2.Cross(b, a), Point2.Dot(b, a)) * 180 / Math.PI;
                r[i] = ang < 0 ? ang + 360 : ang;
            }
            return r;
        }
    }
}
