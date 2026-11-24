using System;
using System.Collections.Generic;
using System.Linq;
using KopruKesim.Core;
using OpenCvSharp;

namespace KopruKesim.Vision
{
    /// <summary>
    /// Tabla koselerine konan mavi paspaslari (dikdortgen) bulur. Paspasin tablanin kosesine denk gelen dis kosesi tabla
    /// kosesidir; dort kenari tabla duzleminde duz oldugu icin lens bukulmesini bulmaya da yarar (sakul yontemi).
    ///
    /// Kameranin renk dengesi kayik olabilir (Dahua'da beton ve beyaz kagit mavimsi gorunuyor): renk once beyaz bir
    /// referansa (dama kagidinin beyaz kareleri) gore duzeltilir, sonra doygun mavi alanlar aranir.
    /// </summary>
    public static class BlueMatFinder
    {
        public sealed class BlueMat
        {
            /// <summary>Fotograftaki dort kose (kabaca, kenarlar lens yuzunden egri olabilir), saat yonunde.</summary>
            public Point2[] Quad;
            /// <summary>Dort kenarin noktalari (Quad[i] ile Quad[i+1] arasi), uclari atilmis.</summary>
            public Point2[][] Sides;
            public Point2 Center;
            public double AreaPx;
            internal Point[] Contour;
            internal float[] Blueness;
            internal int W, H;
        }

        /// <summary>
        /// Kenar noktalarini verilen (tahmini) dortgenin kenarlarina gore yeniden toplar: kontur noktalari en yakin tahmini
        /// kenara atanir, kenardan maxDistPx'ten uzaktakiler (paspasa yapisan golge, leke) atilir, sonra mavilik
        /// profilinde piksel alti oturtulur. quad: tahmini koseler (herhangi bir dongusel sirada); Sides[i] quad[i]..quad[i+1].
        /// </summary>
        public static BlueMat Resample(BlueMat mat, Point2[] quad, double maxDistPx)
        {
            var sides = new List<Point2>[4];
            for (int i = 0; i < 4; i++) sides[i] = new List<Point2>();
            foreach (Point p in mat.Contour)
            {
                var pt = new Point2(p.X, p.Y);
                int best = -1; double bd = double.MaxValue, bt = 0;
                for (int i = 0; i < 4; i++)
                {
                    Point2 a = quad[i], ab = quad[(i + 1) % 4] - a;
                    double t = Point2.Dot(pt - a, ab) / Math.Max(1e-9, Point2.Dot(ab, ab));
                    double d = Point2.Distance(pt, a + ab * Math.Max(0, Math.Min(1, t)));
                    if (d < bd) { bd = d; best = i; bt = t; }
                }
                if (bd <= maxDistPx && bt > 0.1 && bt < 0.9) sides[best].Add(pt);
            }
            const int maxPerSide = 40;
            var center = new Point2(quad.Average(p => p.X), quad.Average(p => p.Y));
            var picked = new Point2[4][];
            for (int i = 0; i < 4; i++)
            {
                List<Point2> s = sides[i];
                Point2[] sel = s.Count <= maxPerSide ? s.ToArray() : Enumerable.Range(0, maxPerSide).Select(j => s[(int)((j + 0.5) * s.Count / maxPerSide)]).ToArray();
                Point2 dir = quad[(i + 1) % 4] - quad[i];
                var nrm = new Point2(-dir.Y, dir.X) * (1 / Math.Max(1e-9, dir.Length));
                if (Point2.Dot(nrm, (quad[i] + quad[(i + 1) % 4]) * 0.5 - center) < 0) nrm = nrm * -1;
                picked[i] = sel.Select(pt => RefineEdge(mat.Blueness, mat.W, mat.H, pt, nrm)).Where(pt => !double.IsNaN(pt.X)).ToArray();
            }
            return new BlueMat { Quad = (Point2[])quad.Clone(), Sides = picked, Center = center, AreaPx = mat.AreaPx, Contour = mat.Contour, Blueness = mat.Blueness, W = mat.W, H = mat.H };
        }

        private static bool Debug { get { return Environment.GetEnvironmentVariable("KK_DEBUG") == "1"; } }

