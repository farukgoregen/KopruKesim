using System;
using System.Collections.Generic;
using System.Linq;
using KopruKesim.Core;
using OpenCvSharp;

namespace KopruKesim.Vision
{
    /// <summary>
    /// Fotograftaki dama tahtasi desenlerini (kalibrasyon etiketleri) bulur. Her desenin merkezi, ic koselere
    /// oturtulan izgaranin orta noktasidir (piksel alti hassas, perspektif hesaba katilir). Hangi desenin tablanin
    /// neresinde oldugunu LensCalibrator fotograftaki yerlerinden eslestirir.
    ///
    /// Arama iki adimlidir. Tum fotografta tek seferde aramak guvenilir degil: uzaktaki etiket egik
    /// bakista cok basik gorunur (kare yuksekligi ~10 piksel), yanlis oturur ya da hic bulunmaz.
    /// 1) Fotograf ust uste binen karolara bolunur (birkac olcekte), her karoda desen aranir: kaba yerler.
    /// 2) Her kaba yer kirpilip buyutulur, desen yeniden aranir ve izgaranin duzgunlugu denetlenir.
    /// </summary>
    public static class CheckerFinder
    {
        private const int Tile = 800;                 // karo boyu (piksel, o olcekte)
        private const int RoughMaxPx = 2400;          // kaba aramada fotografin uzun kenari en cok bu kadar
        private const double TargetSquarePx = 32;     // ikinci adimda kare kenari en az bu kadar piksel olsun
        private const double MaxResidual = 0.25;      // izgaradan en buyuk sapma (ortalama kare kenarina oranla)

        private static bool Debug { get { return Environment.GetEnvironmentVariable("KK_DEBUG") == "1"; } }

        private sealed class Board
        {
            public Point2[] Corners;    // ic koseler, tam cozunurluk
            public Point2 Center;       // izgaranin ortasi
            public double Residual;     // izgaradan en buyuk sapma / kare kenari
            public double Step;         // en kisa izgara adimi (piksel)
            public Rect Box;
        }

        /// <summary>Fotograftaki butun desenler (numarasiz): merkez ve ic koseler. Kamera kalibrasyonu kullanir.</summary>
        public static List<KeyValuePair<Point2, Point2[]>> FindAll(Mat frame, int innerCols, int innerRows, int expectedCount = 0)
        {
            using (var gray = new Mat())
            {
                if (frame.Channels() == 1) frame.CopyTo(gray); else Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
                return FindBoards(gray, innerCols, innerRows, expectedCount).Select(b => new KeyValuePair<Point2, Point2[]>(b.Center, b.Corners)).ToList();
            }
        }

        private static List<Board> FindBoards(Mat gray, int cols, int rows, int expectedCount)
        {
            var size = new Size(cols, rows);
            // 1) Kaba yerler: olcek piramidi + ust uste binen karolar. Bir olcekte karoya sigmayan buyuk etiket
            //    bir sonraki (kucultulmus) olcekte sigar. Kaba arama icin tam cozunurluk gerekmez (yavas, baski
            //    cizgileri gibi ince ayrintilar yaniltir): uzun kenar en cok RoughMaxPx olacak olcekten baslanir.
            // 2) Her kaba yer kirpilip yeniden aranir (Refine).
            // Dort etiket cikmazsa ayni tarama dikey yonu iki kat cozunurlukte yinelenir: alcak kameradan uzaktaki
            // etiketin kareleri dikeyde ~3 kat basik gorunur; yatayda okunur boydayken dikeyde kaybolur.
            var rough = new List<Rect>();
            var found = new List<Board>();
            double k0 = 1;
            while (Math.Max(gray.Cols, gray.Rows) * k0 > RoughMaxPx) k0 *= 0.5;
            foreach (double stretch in new[] { 1.0, 2.0 })
            {
                if (stretch > 1 && found.Count >= 4) break;
                var fresh = new List<Rect>();
                for (double k = k0; ; k *= 0.5)
                {
                    double ky = k * stretch;
                    if (ky <= 1) ScanLevel(gray, size, k, ky, rough, fresh);
                    if (gray.Cols * k <= Tile && gray.Rows * k <= Tile) break;
                }
                foreach (Rect r in fresh)
                {
                    Board b = Refine(gray, r, size);
                    if (Debug) Console.WriteLine("dama: kaba yer {0},{1} {2}x{3} -> {4}", r.X, r.Y, r.Width, r.Height,
                        b == null ? "atildi" : string.Format("merkez {0:F1},{1:F1} sapma {2:F3} adim {3:F1}", b.Center.X, b.Center.Y, b.Residual, b.Step));
                    if (b == null) continue;
                    int same = found.FindIndex(o => Point2.Distance(o.Center, b.Center) < 2 * Math.Max(o.Step, b.Step));
                    if (same < 0) found.Add(b);
                    else if (b.Residual < found[same].Residual) found[same] = b;
                }
            }
            if (found.Count < expectedCount) FindMissingBoards(gray, size, expectedCount, found);
            return found;
        }

