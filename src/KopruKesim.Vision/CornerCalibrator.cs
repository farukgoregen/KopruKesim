using System;
using System.Collections.Generic;
using System.Linq;
using KopruKesim.Core;
using OpenCvSharp;

namespace KopruKesim.Vision
{
    /// <summary>
    /// Damasiz kamera kalibrasyonu (sabit kamera, bir kez): kullanici fotografta tablanin 4 kosesini ve ortasini
    /// isaretler; tabla olcusu bilinir. Sonuc damali kalibrasyonla ayni modeldir (LensCalibration: odak, radyal k1 k2,
    /// kameranin tablaya gore konumu); olcum yolu degismez.
    ///
    /// 5 nokta, kamera konumu + odak + bukulme (9 bilinmeyen) icin tek basina yetmez. Lens bukulmesi tablanin
    /// kenarlarindan bulunur: kenarlar gercekte duzdur, fotografta egridir. Adimlar:
    ///  1) Bukulmesiz kabul edilip 5 noktadan kamera konumu (OpenCV solvePnP + inceltme).
    ///  2) Model tabla kenarini fotografa izdusurur; kenar boyunca 70'er noktada kenara dik yonde en guclu parlaklik
    ///     gecisi aranir (arama yaricapi 30 -> 15 -> 8 -> 5 piksel daralir).
    ///  3) 5 nokta (piksel hatasi) + kenar noktalari (tabla duzlemine geri izdusurulunce kenar cizgisine uzaklik)
    ///     birlikte Levenberg-Marquardt ile cozulur; aykiri noktalar (kablo, golge, insan) yumusak kayipla bastirilir,
    ///     odak ve bukulme makul araliklarda tutulur.
    /// Optik merkez goruntu ortasinda, piksel kare kabul edilir.
    /// Koordinat: orijin sol alt kose, X uzun kenar boyunca saga, Y kisa kenar boyunca yukari (kullanici).
    /// </summary>
    public static class CornerCalibrator
    {
        /// <summary>Isaretlenecek noktalarin sirasi.</summary>
        public static readonly string[] PointNames = { "Sol alt köşe (0 noktası)", "Sağ alt köşe", "Sağ üst köşe", "Sol üst köşe", "Tablanın ortası",
            "Alt kenarın ortası", "Sağ kenarın ortası", "Üst kenarın ortası", "Sol kenarın ortası" };
        public const int MaxPoints = 9;

        public sealed class EdgePoint
        {
            /// <summary>0 alt (Y = 0), 1 sag (X = L), 2 ust (Y = W), 3 sol (X = 0).</summary>
            public int Edge;
            public Point2 Pixel;
            /// <summary>Tabla duzleminde kenar cizgisine uzaklik (mm), sonuc modeliyle.</summary>
            public double OffsetMm;
            public bool Outlier;
        }

        public sealed class Result
        {
            public LensCalibration Calibration;
            public string Error;
            public Point2[] Clicked;
            /// <summary>9 noktanin modelin bekledigi yeri (piksel) ve isaret-model farki (piksel, mm; isaretlenmeyende NaN).</summary>
            public Point2[] ModelPixels;
            public double[] PointErrorPx, PointErrorMm;
            public List<EdgePoint> Edges = new List<EdgePoint>();
            /// <summary>Kenar noktalarinin (aykirilar disinda) kenar cizgisine uzakligi: ortalama karekok ve en buyuk (mm).</summary>
            public double EdgeRmsMm, EdgeMaxMm;
            public int[] EdgeCounts = new int[4];
            /// <summary>Isaretli noktalarin TablePoints sirasindaki yeri.</summary>
            public int[] PointIds;
            /// <summary>Kayitli lens kullanildi (yalniz kameranin yeri bulundu).</summary>
            public bool UsedLens;
        }

        /// <summary>Isaretlenebilecek 9 noktanin tabla koordinati (mm): 4 kose, orta, alt/sag/ust/sol kenarin ortasi.</summary>
        public static Point2[] TablePoints(double l, double w)
        {
            return new[] { new Point2(0, 0), new Point2(l, 0), new Point2(l, w), new Point2(0, w), new Point2(l / 2, w / 2),
                           new Point2(l / 2, 0), new Point2(l, w / 2), new Point2(l / 2, w), new Point2(0, w / 2) };
        }