        /// <summary>Damalarin beyaz karelerinin ortalama rengi (BGR); dama yoksa fotografin parlak kisimlari.</summary>
        public static Scalar WhiteReference(Mat frame, IEnumerable<Point2[]> boardCorners)
        {
            using (var gray = new Mat())
            {
                Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
                var samples = new List<Vec3b>();
                foreach (Point2[] c in boardCorners)
                {
                    int x0 = Math.Max(0, (int)c.Min(p => p.X)), y0 = Math.Max(0, (int)c.Min(p => p.Y));
                    int x1 = Math.Min(frame.Cols - 1, (int)c.Max(p => p.X)), y1 = Math.Min(frame.Rows - 1, (int)c.Max(p => p.Y));
                    if (x1 - x0 < 4 || y1 - y0 < 4) continue;
                    var box = new Rect(x0, y0, x1 - x0, y1 - y0);
                    using (var g = new Mat(gray, box))
                    using (var col = new Mat(frame, box))
                    {
                        // Kutudaki en parlak %35: beyaz kareler
                        byte[] gv; g.Clone().GetArray(out gv);
                        byte thr = gv.OrderBy(v => v).ElementAt((int)(gv.Length * 0.65));
                        for (int y = 0; y < box.Height; y += 2)
                            for (int x = 0; x < box.Width; x += 2)
                                if (g.At<byte>(y, x) >= thr) samples.Add(col.At<Vec3b>(y, x));
                    }
                }
                if (samples.Count < 50) return Cv2.Mean(frame);   // dama yok: gri dunya kabulu
                return new Scalar(samples.Average(v => v.Item0), samples.Average(v => v.Item1), samples.Average(v => v.Item2));
            }
        }