        private static void FindMissingBoards(Mat gray, Size pattern, int expectedCount, List<Board> boards)
        {
            int width = Math.Max(1, gray.Cols / 3);
            int height = Math.Max(1, gray.Rows / 3);
            foreach (int magnification in new[] { 1, 2 })
            foreach (int y in Starts(gray.Rows, height))
            foreach (int x in Starts(gray.Cols, width))
            {
                var area = new Rect(x, y, width, height);
                using (var crop = new Mat(gray, area))
                using (var image = new Mat())
                {
                    double scale = Math.Min(magnification, 1400.0 / Math.Max(width, height));
                    Cv2.Resize(crop, image, new Size(), scale, scale, scale < 1 ? InterpolationFlags.Area : InterpolationFlags.Cubic);
                    if (!Cv2.FindChessboardCornersSB(image, pattern, out Point2f[] corners, ChessboardFlags.Exhaustive))
                        continue;

                    double sx = image.Cols / (double)width, sy = image.Rows / (double)height;
                    var points = corners.Select(p => new Point2((p.X + 0.5) / sx - 0.5 + x, (p.Y + 0.5) / sy - 0.5 + y)).ToArray();
                    Board candidate = Check(points, pattern);
                    if (candidate == null || boards.Any(b => Point2.Distance(b.Center, candidate.Center) < 2 * Math.Max(b.Step, candidate.Step)))
                        continue;
                    boards.Add(Refine(gray, candidate.Box, pattern) ?? candidate);
                    if (boards.Count == expectedCount) return;
                }
            }
        }

        /// <summary>Goruntuyu (k, ky) olcegine getirip karolarda desen arar; yeni kaba yerleri ekler.</summary>
        private static void ScanLevel(Mat gray, Size size, double k, double ky, List<Rect> all, List<Rect> fresh)
        {
            Mat level = gray;
            if (k < 1 || ky < 1) { level = new Mat(); Cv2.Resize(gray, level, new Size(), k, ky, InterpolationFlags.Area); }
            try
            {
                foreach (Rect t in Tiles(level.Cols, level.Rows))
                {
                    // Bir karoda birden cok etiket olabilir (ornek: dikey fotografta ust iki etiket yan yana):
                    // dedektor birini dondurur; bulunan boyanip ayni karo yeniden aranir.
                    using (var crop = new Mat(level, t).Clone())
                    {
                        double fill = Cv2.Mean(crop).Val0;
                        for (int pass = 0; pass < 4; pass++)
                        {
                            Point2f[] c;
                            // Ilk arama ayrintili; sonrakiler (boyanmis karoda kalan etiket) hizli ayarla
                            if (!Cv2.FindChessboardCornersSB(crop, size, out c, pass == 0 ? ChessboardFlags.Exhaustive : ChessboardFlags.None) || c == null || c.Length != size.Width * size.Height) break;
                            double x0 = (c.Min(p => p.X) + t.X) / k, x1 = (c.Max(p => p.X) + t.X) / k;
                            double y0 = (c.Min(p => p.Y) + t.Y) / ky, y1 = (c.Max(p => p.Y) + t.Y) / ky;
                            var box = new Rect((int)x0, (int)y0, Math.Max(1, (int)(x1 - x0)), Math.Max(1, (int)(y1 - y0)));
                            if (!all.Any(r => SameSpot(r, box))) { all.Add(box); fresh.Add(box); }
                            // Bulunan deseni dis kareleriyle birlikte kapat (ic koselerin zarfi, bir kare payla)
                            Point[] hull = Cv2.ConvexHull(c).Select(p => new Point((int)p.X, (int)p.Y)).ToArray();
                            double mx = hull.Average(p => (double)p.X), my = hull.Average(p => (double)p.Y);
                            double grow = 1.0 + 2.5 / Math.Min(size.Width, size.Height);
                            Point[] big = hull.Select(p => new Point((int)(mx + (p.X - mx) * grow), (int)(my + (p.Y - my) * grow))).ToArray();
                            Cv2.FillConvexPoly(crop, big, Scalar.All(fill));
                        }
                    }
                }
            }
            finally { if (!ReferenceEquals(level, gray)) level.Dispose(); }
        }