        /// <param name="clicked">Sol alt, sag alt, sag ust, sol ust kose (piksel) ve istege bagli tablanin ortasi.</param>
        /// <param name="lens">
        /// Ayni kameranin bilinen lensi (ornek: damali kalibrasyondan). Verilirse odak ve bukulme sabit tutulur, koselerden
        /// yalniz kameranin yeri bulunur. Yalniz kose ve kenarlardan lens tahmini tablanin ortasindaki olcegi
        /// belirleyemez (sanal denemede kenarlar kusursuz otururken ortadaki A4 %19 buyuk cikti): guclu bukulme + buyuk
        /// merkez olcegi ile zayif bukulme + kucuk olcek kenarda ayni cerceveyi verir. Lens kameranin ozelligidir;
        /// kamera ayni kaldikca bir kez ogrenilmesi yeter.
        /// </param>
        public static Result Calibrate(Mat frame, double lengthMm, double widthMm, Point2[] marked, LensCalibration lens = null)
        {
            var res = new Result { Clicked = marked };
            if (marked == null || marked.Length < 4 || marked.Length > MaxPoints || Enumerable.Range(0, 4).Any(i => !Finite(marked[i])))
            { res.Error = "Dört köşe gerekli (istenirse orta ve kenar ortaları da)."; return res; }
            // Isaretlenmemis noktalar (NaN) atlanir; ids isaretlilerin TablePoints sirasindaki yeri
            int[] ids = Enumerable.Range(0, marked.Length).Where(i => Finite(marked[i])).ToArray();
            res.PointIds = ids;
            Point2[] clicked = ids.Select(i => marked[i]).ToArray();
            int w = frame.Cols, h = frame.Rows;
            if (lens != null && (lens.ImageWidth != w || lens.ImageHeight != h)) lens = null;   // baska cozunurluk: kullanilamaz
            res.UsedLens = lens != null;
            double cx = lens != null ? lens.Cx : w / 2.0, cy = lens != null ? lens.Cy : h / 2.0, L = lengthMm, W = widthMm;
            Point2[] allTable = TablePoints(L, W);
            Point2[] table = ids.Select(i => allTable[i]).ToArray();

            float[] gx, gy;
            using (var gray = new Mat())
            using (var gxm = new Mat())
            using (var gym = new Mat())
            {
                Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
                Cv2.GaussianBlur(gray, gray, new Size(5, 5), 0);
                Cv2.Sobel(gray, gxm, MatType.CV_32F, 1, 0);
                Cv2.Sobel(gray, gym, MatType.CV_32F, 0, 1);
                gxm.GetArray(out gx); gym.GetArray(out gy);
            }

            // Parametreler: 0-2 donme, 3-5 oteleme, 6 odak, 7 k1, 8 k2, 9 k3
            // Bu cozum radyal formulu kullanir; balik gozu lensi (L + dama kalibrasyonu) burada yorumlanamaz, lens yokmus gibi
            if (lens != null && lens.Fisheye) lens = null;
            int n = clicked.Length;
            double f0 = lens != null ? lens.Fx : 0.42 * w;
            var p = new double[10];
            if (lens != null)
            {
                double[] d = lens.Distortion;
                p[7] = d.Length > 0 ? d[0] : 0; p[8] = d.Length > 1 ? d[1] : 0; p[9] = d.Length > 4 ? d[4] : 0;
            }
            // 1) Baslangic konumu: OpenCV solvePnP (lens biliniyorsa bukulmesiyle, yoksa bukulmesiz)
            using (var obj = new Mat(n, 1, MatType.CV_64FC3))
            using (var img = new Mat(n, 1, MatType.CV_64FC2))
            using (var k = new Mat(3, 3, MatType.CV_64FC1, Scalar.All(0)))
            using (var dist = new Mat(1, 5, MatType.CV_64FC1, Scalar.All(0)))
            using (var rv = new Mat())
            using (var tv = new Mat())
            {
                for (int i = 0; i < n; i++) { obj.Set(i, 0, new Vec3d(table[i].X, table[i].Y, 0)); img.Set(i, 0, new Vec2d(clicked[i].X, clicked[i].Y)); }
                k.Set(0, 0, f0); k.Set(1, 1, f0); k.Set(0, 2, cx); k.Set(1, 2, cy); k.Set(2, 2, 1.0);
                dist.Set(0, 0, p[7]); dist.Set(0, 1, p[8]); dist.Set(0, 4, p[9]);
                try { Cv2.SolvePnP(obj, img, k, dist, rv, tv); }
                catch (OpenCVException) { res.Error = "Noktalardan kamera konumu bulunamadı; köşelerin sırasını kontrol edin."; return res; }
                for (int i = 0; i < 3; i++) { p[i] = rv.Get<double>(i); p[3 + i] = tv.Get<double>(i); }
            }
            p[6] = f0;
            double mmPerPx = L / (0.8 * w);   // kenar hatasini piksel olcegine cevirmek icin (yaklasik)
            Func<double[], List<EdgePoint>, double[]> residuals = (q, edges) => Residuals(q, cx, cy, L, W, table, clicked, edges, mmPerPx);

            if (lens != null)
            {
                // Lens sabit: yalniz kameranin yeri (5 nokta + modelin izledigi kenarlar)
                var flo = new double[10]; var fhi = new double[10];
                for (int i = 0; i < 10; i++) { flo[i] = i < 6 ? double.NegativeInfinity : p[i]; fhi[i] = i < 6 ? double.PositiveInfinity : p[i]; }
                p = Lm(q => residuals(q, null), p, flo, fhi, 0);
                if (!PointsInFront(p, table, cx, cy)) { res.Error = "Noktalar tutarlı bir kamera vermiyor. Köşelerin sırasını kontrol edin: 1 sol alt, 2 sağ alt, 3 sağ üst, 4 sol üst."; return res; }
                List<EdgePoint> lensEdges = null;
                foreach (double radius in new[] { 10.0, 6, 4 })
                {
                    List<EdgePoint> edges = FindEdges(p, cx, cy, L, W, w, h, gx, gy, radius);
                    if (edges.Count < 20) break;
                    p = Lm(q => residuals(q, edges), p, flo, fhi, 2.0);
                    lensEdges = edges;
                }
                return Finish(res, p, cx, cy, L, W, w, h, table, clicked, lensEdges ?? new List<EdgePoint>(), mmPerPx);
            }

            // Odak, bukulme ve kamera yuksekligi birbirinin yerine gecebilir: "lens duz" kabuluyle birlikte cozmeye
            // baslamak yanlis cozume takiliyordu (sanal denemede odak 1105 / gercek 820, k1 +0,30 / gercek -0,31).
            // Sira: (a) kenar noktalari, isaretlenen koseler arasindaki duz cizgilerin cevresindeki genis bantta aranir
            // (kamera modeli gerekmez); (b) bukulme yalniz bu noktalari duzlestirerek bulunur (cekul yontemi); (c) bukulme
            // bilinirken 5 noktadan kamera konumu; (d) hepsi birlikte, modelin izledigi kenarlarla inceltilir.
            // Orta nokta bukulmeyi verir: varil bukulmesi koseleri merkeze ceker, merkez yerinde kalir. 5 nokta
            // (10 denklem) kamera konumu + k1 (7 bilinmeyen) icin yeter; odak f0'da, k2 = 0 tutulur. Birkac k1 baslangici,
            // en kucuk nokta hatasi secilir. Kenarlar ancak bundan sonra modelin gosterdigi yerin yaninda aranir.
            // (Modelsiz kenar arama denendi: bukuk kenarin ortasinda ahsap dokusuna/kabloya kayiyordu.)
            // Odak ile kamera yuksekligi tek fotografta neredeyse ayrilamaz: kamera tablaya dik bakarken (f, Z) yerine
            // (s f, s Z) ve k1 s^2, k2 s^4, k3 s^6 tam ayni goruntuyu verir. Tabla duzlemindeki olcum bundan etkilenmez
            // (piksel <-> tabla eslemesi aynidir), yalniz kamera yuksekligi (kalinlik duzeltmesi) degisir. Bu yuzden odak
            // serbest birakilmaz; bir izgarada denenir, her birinde bukulme genis sinirlarla cozulur, en iyi uyan secilir
            // (kamera egikse perspektif odagi belirler). Dar sinir (k1 >= -0,8) dogru cozumu engelliyordu (A4 %2 buyuk).
            // Bukulme terimleri nokta sayisina gore: 5: k1; 6-7: k1 k2; 8-9 (kenar ortalariyla): k1 k2 k3.
            bool freeK1 = n >= 5, freeK2 = n >= 6, freeK3 = n >= 8;
            double rMaxPx = clicked.Max(c => Math.Sqrt((c.X - cx) * (c.X - cx) + (c.Y - cy) * (c.Y - cy)));
            var lo1 = new double[10]; var hi1 = new double[10];
            for (int i = 0; i < 10; i++) { lo1[i] = double.NegativeInfinity; hi1[i] = double.PositiveInfinity; }
            lo1[7] = -2; hi1[7] = 1; lo1[8] = -4; hi1[8] = 4; lo1[9] = -4; hi1[9] = 4;
            if (!freeK1) lo1[7] = hi1[7] = 0;
            if (!freeK2) lo1[8] = hi1[8] = 0;
            if (!freeK3) lo1[9] = hi1[9] = 0;
            double[] focalGrid = n >= 6 ? Enumerable.Range(0, 9).Select(i => (0.30 + 0.05 * i) * w).ToArray() : new[] { f0 };
            double[] startBest = null; double startErr = double.MaxValue;
            foreach (double fs in focalGrid)
                foreach (double k1s in freeK1 ? new[] { 0.0, -0.2, -0.45 } : new[] { 0.0 })
                {
                    var q = (double[])p.Clone(); q[6] = fs; q[7] = k1s; q[8] = 0; q[9] = 0;
                    var lo0 = (double[])lo1.Clone(); var hi0 = (double[])hi1.Clone();
                    lo0[6] = hi0[6] = fs;
                    for (int i = 7; i < 10; i++) { lo0[i] = hi0[i] = q[i]; }
                    q = Lm(qq => residuals(qq, null), q, lo0, hi0, 0);   // bu odak ve bukulmeyle yalniz konum
                    lo0 = (double[])lo1.Clone(); hi0 = (double[])hi1.Clone(); lo0[6] = hi0[6] = fs;
                    q = Lm(qq => residuals(qq, null), q, lo0, hi0, 0);   // bukulmeyle birlikte
                    if (Environment.GetEnvironmentVariable("KK_DEBUG") == "1") Console.WriteLine("  aday f {0:F0}: k {1:F3} {2:F3} {3:F3} onde {4} tekyon {5}", fs, q[7], q[8], q[9], PointsInFront(q, table, cx, cy), Monotonic(q, rMaxPx));
                    if (!PointsInFront(q, table, cx, cy) || !Monotonic(q, rMaxPx)) continue;
                    double err = Enumerable.Range(0, n).Average(i => Point2.Distance(Project(q, cx, cy, table[i].X, table[i].Y), clicked[i]));
                    if (Environment.GetEnvironmentVariable("KK_DEBUG") == "1") Console.WriteLine("  baslangic f {0:F0} k1 {1:F2} -> k1 {2:F3} k2 {3:F3} k3 {4:F3}, nokta {5:F2} px", fs, k1s, q[7], q[8], q[9], err);
                    if (err < startErr - 1e-6) { startErr = err; startBest = q; }
                }
            if (startBest == null) { res.Error = "Noktalar tutarlı bir kamera vermiyor. Köşelerin sırasını kontrol edin: 1 sol alt, 2 sağ alt, 3 sağ üst, 4 sol üst."; return res; }
            p = startBest;
            List<EdgePoint> found;
            lo1[6] = hi1[6] = p[6];   // secilen odak sabit
            // Kenarlar bukulme bilgisi tasir: nokta az olsa da kenar asamasinda k1 k2 cozulur (k3 yalniz kenar ortalariyla)
            lo1[7] = -2; hi1[7] = 1; lo1[8] = -4; hi1[8] = 4;
            p = Refine(p, residuals, cx, cy, L, W, w, h, gx, gy, new[] { 12.0, 8, 5 }, table, lo1, hi1, rMaxPx, out found);
            if (p == null) { res.Error = "Noktalar tutarlı bir kamera vermiyor. Köşelerin sırasını kontrol edin: 1 sol alt, 2 sağ alt, 3 sağ üst, 4 sol üst."; return res; }
            if (found == null || found.Count < 40) { res.Error = "Tablanın kenarları fotoğrafta yeterince bulunamadı; köşeleri kontrol edin ya da kenarların göründüğü bir fotoğraf kullanın."; return res; }
            return Finish(res, p, cx, cy, L, W, w, h, table, clicked, found, mmPerPx);
        }