        /// <summary>Fotograftaki mavi paspas adaylari, buyukten kucuge.</summary>
        public static List<BlueMat> Find(Mat frame, Scalar white)
        {
            var found = new List<BlueMat>();
            int w = frame.Cols, h = frame.Rows;
            using (var balanced = new Mat())
            using (var hsv = new Mat())
            using (var mask = new Mat())
            using (var blue = new Mat())
            {
                // Renk dengesi: beyaz referans griye cekilir
                double wg = (white.Val0 + white.Val1 + white.Val2) / 3;
                var gains = new[] { wg / Math.Max(1, white.Val0), wg / Math.Max(1, white.Val1), wg / Math.Max(1, white.Val2) };
                Mat[] ch = Cv2.Split(frame);
                try
                {
                    for (int i = 0; i < 3; i++) ch[i].ConvertTo(ch[i], MatType.CV_8UC1, gains[i]);
                    Cv2.Merge(ch, balanced);
                }
                finally { foreach (Mat m in ch) m.Dispose(); }
                Cv2.CvtColor(balanced, hsv, ColorConversionCodes.BGR2HSV);
                // Mavilik kanali B - (R+G)/2: paspas ~110, beton/kagit/ahsap/celik < 35. Kenar bu kanalda piksel alti
                // bulunur (esikli maskenin kenari paspasin koyu kenar bandinda birkac piksel zikzak yapiyor).
                Mat[] bc = Cv2.Split(balanced);
                try
                {
                    using (var gr = new Mat())
                    {
                        Cv2.AddWeighted(bc[1], 0.5, bc[2], 0.5, 0, gr, MatType.CV_32F);   // (G + R) / 2
                        bc[0].ConvertTo(blue, MatType.CV_32FC1);
                        Cv2.Subtract(blue, gr, blue);
                    }
                    Cv2.GaussianBlur(blue, blue, new Size(3, 3), 0);
                }
                finally { foreach (Mat m in bc) m.Dispose(); }
                float[] blueness; blue.GetArray(out blueness);
                // Mavi: ton 104-130 (OpenCV 0-180), cok doygun, cok karanlik degil. Olculen (2026-11-24, iki canli kare):
                // paspas ici ton 108-112, doygunluk ortanca 243-255; mavimsi beton zemin ve beyaz yan paneller ton 99-104,
                // doygunluk ortanca ~150, en fazla ~200. Eski esik (ton 90, doygunluk 140) isik degisince zemini ve paneli
                // de maviye katiyordu: sol ust paspas zeminle birlesti, kalibrasyon "kenarlar gorunmuyor" ile basarisiz oldu.
                Cv2.InRange(hsv, new Scalar(104, 170, 50), new Scalar(130, 255, 255), mask);
                int k = Math.Max(3, (int)Math.Round(Math.Max(w, h) / 800.0)) | 1;
                // Acma daha genis: koyu-parlak gecislerde lensin mavi renk sacagi (kromatik sapma) birkac piksel
                // genisliginde serit verir ve paspasa yapisir (4K'da ~8 px); paspas en az ~100 px kalinliktadir.
                int kOpen = Math.Max(5, (int)Math.Round(Math.Max(w, h) / 250.0)) | 1;
                using (var ker = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(k, k)))
                using (var kerOpen = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(kOpen, kOpen)))
                {
                    Cv2.MorphologyEx(mask, mask, MorphTypes.Close, ker);       // dokunun koyu araliklari
                    Cv2.MorphologyEx(mask, mask, MorphTypes.Open, kerOpen);    // renk sacagi serit, tek tuk pikseller
                }
                if (Debug) mask.SaveImage(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kk_mavi.png"));
                Point[][] contours;
                HierarchyIndex[] hier;
                Cv2.FindContours(mask, out contours, out hier, RetrievalModes.External, ContourApproximationModes.ApproxNone);
                double minArea = w * (double)h * 0.0008, maxArea = w * (double)h * 0.12;
                var queue = new Queue<KeyValuePair<Point[], bool>>(contours.Select(c => new KeyValuePair<Point[], bool>(c, false)));
                while (queue.Count > 0)
                {
                    var item = queue.Dequeue();
                    Point[] c = item.Key;
                    double area = Cv2.ContourArea(c);
                    if (area < minArea || area > maxArea * 1.5) continue;
                    if (!item.Value)
                    {
                        // Once bu bolgede uc kat genis acma: paspasa yapisan kucuk lekeler, golge cikintilari ve ince
                        // baglantilar (kablo, ekran yansimasi) kopar. Yoksa dis zarf onlari da icine alir ve dortgenin bir
                        // kosesi oraya kayar (canli kare 2026-11-23: sag alt paspas 70 px kayik eslesiyordu). Paspas en az
                        // ~100 px kalinlikta, acmadan etkilenmez; koseler zaten kenar dogrularinin kesisiminden.
                        foreach (Point[] part in ReOpen(mask, c, kOpen * 3 | 1))
                            queue.Enqueue(new KeyValuePair<Point[], bool>(part, true));
                        continue;
                    }
                    if (area > maxArea) continue;
                    Point[] hull = Cv2.ConvexHull(c);
                    double solidity = area / Math.Max(1, Cv2.ContourArea(hull));
                    Point[] quad = null;
                    double peri = Cv2.ArcLength(hull, true);
                    for (double eps = 0.015; eps <= 0.08 && quad == null; eps += 0.005)
                    {
                        Point[] a = Cv2.ApproxPolyDP(hull, eps * peri, true);
                        if (a.Length == 4) quad = a;
                    }
                    double fill = quad == null ? 0 : area / Math.Max(1, Cv2.ContourArea(quad));
                    if (Debug) Console.WriteLine("mavi aday: alan {0:F0} doluluk {1:F2} dortgen {2} ({3:F2}) kutu {4}", area, solidity, quad != null, fill, Cv2.BoundingRect(c));
                    if (solidity < 0.85 || quad == null || fill < 0.85) continue;
                    BlueMat mat = Build(c, quad, area, blueness, w, h);
                    if (mat != null) found.Add(mat);
                }
            }
            return found.OrderByDescending(m => m.AreaPx).ToList();
        }

