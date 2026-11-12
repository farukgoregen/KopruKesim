using System;
using System.Collections.Generic;
using System.Linq;
using KopruKesim.Core;
using OpenCvSharp;

namespace KopruKesim.Vision
{
    /// <summary>
    /// BOS tabla goruntusunde tablanin dort kosesini bulur. Once ahsap renginden kaba bir dortgen
    /// cikarilir; renkten ayrilamayan (soluk tahta) ya da ortulu (testere kolu) bir kenar varsa
    /// kenarlar boyunca yurunerek gercek uclar bulunur (bkz. FindCorners).
    /// Uzerinde parca varken kullanilmamalidir (kenarlar ortulur).
    /// </summary>
    public static class TableFinder
    {
        private static readonly bool Debug = Environment.GetEnvironmentVariable("KK_DEBUG") == "1";

        /// <summary>
        /// Koseleri goruntudeki yerine gore sirali dondurur: sol ust, sag ust, sag alt, sol alt.
        /// Bulunamazsa null. Tabla olcusu verilmedigi icin ortulu kenar tamamlanamaz.
        /// </summary>
        public static Point2[] FindCorners(Mat emptyTableFrame)
        {
            return FindCorners(emptyTableFrame, 0, 0);
        }

        /// <summary>
        /// Koseleri bulur; tabla olcusu (mm) biliniyorsa ortulu ya da renkten ayrilamayan bir kenari
        /// tamamlar. Sira: sol ust (0 noktasi), sag ust, sag alt, sol alt (X ucu).
        /// Yontem: kenarlar boyunca yurunur, her kenarin gorundugu uclar bulunur; ortasi kapali
        /// (ornek: testere kolu) bir kenarin koseleri komsu kenarlarin bittigi yerden alinir.
        /// Bilinen olcu, adaylar arasindan kamera modeline en uygun olani secer (tutarlilik ~ 1).
        /// </summary>
        public static Point2[] FindCorners(Mat emptyTableFrame, double lengthMm, double widthMm)
        {
            int w = emptyTableFrame.Cols, h = emptyTableFrame.Rows;
            var candidates = new List<Point2[]>();
            // 1) Ahsap rengine gore (kol / soluk tahta tamamlamasi dahil)
            Point2[] wood = FindCornersRaw(emptyTableFrame, lengthMm, widthMm);
            if (wood != null && IsValidQuad(wood, w, h)) candidates.Add(wood);
            // 2) Renkten bagimsiz: goruntu ortasindaki yuzeyi buyut, dortgene oturt, kenarlara incelt
            int snap = Math.Max(8, w / 100);
            foreach (bool barrier in new[] { false, true })
            foreach (double tol in new[] { 1.0, 1.5 })
            {
                Point2[] region = FindByRegion(emptyTableFrame, tol, barrier);
                if (region == null) continue;
                Point2[] refined = RefineCorners(emptyTableFrame, region, snap);
                if (IsValidQuad(refined, w, h)) candidates.Add(refined);
            }
            if (candidates.Count == 0) return null;
            // Kenarlari gercek kenar cizgisine en iyi oturan aday kazanir
            Point2[] best = null;
            double bestScore = -1;
            foreach (Point2[] c in candidates)
            {
                double score = EdgeSupport(emptyTableFrame, c).Average();
                if (Debug) Console.WriteLine("  [aday] {0} kenar destegi={1:F2}", string.Join(" ", c.Select(p => p.ToString())), score);
                if (score > bestScore) { bestScore = score; best = c; }
            }
            return best;
        }