        private static Result Finish(Result res, double[] p, double cx, double cy, double L, double W, int w, int h,
            Point2[] table, Point2[] clicked, List<EdgePoint> found, double mmPerPx)
        {
            int n = clicked.Length;
            var cal = ToCalibration(p, cx, cy, L, W, w, h);
            // Tum 9 nokta icin modelin bekledigi yer; hatalar yalniz isaretli noktalar icin (digerleri NaN)
            res.ModelPixels = TablePoints(L, W).Select(t => Project(p, cx, cy, t.X, t.Y)).ToArray();
            Point2[] back = LensCalibrator.ImageToTable(cal, clicked);
            res.PointErrorPx = Enumerable.Repeat(double.NaN, MaxPoints).ToArray();
            res.PointErrorMm = Enumerable.Repeat(double.NaN, MaxPoints).ToArray();
            for (int i = 0; i < n; i++)
            {
                res.PointErrorPx[res.PointIds[i]] = Point2.Distance(Project(p, cx, cy, table[i].X, table[i].Y), clicked[i]);
                res.PointErrorMm[res.PointIds[i]] = Point2.Distance(back[i], table[i]);
            }
            Point2[] edgeBack = LensCalibrator.ImageToTable(cal, found.Select(e => e.Pixel).ToArray());
            for (int i = 0; i < found.Count; i++)
            {
                found[i].OffsetMm = EdgeOffset(found[i].Edge, edgeBack[i], L, W);
                found[i].Outlier = Math.Abs(found[i].OffsetMm) > 6 * mmPerPx;   // kenar degil (kablo, golge, insan)
                res.EdgeCounts[found[i].Edge]++;
            }
            res.Edges = found;
            double[] good = found.Where(e => !e.Outlier).Select(e => e.OffsetMm).ToArray();
            res.EdgeRmsMm = good.Length > 0 ? Math.Sqrt(good.Average(v => v * v)) : double.NaN;
            res.EdgeMaxMm = good.Length > 0 ? good.Max(v => Math.Abs(v)) : double.NaN;
            double[] epx = res.PointErrorPx.Where(v => !double.IsNaN(v)).ToArray(), emm = res.PointErrorMm.Where(v => !double.IsNaN(v)).ToArray();
            cal.RmsPx = Math.Sqrt(epx.Average(v => v * v));
            cal.MeanMm = emm.Average();
            cal.MaxMm = emm.Max();
            cal.Points = n + good.Length;
            res.Calibration = cal;
            return res;
        }

