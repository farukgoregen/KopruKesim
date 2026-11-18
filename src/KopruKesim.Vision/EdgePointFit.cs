using System;
using System.Collections.Generic;
using System.Linq;
using KopruKesim.Core;
using OpenCvSharp;

namespace KopruKesim.Vision
{
    /// <summary>Bir tabla kenarina uydurulan dogru ve o kenardaki noktalarin durumu.</summary>
    public sealed class FittedEdge
    {
        /// <summary>Kenara oturtulmus noktalar (piksel).</summary>
        public Point2[] Points;
        /// <summary>Her noktanin uydurulan dogruya uzakligi (piksel, isaretli).</summary>
        public double[] Residuals;
        /// <summary>Nokta dogruya uyuyor (false: aykiri, dogru hesabina katilmadi).</summary>
        public bool[] Inlier;
        /// <summary>Nokta kenar uzerinde belirgin bir gecise oturtulabildi.</summary>
        public bool[] Snapped;
        /// <summary>Uyan noktalarin dogruya ortalama ve en buyuk uzakligi (piksel).</summary>
        public double Rms, Max;
        /// <summary>Uyan noktalar duz bir cizgi uzerinde (en buyuk uzaklik esigin altinda).</summary>
        public bool Straight;
        /// <summary>Dogru: a*x + b*y + c = 0, (a,b) birim normal.</summary>
        public double A, B, C;
    }

    public sealed class EdgeFitResult
    {
        /// <summary>Kenar dogrularinin kesisimi: 0 sol ust, 1 sag ust, 2 sag alt, 3 sol alt. Hata varsa null.</summary>
        public Point2[] Corners;
        /// <summary>0 ust (kose 0-1), 1 sag (1-2), 2 alt (2-3), 3 sol (3-0).</summary>
        public FittedEdge[] Edges;
        public string Error;
    }

    /// <summary>
    /// Cok noktali tabla tespiti. Her kenar icin birden cok nokta verilir (elle ya da tahmini dortgenden).
    /// Her nokta, kenara dik yonde kucuk bir aralikta en guclu parlaklik gecisine piksel alti hassasiyetle
    /// oturtulur; kenarin noktalarina en kucuk kareler dogrusu uydurulur, dogrudan belirgin sapan nokta
    /// aykiri sayilip cikarilir ve gosterilir. Koseler komsu kenar dogrularinin kesisimidir.
    /// Dort noktadan farkli olarak hata gorulebilir: noktalar duz bir cizgi uzerinde degilse kenar "egri".
    /// </summary>
    public static class EdgePointFit
    {
        /// <summary>Bu kadar pikselden fazla sapan nokta aykiri sayilir (oturtma yanlis gecise atlamis).</summary>
        public const double OutlierPx = 4.0;
        /// <summary>Uyan noktalarin en buyuk sapmasi bunun ustundeyse kenar duz sayilmaz (lens bukulmesi, yanlis nokta).</summary>
        public const double StraightPx = 2.5;

        /// <summary>Dortgenin her kenarina esit aralikla perEdge nokta (koselerin hemen yanina konmaz).</summary>
        public static List<Point2>[] Sample(Point2[] corners, int perEdge)
        {
            var edges = new List<Point2>[4];
            for (int e = 0; e < 4; e++)
            {
                Point2 p = corners[e], q = corners[(e + 1) % 4];
                edges[e] = new List<Point2>();
                for (int i = 0; i < perEdge; i++)
                {
                    double t = 0.08 + 0.84 * i / Math.Max(1, perEdge - 1);
                    edges[e].Add(p + (q - p) * t);
                }
            }
            return edges;
        }

        /// <param name="snapPx">Noktanin kenara dik yonde aranacagi yaricap (piksel); 0 ise oturtma yapilmaz.</param>
        public static EdgeFitResult Fit(Mat frame, IList<List<Point2>> edgePoints, double snapPx)
        {
            var result = new EdgeFitResult();
            if (edgePoints == null || edgePoints.Count != 4 || edgePoints.Any(l => l == null || l.Count < 2))
            {
                result.Error = "Her kenarda en az iki nokta olmali.";
                return result;
            }
            int w = frame.Cols, h = frame.Rows;
            float[] gx = null, gy = null;
            if (snapPx > 0)
            {
                using (var gray = new Mat())
                using (var gxm = new Mat())
                using (var gym = new Mat())
                {
                    if (frame.Channels() == 1) frame.CopyTo(gray); else Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
                    Cv2.GaussianBlur(gray, gray, new Size(3, 3), 0);
                    Cv2.Sobel(gray, gxm, MatType.CV_32F, 1, 0);
                    Cv2.Sobel(gray, gym, MatType.CV_32F, 0, 1);
                    gxm.GetArray(out gx);
                    gym.GetArray(out gy);
                }
            }
            result.Edges = new FittedEdge[4];
            for (int e = 0; e < 4; e++)
            {
                List<Point2> pts = edgePoints[e];
                // Kenar yonu: noktalarin ilk dogrusu (oturtmanin hangi yone arayacagini belirler)
                double a0, b0, c0;
                FitLine(pts, Enumerable.Repeat(true, pts.Count).ToArray(), out a0, out b0, out c0);
                var n = new Point2(a0, b0);
                var fe = new FittedEdge { Points = new Point2[pts.Count], Snapped = new bool[pts.Count] };
                for (int i = 0; i < pts.Count; i++)
                {
                    fe.Points[i] = pts[i];
                    if (snapPx > 0)
                    {
                        Point2 s;
                        if (SnapToEdge(gx, gy, w, h, pts[i], n, snapPx, out s)) { fe.Points[i] = s; fe.Snapped[i] = true; }
                    }
                }
                FitRobust(fe);
                result.Edges[e] = fe;
            }
            var corners = new Point2[4];
            for (int i = 0; i < 4; i++)
            {
                // Kose i: kenar (i-1) ile kenar i'nin kesisimi (kose 0 = sol kenar x ust kenar)
                FittedEdge prev = result.Edges[(i + 3) % 4], cur = result.Edges[i];
                Point2? x = Intersect(prev, cur);
                if (x == null) { result.Error = "Komsu iki kenar paralel; koseler bulunamadi."; return result; }
                corners[i] = x.Value;
            }
            result.Corners = corners;
            return result;
        }

