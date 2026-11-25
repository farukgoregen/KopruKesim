using System;
using System.Collections.Generic;
using System.Linq;
using KopruKesim.Core;
using OpenCvSharp;

namespace KopruKesim.Vision
{
    /// <summary>
    /// Tablanin dort kosesine oturan turkuaz L parcalarini (3D baski) bos tabla fotografinda bulur. Tabla kosesi L'nin dis
    /// kosesidir: seritle olcum de L'ler yerindeyken dis koseler arasindan yapilir. L'nin dis kosesi pahli oldugu icin kose
    /// noktasi dogrudan aranmaz; iki kolun dis kenari ayri ayri dogru olarak oturtulur, kesisimi kosedir.
    ///
    /// Yalniz renk kullanilir (parcanin kalinligi ve kol olculeri gerekmez): her pikselde "turkuazlik" puani (ton,
    /// doygunluk, parlaklik), kolun dis kenarina dik profillerde disaridan iceri puanin 0,5'i gectigi yer (piksel alti)
    /// kenar noktasidir; noktalara aykirilari atarak dogru oturtulur.
    /// </summary>
    public static class LCornerFinder
    {
        public sealed class CornerFit
        {
            /// <summary>Kose (modelin mm'si, z = 0).</summary>
            public Point2 Point;
            /// <summary>Uzun kenar (X boyunca) ve kisa kenar (Y boyunca) dogrularina uyan / alinan profil sayisi.</summary>
            public int InliersX, SamplesX, InliersY, SamplesY;
            /// <summary>Uyan noktalarin dogrudan sapmasinin karekok ortalamasi (mm).</summary>
            public double RmsX, RmsY;
            /// <summary>Iki dis kenar arasindaki aci (derece; kamera modeli ve tabla dogruysa 90'a yakin).</summary>
            public double AngleDeg;
            /// <summary>Kollarin fotografta gorunen boyu (mm), bilgi icin.</summary>
            public double ArmXMm, ArmYMm;
            /// <summary>Oturtulan dogrular: uzun kenar y = AX + BX x, kisa kenar x = AY + BY y (modelin mm'si).</summary>
            public double AX, BX, AY, BY;
            /// <summary>Profillerde bulunan dis kenar noktalari (inceleme icin).</summary>
            public List<Point2> PointsX, PointsY;
        }

        public sealed class Result
        {
            /// <summary>Tabla koseleri (modelin mm'si, z = 0): sol ust, sag ust, sol alt, sag alt. Hata varsa null.</summary>
            public Point2[] Corners;
            public string Error;
            public CornerFit[] Fits = new CornerFit[4];
        }

        public static readonly string[] CornerNames = { "sol üst", "sağ üst", "sol alt", "sağ alt" };

        /// <summary>L parcasinin tabla kenarinin disina inen duvarinin kalinligi (mm; kullanici, 2026-11-25).</summary>
        public const double WallMm = 5;

        /// <summary>Turkuaz puani (0..1): ton 80-94 tam, 76/98'de sifir (zemin ~100); doygunluk 60 -> 140; parlaklik 40 -> 70.</summary>
        public static Mat Score(Mat frame)
        {
            using (var hsv = new Mat())
            {
                Cv2.CvtColor(frame, hsv, ColorConversionCodes.BGR2HSV);
                hsv.GetArray(out Vec3b[] px);
                var s = new float[px.Length];
                for (int i = 0; i < px.Length; i++)
                {
                    int h = px[i].Item0, sat = px[i].Item1, v = px[i].Item2;
                    double hw = h < 76 || h > 98 ? 0 : h < 80 ? (h - 76) / 4.0 : h > 94 ? (98 - h) / 4.0 : 1;
                    double sw = Math.Min(1, Math.Max(0, (sat - 60) / 80.0));
                    double vw = Math.Min(1, Math.Max(0, (v - 40) / 30.0));
                    s[i] = (float)(hw * sw * vw);
                }
                var m = new Mat(frame.Rows, frame.Cols, MatType.CV_32FC1);
                m.SetArray(s);
                return m;
            }
        }