        /// <summary>Baslangictan: once yalniz konum (odak, bukulme sabit), sonra kenarlarla birlikte her sey.</summary>
        private static double[] Refine(double[] start, Func<double[], List<EdgePoint>, double[]> residuals, double cx, double cy,
            double l, double wmm, int width, int height, float[] gx, float[] gy, double[] radii, Point2[] table, double[] lo, double[] hi, double rMaxPx, out List<EdgePoint> found)
        {
            found = null;
            double[] p = start;
            if (!PointsInFront(p, table, cx, cy)) return null;
            foreach (double radius in radii)
            {
                List<EdgePoint> edges = FindEdges(p, cx, cy, l, wmm, width, height, gx, gy, radius);
                if (edges.Count < 20) return null;
                p = Lm(q => residuals(q, edges), p, lo, hi, 2.0);
                found = edges;
                if (!PointsInFront(p, table, cx, cy) || !Monotonic(p, rMaxPx)) return null;
            }
            return p;
        }

        public static LensCalibration ToCalibration(double[] p, double cx, double cy, double l, double w, int width, int height)
        {
            return new LensCalibration
            {
                TableLengthMm = l, TableWidthMm = w, ImageWidth = width, ImageHeight = height,
                Fx = p[6], Fy = p[6], Cx = cx, Cy = cy, Distortion = new[] { p[7], p[8], 0, 0, p.Length > 9 ? p[9] : 0 },
                Rvec = new[] { p[0], p[1], p[2] }, Tvec = new[] { p[3], p[4], p[5] }, TagType = "damasiz"
            };
        }