        private static IEnumerable<Rect> Tiles(int w, int h)
        {
            int tw = Math.Min(Tile, w), th = Math.Min(Tile, h);
            foreach (int y in Starts(h, th))
                foreach (int x in Starts(w, tw))
                    yield return new Rect(x, y, tw, th);
        }

        /// <summary>Yarim karo adimla baslangiclar; sonuncusu kenara dayanir.</summary>
        private static IEnumerable<int> Starts(int length, int tile)
        {
            if (length <= tile) { yield return 0; yield break; }
            int step = tile / 2;
            for (int p = 0; p + tile < length; p += step) yield return p;
            yield return length - tile;
        }

        private static bool SameSpot(Rect a, Rect b)
        {
            double dx = (a.X + a.Width / 2.0) - (b.X + b.Width / 2.0), dy = (a.Y + a.Height / 2.0) - (b.Y + b.Height / 2.0);
            double reach = 0.5 * Math.Max(Math.Max(a.Width, a.Height), Math.Max(b.Width, b.Height));
            return Math.Sqrt(dx * dx + dy * dy) < reach;
        }

        /// <summary>Kaba yerin cevresini kirpar, kareler yeterince buyuk gorunecek kadar buyutup deseni yeniden arar.</summary>
        private static Board Refine(Mat gray, Rect rough, Size size)
        {
            Board b = RefineIn(gray, rough, size, 1.0);
            // Etiket tabla kenarindaysa beyaz payi koyu zemine deger; genis kirpmada dedektor bu siniri fazladan bir
            // kare sirasi sanip izgarayi kaydirabilir (sapma ~0,2). Daha dar kirpmayla yeniden aranir.
            // 90 derece cevrik konmus etikette ic koseler sutun x satir yerine satir x sutun dizilir; dedektor bu
            // durumda istenen boyutu kenardaki noktalarla doldurup kaymis bir izgara verebilir. Cevrik boyut da denenir.
            var swapped = new Size(size.Height, size.Width);
            if (b == null || b.Residual > 0.08)
            {
                foreach (var t in new[] { RefineIn(gray, rough, swapped, 1.0), RefineIn(gray, b != null ? b.Box : rough, size, 0.55) })
                {
                    if (Debug) Console.WriteLine("  dama yeniden: {0}", t == null ? "bulunamadi" : "sapma " + t.Residual.ToString("F3"));
                    if (t != null && (b == null || t.Residual < b.Residual)) b = t;
                }
            }
            return b;
        }