        /// <param name="lens">Duzeltmesiz kamera modeli (WithoutCorrection).</param>
        /// <param name="expected">Koselerin modeldeki tahmini (sol ust, sag ust, sol alt, sag alt).</param>
        /// <param name="searchMm">Tahmini koseden en fazla bu kadar uzakta aranir.</param>
        public static Result Find(Mat frame, LensCalibration lens, Point2[] expected, double searchMm = 300)
        {
            var res = new Result();
            using (Mat score = Score(frame))
            using (var mask = new Mat())
            using (var labels = new Mat())
            using (var stats = new Mat())
            using (var cents = new Mat())
            {
                Cv2.Threshold(score, mask, 0.5, 255, ThresholdTypes.Binary);
                mask.ConvertTo(mask, MatType.CV_8UC1);
                // Ince cizgileri (yesil serit kenari, zemindeki kirintilar) kopar
                using (var k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5)))
                    Cv2.MorphologyEx(mask, mask, MorphTypes.Open, k);
                int n = Cv2.ConnectedComponentsWithStats(mask, labels, stats, cents);
                score.GetArray(out float[] sc);
                labels.GetArray(out int[] lab);
                int w = frame.Cols, h = frame.Rows;
                Point2[] expImg = LensCalibrator.TableToImage(lens, expected);
                for (int c = 0; c < 4; c++)
                {
                    CornerFit fit = FitCorner(c, lens, expected[c], expImg[c], searchMm, n, stats, lab, sc, w, h, out string err);
                    if (fit == null) { res.Error = "Tablanın " + CornerNames[c] + " köşesindeki L parçası: " + err; return res; }
                    res.Fits[c] = fit;
                }
            }
            res.Corners = res.Fits.Select(f => f.Point).ToArray();
            return res;
        }

        static CornerFit FitCorner(int c, LensCalibration lens, Point2 exp, Point2 expImg, double searchMm, int n, Mat stats, int[] lab, float[] sc,
            int w, int h, out string err)
        {
            err = null;
            // Dis taraflar: sol koselerde X kucuk, sag koselerde X buyuk; ust koselerde Y buyuk, alt koselerde Y kucuk
            bool right = c == 1 || c == 3, top = c == 0 || c == 1;
            // Aday parcalar: tahmini koseye yakin, yeterince buyuk. Mesafe tabla mm'sinde (parcanin piksellerinden).
            int best = -1; double bestArea = 0;
            Point2[] bestPts = null;
            for (int i = 1; i < n; i++)
            {
                int area = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
                if (area < 400) continue;
                int bx = stats.At<int>(i, 0), by = stats.At<int>(i, 1), bw = stats.At<int>(i, 2), bh = stats.At<int>(i, 3);
                // Kaba on eleme: kutunun tahmini koseye goruntude uzakligi
                double dx = Math.Max(0, Math.Max(bx - expImg.X, expImg.X - (bx + bw))), dy = Math.Max(0, Math.Max(by - expImg.Y, expImg.Y - (by + bh)));
                if (Math.Sqrt(dx * dx + dy * dy) > searchMm) continue;
                // Parcanin pikselleri (seyrek) tabla mm'sine; arama alanindakiler
                int step = Math.Max(1, (int)Math.Sqrt(area / 1500.0));
                var px = new List<Point2>();
                for (int y = by; y < by + bh; y += step)
                    for (int x = bx; x < bx + bw; x += step)
                        if (lab[y * w + x] == i) px.Add(new Point2(x, y));
                Point2[] tp = LensCalibrator.ImageToTable(lens, px.ToArray()).Where(p => Point2.Distance(p, exp) <= searchMm).ToArray();
                double inArea = tp.Length * step * step;
                if (inArea > bestArea) { bestArea = inArea; best = i; bestPts = tp; }
            }
            if (best < 0 || bestPts.Length < 30) { err = "turkuaz parça bulunamadı (beklenen yerin " + searchMm.ToString("F0") + " mm çevresinde)."; return null; }

            double minX = bestPts.Min(p => p.X), maxX = bestPts.Max(p => p.X), minY = bestPts.Min(p => p.Y), maxY = bestPts.Max(p => p.Y);
            double spanX = maxX - minX, spanY = maxY - minY;
            if (spanX < 40 || spanY < 40) { err = string.Format(System.Globalization.CultureInfo.InvariantCulture, "parça L gibi görünmüyor ({0:F0} × {1:F0} mm).", spanX, spanY); return null; }
            double outX = right ? maxX : minX, outY = top ? maxY : minY;
            // Kollarin uzak ucu (koseden uzak)
            double farX = right ? minX : maxX, farY = top ? minY : maxY;

            // Uzun kenar boyunca kol (dis kenar Y ~ outY): profiller koseden uzak kisimda; kose ve pah bolgesi (%35) atlanir.
            // Once maskenin dis sinirindan kaba dogru, sonra profiller yalniz bu dogrunun +-10 mm'sinde (disaridaki
            // mavimsi zemin ya da yesil serit kenar sanilmasin).
            double fx0 = Lerp(outX, farX, 0.35), fx1 = Lerp(outX, farX, 0.95), fy0 = Lerp(outY, farY, 0.35), fy1 = Lerp(outY, farY, 0.95);
            if (!CoarseLine(bestPts, true, fx0, fx1, top ? 1 : -1, out double cax, out double cbx) ||
                !CoarseLine(bestPts, false, fy0, fy1, right ? 1 : -1, out double cay, out double cby))
            { err = "parçanın dış kenarları maskede seçilemedi."; return null; }
            List<Point2> ptsX = EdgePoints(lens, sc, w, h, true, cax, cbx, top ? -1 : 1, fx0, fx1, out int samplesX);
            List<Point2> ptsY = EdgePoints(lens, sc, w, h, false, cay, cby, right ? -1 : 1, fy0, fy1, out int samplesY);
            // Uzun kenar: y = a + b x; kisa kenar: x = a + b y
            if (!FitLine(ptsX.Select(p => new Point2(p.X, p.Y)).ToList(), out double ax, out double bxs, out int inX, out double rmsX))
            { err = "uzun kenar boyunca dış kenar düz bir çizgi olarak bulunamadı (" + ptsX.Count + "/" + samplesX + " profil)."; return null; }
            if (!FitLine(ptsY.Select(p => new Point2(p.Y, p.X)).ToList(), out double ay, out double bys, out int inY, out double rmsY))
            { err = "kısa kenar boyunca dış kenar düz bir çizgi olarak bulunamadı (" + ptsY.Count + "/" + samplesY + " profil)."; return null; }
            if (inX < Math.Max(8, 0.5 * samplesX) || inY < Math.Max(8, 0.5 * samplesY))
            { err = string.Format("dış kenarlarda yeterli nokta yok (uzun {0}/{1}, kısa {2}/{3}).", inX, samplesX, inY, samplesY); return null; }
            // Kesisim: y = ax + bx x, x = ay + by y
            double xc = (ay + bys * ax) / (1 - bys * bxs), yc = ax + bxs * xc;
            var corner = new Point2(xc, yc);
            if ((double.IsNaN(xc) || double.IsInfinity(xc)) || (double.IsNaN(yc) || double.IsInfinity(yc)) || Point2.Distance(corner, exp) > searchMm)
            { err = "bulunan köşe beklenen yerden çok uzak."; return null; }
            // Uzun kenar yonu (1, bx), kisa kenar yonu (by, 1)
            double cosA = (bys + bxs) / (Math.Sqrt(1 + bxs * bxs) * Math.Sqrt(1 + bys * bys));
            double angle = Math.Acos(Math.Max(-1, Math.Min(1, cosA))) * 180 / Math.PI;
            return new CornerFit
            {
                Point = corner, InliersX = inX, SamplesX = samplesX, InliersY = inY, SamplesY = samplesY, RmsX = rmsX, RmsY = rmsY,
                AngleDeg = angle, ArmXMm = spanX, ArmYMm = spanY, AX = ax, BX = bxs, AY = ay, BY = bys, PointsX = ptsX, PointsY = ptsY
            };
        }

        static double Lerp(double a, double b, double t) => a + (b - a) * t;

        /// <summary>
        /// Tabla koseleri (modelin mm'si): L'nin iki dis kenar dogrusu tablanin icine dogru wallMm kaydirilip kesistirilir.
        /// L parcasinin dis duvari (5 mm) tablanin kenarindan disarida durur; dis kenar dogrusu tablanin kenari degildir.
        /// </summary>
        public static Point2[] InsetCorners(Result r, double wallMm)
        {
            var res = new Point2[4];
            for (int c = 0; c < 4; c++)
            {
                CornerFit f = r.Fits[c];
                bool right = c == 1 || c == 3, top = c == 0 || c == 1;
                // Uzun kenar y = AX + BX x: ust kosede iceri -y, alt kosede +y; kisa kenar x = AY + BY y: solda +x, sagda -x
                double ax = f.AX + (top ? -1 : 1) * wallMm * Math.Sqrt(1 + f.BX * f.BX);
                double ay = f.AY + (right ? -1 : 1) * wallMm * Math.Sqrt(1 + f.BY * f.BY);
                double xc = (ay + f.BY * ax) / (1 - f.BY * f.BX), yc = ax + f.BX * xc;
                res[c] = new Point2(xc, yc);
            }
            return res;
        }

        /// <summary>
        /// Inceleme goruntusu: her kosenin cevresi (tile x tile piksel) zoom kat buyutulur; profillerde bulunan kenar
        /// noktalari (mor), oturtulan iki dis kenar dogrusu (sari, ince), duvar kalinligi kadar iceride tabla kenari (yesil)
        /// ve tabla kosesi (kirmizi halka) cizilir. Sira: ust satir sol ust, sag ust; alt satir sol alt, sag alt.
        /// </summary>
        public static Mat DrawCorners(Mat frame, LensCalibration lens, Result r, double wallMm = 0, int tile = 260, int zoom = 3)
        {
            var tiles = new Mat[4];
            Point2[] inner = InsetCorners(r, wallMm);
            try
            {
                for (int i = 0; i < 4; i++)
                {
                    CornerFit f = r.Fits[i];
                    Point2 c = f.Point;
                    bool right = i == 1 || i == 3, top = i < 2;
                    double sx = right ? -1 : 1, sy = top ? -1 : 1;
                    // Dogrular koseden 30 mm disaridan kolun 300 mm icine kadar (tabla mm'sinde, fotografta egri olarak)
                    Point2[] lx = Enumerable.Range(-30, 331).Select(t => { double x = c.X + sx * t; return new Point2(x, f.AX + f.BX * x); }).ToArray();
                    Point2[] ly = Enumerable.Range(-30, 331).Select(t => { double y = c.Y + sy * t; return new Point2(f.AY + f.BY * y, y); }).ToArray();
                    // Tabla kenari: dis kenar dogrulari duvar kalinligi kadar iceri (InsetCorners ile ayni kaydirma)
                    double axIn = f.AX + (top ? -1 : 1) * wallMm * Math.Sqrt(1 + f.BX * f.BX), ayIn = f.AY + (right ? -1 : 1) * wallMm * Math.Sqrt(1 + f.BY * f.BY);
                    Point2 ci = inner[i];
                    Point2[] gx = Enumerable.Range(0, 301).Select(t => { double x = ci.X + sx * t; return new Point2(x, axIn + f.BX * x); }).ToArray();
                    Point2[] gy = Enumerable.Range(0, 301).Select(t => { double y = ci.Y + sy * t; return new Point2(ayIn + f.BY * y, y); }).ToArray();
                    Point2[] px = LensCalibrator.TableToImage(lens, lx), py = LensCalibrator.TableToImage(lens, ly);
                    Point2[] qx = LensCalibrator.TableToImage(lens, gx), qy = LensCalibrator.TableToImage(lens, gy);
                    Point2[] pts = LensCalibrator.TableToImage(lens, f.PointsX.Concat(f.PointsY).ToArray());
                    Point2 cp = LensCalibrator.TableToImage(lens, new[] { ci })[0];
                    int x0 = Math.Max(0, Math.Min(frame.Cols - tile, (int)cp.X - tile / 2)), y0 = Math.Max(0, Math.Min(frame.Rows - tile, (int)cp.Y - tile / 2));
                    var t2 = new Mat();
                    using (var crop = new Mat(frame, new Rect(x0, y0, tile, tile)))
                        Cv2.Resize(crop, t2, new Size(tile * zoom, tile * zoom), 0, 0, InterpolationFlags.Cubic);
                    Point Z(Point2 p) => new Point((p.X - x0 + 0.5) * zoom - 0.5, (p.Y - y0 + 0.5) * zoom - 0.5);
                    foreach (Point2 p in pts) Cv2.Circle(t2, Z(p), 2, new Scalar(255, 0, 255), -1, LineTypes.AntiAlias);
                    foreach (Point2[] line in new[] { px, py })
                        for (int k = 1; k < line.Length; k++) Cv2.Line(t2, Z(line[k - 1]), Z(line[k]), new Scalar(0, 230, 255), 1, LineTypes.AntiAlias);
                    if (wallMm > 0)
                        foreach (Point2[] line in new[] { qx, qy })
                            for (int k = 1; k < line.Length; k++) Cv2.Line(t2, Z(line[k - 1]), Z(line[k]), new Scalar(0, 220, 0), 2, LineTypes.AntiAlias);
                    Cv2.Circle(t2, Z(cp), 7, new Scalar(0, 0, 255), 2, LineTypes.AntiAlias);
                    Cv2.PutText(t2, CornerNames[i].Replace('ü', 'u').Replace('ğ', 'g'), new Point(10, 30), HersheyFonts.HersheySimplex, 0.9, Scalar.White, 3, LineTypes.AntiAlias);
                    Cv2.PutText(t2, CornerNames[i].Replace('ü', 'u').Replace('ğ', 'g'), new Point(10, 30), HersheyFonts.HersheySimplex, 0.9, Scalar.Black, 1, LineTypes.AntiAlias);
                    tiles[i] = t2;
                }
                using (var topRow = new Mat())
                using (var bottomRow = new Mat())
                {
                    Cv2.HConcat(new[] { tiles[0], tiles[1] }, topRow);
                    Cv2.HConcat(new[] { tiles[2], tiles[3] }, bottomRow);
                    var all = new Mat();
                    Cv2.VConcat(new[] { topRow, bottomRow }, all);
                    return all;
                }
            }
            finally { foreach (Mat t in tiles) t?.Dispose(); }
        }

        /// <summary>
        /// Maskenin kol boyunca dis siniri: 'from'-'to' arasinda 3 mm'lik dilimlerde en distaki nokta, bunlara dogru
        /// (alongX: y = a + b x; degilse x = a + b y). outward: dis yon (+1 buyuk deger, -1 kucuk deger).
        /// </summary>
        static bool CoarseLine(Point2[] pts, bool alongX, double from, double to, int outward, out double a, out double b)
        {
            double lo = Math.Min(from, to), hi = Math.Max(from, to);
            var best = new Dictionary<int, double>();
            foreach (Point2 p in pts)
            {
                double t = alongX ? p.X : p.Y, v = alongX ? p.Y : p.X;
                if (t < lo || t > hi) continue;
                int bin = (int)Math.Floor(t / 3);
                double cur;
                if (!best.TryGetValue(bin, out cur) || v * outward > cur * outward) best[bin] = v;
            }
            var uv = best.Select(kv => new Point2(kv.Key * 3 + 1.5, kv.Value)).ToList();
            int n; double rms;
            return FitLine(uv, out a, out b, out n, out rms) && n >= 5;
        }

        /// <summary>
        /// Dis kenar noktalari: kenar boyunca 'from'-'to' arasinda 2 mm arayla, kenara dik profil (disaridan +-25 mm,
        /// 0,25 mm adim). Disaridan ilk kez puan 0,5'i gectigi ve sonraki 4 mm boyunca 0,5 ustunde kaldigi yer kenardir.
        /// </summary>
        static List<Point2> EdgePoints(LensCalibration lens, float[] sc, int w, int h, bool alongX, double ca, double cb, int inward,
            double from, double to, out int samples)
        {
            const double half = 10, ds = 0.25, along = 2, hold = 4;
            int m = (int)(2 * half / ds) + 1, holdN = (int)(hold / ds);
            int count = Math.Max(2, (int)(Math.Abs(to - from) / along) + 1);
            samples = count;
            var tp = new Point2[count * m];
            for (int i = 0; i < count; i++)
            {
                double t = from + (to - from) * i / (count - 1.0), outCoord = ca + cb * t;
                for (int j = 0; j < m; j++)
                {
                    double o = outCoord - inward * half + inward * j * ds;   // disaridan iceri
                    tp[i * m + j] = alongX ? new Point2(t, o) : new Point2(o, t);
                }
            }
            Point2[] ip = LensCalibrator.TableToImage(lens, tp);
            var res = new List<Point2>();
            var prof = new double[m];
            for (int i = 0; i < count; i++)
            {
                bool ok = true;
                for (int j = 0; j < m && ok; j++) { prof[j] = Sample(sc, w, h, ip[i * m + j]); if (double.IsNaN(prof[j])) ok = false; }
                if (!ok || prof[0] >= 0.5) continue;
                for (int j = 1; j + holdN < m; j++)
                {
                    if (prof[j] < 0.5 || prof[j - 1] >= 0.5) continue;
                    bool stays = true;
                    for (int q = j; q <= j + holdN; q++) if (prof[q] < 0.5) { stays = false; break; }
                    if (!stays) continue;
                    double f = (0.5 - prof[j - 1]) / (prof[j] - prof[j - 1]);
                    double t = from + (to - from) * i / (count - 1.0), outCoord = ca + cb * t;
                    double o = outCoord - inward * half + inward * (j - 1 + f) * ds;
                    res.Add(alongX ? new Point2(t, o) : new Point2(o, t));
                    break;
                }
            }
            return res;
        }

        static double Sample(float[] sc, int w, int h, Point2 p)
        {
            if ((double.IsNaN(p.X) || double.IsInfinity(p.X)) || (double.IsNaN(p.Y) || double.IsInfinity(p.Y)) || p.X < 0 || p.Y < 0 || p.X > w - 2 || p.Y > h - 2) return double.NaN;
            int x = (int)p.X, y = (int)p.Y;
            double fx = p.X - x, fy = p.Y - y;
            int i = y * w + x;
            return (sc[i] * (1 - fx) + sc[i + 1] * fx) * (1 - fy) + (sc[i + w] * (1 - fx) + sc[i + w + 1] * fx) * fy;
        }

        /// <summary>v = a + b u (noktalar (u, v)); en kucuk kareler, 2,5 x MAD disindakiler atilarak yinelenir.</summary>
        static bool FitLine(List<Point2> pts, out double a, out double b, out int inliers, out double rms)
        {
            a = b = rms = double.NaN; inliers = 0;
            if (pts.Count < 5) return false;
            var keep = Enumerable.Repeat(true, pts.Count).ToArray();
            for (int it = 0; it < 8; it++)
            {
                double su = 0, sv = 0, suu = 0, suv = 0; int k = 0;
                for (int i = 0; i < pts.Count; i++) if (keep[i]) { su += pts[i].X; sv += pts[i].Y; suu += pts[i].X * pts[i].X; suv += pts[i].X * pts[i].Y; k++; }
                if (k < 5) return false;
                double den = k * suu - su * su;
                if (Math.Abs(den) < 1e-9) return false;
                b = (k * suv - su * sv) / den; a = (sv - b * su) / k;
                double aa = a, bb = b;
                double[] r = pts.Select(p => Math.Abs(p.Y - (aa + bb * p.X))).ToArray();
                double mad = r.Where((x, i) => keep[i]).OrderBy(x => x).ElementAt(k / 2);
                double lim = Math.Max(0.5, 2.5 * 1.4826 * mad);
                bool changed = false;
                for (int i = 0; i < pts.Count; i++) { bool nk = r[i] <= lim; if (nk != keep[i]) { keep[i] = nk; changed = true; } }
                inliers = keep.Count(x => x);
                rms = Math.Sqrt(r.Where((x, i) => keep[i]).Select(x => x * x).DefaultIfEmpty(0).Average());
                if (!changed) break;
            }
            return inliers >= 5;
        }
    }
}