        private static bool Finite(Point2 q) => !double.IsNaN(q.X) && !double.IsNaN(q.Y) && !double.IsInfinity(q.X) && !double.IsInfinity(q.Y);

        private static double EdgeOffset(int edge, Point2 t, double l, double w)
        {
            switch (edge)
            {
                case 0: return t.Y;
                case 1: return t.X - l;
                case 2: return t.Y - w;
                default: return t.X;
            }
        }

        private static double[] Residuals(double[] p, double cx, double cy, double l, double w, Point2[] table, Point2[] clicked, List<EdgePoint> edges, double mmPerPx)
        {
            int np = clicked.Length;
            int n = 2 * np + (edges == null ? 0 : edges.Count);
            var r = new double[n];
            for (int i = 0; i < np; i++)
            {
                Point2 q = Project(p, cx, cy, table[i].X, table[i].Y);
                r[2 * i] = q.X - clicked[i].X; r[2 * i + 1] = q.Y - clicked[i].Y;
            }
            if (edges != null)
                for (int i = 0; i < edges.Count; i++)
                {
                    Point2 t = BackProject(p, cx, cy, edges[i].Pixel);
                    double off = EdgeOffset(edges[i].Edge, t, l, w);
                    r[2 * np + i] = double.IsNaN(off) ? 1e3 : off / mmPerPx;
                }
            return r;
        }