        /// <summary>Normal yonunde +-r aralikta en guclu gecisi bulur; parabolle piksel alti. Gecis zayifsa false.</summary>
        private static bool SnapToEdge(float[] gx, float[] gy, int w, int h, Point2 p, Point2 n, double r, out Point2 snapped)
        {
            snapped = p;
            const double step = 0.5;
            int count = (int)Math.Ceiling(r / step);
            double best = -1;
            int bestK = 0;
            var vals = new double[2 * count + 1];
            for (int k = -count; k <= count; k++)
            {
                double x = p.X + n.X * k * step, y = p.Y + n.Y * k * step;
                double v = Math.Abs(Bilinear(gx, w, h, x, y) * n.X + Bilinear(gy, w, h, x, y) * n.Y);
                vals[k + count] = v;
                if (v > best) { best = v; bestK = k; }
            }
            // Zayif gecis (duz yuzey): oturtma guvenilmez, nokta yerinde kalir
            if (best < 30) return false;
            double off = bestK * step;
            int idx = bestK + count;
            if (idx > 0 && idx < vals.Length - 1)
            {
                double l = vals[idx - 1], c = vals[idx], rr = vals[idx + 1];
                double den = l - 2 * c + rr;
                if (Math.Abs(den) > 1e-9) off += Math.Max(-0.5, Math.Min(0.5, 0.5 * (l - rr) / den)) * step;
            }
            snapped = new Point2(p.X + n.X * off, p.Y + n.Y * off);
            return true;
        }

        private static double Bilinear(float[] g, int w, int h, double x, double y)
        {
            if (x < 0 || y < 0 || x > w - 2 || y > h - 2) return 0;
            int x0 = (int)x, y0 = (int)y;
            double fx = x - x0, fy = y - y0;
            int i = y0 * w + x0;
            return g[i] * (1 - fx) * (1 - fy) + g[i + 1] * fx * (1 - fy) + g[i + w] * (1 - fx) * fy + g[i + w + 1] * fx * fy;
        }

        /// <summary>Toplam en kucuk kareler dogrusu; aykiri nokta tek tek cikarilir (en az iki nokta kalir).</summary>
        private static void FitRobust(FittedEdge fe)
        {
            int n = fe.Points.Length;
            fe.Inlier = Enumerable.Repeat(true, n).ToArray();
            fe.Residuals = new double[n];
            while (true)
            {
                FitLine(fe.Points, fe.Inlier, out fe.A, out fe.B, out fe.C);
                int worst = -1;
                double worstAbs = 0;
                for (int i = 0; i < n; i++)
                {
                    fe.Residuals[i] = fe.A * fe.Points[i].X + fe.B * fe.Points[i].Y + fe.C;
                    if (fe.Inlier[i] && Math.Abs(fe.Residuals[i]) > worstAbs) { worstAbs = Math.Abs(fe.Residuals[i]); worst = i; }
                }
                if (worstAbs <= OutlierPx || fe.Inlier.Count(b => b) <= 2) break;
                fe.Inlier[worst] = false;
            }
            var ins = Enumerable.Range(0, n).Where(i => fe.Inlier[i]).Select(i => Math.Abs(fe.Residuals[i])).ToList();
            fe.Rms = Math.Sqrt(ins.Sum(v => v * v) / Math.Max(1, ins.Count));
            fe.Max = ins.Count == 0 ? 0 : ins.Max();
            fe.Straight = fe.Max <= StraightPx;
        }

        private static void FitLine(IList<Point2> pts, bool[] use, out double a, out double b, out double c)
        {
            double mx = 0, my = 0;
            int n = 0;
            for (int i = 0; i < pts.Count; i++) if (use[i]) { mx += pts[i].X; my += pts[i].Y; n++; }
            mx /= n; my /= n;
            double sxx = 0, syy = 0, sxy = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                if (!use[i]) continue;
                double dx = pts[i].X - mx, dy = pts[i].Y - my;
                sxx += dx * dx; syy += dy * dy; sxy += dx * dy;
            }
            // En buyuk ozdeger yonu dogrultu, dik olani normal
            double ang = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
            double dxu = Math.Cos(ang), dyu = Math.Sin(ang);
            a = -dyu; b = dxu;
            c = -(a * mx + b * my);
        }

        private static Point2? Intersect(FittedEdge p, FittedEdge q)
        {
            double det = p.A * q.B - q.A * p.B;
            if (Math.Abs(det) < 1e-9) return null;
            double x = (p.B * q.C - q.B * p.C) / det;
            double y = (q.A * p.C - p.A * q.C) / det;
            return new Point2(x, y);
        }
    }
}
