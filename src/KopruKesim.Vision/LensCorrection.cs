using System;
using System.Collections.Generic;
using System.Linq;
using KopruKesim.Core;
using OpenCvSharp;

namespace KopruKesim.Vision
{
    /// <summary>
    /// Tek katsayili radyal lens bukulmesi. Bukulmemis (duz) goruntudeki u noktasi fotografta
    /// d = c + (u - c) * (1 + K * r^2) noktasinda gorunur; r = |u - c| / R0, c goruntu merkezi, R0 kosegenin yarisi.
    /// K &gt; 0: kenarlara dogru disa itme (yastik), K &lt; 0: ice cekme (fici).
    /// </summary>
    public sealed class LensModel
    {
        public double K;
        public double Cx, Cy, R0;

        public LensModel(int width, int height, double k)
        {
            K = k;
            Cx = (width - 1) / 2.0;
            Cy = (height - 1) / 2.0;
            R0 = Math.Sqrt(width * (double)width + height * (double)height) / 2;
        }

        /// <summary>Duz goruntudeki noktanin fotograftaki yeri.</summary>
        public Point2 Distort(Point2 u)
        {
            double dx = u.X - Cx, dy = u.Y - Cy;
            double s = 1 + K * (dx * dx + dy * dy) / (R0 * R0);
            return new Point2(Cx + dx * s, Cy + dy * s);
        }

        /// <summary>Fotograftaki noktanin duz goruntudeki yeri (sabit nokta yinelemesi).</summary>
        public Point2 Undistort(Point2 d)
        {
            Point2 u = d;
            for (int i = 0; i < 12; i++)
            {
                Point2 dd = Distort(u);
                u = new Point2(u.X + (d.X - dd.X), u.Y + (d.Y - dd.Y));
            }
            return u;
        }

        /// <summary>Fotografi duzlestirir (ayni boyut; her cikti pikseli icin fotograftaki kaynagi okunur).</summary>
        public Mat UndistortImage(Mat frame)
        {
            int w = frame.Cols, h = frame.Rows;
            var mx = new float[w * h];
            var my = new float[w * h];
            double inv = 1.0 / (R0 * R0);
            for (int y = 0; y < h; y++)
            {
                double dy = y - Cy;
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    double dx = x - Cx;
                    double s = 1 + K * (dx * dx + dy * dy) * inv;
                    mx[row + x] = (float)(Cx + dx * s);
                    my[row + x] = (float)(Cy + dy * s);
                }
            }
            var result = new Mat();
            using (var mapX = new Mat(h, w, MatType.CV_32FC1))
            using (var mapY = new Mat(h, w, MatType.CV_32FC1))
            {
                mapX.SetArray(mx);
                mapY.SetArray(my);
                Cv2.Remap(frame, result, mapX, mapY, InterpolationFlags.Linear, BorderTypes.Replicate);
            }
            return result;
        }
    }

    /// <summary>
    /// Cekul (plumb-line) yontemi: tablanin dort kenari gercekte duzdur. Kenar noktalarini en duz yapan
    /// bukulme katsayisi aranir (altin oran aramasi). Duzelme belirgin degilse (masa kenari zaten duz
    /// gorunuyor ya da noktalar yetersiz) null doner ve fotograf duzeltilmez.
    /// </summary>
    public static class LensCorrection
    {
        /// <summary>Aranan katsayi araligi; disi fiziksel olarak beklenmez (asiri sonuc yanlis noktaya isaret eder).</summary>
        public const double MaxAbsK = 0.25;
        /// <summary>Son aramada bulunan katsayi (kabul edilmese de; teshis icin).</summary>
        public static double LastK;

        /// <param name="before">Duzeltmeden once kenar noktalarinin dogrularina ortalama uzakligi (piksel).</param>
        /// <param name="after">Duzeltmeden sonra ayni olcu.</param>
        public static LensModel Estimate(int width, int height, IList<FittedEdge> edges, out double before, out double after)
        {
            before = after = double.NaN;
            // Yalniz guvenilir noktalar: kenara oturmus olanlar. Bukulme yuzunden aykiri sayilmis olabilecekleri
            // de katariz; her denemede kendi aykiri ayiklamasi yapilir.
            var groups = edges.Select(e => e.Points.Where((p, i) => e.Snapped[i]).ToList()).Where(g => g.Count >= 4).ToList();
            if (groups.Count < 3) return null;

            Func<double, double> cost = k =>
            {
                var model = new LensModel(width, height, k);
                return Rms(groups.Select(g => g.Select(model.Undistort).ToList()).ToList());
            };
            before = cost(0);
            // Altin oran aramasi
            double a = -MaxAbsK, b = MaxAbsK, gr = (Math.Sqrt(5) - 1) / 2;
            double x1 = b - gr * (b - a), x2 = a + gr * (b - a);
            double f1 = cost(x1), f2 = cost(x2);
            for (int i = 0; i < 40; i++)
            {
                if (f1 < f2) { b = x2; x2 = x1; f2 = f1; x1 = b - gr * (b - a); f1 = cost(x1); }
                else { a = x1; x1 = x2; f1 = f2; x2 = a + gr * (b - a); f2 = cost(x2); }
            }
            double kBest = (a + b) / 2;
            after = cost(kBest);
            // Duzelme kucukse (yarim piksel altinda bukulme ya da az kazanc) dokunma
            LastK = kBest;
            if (before < 0.5 || after > before * 0.7 || Math.Abs(kBest) > MaxAbsK * 0.98) return null;
            return new LensModel(width, height, kBest);
        }

        /// <summary>Her kenara toplam en kucuk kareler dogrusu; 4 px'ten fazla sapan tek tek ayiklanir; tum uyanlarin RMS'i.</summary>
        private static double Rms(List<List<Point2>> groups)
        {
            double sum = 0;
            int n = 0;
            foreach (List<Point2> g in groups)
            {
                var use = Enumerable.Repeat(true, g.Count).ToArray();
                double[] res = null;
                while (true)
                {
                    res = Residuals(g, use);
                    int worst = -1; double wv = 0;
                    for (int i = 0; i < g.Count; i++) if (use[i] && Math.Abs(res[i]) > wv) { wv = Math.Abs(res[i]); worst = i; }
                    if (wv <= EdgePointFit.OutlierPx || use.Count(u => u) <= 3) break;
                    use[worst] = false;
                }
                for (int i = 0; i < g.Count; i++) if (use[i]) { sum += res[i] * res[i]; n++; }
            }
            return n == 0 ? double.MaxValue : Math.Sqrt(sum / n);
        }

        private static double[] Residuals(List<Point2> g, bool[] use)
        {
            double mx = 0, my = 0; int n = 0;
            for (int i = 0; i < g.Count; i++) if (use[i]) { mx += g[i].X; my += g[i].Y; n++; }
            mx /= n; my /= n;
            double sxx = 0, syy = 0, sxy = 0;
            for (int i = 0; i < g.Count; i++)
            {
                if (!use[i]) continue;
                double dx = g[i].X - mx, dy = g[i].Y - my;
                sxx += dx * dx; syy += dy * dy; sxy += dx * dy;
            }
            double ang = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
            double a = -Math.Sin(ang), b = Math.Cos(ang), c = -(a * mx + b * my);
            return g.Select(p => a * p.X + b * p.Y + c).ToArray();
        }
    }
}