        private static double[] Rot(double[] p)
        {
            double rx = p[0], ry = p[1], rz = p[2], th = Math.Sqrt(rx * rx + ry * ry + rz * rz);
            if (th < 1e-12) return new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
            double kx = rx / th, ky = ry / th, kz = rz / th, c = Math.Cos(th), s = Math.Sin(th), v = 1 - c;
            return new[]
            {
                c + kx * kx * v, kx * ky * v - kz * s, kx * kz * v + ky * s,
                ky * kx * v + kz * s, c + ky * ky * v, ky * kz * v - kx * s,
                kz * kx * v - ky * s, kz * ky * v + kx * s, c + kz * kz * v
            };
        }

        /// <summary>Tabla noktasi (Z = 0) -> piksel: x_kamera = R X + t, radyal bukulme, odak.</summary>
        public static Point2 Project(double[] p, double cx, double cy, double X, double Y)
        {
            double[] R = Rot(p);
            double xc = R[0] * X + R[1] * Y + p[3], yc = R[3] * X + R[4] * Y + p[4], zc = R[6] * X + R[7] * Y + p[5];
            double k3 = p.Length > 9 ? p[9] : 0;
            double x = xc / zc, y = yc / zc, r2 = x * x + y * y, rad = 1 + r2 * (p[7] + r2 * (p[8] + r2 * k3));
            return new Point2(p[6] * x * rad + cx, p[6] * y * rad + cy);
        }

        /// <summary>
        /// Bukulme polinomu isaretli en uzak noktaya (biraz otesine) kadar tek yonlu mu (yaricap arttikca bukuk yaricap da artmali). Genis
        /// sinirlarla cozulen k1 k2 k3 kenarda geri katlanan, fiziksel olmayan bir lens verebilir.
        /// </summary>
        private static bool Monotonic(double[] p, double rmaxPx)
        {
            double rmax = rmaxPx / p[6];
            double k3 = p.Length > 9 ? p[9] : 0, prev = 0;
            // Bukuk yaricap rd(ru) en uzak noktaya ulasana dek artmali (genis acili lens goruntu kosesinde katlanabilir)
            for (double ru = 0.01; ru < 4; ru += 0.01)
            {
                double r2 = ru * ru, rd = ru * (1 + r2 * (p[7] + r2 * (p[8] + r2 * k3)));
                if (rd <= prev) return false;
                if (rd >= rmax) return true;
                prev = rd;
            }
            return false;
        }

        private static bool PointsInFront(double[] p, Point2[] table, double cx, double cy)
        {
            double[] R = Rot(p);
            return table.All(t => R[6] * t.X + R[7] * t.Y + p[5] > 0);
        }