        /// <summary>Konturun kutusunda maskeyi genis cekirdekle acar; kalan parcalarin konturlari (tam goruntu koordinatinda).</summary>
        private static List<Point[]> ReOpen(Mat mask, Point[] contour, int kernel)
        {
            var res = new List<Point[]>();
            Rect box = Cv2.BoundingRect(contour);
            box = new Rect(Math.Max(0, box.X - 2), Math.Max(0, box.Y - 2), 0, 0)
            {
                Width = Math.Min(mask.Cols, box.Right + 2) - Math.Max(0, box.X - 2),
                Height = Math.Min(mask.Rows, box.Bottom + 2) - Math.Max(0, box.Y - 2)
            };
            using (var roi = new Mat(box.Height, box.Width, MatType.CV_8UC1, Scalar.All(0)))
            using (var ker = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(kernel, kernel)))
            {
                // Yalniz bu konturun ici (komsu adaylar karismasin)
                Cv2.DrawContours(roi, new[] { contour.Select(p => new Point(p.X - box.X, p.Y - box.Y)).ToArray() }, 0, Scalar.All(255), -1);
                using (var src = new Mat(mask, box)) Cv2.BitwiseAnd(roi, src, roi);
                Cv2.MorphologyEx(roi, roi, MorphTypes.Open, ker);
                Cv2.FindContours(roi, out Point[][] parts, out _, RetrievalModes.External, ContourApproximationModes.ApproxNone);
                foreach (Point[] p in parts) res.Add(p.Select(q => new Point(q.X + box.X, q.Y + box.Y)).ToArray());
            }
            return res;
        }

        private static BlueMat Build(Point[] contour, Point[] quad, double area, float[] blueness, int w, int h)
        {
            // Saat yonu (goruntu ekseninde) sira
            Point2[] q = quad.Select(p => new Point2(p.X, p.Y)).ToArray();
            double signed = 0;
            for (int i = 0; i < 4; i++) signed += q[i].X * q[(i + 1) % 4].Y - q[(i + 1) % 4].X * q[i].Y;
            if (signed < 0) Array.Reverse(q);
            // Kontur pikselleri kenarin kaba yeridir; asagida mavilik profilinde piksel alti duzeltilir.
            var sides = new List<Point2>[4];
            for (int i = 0; i < 4; i++) sides[i] = new List<Point2>();
            foreach (Point p in contour)
            {
                var pt = new Point2(p.X, p.Y);
                int best = -1; double bd = double.MaxValue, bt = 0;
                for (int i = 0; i < 4; i++)
                {
                    Point2 a = q[i], b = q[(i + 1) % 4], ab = b - a;
                    double len2 = Math.Max(1e-9, Point2.Dot(ab, ab)), t = Point2.Dot(pt - a, ab) / len2;
                    double d = Point2.Distance(pt, a + ab * Math.Max(0, Math.Min(1, t)));
                    if (d < bd) { bd = d; best = i; bt = t; }
                }
                // Kose bolgesi (yuvarlak kose, kesim pacasi) atilir
                if (bt > 0.1 && bt < 0.9) sides[best].Add(pt);
            }
            const int maxPerSide = 40;
            Point2[][] picked = sides.Select(s => s.Count <= maxPerSide ? s.ToArray()
                : Enumerable.Range(0, maxPerSide).Select(j => s[(int)((j + 0.5) * s.Count / maxPerSide)]).ToArray()).ToArray();
            // Her nokta kenara dik profilde mavilik kanalinin yari seviyeyi gectigi yere tasinir (piksel alti)
            var center = new Point2(q.Average(p => p.X), q.Average(p => p.Y));
            for (int i = 0; i < 4; i++)
            {
                Point2 dir = q[(i + 1) % 4] - q[i];
                var nrm = new Point2(-dir.Y, dir.X) * (1 / Math.Max(1e-9, dir.Length));
                if (Point2.Dot(nrm, (q[i] + q[(i + 1) % 4]) * 0.5 - center) < 0) nrm = nrm * -1;   // disari bakan normal
                picked[i] = picked[i].Select(pt => RefineEdge(blueness, w, h, pt, nrm)).Where(pt => !double.IsNaN(pt.X)).ToArray();
            }
            return new BlueMat
            {
                Quad = q,
                Sides = picked,
                Center = new Point2(q.Average(p => p.X), q.Average(p => p.Y)),
                AreaPx = area,
                Contour = contour, Blueness = blueness, W = w, H = h
            };
        }

        /// <summary>
        /// Kenar noktasini normal boyunca (+ disari) mavilik profilinde iceri ve disari seviyelerin ortasini gectigi yere
        /// tasir. Karsitlik zayifsa ya da gecis bulunamazsa NaN.
        /// </summary>
        private static Point2 RefineEdge(float[] img, int w, int h, Point2 p, Point2 n)
        {
            // 16 px: koyu zemine bakan kenarda mavimsi golge maskeye karisip konturu 10 px'e kadar disari itebiliyor
            const double reach = 16, step = 0.25;
            int count = (int)(2 * reach / step) + 1;
            var v = new double[count];
            for (int k = 0; k < count; k++)
            {
                double t = -reach + k * step, x = p.X + n.X * t, y = p.Y + n.Y * t;
                if (x < 0 || y < 0 || x >= w - 1 || y >= h - 1) return new Point2(double.NaN, double.NaN);
                int x0 = (int)x, y0 = (int)y; double fx = x - x0, fy = y - y0;
                int a = y0 * w + x0;
                v[k] = (img[a] * (1 - fx) + img[a + 1] * fx) * (1 - fy) + (img[a + w] * (1 - fx) + img[a + w + 1] * fx) * fy;
            }
            int quarter = count / 4;
            double inside = v.Take(quarter).OrderBy(z => z).ElementAt(quarter / 2);
            double outside = v.Skip(count - quarter).OrderBy(z => z).ElementAt(quarter / 2);
            if (inside - outside < 30) return new Point2(double.NaN, double.NaN);
            // Kenar: disari dogru en dik dusus (turevin en kucuk oldugu yer, parabolle piksel alti). Yari seviye
            // kullanilmaz: karanlik zeminde paspasin cevresinde mavi hale (parlama, renk sacagi) yavas soner ve yari
            // seviyeyi 5-8 piksel disari kaydirir; asil kenar ise dik bir gecistir.
            var g = new double[count];
            for (int k = 1; k + 1 < count; k++) g[k] = (v[k + 1] - v[k - 1]) / (2 * step);
            int lim = (int)((reach - 2) / step), mid = count / 2, bk = -1;
            for (int k = Math.Max(1, mid - lim); k <= Math.Min(count - 2, mid + lim); k++)
                if (bk < 0 || g[k] < g[bk]) bk = k;
            if (bk <= 1 || bk >= count - 2 || -g[bk] * 2 < (inside - outside) / reach) return new Point2(double.NaN, double.NaN);
            double den = g[bk - 1] - 2 * g[bk] + g[bk + 1];
            double off = Math.Abs(den) < 1e-12 ? 0 : 0.5 * (g[bk - 1] - g[bk + 1]) / den;
            double best = -reach + (bk + Math.Max(-1, Math.Min(1, off))) * step;
            return p + n * best;
        }

        /// <summary>
        /// Adaylardan dort koseyi secer: birbirinden en uzak yayilan dortlu (merkezlerinin olusturdugu dortgenin alani en
        /// buyuk). Sira: sol ust, sag ust, sol alt, sag alt (fotografta). Dortten az aday varsa null.
        /// </summary>
        public static BlueMat[] PickCorners(List<BlueMat> mats)
        {
            if (mats.Count < 4) return null;
            List<BlueMat> pool = mats.Take(7).ToList();
            BlueMat[] best = null; double bestArea = 0;
            int n = pool.Count;
            for (int a = 0; a < n; a++)
                for (int b = a + 1; b < n; b++)
                    for (int c = b + 1; c < n; c++)
                        for (int d = c + 1; d < n; d++)
                        {
                            BlueMat[] o = Order(new[] { pool[a], pool[b], pool[c], pool[d] });
                            if (o == null) continue;
                            // Dortgen: sol ust, sag ust, sag alt, sol alt
                            Point2[] p = { o[0].Center, o[1].Center, o[3].Center, o[2].Center };
                            double s = 0;
                            for (int i = 0; i < 4; i++) s += p[i].X * p[(i + 1) % 4].Y - p[(i + 1) % 4].X * p[i].Y;
                            if (Math.Abs(s) / 2 > bestArea) { bestArea = Math.Abs(s) / 2; best = o; }
                        }
            return best;
        }

        private static BlueMat[] Order(BlueMat[] four)
        {
            var byY = four.OrderBy(m => m.Center.Y).ToList();
            var top = byY.Take(2).OrderBy(m => m.Center.X).ToList();
            var bottom = byY.Skip(2).OrderBy(m => m.Center.X).ToList();
            // Ust iki gercekten ustte, soldakiler gercekten solda olmali
            if (Math.Max(top[0].Center.Y, top[1].Center.Y) >= Math.Min(bottom[0].Center.Y, bottom[1].Center.Y)) return null;
            return new[] { top[0], top[1], bottom[0], bottom[1] };
        }

        /// <summary>Paspasin fotograftaki dis kosesi (kabaca): dort paspasin ortasindan en uzak kose.</summary>
        public static Point2 OuterImageCorner(BlueMat mat, Point2 middle)
        {
            return mat.Quad.OrderByDescending(p => Point2.Distance(p, middle)).First();
        }
    }
}
