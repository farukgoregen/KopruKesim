using System;
using KopruKesim.Core;
using OpenCvSharp;

namespace KopruKesim.Vision
{
    /// <summary>
    /// Kullanici yalniz iki karsi koseyi (sol ust ve sag alt) verir; diger iki kose tablanin kenarlarindan
    /// bulunur. Her verilen koseden cikan iki kenarin yonu, tahmini dortgenin yonu etrafinda taranir ve
    /// kenar boyunca gradyan destegi en yuksek aci secilir. Ust kenar ile sag kenarin kesisimi sag ust,
    /// sol kenar ile alt kenarin kesisimi sol alt kosedir. Verilen iki kose aynen korunur.
    /// Kose sirasi TableCalibration ile ayni: 0 sol ust, 1 sag ust, 2 sag alt, 3 sol alt.
    /// </summary>
    public static class DiagonalCorners
    {
        /// <summary>Tahmini yon etrafinda taranan aci (derece).</summary>
        private const double SearchDeg = 12;
        private const double StepDeg = 0.05;

        /// <param name="guess">Tahmini dortgen (genelde otomatik bulunan ya da onceki koseler); yon ve uzunluk icin.</param>
        public static Point2[] Complete(Mat frame, Point2 topLeft, Point2 bottomRight, Point2[] guess)
        {
            if (guess == null || guess.Length != 4) throw new ArgumentException("Tahmini dort kose gerekli.");
            int w = frame.Cols, h = frame.Rows;
            float[] gx, gy;
            using (var gray = new Mat())
            using (var gxm = new Mat())
            using (var gym = new Mat())
            {
                if (frame.Channels() == 1) frame.CopyTo(gray); else Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
                Cv2.GaussianBlur(gray, gray, new Size(5, 5), 0);
                Cv2.Sobel(gray, gxm, MatType.CV_32F, 1, 0);
                Cv2.Sobel(gray, gym, MatType.CV_32F, 0, 1);
                gxm.GetArray(out gx);
                gym.GetArray(out gy);
            }
            // Verilen koseler tahminde ayni yerde degilse tahmini onlara gore kaydir: yonler tahminden,
            // uzunluklar verilen koselerden
            Point2 tl = topLeft, br = bottomRight;
            Point2 dirTop = Unit(guess[1] - guess[0]), dirLeft = Unit(guess[3] - guess[0]);
            Point2 dirBottom = Unit(guess[3] - guess[2]), dirRight = Unit(guess[1] - guess[2]);
            double lenTop = Point2.Distance(guess[0], guess[1]), lenLeft = Point2.Distance(guess[0], guess[3]);
            double lenBottom = Point2.Distance(guess[2], guess[3]), lenRight = Point2.Distance(guess[2], guess[1]);

            Point2 top = BestDirection(gx, gy, w, h, tl, dirTop, lenTop);
            Point2 left = BestDirection(gx, gy, w, h, tl, dirLeft, lenLeft);
            Point2 bottom = BestDirection(gx, gy, w, h, br, dirBottom, lenBottom);
            Point2 right = BestDirection(gx, gy, w, h, br, dirRight, lenRight);

            Point2? tr = Intersect(tl, top, br, right);
            Point2? bl = Intersect(tl, left, br, bottom);
            if (tr == null || bl == null) throw new ArgumentException("Kenarlar kesismiyor.");
            return new[] { tl, tr.Value, br, bl.Value };
        }

        /// <summary>Koseden cikan isin yonunu, tahmin etrafinda kenar destegi en yuksek olacak sekilde secer.</summary>
        private static Point2 BestDirection(float[] gx, float[] gy, int w, int h, Point2 from, Point2 guessDir, double guessLen)
        {
            double baseAng = Math.Atan2(guessDir.Y, guessDir.X);
            double len = Math.Max(20, guessLen * 0.85);
            Point2 best = guessDir;
            double bestScore = -1, bestOff = double.MaxValue;
            for (double off = -SearchDeg; off <= SearchDeg + 1e-9; off += StepDeg)
            {
                double a = baseAng + off * Math.PI / 180;
                var d = new Point2(Math.Cos(a), Math.Sin(a));
                double s = Support(gx, gy, w, h, from, d, len);
                // Esitlikte tahmine yakin aci: duz desenli yuzeyde komsu cizgiye kaymasin
                if (s > bestScore + 1e-6 || (Math.Abs(s - bestScore) <= 1e-6 && Math.Abs(off) < bestOff))
                {
                    bestScore = s; best = d; bestOff = Math.Abs(off);
                }
            }
            return best;
        }

        /// <summary>Isin boyunca, isina dik gradyanin guclu oldugu orneklerin orani (kose yuvarlakligi icin bastaki %5 atlanir).</summary>
        private static double Support(float[] gx, float[] gy, int w, int h, Point2 from, Point2 d, double len)
        {
            var n = new Point2(-d.Y, d.X);
            const int k = 240;
            double sum = 0;
            int used = 0;
            for (int i = 0; i < k; i++)
            {
                double t = len * (0.05 + 0.95 * i / (k - 1));
                double cx = from.X + d.X * t, cy = from.Y + d.Y * t;
                if (cx < 1 || cy < 1 || cx >= w - 1 || cy >= h - 1) continue;
                used++;
                double best = 0;
                for (int o = -2; o <= 2; o++)
                {
                    int x = (int)Math.Round(cx + n.X * o), y = (int)Math.Round(cy + n.Y * o);
                    if (x < 0 || y < 0 || x >= w || y >= h) continue;
                    int idx = y * w + x;
                    best = Math.Max(best, Math.Abs(gx[idx] * n.X + gy[idx] * n.Y));
                }
                sum += Math.Min(best, 200);   // tek parlak lekenin skoru ele gecirmemesi icin kirpilir
            }
            return used == 0 ? 0 : sum / (k * 200.0);
        }

        private static Point2 Unit(Point2 v)
        {
            double l = v.Length;
            return l < 1e-9 ? new Point2(1, 0) : v * (1 / l);
        }

        private static Point2? Intersect(Point2 p, Point2 d, Point2 q, Point2 e)
        {
            double den = d.X * e.Y - d.Y * e.X;
            if (Math.Abs(den) < 1e-9) return null;
            double t = ((q.X - p.X) * e.Y - (q.Y - p.Y) * e.X) / den;
            return new Point2(p.X + d.X * t, p.Y + d.Y * t);
        }
    }
}