        private static Board RefineIn(Mat gray, Rect rough, Size size, double padFactor)
        {
            // Etiketin tamami (dis kareler + beyaz pay) kirpmaya girsin
            int padX = (int)(rough.Width * padFactor) + (padFactor >= 1 ? 20 : 4), padY = (int)(rough.Height * padFactor) + (padFactor >= 1 ? 20 : 4);
            int x0 = Math.Max(0, rough.X - padX), y0 = Math.Max(0, rough.Y - padY);
            int x1 = Math.Min(gray.Cols, rough.X + rough.Width + padX), y1 = Math.Min(gray.Rows, rough.Y + rough.Height + padY);
            var area = new Rect(x0, y0, x1 - x0, y1 - y0);
            // Kaba adim: kare kenari yaklasik kac piksel (egik bakista bir yon basik). Iri kareler kucultulur,
            // basik kareler buyutulur: desen hep benzer boyda aranir.
            double stepX = rough.Width / (double)Math.Max(1, Math.Max(size.Width, size.Height) - 1);
            double stepY = rough.Height / (double)Math.Max(1, Math.Max(size.Width, size.Height) - 1);
            double fx = Clamp(TargetSquarePx / Math.Max(1, stepX), 0.25, 6), fy = Clamp(TargetSquarePx / Math.Max(1, stepY), 0.25, 6);
            double f = Math.Max(fx, fy);
            // 2x: tabla kenarinda koyu zemine degen, egik gorunen etikette dedektor kucuk olcekte kenar noktalarini
            // ic kose sanabiliyor; buyutulunce dogru izgarayi buluyor (sanal iki fotografli deneme, 2026-11-20).
            var tries = new[] { new[] { f, f }, new[] { fx, fy }, new[] { 1.0, 1.0 }, new[] { Math.Min(6, f * 1.5), Math.Min(6, f * 1.5) }, new[] { Math.Min(6, f * 2), Math.Min(6, f * 2) } };

            Board best = null;
            using (var crop = new Mat(gray, area))
            {
                foreach (double[] s in tries)
                {
                    // Cok buyuk kirpmayi buyutme (yakin, zaten iri etiket)
                    if (area.Width * s[0] > 2400 || area.Height * s[1] > 2400) continue;
                    using (var big = new Mat())
                    {
                        if (s[0] == 1.0 && s[1] == 1.0) crop.CopyTo(big);
                        else Cv2.Resize(crop, big, new Size(), s[0], s[1], s[0] < 1 && s[1] < 1 ? InterpolationFlags.Area : InterpolationFlags.Cubic);
                        Point2f[] c;
                        bool ok = Cv2.FindChessboardCornersSB(big, size, out c, ChessboardFlags.Exhaustive | ChessboardFlags.Accuracy) && c != null && c.Length == size.Width * size.Height;
                        if (!ok) { if (Debug) Console.WriteLine("  dama inceltme {0:F2}x{1:F2}: bulunamadi", s[0], s[1]); continue; }
                        // Buyutulmus kirpmadan tam cozunurluge (piksel merkezi kabulu)
                        Point2[] pts = c.Select(p => new Point2((p.X + 0.5) / s[0] - 0.5 + area.X, (p.Y + 0.5) / s[1] - 0.5 + area.Y)).ToArray();
                        Board b = Check(pts, size);
                        if (Debug) Console.WriteLine("  dama inceltme {0:F2}x{1:F2}: {2}", s[0], s[1], b == null ? "izgara bozuk" : "sapma " + b.Residual.ToString("F3"));
                        if (b != null && (best == null || b.Residual < best.Residual)) best = b;
                        if (best != null && best.Residual < MaxResidual / 3) break;   // yeterince iyi
                    }
                }
            }
            return best;
        }

        /// <summary>Koseler duzgun bir izgara mi? Izgaradan goruntuye homografi oturtulur; en buyuk sapma kare kenarina oranlanir.</summary>
        private static Board Check(Point2[] pts, Size size)
        {
            var grid = new List<Point2>();
            for (int j = 0; j < size.Height; j++)
                for (int i = 0; i < size.Width; i++) grid.Add(new Point2(i, j));
            Homography h;
            try { h = Homography.FromPoints(grid, pts); }
            catch (ArgumentException) { return null; }
            double worst = 0, step = double.MaxValue;
            for (int n = 0; n < pts.Length; n++)
            {
                worst = Math.Max(worst, Point2.Distance(h.Transform(grid[n]), pts[n]));
                int i = n % size.Width, j = n / size.Width;
                if (i + 1 < size.Width) step = Math.Min(step, Point2.Distance(pts[n], pts[n + 1]));
                if (j + 1 < size.Height) step = Math.Min(step, Point2.Distance(pts[n], pts[n + size.Width]));
            }
            // Sapma ortalama adima oranlanir (egik bakista en kisa adim cok kucuk olabilir). Kenardan sarkan,
            // hafif kivrik etikette ~0,15 olur; yanlis oturmus izgarada cok daha buyuk.
            double mean = 0; int cnt = 0;
            for (int n = 0; n < pts.Length; n++)
            {
                int i = n % size.Width, j = n / size.Width;
                if (i + 1 < size.Width) { mean += Point2.Distance(pts[n], pts[n + 1]); cnt++; }
                if (j + 1 < size.Height) { mean += Point2.Distance(pts[n], pts[n + size.Width]); cnt++; }
            }
            mean /= Math.Max(1, cnt);
            if (Debug && (step < 2 || worst / mean > MaxResidual)) Console.WriteLine("  izgara: en buyuk sapma {0:F1} px, ortalama adim {1:F1} px", worst, mean);
            if (step < 2 || worst / mean > MaxResidual) return null;
            double minX = pts.Min(p => p.X), minY = pts.Min(p => p.Y);
            return new Board
            {
                Corners = pts,
                Center = h.Transform(new Point2((size.Width - 1) / 2.0, (size.Height - 1) / 2.0)),
                Residual = worst / mean,
                Step = step,
                Box = new Rect((int)minX, (int)minY, (int)(pts.Max(p => p.X) - minX) + 1, (int)(pts.Max(p => p.Y) - minY) + 1)
            };
        }

        private static double Clamp(double v, double lo, double hi) { return v < lo ? lo : v > hi ? hi : v; }
    }
}
