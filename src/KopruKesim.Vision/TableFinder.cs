using System;
using System.Collections.Generic;
using KopruKesim.Core;
using OpenCvSharp;

namespace KopruKesim.Vision
{
    /// <summary>
    /// BOS tabla goruntusunde tablanin dort kosesini bulur. Tabla ahsap renginden ayrilir;
    /// en buyuk ahsap bolgenin dort kenarina dogru uydurulur, koseler bu dogrularin kesisimidir.
    /// Uzerinde parca varken kullanilmamalidir (kenarlar ortulur).
    /// </summary>
    public static class TableFinder
    {
        /// <summary>
        /// Koseleri goruntudeki yerine gore sirali dondurur: sol ust, sag ust, sag alt, sol alt.
        /// Bulunamazsa null.
        /// </summary>
        public static Point2[] FindCorners(Mat emptyTableFrame)
        {
            using (var hsv = new Mat())
            using (var mask = new Mat())
            {
                Cv2.CvtColor(emptyTableFrame, hsv, ColorConversionCodes.BGR2HSV);
                // Ahsap: sari-turuncu ton, belirgin doygunluk, karanlik degil
                Cv2.InRange(hsv, new Scalar(5, 40, 80), new Scalar(32, 255, 255), mask);

                int k = Math.Max(3, (emptyTableFrame.Cols / 100) | 1);
                using (Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(k, k)))
                {
                    Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);   // tahta araliklari ve delikler
                    Cv2.MorphologyEx(mask, mask, MorphTypes.Open, kernel);    // kucuk lekeler
                }

                Point[][] contours;
                HierarchyIndex[] hierarchy;
                Cv2.FindContours(mask, out contours, out hierarchy, RetrievalModes.External, ContourApproximationModes.ApproxNone);
                Point[] best = null;
                double bestArea = 0;
                foreach (Point[] c in contours)
                {
                    double a = Cv2.ContourArea(c);
                    if (a > bestArea) { bestArea = a; best = c; }
                }
                if (best == null || bestArea < 0.05 * emptyTableFrame.Cols * emptyTableFrame.Rows) return null;

                // Kaba dortgen: disbukey zarfi 4 koseye inene kadar sadelestir
                Point[] hull = Cv2.ConvexHull(best);
                double perimeter = Cv2.ArcLength(hull, true);
                Point[] quad = null;
                for (double eps = 0.005; eps <= 0.08; eps += 0.005)
                {
                    Point[] approx = Cv2.ApproxPolyDP(hull, eps * perimeter, true);
                    if (approx.Length == 4) { quad = approx; break; }
                    if (approx.Length < 4) break;
                }
                if (quad == null) return null;

                // Ince ayar: her kenara yakin kontur noktalarina dogru uydur, komsu dogrulari kesistir
                var lines = new Line2D[4];
                double tol = Math.Max(4.0, 0.01 * perimeter);
                for (int i = 0; i < 4; i++)
                {
                    Point a = quad[i], b = quad[(i + 1) % 4];
                    double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
                    if (len < 1) return null;
                    var near = new List<Point2f>();
                    foreach (Point p in best)
                    {
                        double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / (len * len);
                        if (t < 0.08 || t > 0.92) continue;   // yuvarlak kose uclari disarida
                        double dist = Math.Abs((p.X - a.X) * dy - (p.Y - a.Y) * dx) / len;
                        if (dist <= tol) near.Add(new Point2f(p.X, p.Y));
                    }
                    if (near.Count < 10) return null;
                    lines[i] = Cv2.FitLine(near, DistanceTypes.Huber, 0, 0.01, 0.01);
                }

                var corners = new Point2[4];
                for (int i = 0; i < 4; i++)
                {
                    Point2? x = Intersect(lines[(i + 3) % 4], lines[i]);
                    if (x == null) return null;
                    corners[i] = x.Value;
                }
                return OrderByImagePosition(corners);
            }
        }

        /// <summary>Dort koseyi sol ust, sag ust, sag alt, sol alt sirasina dizer.</summary>
        public static Point2[] OrderByImagePosition(Point2[] c)
        {
            int tl = 0, br = 0, tr = 0, bl = 0;
            for (int i = 1; i < 4; i++)
            {
                if (c[i].X + c[i].Y < c[tl].X + c[tl].Y) tl = i;
                if (c[i].X + c[i].Y > c[br].X + c[br].Y) br = i;
                if (c[i].X - c[i].Y > c[tr].X - c[tr].Y) tr = i;
                if (c[i].X - c[i].Y < c[bl].X - c[bl].Y) bl = i;
            }
            return new[] { c[tl], c[tr], c[br], c[bl] };
        }

        private static Point2? Intersect(Line2D l1, Line2D l2)
        {
            double d = l1.Vx * l2.Vy - l1.Vy * l2.Vx;
            if (Math.Abs(d) < 1e-9) return null;
            double t = ((l2.X1 - l1.X1) * l2.Vy - (l2.Y1 - l1.Y1) * l2.Vx) / d;
            return new Point2(l1.X1 + t * l1.Vx, l1.Y1 + t * l1.Vy);
        }
    }
}