        /// <summary>Piksel -> tabla duzlemi (mm): radyal bukulme tersine cozulur, isin Z = 0 duzlemiyle kesisimi.</summary>
        private static Point2 BackProject(double[] p, double cx, double cy, Point2 px)
        {
            double xd = (px.X - cx) / p[6], yd = (px.Y - cy) / p[6], rd = Math.Sqrt(xd * xd + yd * yd);
            double ru = rd;
            for (int it = 0; it < 30 && rd > 1e-12; it++)
            {
                double k3 = p.Length > 9 ? p[9] : 0;
                double r2 = ru * ru, f = ru * (1 + r2 * (p[7] + r2 * (p[8] + r2 * k3))) - rd, df = 1 + 3 * p[7] * r2 + 5 * p[8] * r2 * r2 + 7 * k3 * r2 * r2 * r2;
                if (Math.Abs(df) < 1e-9) break;
                double step = f / df; ru -= step;
                if (Math.Abs(step) < 1e-13) break;
            }
            double sc = rd > 1e-12 ? ru / rd : 1, x = xd * sc, y = yd * sc;
            double[] R = Rot(p);
            double dx = R[0] * x + R[3] * y + R[6], dy = R[1] * x + R[4] * y + R[7], dz = R[2] * x + R[5] * y + R[8];
            double Cx = -(R[0] * p[3] + R[3] * p[4] + R[6] * p[5]), Cy = -(R[1] * p[3] + R[4] * p[4] + R[7] * p[5]), Cz = -(R[2] * p[3] + R[5] * p[4] + R[8] * p[5]);
            if (Math.Abs(dz) < 1e-12) return new Point2(double.NaN, double.NaN);
            double s = -Cz / dz;
            return new Point2(Cx + s * dx, Cy + s * dy);
        }

        private static List<EdgePoint> FindEdges(double[] p, double cx, double cy, double l, double w, int width, int height, float[] gx, float[] gy, double radius)
        {
            var found = new List<EdgePoint>();
            var segs = new[] { new[] { new Point2(0, 0), new Point2(l, 0) }, new[] { new Point2(l, 0), new Point2(l, w) },
                               new[] { new Point2(l, w), new Point2(0, w) }, new[] { new Point2(0, w), new Point2(0, 0) } };
            for (int e = 0; e < 4; e++)
            {
                Point2 a = segs[e][0], b = segs[e][1];
                for (int i = 0; i < 70; i++)
                {
                    double t = 0.04 + 0.92 * i / 69.0;
                    Point2 tp = a + (b - a) * t, tp2 = a + (b - a) * (t + 0.002);
                    Point2 u = Project(p, cx, cy, tp.X, tp.Y), u2 = Project(p, cx, cy, tp2.X, tp2.Y);
                    Point2 d = u2 - u; double len = d.Length;
                    if (len < 1e-9) continue;
                    var nrm = new Point2(-d.Y / len, d.X / len);
                    double best = 0; Point2 at = u; bool any = false;
                    for (double o = -radius; o <= radius; o += 0.5)
                    {
                        double x = u.X + nrm.X * o, y = u.Y + nrm.Y * o;
                        int xi = (int)Math.Round(x), yi = (int)Math.Round(y);
                        if (xi < 2 || yi < 2 || xi >= width - 2 || yi >= height - 2) continue;
                        int idx = yi * width + xi;
                        double s = Math.Abs(gx[idx] * nrm.X + gy[idx] * nrm.Y);
                        if (s > best) { best = s; at = new Point2(x, y); any = true; }
                    }
                    if (any && best > 40) found.Add(new EdgePoint { Edge = e, Pixel = at });
                }
            }
            return found;
        }

