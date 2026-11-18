using System;
using System.Collections.Generic;
using System.Linq;
using KopruKesim.Core;
using OpenCvSharp;

namespace KopruKesim.Vision
{
    /// <summary>
    /// Tablayi rengi yerine uzun duz kenarlarindan bulur (tabla zeminle ayni renkteyse, ornek: gri
    /// ahsap masa / beton zemin). Adimlar: LSD ile duz parcalar -> ayni dogru uzerindekiler birlestirilir
    /// -> iki "yatay" + iki "diger" dogrudan dortgen adaylari -> kenar destegi ve alanla on eleme ->
    /// en iyi adaylarda her kenarin ici tabla rengine benzemeli, disi benzememeli.
    /// Yalnizca renk tabanli yontem basarisiz oldugunda yedek olarak kullanilir.
    /// </summary>
    public static class LineTableFinder
    {
        private sealed class Cluster
        {
            public double Theta, Rho, Length;
            public List<Point2f> Points = new List<Point2f>();
            public double[] Line;   // ax + by + c = 0
        }

        public static Point2[] Find(Mat frame)
        {
            int w = frame.Cols, h = frame.Rows;
            double L = Math.Max(w, h);
            float[] gx, gy;
            Vec3f[] lab;
            Vec4f[] segs;
            using (var gray = new Mat())
            using (var gxm = new Mat())
            using (var gym = new Mat())
            using (var blur = new Mat())
            using (var labm = new Mat())
            {
                Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
                Cv2.GaussianBlur(gray, gray, new Size(5, 5), 0);
                Cv2.Sobel(gray, gxm, MatType.CV_32F, 1, 0);
                Cv2.Sobel(gray, gym, MatType.CV_32F, 0, 1);
                gxm.GetArray(out gx);
                gym.GetArray(out gy);
                Cv2.GaussianBlur(frame, blur, new Size(9, 9), 0);
                blur.ConvertTo(blur, MatType.CV_32FC3, 1.0 / 255);
                Cv2.CvtColor(blur, labm, ColorConversionCodes.BGR2Lab);
                labm.GetArray(out lab);
                using (LineSegmentDetector lsd = LineSegmentDetector.Create(LineSegmentDetectorModes.RefineStd))
                {
                    double[] a, b, c;
                    lsd.Detect(gray, out segs, out a, out b, out c);
                }
            }

            // Ayni dogru uzerindeki parcalari birlestir
            var clusters = new List<Cluster>();
            foreach (Vec4f s in segs)
            {
                double len = Math.Sqrt(Sq(s.Item2 - s.Item0) + Sq(s.Item3 - s.Item1));
                if (len < 0.025 * L) continue;
                double th = Mod(Math.Atan2(s.Item3 - s.Item1, s.Item2 - s.Item0), Math.PI);
                double rho = s.Item0 * -Math.Sin(th) + s.Item1 * Math.Cos(th);
                Cluster hit = clusters.FirstOrDefault(cl =>
                    Math.Abs(Mod(th - cl.Theta + Math.PI / 2, Math.PI) - Math.PI / 2) < 2.5 * Math.PI / 180 && Math.Abs(rho - cl.Rho) < 0.012 * L);
                if (hit == null) { hit = new Cluster { Theta = th, Rho = rho }; clusters.Add(hit); }
                hit.Length += len;
                hit.Points.Add(new Point2f(s.Item0, s.Item1));
                hit.Points.Add(new Point2f(s.Item2, s.Item3));
            }
            clusters = clusters.OrderByDescending(c => c.Length).Take(40).ToList();
            foreach (Cluster c in clusters)
            {
                Line2D l = Cv2.FitLine(c.Points, DistanceTypes.L2, 0, 0.01, 0.01);
                c.Line = new[] { l.Vy, -l.Vx, l.Vx * l.Y1 - l.Vy * l.X1 };
            }
            double sin30 = Math.Sin(30 * Math.PI / 180);
            var hor = clusters.Where(c => Math.Abs(Math.Sin(c.Theta)) < sin30).ToList();
            var oth = clusters.Where(c => Math.Abs(Math.Sin(c.Theta)) >= sin30).ToList();

            Func<Point2, Point2, double> support = (p, q) =>
            {
                Point2 d = q - p; double len = d.Length; if (len < 2) return 0;
                d = d * (1 / len); var n = new Point2(-d.Y, d.X); int ok = 0; const int k = 80;
                for (int i = 0; i < k; i++)
                {
                    Point2 c = p + d * (len * (0.05 + 0.9 * i / (k - 1))); double best = 0;
                    for (int o = -3; o <= 3; o++)
                    {
                        int x = (int)Math.Round(c.X + n.X * o), y = (int)Math.Round(c.Y + n.Y * o);
                        if (x < 0 || y < 0 || x >= w || y >= h) continue;
                        int idx = y * w + x; best = Math.Max(best, Math.Abs(gx[idx] * n.X + gy[idx] * n.Y));
                    }
                    if (best > 25) ok++;
                }
                return (double)ok / k;
            };

            // On eleme: kenar destegi x alan
            var cands = new List<Tuple<double, double, Point2[]>>();
            var center = new Point2(w / 2.0, h / 2.0);
            for (int i = 0; i < hor.Count; i++)
                for (int j = i + 1; j < hor.Count; j++)
                    for (int k = 0; k < oth.Count; k++)
                        for (int m = k + 1; m < oth.Count; m++)
                        {
                            var pts = new[] { X(hor[i].Line, oth[k].Line), X(hor[i].Line, oth[m].Line), X(hor[j].Line, oth[m].Line), X(hor[j].Line, oth[k].Line) };
                            if (pts.Any(p => p == null)) continue;
                            Point2[] q = TableFinder.OrderByImagePosition(pts.Select(p => p.Value).ToArray());
                            if (q.Any(p => p.X < -0.05 * w || p.X > 1.05 * w || p.Y < -0.05 * h || p.Y > 1.05 * h)) continue;
                            if (!TableFinder.IsValidQuad(q, w, h)) continue;
                            double area = Math.Abs(PolygonMath.SignedArea(q));
                            if (area < 0.1 * w * h || !PolygonMath.Contains(q, center)) continue;
                            double sup = 0;
                            for (int e = 0; e < 4; e++) sup += support(q[e], q[(e + 1) % 4]);
                            sup /= 4;
                            cands.Add(Tuple.Create(sup * Math.Sqrt(area), sup, q));
                        }
            if (cands.Count == 0) return null;

            // Son secim: kenarin ici tabla rengine benzemeli, disi benzememeli
            Point2[] bestQ = null; double bestScore = 0;
            foreach (var c in cands.OrderByDescending(t => t.Item1).Take(30))
            {
                double side = SideScore(c.Item3, lab, w, h, L);
                double score = c.Item2 * side;
                if (score > bestScore) { bestScore = score; bestQ = c.Item3; }
            }
            return bestQ;
        }