        /// <summary>
        /// Renkten bagimsiz kaba dortgen: goruntu ortasindaki yuzeyin rengine benzeyen, ortayi iceren
        /// bolge buyutulur; dortgene sadelestirilir. Tablanin fotografin ortasinda oldugu ve cevresinden
        /// renkce ayrildigi varsayilir. Bolge goruntu kenarina tasarsa (cevreyle karisti) null.
        /// </summary>
        public static Point2[] FindByRegion(Mat frame, double tolerance, bool edgeBarrier = false)
        {
            double sc = 800.0 / Math.Max(frame.Cols, frame.Rows);
            using (var small = new Mat())
            using (var smooth = new Mat())
            using (var lab = new Mat())
            {
                Cv2.Resize(frame, small, new Size(), sc, sc, InterpolationFlags.Area);
                Cv2.BilateralFilter(small, smooth, 9, 40, 9);
                Cv2.CvtColor(smooth, lab, ColorConversionCodes.BGR2Lab);
                int hh = lab.Rows, ww = lab.Cols;
                lab.GetArray(out Vec3b[] px);

                // Tohum rengi: ortadaki %30'luk bolgenin ortancasi; yayilim: 90. yuzdelik uzaklik
                var ls = new List<double>(); var a = new List<double>(); var b = new List<double>();
                for (int y = (int)(hh * 0.35); y < (int)(hh * 0.65); y++)
                    for (int x = (int)(ww * 0.35); x < (int)(ww * 0.65); x++)
                    { Vec3b v = px[y * ww + x]; ls.Add(v.Item0); a.Add(v.Item1); b.Add(v.Item2); }
                double mL = Median(ls), mA = Median(a), mB = Median(b);
                var dist = new List<double>(ls.Count);
                for (int i = 0; i < ls.Count; i++) dist.Add(Math.Sqrt(Sq(ls[i] - mL) + Sq(a[i] - mA) + Sq(b[i] - mB)));
                dist.Sort();
                double thr = Math.Max(8, dist[(int)(0.9 * (dist.Count - 1))] * tolerance);

                using (var mask = new Mat(hh, ww, MatType.CV_8UC1))
                {
                    var m = new byte[hh * ww];
                    for (int i = 0; i < m.Length; i++)
                    {
                        Vec3b v = px[i];
                        double d = Math.Sqrt(Sq(0.5 * (v.Item0 - mL)) + Sq(v.Item1 - mA) + Sq(v.Item2 - mB));   // parlaklik yari agirlik
                        m[i] = d < thr ? (byte)255 : (byte)0;
                    }
                    mask.SetArray(m);
                    using (Mat k9 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(9, 9)))
                    using (Mat k5 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5)))
                    {
                        if (!edgeBarrier)
                        {
                            Cv2.MorphologyEx(mask, mask, MorphTypes.Close, k9);
                            Cv2.MorphologyEx(mask, mask, MorphTypes.Open, k5);
                        }
                        else
                        {
                            // Tabla cevresiyle (duvar, beyaz dolap) ayni renkteyse aradaki kenar cizgisi bariyer olur
                            using (Mat k3 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3)))
                            using (var gray = new Mat())
                            using (var edges = new Mat())
                            {
                                Cv2.MorphologyEx(mask, mask, MorphTypes.Close, k5);
                                Cv2.CvtColor(smooth, gray, ColorConversionCodes.BGR2GRAY);
                                Cv2.Canny(gray, edges, 20, 60);
                                Cv2.Dilate(edges, edges, k3);
                                mask.SetTo(Scalar.All(0), edges);
                                Cv2.MorphologyEx(mask, mask, MorphTypes.Open, k3);
                            }
                        }
                    }
                    using (var labels = new Mat())
                    using (var stats = new Mat())
                    using (var cents = new Mat())
                    {
                        Cv2.ConnectedComponentsWithStats(mask, labels, stats, cents);
                        // Ortadaki %30'luk bolgede en cok yer kaplayan parca tablanin kendisidir
                        // (tam ortada tablanin uzerindeki bir parca olabilir)
                        labels.GetArray(out int[] lbl);
                        var votes = new Dictionary<int, int>();
                        for (int y = (int)(hh * 0.35); y < (int)(hh * 0.65); y++)
                            for (int x = (int)(ww * 0.35); x < (int)(ww * 0.65); x++)
                            {
                                int v = lbl[y * ww + x];
                                if (v == 0) continue;
                                votes.TryGetValue(v, out int n); votes[v] = n + 1;
                            }
                        if (votes.Count == 0) return null;
                        int k = votes.OrderByDescending(p => p.Value).First().Key;
                        using (Mat region = new Mat())
                        {
                            Cv2.Compare(labels, Scalar.All(k), region, CmpType.EQ);
                            if (edgeBarrier)
                                using (Mat k9b = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(9, 9)))
                                    Cv2.MorphologyEx(region, region, MorphTypes.Close, k9b);   // damar/leke cizgilerinin actigi delikler
                            Point[][] cs; HierarchyIndex[] hi;
                            Cv2.FindContours(region, out cs, out hi, RetrievalModes.External, ContourApproximationModes.ApproxNone);
                            if (cs.Length == 0) return null;
                            Point[] cnt = cs.OrderByDescending(c => Cv2.ContourArea(c)).First();
                            double area = Cv2.ContourArea(cnt);
                            Point[] hull = Cv2.ConvexHull(cnt);
                            double solidity = area / Math.Max(1, Cv2.ContourArea(hull));
                            int touch = cnt.Count(p => p.X <= 1 || p.Y <= 1 || p.X >= ww - 2 || p.Y >= hh - 2);
                            if (area < 0.08 * hh * ww || area > 0.92 * hh * ww || solidity < 0.9 || touch > 0.02 * cnt.Length) return null;
                            double per = Cv2.ArcLength(hull, true);
                            for (double eps = 0.01; eps < 0.1; eps += 0.005)
                            {
                                Point[] q = Cv2.ApproxPolyDP(hull, eps * per, true);
                                if (q.Length == 4) return OrderCorners(q.Select(p => new Point2(p.X / sc, p.Y / sc)).ToArray());
                                if (q.Length < 4) break;
                            }
                            return null;
                        }
                    }
                }
            }
        }

        private static double Sq(double v) { return v * v; }
        private static double Median(List<double> v) { var s = new List<double>(v); s.Sort(); return s[s.Count / 2]; }

        /// <summary>
        /// Dort kose gecerli bir tabla dortgeni mi: disbukey, kenarlar kesismiyor, alan goruntunun
        /// en az %3'u. Tahta rengi olmayan tablada renk maskesi anlamsiz bir dortgen verebilir.
        /// </summary>
        public static bool IsValidQuad(Point2[] c, int w, int h)
        {
            if (c == null || c.Length != 4) return false;
            int sign = 0;
            for (int i = 0; i < 4; i++)
            {
                double cr = Point2.Cross(c[(i + 1) % 4] - c[i], c[(i + 2) % 4] - c[(i + 1) % 4]);
                if (Math.Abs(cr) < 1e-6) return false;
                int s = Math.Sign(cr);
                if (sign == 0) sign = s; else if (s != sign) return false;
            }
            return Math.Abs(PolygonMath.SignedArea(c)) >= 0.03 * w * h;
        }

        private static Point2[] FindCornersRaw(Mat emptyTableFrame, double lengthMm, double widthMm)
        {
            Point2[] rough = FindRough(emptyTableFrame);
            if (rough == null || lengthMm <= 0 || widthMm <= 0) return rough;
            int w = emptyTableFrame.Cols, h = emptyTableFrame.Rows;
            double? k0 = Consistency(rough, lengthMm, widthMm, w, h);
            if (Debug) Console.WriteLine("  [kaba] {0} tutarlilik={1}", string.Join(" ", rough.Select(p => p.ToString())), k0);
            if (k0 != null && k0 > 0.9 && k0 < 1.1) return rough;   // kaba sonuc zaten tutarli

            // Her kenarin gercekten gorunen uclarini bul. Bir kenar yanlis (ortulu ya da renkten
            // ayrilamamis) ise, iki kosesini komsu kenarlarin bittigi yerlerden al; bilinen tabla
            // olcusuyle en tutarli aday kazanir.
            var field = new EdgeField(emptyTableFrame);
            var ext = new Point2[4][];
            for (int i = 0; i < 4; i++) ext[i] = Extent(field, rough[i], rough[(i + 1) % 4]);
            Point2[] best = rough;
            double bestErr = k0 == null ? double.MaxValue : Math.Abs(Math.Log(k0.Value));
            int bestEdge = -1;
            for (int e = 0; e < 4; e++)
            {
                int a = e, b = (e + 1) % 4, prevEdge = (a + 3) % 4, nextEdge = b;
                if (ext[prevEdge] == null || ext[nextEdge] == null) continue;
                var cand = (Point2[])rough.Clone();
                cand[a] = ext[prevEdge][1];
                cand[b] = ext[nextEdge][0];
                double? k = Consistency(cand, lengthMm, widthMm, w, h);
                if (Debug) Console.WriteLine("  [kenar {0} yeniden] {1} tutarlilik={2}", e, string.Join(" ", cand.Select(p => p.ToString())), k);
                if (k == null) continue;
                double err = Math.Abs(Math.Log(k.Value));
                if (err < bestErr) { bestErr = err; best = cand; bestEdge = e; }
            }
            if (bestEdge < 0) return best;

            // Ince ayar: yeniden kurulan kenarin gorunen parcalarina dogru oturt, koseleri
            // komsu (dogru bulunmus) kenarlarla kesistir. Ortulu kisim aykiri nokta olarak elenir.
            // Her kose ayri ayarlanir: kenarin o koseye yakin %30'luk parcasina dogru oturtulur ve
            // komsu (dogru bulunmus) kenarla kesistirilir. Tutarlilik birkac piksellik farka cok
            // duyarli oldugu icin (optik merkez, lens) burada yargic degil; kenar kaniti esastir.
            int ra = bestEdge, rb = (bestEdge + 1) % 4;
            double[] prevLine = Line(rough[(ra + 3) % 4], rough[ra]);
            double[] nextLine = Line(rough[rb], rough[(rb + 1) % 4]);
            Point2 span = best[rb] - best[ra];
            var refinedA = RefineCorner(field, best[ra], best[ra] + span * 0.3, prevLine, w);
            var refinedB = RefineCorner(field, best[rb] - span * 0.3, best[rb], nextLine, w);
            var trial = (Point2[])best.Clone();
            if (refinedA != null) trial[ra] = refinedA.Value;
            if (refinedB != null) trial[rb] = refinedB.Value;
            double? kt = Consistency(trial, lengthMm, widthMm, w, h);
            if (Debug) Console.WriteLine("  [ince ayar] {0} tutarlilik={1}", string.Join(" ", trial.Select(p => p.ToString())), kt);
            return kt != null ? trial : best;   // fiziksel olarak anlamsiz hale gelirse ayari alma
        }

        /// <summary>p-q parcasina gorunen kenari oturtur, komsu kenar dogrusuyla kesisimini dondurur.</summary>
        private static Point2? RefineCorner(EdgeField f, Point2 p, Point2 q, double[] neighbourLine, int w)
        {
            double[] fitted = FitVisibleEdge(f, p, q);
            if (fitted == null) return null;
            double[] x = Cross(fitted, neighbourLine);
            if (Math.Abs(x[2]) < 1e-12) return null;
            var c = new Point2(x[0] / x[2], x[1] / x[2]);
            // Kose, yurume ile bulunan yerden en fazla goruntu genisliginin %3'u uzakta olabilir
            Point2 corner = Point2.Distance(c, p) < Point2.Distance(c, q) ? p : q;
            return Point2.Distance(c, corner) < 0.03 * w ? c : (Point2?)null;
        }

        /// <summary>Renk maskesinden kaba dortgen.</summary>
        private static Point2[] FindRough(Mat emptyTableFrame)
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
        public static Point2[] OrderByImagePosition(Point2[] c) { return OrderCorners(c); }

        /// <summary>
        /// Kabaca verilmis (elle tiklanmis ya da surüklenmis) dort koseyi inceltir: her kenar,
        /// kaba dogrunun +-searchPx cevresindeki en guclu parlaklik gecisine piksel alti dogru olarak
        /// oturtulur; koseler bu dogrularin kesisimidir. Yuvarlatilmis kose uclari (%12) disarida
        /// birakilir. Bir kenar oturtulamazsa o kenarin kaba dogrusu kalir. Sira korunur.
        /// </summary>
        public static Point2[] RefineCorners(Mat frame, Point2[] rough, int searchPx)
        {
            var f = new EdgeField(frame);
            var lines = new double[4][];
            for (int i = 0; i < 4; i++)
            {
                Point2 p = rough[i], q = rough[(i + 1) % 4];
                lines[i] = FitVisibleEdge(f, p, q, searchPx, 0.12, 0.88) ?? Line(p, q);
            }
            var c = new Point2[4];
            for (int i = 0; i < 4; i++)
            {
                double[] x = Cross(lines[(i + 3) % 4], lines[i]);
                if (Math.Abs(x[2]) < 1e-12) return rough;
                c[i] = new Point2(x[0] / x[2], x[1] / x[2]);
                if (Point2.Distance(c[i], rough[i]) > 3 * searchPx) return rough;   // kenar baska bir cizgiye kaydi
            }
            return c;
        }

        /// <summary>
        /// Verilen dort kenarin (kose i -> i+1) goruntude ne kadar "kenar" oldugunu olcer: dogru boyunca
        /// orneklerin kacinda dik yonde belirgin parlaklik gecisi var (0-1). Kamera oynadiysa
        /// kalibrasyondaki kenarlar artik gercek kenarin uzerinde olmaz ve deger duser.
        /// </summary>
        public static double[] EdgeSupport(Mat frame, Point2[] corners)
        {
            var f = new EdgeField(frame);
            var s = new double[4];
            for (int i = 0; i < 4; i++)
            {
                Point2 p = corners[i], q = corners[(i + 1) % 4];
                double len = (q - p).Length;
                if (len < 2) continue;
                Point2 d = (q - p) * (1 / len);
                var n = new Point2(-d.Y, d.X);
                int ok = 0;
                const int samples = 200;
                for (int k = 0; k < samples; k++)
                    if (f.Strength(p + d * (len * (0.03 + 0.94 * k / (samples - 1))), n) > 40) ok++;
                s[i] = (double)ok / samples;
            }
            return s;
        }

        private static Point2[] OrderCorners(Point2[] c)
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

        /// <summary>
        /// Kenar dogrusu boyunca ortadan iki yone yurur; parlaklik gecisinin surekli kesildigi
        /// yerde durur. Donen iki nokta kenarin gercekte gorunen uclaridir (p tarafi, q tarafi).
        /// Kenarin ortasi ortulu olsa da uclar gorunuyorsa kose dogru bulunur.
        /// </summary>
        private static Point2[] Extent(EdgeField f, Point2 p, Point2 q)
        {
            double len = (q - p).Length;
            if (len < 2) return null;
            Point2 d = (q - p) * (1 / len);
            var n = new Point2(-d.Y, d.X);
            Point2 mid = (p + q) * 0.5;

            // Esik: kenarin orta bolumundeki tipik gecis gucunun dortte biri
            var vals = new List<double>();
            for (double s = -0.3 * len; s <= 0.3 * len; s += 2) vals.Add(f.Strength(mid + d * s, n));
            vals.Sort();
            double thr = Math.Max(5, 0.25 * vals[vals.Count / 2]);

            const double stepPx = 2;
            const int window = 15;                       // 30 piksel boyunca
            double maxRun = 1.5 * Math.Max(f.Width, f.Height);
            Func<int, double> walk = dir =>
            {
                for (double s = 0; s < maxRun; s += stepPx)
                {
                    if (!f.Inside(mid + d * (dir * s))) return s;
                    int low = 0;
                    for (int k = 0; k < window; k++)
                        if (f.Strength(mid + d * (dir * (s + k * stepPx)), n) < thr) low++;
                    if (low >= 0.8 * window) return s;
                }
                return maxRun;
            };
            return new[] { mid - d * walk(-1), mid + d * walk(1) };
        }

        private static double? Consistency(Point2[] c, double lengthMm, double widthMm, int w, int h)
        {
            try
            {
                var cal = new TableCalibration
                {
                    TableLengthMm = lengthMm, TableWidthMm = widthMm, ImageWidth = w, ImageHeight = h,
                    CornerOrigin = c[0], CornerYEnd = c[1], CornerFar = c[2], CornerXEnd = c[3]
                };
                cal.Build();
                if (cal.Camera == null) return null;
                // Fiziksel olarak anlamsiz cozumleri ele: kamera tablanin cok yakininda / altinda olamaz
                if (cal.Camera.Height < 0.5 * Math.Min(lengthMm, widthMm) || cal.Camera.Height > 10 * Math.Max(lengthMm, widthMm)) return null;
                return cal.Camera.Consistency;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// p-q dogrultusundaki kenarin gorunen parcalarina dogru oturtur. Her ornekte dik yonde
        /// +-20 piksel icindeki en guclu gecis aranir; zayif ornekler (ortulu kisim) atilir,
        /// kalanlara yinelemeli olarak dogru uydurulur. Yeterli nokta yoksa null.
        /// </summary>
        private static double[] FitVisibleEdge(EdgeField f, Point2 p, Point2 q, int window = 20, double from = 0.02, double to = 0.98)
        {
            double len = (q - p).Length;
            if (len < 20) return null;
            Point2 d = (q - p) * (1 / len);
            var n = new Point2(-d.Y, d.X);
            const int samples = 200;
            var pts = new List<Point2>();
            var str = new List<double>();
            for (int i = 0; i < samples; i++)
            {
                Point2 c = p + d * (len * (from + (to - from) * i / (samples - 1)));
                double best = 0; Point2 at = c;
                for (int o = -window; o <= window; o++)
                {
                    Point2 s = c + n * o;
                    if (!f.Inside(s)) continue;
                    double g = f.Raw(s, n);
                    if (g > best) { best = g; at = s; }
                }
                pts.Add(at); str.Add(best);
            }
            var sorted = new List<double>(str);
            sorted.Sort();
            double thr = Math.Max(30, 0.5 * sorted[(int)(0.8 * (sorted.Count - 1))]);   // guclu orneklerin yarisi
            var keep = new List<Point2>();
            for (int i = 0; i < samples; i++) if (str[i] >= thr) keep.Add(pts[i]);

            double[] line = null;
            for (int iter = 0; iter < 4; iter++)
            {
                if (keep.Count < 0.15 * samples) return null;
                Line2D l = Cv2.FitLine(keep.Select(k => new Point2f((float)k.X, (float)k.Y)), DistanceTypes.L2, 0, 0.01, 0.01);
                line = new[] { l.Vy, -l.Vx, l.Vx * l.Y1 - l.Vy * l.X1 };
                double tol = iter < 2 ? 6 : 3;
                var next = keep.Where(k => Math.Abs(Dist(line, k)) <= tol).ToList();
                if (next.Count == keep.Count) break;
                keep = next;
            }
            return keep.Count >= 0.15 * samples ? line : null;
        }

        /// <summary>Gradyan alani; bir noktada verilen yone dik parlaklik gecisinin gucu.</summary>
        private sealed class EdgeField
        {
            private readonly float[] _gx, _gy;
            public readonly int Width, Height;

            public EdgeField(Mat frame)
            {
                Width = frame.Cols; Height = frame.Rows;
                using (var gray = new Mat())
                using (var gx = new Mat())
                using (var gy = new Mat())
                {
                    Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
                    Cv2.GaussianBlur(gray, gray, new Size(5, 5), 0);
                    Cv2.Sobel(gray, gx, MatType.CV_32F, 1, 0);
                    Cv2.Sobel(gray, gy, MatType.CV_32F, 0, 1);
                    gx.GetArray(out _gx);
                    gy.GetArray(out _gy);
                }
            }

            public bool Inside(Point2 c) { return c.X >= 0 && c.Y >= 0 && c.X < Width - 1 && c.Y < Height - 1; }

            /// <summary>Tek pikselde n yonundeki gecis gucu.</summary>
            public double Raw(Point2 c, Point2 n)
            {
                int x = (int)Math.Round(c.X), y = (int)Math.Round(c.Y);
                if (x < 0 || y < 0 || x >= Width || y >= Height) return 0;
                int idx = y * Width + x;
                return Math.Abs(_gx[idx] * n.X + _gy[idx] * n.Y);
            }

            /// <summary>Noktanin +-3 piksel cevresinde, n yonundeki en guclu gecis.</summary>
            public double Strength(Point2 c, Point2 n)
            {
                double best = 0;
                for (int o = -3; o <= 3; o++)
                {
                    int x = (int)Math.Round(c.X + n.X * o), y = (int)Math.Round(c.Y + n.Y * o);
                    if (x < 0 || y < 0 || x >= Width || y >= Height) continue;
                    int idx = y * Width + x;
                    best = Math.Max(best, Math.Abs(_gx[idx] * n.X + _gy[idx] * n.Y));
                }
                return best;
            }
        }

        private static double[] Line(Point2 p, Point2 q) { return Cross(new[] { p.X, p.Y, 1.0 }, new[] { q.X, q.Y, 1.0 }); }
        private static double[] Cross(double[] u, double[] v)
        {
            return new[] { u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0] };
        }
        private static double Dist(double[] l, Point2 p) { return (l[0] * p.X + l[1] * p.Y + l[2]) / Math.Sqrt(l[0] * l[0] + l[1] * l[1]); }

        private static Point2? Intersect(Line2D l1, Line2D l2)
        {
            double d = l1.Vx * l2.Vy - l1.Vy * l2.Vx;
            if (Math.Abs(d) < 1e-9) return null;
            double t = ((l2.X1 - l1.X1) * l2.Vy - (l2.Y1 - l1.Y1) * l2.Vx) / d;
            return new Point2(l1.X1 + t * l1.Vx, l1.Y1 + t * l1.Vy);
        }
    }
}