        /// <summary>
        /// Sinirli Levenberg-Marquardt, sayisal Jacobian. robust > 0 ise yumusak L1 (Huber benzeri) agirliklar: |r| bu
        /// olcegin cok uzerindeki artiklar (aykiri kenar noktalari) cozumu cekmez.
        /// </summary>
        internal static double[] Lm(Func<double[], double[]> f, double[] p0, double[] lo, double[] hi, double robust)
        {
            int m = p0.Length;
            var p = (double[])p0.Clone();
            Clamp(p, lo, hi);
            double lambda = 1e-3;
            Func<double[], double> cost = r => robust > 0 ? r.Sum(v => 2 * robust * robust * (Math.Sqrt(1 + v * v / (robust * robust)) - 1)) : r.Sum(v => v * v);
            double[] r0 = f(p);
            double c0 = cost(r0);
            for (int iter = 0; iter < 80; iter++)
            {
                int n = r0.Length;
                var wgt = new double[n];
                for (int i = 0; i < n; i++) wgt[i] = robust > 0 ? 1 / Math.Sqrt(1 + r0[i] * r0[i] / (robust * robust)) : 1;
                var J = new double[n, m];
                for (int j = 0; j < m; j++)
                {
                    if (lo[j] == hi[j]) continue;
                    double hstep = 1e-6 * Math.Max(1, Math.Abs(p[j]));
                    var pp = (double[])p.Clone(); var pm = (double[])p.Clone();
                    pp[j] += hstep; pm[j] -= hstep;
                    double[] rp = f(pp), rm = f(pm);
                    for (int i = 0; i < n; i++) J[i, j] = (rp[i] - rm[i]) / (2 * hstep);
                }
                var A = new double[m, m]; var g = new double[m];
                for (int i = 0; i < n; i++)
                    for (int a = 0; a < m; a++)
                    {
                        if (J[i, a] == 0) continue;
                        g[a] += wgt[i] * J[i, a] * r0[i];
                        for (int b = 0; b < m; b++) A[a, b] += wgt[i] * J[i, a] * J[i, b];
                    }
                bool improved = false;
                for (int tries = 0; tries < 10; tries++)
                {
                    var M = new double[m, m]; var rhs = new double[m];
                    for (int a = 0; a < m; a++)
                    {
                        for (int b = 0; b < m; b++) M[a, b] = A[a, b];
                        M[a, a] += lambda * (A[a, a] + 1e-9) + (lo[a] == hi[a] ? 1 : 0);
                        rhs[a] = -g[a];
                    }
                    double[] step = Solve(M, rhs);
                    if (step == null) { lambda *= 10; continue; }
                    var pn = new double[m];
                    for (int a = 0; a < m; a++) pn[a] = lo[a] == hi[a] ? p[a] : p[a] + step[a];
                    Clamp(pn, lo, hi);
                    double[] rn = f(pn);
                    double cn = cost(rn);
                    if (cn < c0 && !double.IsNaN(cn))
                    {
                        double rel = (c0 - cn) / Math.Max(c0, 1e-12);
                        p = pn; r0 = rn; c0 = cn; lambda = Math.Max(lambda / 3, 1e-9); improved = true;
                        if (rel < 1e-10) return p;
                        break;
                    }
                    lambda *= 10;
                }
                if (!improved) break;
            }
            return p;
        }

        private static void Clamp(double[] p, double[] lo, double[] hi)
        {
            for (int i = 0; i < p.Length; i++) p[i] = Math.Max(lo[i], Math.Min(hi[i], p[i]));
        }

        private static double[] Solve(double[,] A, double[] b)
        {
            int n = b.Length;
            var M = (double[,])A.Clone(); var x = (double[])b.Clone();
            for (int c = 0; c < n; c++)
            {
                int piv = c;
                for (int r = c + 1; r < n; r++) if (Math.Abs(M[r, c]) > Math.Abs(M[piv, c])) piv = r;
                if (Math.Abs(M[piv, c]) < 1e-15) return null;
                if (piv != c)
                {
                    for (int k = 0; k < n; k++) { double t = M[c, k]; M[c, k] = M[piv, k]; M[piv, k] = t; }
                    double tb = x[c]; x[c] = x[piv]; x[piv] = tb;
                }
                for (int r = c + 1; r < n; r++)
                {
                    double fct = M[r, c] / M[c, c];
                    if (fct == 0) continue;
                    for (int k = c; k < n; k++) M[r, k] -= fct * M[c, k];
                    x[r] -= fct * x[c];
                }
            }
            for (int c = n - 1; c >= 0; c--)
            {
                double s = x[c];
                for (int k = c + 1; k < n; k++) s -= M[c, k] * x[k];
                x[c] = s / M[c, c];
            }
            return x;
        }
    }
}