        private static double SideScore(Point2[] q, Vec3f[] lab, int w, int h, double L)
        {
            var m = new Point2(q.Average(p => p.X), q.Average(p => p.Y));
            // Tabla icinin tipik rengi: ortaya dogru yariya kucultulmus dortgenin ortancasi (seyrek orneklem)
            Point2[] inner = q.Select(p => m + (p - m) * 0.5).ToArray();
            var ls = new List<double>(); var as_ = new List<double>(); var bs = new List<double>();
            double minX = inner.Min(p => p.X), maxX = inner.Max(p => p.X), minY = inner.Min(p => p.Y), maxY = inner.Max(p => p.Y);
            int step = Math.Max(2, (int)(L / 300));
            for (int y = (int)minY; y < maxY; y += step)
                for (int x = (int)minX; x < maxX; x += step)
                {
                    if (x < 0 || y < 0 || x >= w || y >= h || !PolygonMath.Contains(inner, new Point2(x, y))) continue;
                    Vec3f v = lab[y * w + x]; ls.Add(v.Item0); as_.Add(v.Item1); bs.Add(v.Item2);
                }
            if (ls.Count < 10) return 0;
            double rl = Median(ls), ra = Median(as_), rb = Median(bs), off = 0.012 * L, tot = 0;
            for (int i = 0; i < 4; i++)
            {
                Point2 p = q[i], r = q[(i + 1) % 4], d = r - p; d = d * (1 / d.Length);
                var n = new Point2(-d.Y, d.X);
                if (Point2.Dot(m - (p + r) * 0.5, n) < 0) n = n * -1;   // n iceri
                double[] a = Strip(p, r, n, off, lab, w, h), b = Strip(p, r, n * -1, off, lab, w, h);
                if (a == null || b == null) return 0;
                double din = Math.Sqrt(Sq(0.5 * (a[0] - rl)) + Sq(a[1] - ra) + Sq(a[2] - rb));
                double dout = Math.Sqrt(Sq(0.5 * (b[0] - rl)) + Sq(b[1] - ra) + Sq(b[2] - rb));
                tot += Math.Max(0, dout - din);
            }
            return tot / 4;
        }

        private static double[] Strip(Point2 p, Point2 r, Point2 n, double off, Vec3f[] lab, int w, int h)
        {
            var l = new List<double>(); var a = new List<double>(); var b = new List<double>();
            for (int i = 0; i < 40; i++)
            {
                Point2 c = p + (r - p) * (0.15 + 0.7 * i / 39.0);
                for (int j = 0; j < 4; j++)
                {
                    double o = off * (0.5 + 1.5 * j / 3.0);
                    int x = (int)(c.X + n.X * o), y = (int)(c.Y + n.Y * o);
                    if (x < 0 || y < 0 || x >= w || y >= h) continue;
                    Vec3f v = lab[y * w + x]; l.Add(v.Item0); a.Add(v.Item1); b.Add(v.Item2);
                }
            }
            return l.Count == 0 ? null : new[] { Median(l), Median(a), Median(b) };
        }

        private static Point2? X(double[] a, double[] b)
        {
            double x = a[1] * b[2] - a[2] * b[1], y = a[2] * b[0] - a[0] * b[2], z = a[0] * b[1] - a[1] * b[0];
            if (Math.Abs(z) < 1e-12) return null;
            return new Point2(x / z, y / z);
        }
        private static double Sq(double v) { return v * v; }
        private static double Mod(double a, double m) { double r = a % m; return r < 0 ? r + m : r; }
        private static double Median(List<double> v) { v.Sort(); return v[v.Count / 2]; }
    }
}
