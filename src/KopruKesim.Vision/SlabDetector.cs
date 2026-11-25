using System;
using System.Collections.Generic;
using System.Linq;
using KopruKesim.Core;
using OpenCvSharp;

namespace KopruKesim.Vision
{
    public sealed class DetectorOptions
    {
        /// <summary>
        /// Duzeltilmis (ustten) goruntunun olcegi, piksel/mm. 0 = otomatik: kamera goruntusundeki en ince
        /// ayrinti korunur (tablanin kameraya en yakin kenarindaki olcek), en fazla MaxRectifiedPixels.
        /// </summary>
        public double PxPerMm = 0;
        /// <summary>Otomatik olcekte ustten goruntunun en fazla piksel sayisi.</summary>
        public double MaxRectifiedPixels = 16e6;
        /// <summary>Tabla rengine gore renk (Lab a/b) farki esigi.</summary>
        public int ColorThreshold = 10;
        /// <summary>Referans goruntusu yokken: parlaklik (Lab L, 0-255) bunun altindaysa koyu tas sayilir.</summary>
        public int DarkThreshold = 60;
        /// <summary>
        /// Referans yokken: tablanin ortanca parlakligindan bu kadar farkli bolgeler de parca sayilir.
        /// 0 = kapali. Yalnizca tek renkli, duzgun aydinlatilmis tablada acilmali (ahsap tablada damarlar karisir).
        /// </summary>
        public int NoReferenceBrightnessDiff = 0;
        /// <summary>Bos tabla referansi varken: parlaklik farki esigi.</summary>
        public int BrightnessDiffThreshold = 32;
        /// <summary>
        /// Bos tabla referansi varken: parlaklik (yari agirlik) + renk toplam farki esigi. 0 = kapali.
        /// Acik renk parca acik gri eskimiş citanin ustune denk gelirse fark bu duzeye inebiliyor
        /// (2026-11-22: beyaz kagit bazi citalarda birlesik farki ~10-14 veriyordu, maske orada deliniyordu).
        /// </summary>
        public double CombinedDiffThreshold = 12;
        /// <summary>
        /// Bos tabla referansi varken parcanin tablaya dusen golgesi parca sayilmaz: bos tabladakinden karanlik (oran
        /// ShadowMinRatio..ShadowMaxRatio) ama rengi ayni (Lab a/b parlaklikla olceklenince farki ShadowColorTolerance
        /// icinde) pikseller. 2026-11-21: siyah plakanin ~10 mm golgesi kameraya bakan kenara eklenip kenari egiyordu.
        /// </summary>
        public bool SuppressShadows = true;
        public double ShadowMinRatio = 0.40, ShadowMaxRatio = 0.92, ShadowColorTolerance = 6;
        /// <summary>Bundan kucuk lekeler / bosluklar temizlenir (mm).</summary>
        public double CleanupMm = 25;
        /// <summary>
        /// Parcanin maskesi acik cita ustu acik parca gibi kontrasti dusuk bolgelerde delinip parca
        /// ikiye bolunebiliyor; bu kadar genislikteki parca ici yariklar/bosluklar kapatilip birlestirilir
        /// (yalniz Close; Open uygulanmaz ki kucuk parcalar silinmesin). 0 = kapali.
        /// </summary>
        public double FillGapMm = 80;
        /// <summary>Tabla kenarindan bu kadar iceride kalan bolge taranir (mm).</summary>
        public double BorderMarginMm = 40;
        /// <summary>
        /// TableMeasure.RunWithLens: olcum alani tablanin kenarindan bu kadar disari tasar (mm). Cita uclari duz degil:
        /// ortadaki citalar kose citalarindan gecen kenar cizgisinin 15-20 mm disina uzanabiliyor; kenara dayanan parcanin
        /// kenari o citalarin ustunde de aranabilmeli. Kenar seridi (BorderMarginMm) yine gercek tabla kenarindan olculur.
        /// </summary>
        public double OuterMarginMm = 30;
        /// <summary>Bundan kucuk nesneler mermer sayilmaz (mm2).</summary>
        public double MinAreaMm2 = 200 * 200;
        public LocatorOptions Locator = new LocatorOptions();
        /// <summary>Guvenilir sayilmak icin en fazla kose sayisi.</summary>
        public int MaxVertices = 8;
        /// <summary>Siluet alaninin disbukey zarf alanina orani en az bu olmali (L sekli ~0.75-0.9).</summary>
        public double MinSolidity = 0.75;
        /// <summary>Kontur noktalarinin en az bu orani cokgen kenarlarina yakin olmali.</summary>
        public double MinEdgeFit = 0.85;
        /// <summary>Kenara yakinlik toleransi (mm), kalinlik buna eklenir.</summary>
        public double FitToleranceMm = 15;
        /// <summary>Kenar tabanli (duz kenar) bulucu nasil kullanilsin.</summary>
        public EdgeMode EdgeMode = EdgeMode.Fallback;
        public EdgeRectOptions Edges = new EdgeRectOptions();
        /// <summary>
        /// Dort koseli parcalarin kenarlari orijinal fotografta yeniden oturtulur (kenar basina cok nokta, piksel alti,
        /// aykiri ayiklama). Ustten goruntudeki maske kaba (temizleme, olcek); bu adim kenari gercek gecise koyar.
        /// </summary>
        public bool RefineEdges = true;
        /// <summary>Kenar oturtmada noktanin kenara dik yonde aranacagi yaricap (mm).</summary>
        public double RefineSearchMm = 6;
        public Action<byte[]> CaptureTraceMask;
        /// <summary>Kenar basina nokta sayisi.</summary>
        public int RefinePointsPerEdge = 15;
        /// <summary>
        /// Kalinlik varken kameraya bakan kenar da oturtulur; orada en guclu degil en distaki guclu gecis (siluet: yan
        /// yuzun tablaya degdigi cizgi) alinir. Kapaliyken o kenar kaba maskeden kalir (2026-11-21: ust kenar maskeden
        /// 0,3 derece egik gelip plakanin sol ve sag kenarini 6-9 mm farkli olcturuyordu).
        /// </summary>
        public bool RefineFacingEdges = true;
        /// <summary>
        /// Guvenilir cikmayan leke bu caplarla (mm, sirayla) acilip bolunmeye calisilir: birlesik parcalar ve
        /// kenar gurultusuyle birlesmis plakalar ayrilir. Bos dizi = kapali. Plakanin kisa kenari en buyuk captan
        /// genis olmali (daha dar parca bolme sirasinda silinir; ilk denemede guvenilir cikan parcaya dokunulmaz).
        /// </summary>
        public double[] SplitOpeningMm = { 80, 160, 250 };
    }

    /// <summary>Kenar tabanli bulucunun renk yontemiyle birlesimi.</summary>
    public enum EdgeMode
    {
        /// <summary>Kullanilmaz.</summary>
        Off,
        /// <summary>Ayni yerde guvenilir renk sonucu yoksa kenar sonucu kullanilir.</summary>
        Fallback,
        /// <summary>Duz kenarli parca bulunduysa renk sonucunun yerine gecer.</summary>
        Prefer
    }

    /// <summary>
    /// Kamera goruntusunden tabla uzerindeki mermerleri bulur.
    /// Tabla tek bir tabla rengiyle (SetTableColor / SetTableColorAuto) tanitilir; tabladan farkli
    /// gorunen bolgeler mermer adayidir.
    /// </summary>
    public sealed class SlabDetector : IDisposable
    {
        private readonly TableCalibration _cal;
        private readonly DetectorOptions _opt;
        private readonly Mat _warp;
        private readonly Size _rectSize;
        /// <summary>Ustten goruntunun kullanilan olcegi (piksel/mm).</summary>
        public readonly double Scale;
        private Mat _referenceLab;
        private bool _hasRealReference;

        public SlabDetector(TableCalibration calibration, DetectorOptions options)
        {
            _cal = calibration;
            _opt = options ?? new DetectorOptions();
            Scale = _opt.PxPerMm > 0 ? _opt.PxPerMm : AutoScale(_cal, _opt.MaxRectifiedPixels);
            double[] h = _cal.ImageToRectified(Scale).ToArray();
            _warp = new Mat(3, 3, MatType.CV_64FC1);
            for (int i = 0; i < 9; i++) _warp.Set<double>(i / 3, i % 3, h[i]);
            _rectSize = new Size((int)Math.Round(_cal.TableLengthMm * Scale), (int)Math.Round(_cal.TableWidthMm * Scale));
        }

        /// <summary>
        /// Kamera goruntusundeki en ince olcegi (piksel/mm) bulur: tabla kenarlarinin goruntudeki
        /// boyu / gercek boyu; en buyugu alinir. Ustten goruntu bunu korursa hicbir ayrinti kaybolmaz.
        /// </summary>
        public static double AutoScale(TableCalibration cal, double maxPixels)
        {
            Point2[] c = { cal.CornerOrigin, cal.CornerYEnd, cal.CornerFar, cal.CornerXEnd };
            double[] mm = { cal.TableWidthMm, cal.TableLengthMm, cal.TableWidthMm, cal.TableLengthMm };
            double best = 0;
            for (int i = 0; i < 4; i++) best = Math.Max(best, Point2.Distance(c[i], c[(i + 1) % 4]) / mm[i]);
            double cap = Math.Sqrt(maxPixels / (cal.TableLengthMm * cal.TableWidthMm));
            return Math.Max(0.25, Math.Min(best, cap));
        }

        /// <summary>Kamera goruntusunu yatay, olcekli tabla gorunumune cevirir (orijin sol alt).</summary>
        public Mat Rectify(Mat frame)
        {
            var rect = new Mat();
            Cv2.WarpPerspective(frame, rect, _warp, _rectSize);
            return rect;
        }

        /// <summary>
        /// Bos tablanin goruntusunu (bu dedektorle ayni kalibrasyonda: ayni sabit kamera) referans yapar. Olcumde
        /// yalniz bos tabladan farkli yerler parca sayilir: ahsaptaki lekeler, cita aralari, delikler karismaz.
        /// Isik belirgin degisirse yenilenmelidir. (2026-11-20 kalibrasyonlu sabit kamera icin geri getirildi.)
        /// </summary>
        public void SetReference(Mat emptyTableFrame)
        {
            if (_referenceLab != null) _referenceLab.Dispose();
            _referenceLab = ToLab(emptyTableFrame);
            _hasRealReference = true;
            // Bos tablanin genel rengi ve o renkte olmayan yerleri (gri citalar, lekeler): olcumde "simdi tabla renginde
            // degil ama bos tablada tabla rengindeydi" olan yerler de parca sayilir. Gri tas, gri eskimis citanin
            // ustunde bos tabladan az farklidir ama turuncu ahsaptan renkce ayrilir.
            double[] median = MedianLab(_referenceLab);
            _tableColorLab?.Dispose(); _emptyOffColor?.Dispose();
            _tableColorLab = new Mat(_referenceLab.Size(), MatType.CV_8UC3, new Scalar(median[0], median[1], median[2]));
            _emptyOffColor = OffColorMask(_referenceLab, _tableColorLab);
            int r = Math.Max(1, (int)Math.Round(10 * Scale));   // bos tabladaki lekenin cevresi de (10 mm)
            using (var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * r + 1, 2 * r + 1)))
                Cv2.Dilate(_emptyOffColor, _emptyOffColor, kernel);
        }

        private Mat _tableColorLab, _emptyOffColor;

        /// <summary>Tabla renginden (Lab a/b) farkli ya da koyu pikseller.</summary>
        private Mat OffColorMask(Mat lab, Mat tableColorLab)
        {
            var mask = new Mat();
            using (var diff = new Mat())
            using (var tmp = new Mat())
            {
                Cv2.Absdiff(lab, tableColorLab, diff);
                Mat[] d = Cv2.Split(diff);
                Mat[] l = Cv2.Split(lab);
                try
                {
                    Cv2.Threshold(d[1], mask, _opt.ColorThreshold, 255, ThresholdTypes.Binary);
                    Cv2.Threshold(d[2], tmp, _opt.ColorThreshold, 255, ThresholdTypes.Binary);
                    Cv2.BitwiseOr(mask, tmp, mask);
                    Cv2.Threshold(l[0], tmp, _opt.DarkThreshold, 255, ThresholdTypes.BinaryInv);
                    Cv2.BitwiseOr(mask, tmp, mask);
                }
                finally { foreach (Mat m in d) m.Dispose(); foreach (Mat m in l) m.Dispose(); }
            }
            return mask;
        }

        private static double[] MedianLab(Mat lab)
        {
            Mat[] ch = Cv2.Split(lab);
            var median = new double[3];
            try
            {
                for (int i = 0; i < 3; i++)
                    using (var hist = new Mat())
                    {
                        Cv2.CalcHist(new[] { ch[i] }, new[] { 0 }, null, hist, 1, new[] { 256 }, new[] { new Rangef(0, 256) });
                        double half = 0.5 * lab.Rows * lab.Cols, sum = 0;
                        for (int v = 0; v < 256; v++) { sum += hist.Get<float>(v); if (sum >= half) { median[i] = v; break; } }
                    }
            }
            finally { foreach (Mat m in ch) m.Dispose(); }
            return median;
        }

        /// <summary>
        /// Referans yerine tek bir tabla rengi kullanir: verilen goruntude, bos oldugu bilinen
        /// tabla bolgesinin (mm) ortalama rengini olcer.
        /// </summary>
        public void SetTableColor(Mat frame, double minXmm, double minYmm, double maxXmm, double maxYmm)
        {
            using (Mat lab = ToLab(frame))
            {
                double s = Scale;
                var roi = new Rect((int)(minXmm * s), (int)((_cal.TableWidthMm - maxYmm) * s), (int)((maxXmm - minXmm) * s), (int)((maxYmm - minYmm) * s));
                using (var part = new Mat(lab, roi))
                {
                    Scalar mean = Cv2.Mean(part);
                    if (_referenceLab != null) _referenceLab.Dispose();
                    _referenceLab = new Mat(lab.Size(), MatType.CV_8UC3, new Scalar(Math.Round(mean.Val0), Math.Round(mean.Val1), Math.Round(mean.Val2)));
                    _hasRealReference = false;
                }
            }
        }

        /// <summary>
        /// Referans yerine tek bir tabla rengi kullanir: tum tablanin ortanca (medyan) rengini alir.
        /// Tablanin yaridan fazlasi bos gorunuyorsa calisir; bos bolge bilmek gerekmez.
        /// </summary>
        public void SetTableColorAuto(Mat frame)
        {
            using (Mat lab = ToLab(frame))
            {
                Mat[] ch = Cv2.Split(lab);
                var median = new double[3];
                try
                {
                    for (int i = 0; i < 3; i++)
                    {
                        using (var hist = new Mat())
                        {
                            Cv2.CalcHist(new[] { ch[i] }, new[] { 0 }, null, hist, 1, new[] { 256 }, new[] { new Rangef(0, 256) });
                            double half = 0.5 * lab.Rows * lab.Cols, sum = 0;
                            for (int v = 0; v < 256; v++)
                            {
                                sum += hist.Get<float>(v);
                                if (sum >= half) { median[i] = v; break; }
                            }
                        }
                    }
                }
                finally { foreach (Mat m in ch) m.Dispose(); }
                if (_referenceLab != null) _referenceLab.Dispose();
                _referenceLab = new Mat(lab.Size(), MatType.CV_8UC3, new Scalar(median[0], median[1], median[2]));
                _hasRealReference = false;
            }
        }

        /// <summary>Goruntudeki mermerleri bulur; sonuclar buyukten kucuge alan sirasindadir.</summary>
        public List<SlabResult> Detect(Mat frame)
        {
            if (_referenceLab == null) throw new InvalidOperationException("Once SetTableColor ya da SetTableColorAuto cagrilmali.");
            var results = new List<SlabResult>();
            EdgePointFit.GradientField grad = null;   // tum parcalar ve kurtarmalar icin tek kez
            // Birbirine yakin parcalar (ya da parca ile tablanin kenarindaki citalarin gurultusu) maskede ince bir
            // koprüyle tek lekeye donusebiliyor; o leke plakaya benzemedigi icin eleniyor ya da kenar bulucunun
            // buldugu tek plaka butun lekenin yerine geciyordu, oteki parca sessizce kayboluyordu (2026-11-23: iki
            // kopuk tek leke oldu, ustteki hic gorunmedi; beyaz levhalar + siyah plaka + kenar seridi tek leke oldu).
            // Guvenilir cikmayan leke giderek buyuyen bir acmayla (erosion + dilation) bolunur: plakadan dar koprüler
            // ve cikintilar kopar, plakanin kendisi kalir; parcalar ayni yoldan yeniden olculur. Bolmeden guvenilir
            // parca cikmazsa lekenin kendisi (guvenilmez, "mermer degil") eskisi gibi raporlanir.
            Func<EdgePointFit.GradientField> gradient = () => grad ?? (grad = EdgePointFit.Gradients(frame));
            _contourOf.Clear();
            _snapped.Clear();
            foreach (Point2[] contour in FindContoursMm(frame))
            {
                _thicknessHint = null;
                List<SlabResult> found = Resolve(frame, contour, 0, gradient);
                // Bolunmus parcalardan birinde kenarlar kalinlik yuzunden tutarsiz cikti: lekenin genel notu yerine o soylenir
                if (_thicknessHint != null)
                    foreach (SlabResult x in found.Where(x => !x.IsReliable && x.Note == EdgeContactNote)) x.Note = _thicknessHint;
                results.AddRange(found);
            }
            if (_opt.EdgeMode != EdgeMode.Off) AddEdgeResults(frame, results);
            Peel(frame, results, gradient);
            // Once guvenilir parcalar, sonra alana gore
            results.Sort((a, b) => a.IsReliable != b.IsReliable ? (a.IsReliable ? -1 : 1) : b.AreaMm2.CompareTo(a.AreaMm2));
            return results;
        }

        /// <summary>Bir lekeyi olcer; guvenilir cikmazsa bolmeyi dener (bkz. Detect).</summary>
        private List<SlabResult> Resolve(Mat frame, Point2[] contour, int depth, Func<EdgePointFit.GradientField> grad)
        {
            SlabResult r = Evaluate(frame, contour, grad);
            if (Environment.GetEnvironmentVariable("KK_DEBUG") == "1")
                Console.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "  [leke d{0}] x {1:F0}-{2:F0} y {3:F0}-{4:F0} alan {5:F3} m2 -> {6}", depth, contour.Min(p => p.X), contour.Max(p => p.X),
                    contour.Min(p => p.Y), contour.Max(p => p.Y), Math.Abs(PolygonMath.SignedArea(contour)) / 1e6,
                    r == null ? "kose yok" : r.IsReliable ? "guvenilir " + r.Vertices.Length + " kose" : r.Note));
            // Bolunmus lekeden cikan parca yalniz dort koseli plakaysa guvenilir: acma, gurultu lekelerini de
            // yuvarlak, duz kenarli gorunen cokgenlere cevirip sinamayi gecirebiliyor.
            // Ayrica karsilikli kenarlari esit olmali: acmadan kalan gurultu cikintisi bir koseyi 60 mm kaydirip
            // 1200 x 595 plakayi 1190 / 1263 kenarli dortgene ceviriyordu (2026-11-23). Gercek plakalarda fark %1'in altinda.
            if (r != null && depth > 0 && r.IsReliable && !IsSplitQuadOk(r))
            {
                // Acma koseleri yuvarlar (5-8 kose): disbukey zarftan kaba dortgen, kenarlar fotografta gercek gecise
                SlabResult quad = RefineQuadFromHull(frame, contour, grad);
                if (quad != null && quad.IsReliable && IsSplitQuadOk(quad)
                    && !(TouchesBorder(contour) && NearBorder(quad.Vertices, EdgeContactCornerMm)))
                {
                    quad.Method = "renk";
                    _snapped[quad] = _refineUsed;
                    r = quad;
                }
                else
                {
                    r.IsReliable = false;
                    r.Note = "Mermer gibi gorunmuyor: bolunen lekeden cikan parca duzgun bir dortgen degil";
                }
            }
            // Birbirine degen iki plaka L (ya da daha cok koseli) tek leke olur; acma onlari ayiramaz (genis kenar boyunca
            // degerler). Ic koseden olasi kesim cizgilerinden biri boyunca fotografta ek cizgisi varsa oradan bolunur
            // (2026-11-23: alt alta degen iki beyaz levha L oldu, olculmedi). Gercek L biçimli plakada ek cizgisi yoktur.
            if (r != null && r.Vertices != null && r.Vertices.Length >= 5 && depth < 3)
            {
                List<Point2[]> halves = SeamSplit(contour, grad);
                if (halves.Count == 2)
                {
                    List<SlabResult> parts = halves.SelectMany(h => Resolve(frame, h, depth + 1, grad)).ToList();
                    // Birbirine degen parcalar tek parca sayilir (kullanici karari 2026-11-23: L duran iki levha L olarak
                    // gorulmeli): ek cizgisinden bolme yalniz kenarlari dogru bulmak icindir, sonuc iki dortgenin birlesimi
                    if (parts.Count(x => x.IsReliable) == 2)
                    {
                        SlabResult merged = MergeTouching(parts.Where(x => x.IsReliable).ToList());
                        if (merged != null) return new List<SlabResult> { merged };
                    }
                }
            }
            if (r != null && r.IsReliable) return new List<SlabResult> { r };
            // Kucuk acma bolmediyse (koprü ondan genis) bir sonraki capla denenir. Kucuk lekeler (gurultu) bolunmez:
            // icinden olculecek plaka cikamaz, yalniz sure harcar.
            bool splittable = Math.Abs(PolygonMath.SignedArea(contour)) >= EdgeContactMinAreaMm2;
            for (int d = depth; splittable && d < _opt.SplitOpeningMm.Length; d++)
            {
                List<Point2[]> parts = SplitContour(contour, _opt.SplitOpeningMm[d]);
                if (parts.Count == 0) continue;
                var found = parts.SelectMany(p => Resolve(frame, p, d + 1, grad)).Where(x => x.IsReliable).ToList();
                if (found.Count > 0) return found;
                break;
            }
            // Kenar seridine giren parca: bolunmus temiz parcalar denendikten sonra (onlar daha iyi bir kaba dortgen
            // verir) lekenin kendisinde kenarlar fotograftan aranir (bkz. BorderEdgeFinder)
            if (r != null && r.Note == EdgeContactNote)
            {
                string note;
                SlabResult b = BorderRefine(frame, grad, contour, null, out note);
                if (b != null) { b.Method = "renk"; _snapped[b] = 4; return new List<SlabResult> { b }; }
                r.Note = note;
            }
            if (r == null) return new List<SlabResult>();
            _contourOf[r] = contour;
            return new List<SlabResult> { r };
        }

        /// <summary>Son lekede kenar aramasinin "kalinlik yanlis olabilir" notu (yoksa null).</summary>
        private string _thicknessHint;

        /// <summary>Ek cizgisi boyunca gradyanin yandaki paralel cizgilere orani en az bu olmali.</summary>
        private const double SeamMinRatio = 1.8;

        /// <summary>
        /// Cokgen lekeyi (5-8 kose, ic koseli) ic koseden baslayan ve fotografta ek cizgisi gorunen kesimle iki parcaya
        /// boler. Ek cizgisi: kesim boyunca kesime dik gradyan, kesime paralel 40 ve 80 mm yandaki cizgilerdekinin
        /// SeamMinRatio katindan fazla. Bulunamazsa bos liste.
        /// </summary>
        private List<Point2[]> SeamSplit(Point2[] contour, Func<EdgePointFit.GradientField> grad)
        {
            var none = new List<Point2[]>();
            SlabResult raw = SlabLocator.Locate(contour, ScaledLocator(contour), 0, null);
            if (raw == null || raw.Vertices == null || raw.Vertices.Length < 5 || raw.Vertices.Length > 8) return none;
            Point2[] v = PolygonMath.SignedArea(raw.Vertices) < 0 ? raw.Vertices.Reverse().ToArray() : raw.Vertices;
            EdgePointFit.GradientField g = grad();
            int n = v.Length;
            double bestScore = 0, bestLen = 0;
            Point2 bestFrom = default(Point2), bestDir = default(Point2);
            for (int i = 0; i < n; i++)
            {
                Point2 prev = v[(i + n - 1) % n], cur = v[i], next = v[(i + 1) % n];
                if (Point2.Cross(cur - prev, next - cur) >= 0) continue;   // dis bukey kose
                foreach (Point2 d0 in new[] { cur - prev, cur - next })
                {
                    Point2 dir = d0 * (1.0 / Math.Max(1e-9, Math.Sqrt(Point2.Dot(d0, d0))));
                    double len = 0;
                    while (len < 4000 && PolygonMath.Contains(v, cur + dir * (len + 5))) len += 5;
                    if (len < 100) continue;
                    double score = SeamScore(cur, dir, len, v, g);
                    if (score > bestScore) { bestScore = score; bestFrom = cur; bestDir = dir; bestLen = len; }
                }
            }
            if (Environment.GetEnvironmentVariable("KK_DEBUG") == "1" && bestLen > 0)
                Console.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "  [ek cizgisi] en iyi oran {0:F2}, {1:F0} mm", bestScore, bestLen));
            if (bestScore < SeamMinRatio) return none;
            // Ic kosenin tahmini yeri birkac mm kayik olabilir: kesim, ek cizgisinin tam ustune (yana +-20 mm icinde en
            // guclu dik gradyan) kaydirilir. Kayik kesim, levhanin kenarindan ince bir seridi iki parcayi bagli birakiyordu.
            {
                var nrm = new Point2(-bestDir.Y, bestDir.X);
                double bestOff = 0, bestG = -1;
                for (double o = -20; o <= 20; o += 1)
                {
                    double sum = 0; int cnt = 0;
                    for (double t = 0.15 * bestLen; t <= 0.85 * bestLen; t += 3) { sum += PerpGradient(bestFrom + bestDir * t + nrm * o, nrm, g); cnt++; }
                    double mean = cnt > 0 ? sum / cnt : 0;
                    if (mean > bestG) { bestG = mean; bestOff = o; }
                }
                bestFrom = bestFrom + nrm * bestOff;
            }
            // Kesim: lekenin maskesi kesim cizgisi boyunca silinir, iki parca kalir
            double s = Scale, H = _cal.TableWidthMm;
            Func<Point2, Point> toPx = p => new Point((int)Math.Round(p.X * s), (int)Math.Round((H - p.Y) * s));
            Point[] px = contour.Select(toPx).ToArray();
            Rect box = Cv2.BoundingRect(px);
            int pad = 4;
            var off = new Point(pad - box.X, pad - box.Y);
            using (var mask = new Mat(box.Height + 2 * pad, box.Width + 2 * pad, MatType.CV_8UC1, Scalar.All(0)))
            {
                Cv2.FillPoly(mask, new[] { px.Select(p => new Point(p.X + off.X, p.Y + off.Y)).ToArray() }, Scalar.All(255));
                // Ic kosenin tahmini yeri gercek koseden birkac mm sapabilir: kesim kosenin gerisindeki bosluktan baslar
                Point a = toPx(bestFrom - bestDir * 60), b = toPx(bestFrom + bestDir * (bestLen + 60));
                Cv2.Line(mask, new Point(a.X + off.X, a.Y + off.Y), new Point(b.X + off.X, b.Y + off.Y), Scalar.All(0), Math.Max(3, (int)Math.Round(3 * s)));
                if (Environment.GetEnvironmentVariable("KK_DEBUG") == "1")
                    try { mask.SaveImage(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kk_kesim.png")); } catch (OpenCVException) { }
                Point[][] cs;
                HierarchyIndex[] h;
                Cv2.FindContours(mask, out cs, out h, RetrievalModes.External, ContourApproximationModes.ApproxNone);
                // En buyuk iki parca: kesim kenar gurultusundan kucuk bir ucuncu parca da ayirabiliyor
                var parts = cs.Where(c => Cv2.ContourArea(c) >= _opt.MinAreaMm2 * s * s).OrderByDescending(c => Cv2.ContourArea(c)).Take(2)
                    .Select(c => c.Select(p => new Point2((p.X - off.X) / s, H - (p.Y - off.Y) / s)).ToArray()).ToList();
                if (Environment.GetEnvironmentVariable("KK_DEBUG") == "1")
                    Console.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "  [ek cizgisi] kesim ({0:F0},{1:F0}) yon ({2:F2},{3:F2}): {4} kontur, {5} buyuk parca",
                        bestFrom.X, bestFrom.Y, bestDir.X, bestDir.Y, cs.Length, parts.Count));
                return parts.Count == 2 ? parts : none;
            }
        }

        /// <summary>
        /// Birbirine degen guvenilir parcalari (koseleri kalinlik duzeltilmis, tabla mm) tek cokgende birlestirir:
        /// ikisinin birlesiminin dis siniri, koseler yeniden bulunur (kalinlik ikinci kez uygulanmaz). Olmazsa null.
        /// </summary>
        private SlabResult MergeTouching(List<SlabResult> parts)
        {
            // Birlesim yuksek cozunurlukte cizilir (mm basina 4 piksel) ki koseler yuvarlanmasin
            const double k = 4;
            double minX = parts.Min(p => p.Vertices.Min(v => v.X)) - 10, minY = parts.Min(p => p.Vertices.Min(v => v.Y)) - 10;
            double maxX = parts.Max(p => p.Vertices.Max(v => v.X)) + 10, maxY = parts.Max(p => p.Vertices.Max(v => v.Y)) + 10;
            int w = (int)Math.Ceiling((maxX - minX) * k), h = (int)Math.Ceiling((maxY - minY) * k);
            if (w <= 0 || h <= 0 || (long)w * h > 200_000_000) return null;
            Func<Point2, Point> toPx = p => new Point((int)Math.Round((p.X - minX) * k), (int)Math.Round((maxY - p.Y) * k));
            using (var mask = new Mat(h, w, MatType.CV_8UC1, Scalar.All(0)))
            using (Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(13, 13)))
            {
                Cv2.FillPoly(mask, parts.Select(p => p.Vertices.Select(toPx).ToArray()).ToArray(), Scalar.All(255));
                // Ek cizgisindeki kesim boslugu (birkac mm) kapansin
                Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);
                Point[][] cs;
                HierarchyIndex[] hi;
                Cv2.FindContours(mask, out cs, out hi, RetrievalModes.External, ContourApproximationModes.ApproxNone);
                if (cs.Length != 1) return null;
                Point2[] outline = cs[0].Select(p => new Point2(p.X / k + minX, maxY - p.Y / k)).ToArray();
                SlabResult r = SlabLocator.Locate(outline, ScaledLocator(outline), 0, null);
                if (r == null || r.Vertices == null || r.Vertices.Length < 4) return null;
                r.IsReliable = true;
                r.Method = "renk";
                return r;
            }
        }

        /// <summary>Kesim cizgisi boyunca dik gradyan ortalamasinin, cizgiye paralel yandaki cizgilerdekine orani.</summary>
        private double SeamScore(Point2 from, Point2 dir, double len, Point2[] poly, EdgePointFit.GradientField g)
        {
            var nrm = new Point2(-dir.Y, dir.X);
            Func<double, double> line = offset =>
            {
                double sum = 0; int cnt = 0;
                for (double t = 0.15 * len; t <= 0.85 * len; t += 3)
                {
                    Point2 p = from + dir * t + nrm * offset;
                    if (offset != 0 && !PolygonMath.Contains(poly, p)) continue;
                    double best = 0;
                    for (double k = -3; k <= 3; k += 1) best = Math.Max(best, PerpGradient(p + nrm * k, nrm, g));
                    sum += best; cnt++;
                }
                return cnt > 5 ? sum / cnt : double.NaN;
            };
            double seam = line(0);
            var ctrl = new[] { -80.0, -40, 40, 80 }.Select(line).Where(x => !double.IsNaN(x)).ToList();
            if (double.IsNaN(seam) || ctrl.Count == 0) return 0;
            return seam / Math.Max(1, ctrl.Average());
        }

        /// <summary>Tabla mm noktasinda gradyanin verilen (tabla) yondeki bileseni (mutlak).</summary>
        private double PerpGradient(Point2 p, Point2 nrm, EdgePointFit.GradientField g)
        {
            int x = (int)Math.Round(p.X * Scale), y = (int)Math.Round((_cal.TableWidthMm - p.Y) * Scale);
            if (x < 0 || y < 0 || x >= g.W || y >= g.H) return 0;
            int i = y * g.W + x;
            return Math.Abs(g.Gx[i] * nrm.X - g.Gy[i] * nrm.Y);   // goruntude Y asagi
        }

        /// <summary>Guvenilmez sonuclarin lekesi (Peel icin).</summary>
        private readonly Dictionary<SlabResult, Point2[]> _contourOf = new Dictionary<SlabResult, Point2[]>();
        /// <summary>Renk sonucunun kac kenari fotografta gercek gecise oturtuldu (0-4).</summary>
        private readonly Dictionary<SlabResult, int> _snapped = new Dictionary<SlabResult, int>();
        /// <summary>Bolunmus lekeden cikan parcanin karsilikli kenar farki en fazla bu oranda olabilir.</summary>
        private const double SplitOppositeSideTolerance = 0.025;

        /// <summary>Dort koseli ve karsilikli kenarlari birbirine esit (paralelkenara yakin) mi.</summary>
        private static bool IsSplitQuadOk(SlabResult r)
        {
            if (r.Vertices == null || r.Vertices.Length != 4 || r.EdgeLengths == null || r.EdgeLengths.Length != 4) return false;
            double[] e = r.EdgeLengths;
            Func<double, double, bool> same = (a, b) => Math.Abs(a - b) <= SplitOppositeSideTolerance * Math.Max(a, b);
            return same(e[0], e[2]) && same(e[1], e[3]);
        }

        /// <summary>
        /// Guvenilmez bir lekenin icinde guvenilir bulunmus plaka varsa (cogunlukla duz kenar bulucudan) o plaka lekeden
        /// cikarilir ve kalan yeniden olculur. Genis bir yerden degen iki plaka acmayla ayrilmaz (2026-11-23: capraz
        /// kopuk ustteki kopuge kosesiyle degiyordu; kenar bulucu caprazi buldu, ustteki hic raporlanmadi).
        /// </summary>
        private void Peel(Mat frame, List<SlabResult> results, Func<EdgePointFit.GradientField> grad)
        {
            List<SlabResult> reliable = results.Where(x => x.IsReliable && x.Vertices != null && x.Vertices.Length >= 3).ToList();
            if (reliable.Count == 0) return;
            foreach (SlabResult r in results.Where(x => !x.IsReliable).ToList())
            {
                Point2[] c;
                if (!_contourOf.TryGetValue(r, out c) || Math.Abs(PolygonMath.SignedArea(c)) < EdgeContactMinAreaMm2) continue;
                List<SlabResult> inside = reliable.Where(q => PolygonMath.Contains(c, q.Centroid)).ToList();
                if (inside.Count == 0) continue;
                List<SlabResult> found = Subtract(c, inside.Select(q => q.Vertices), 25, 60)
                    .SelectMany(p => Resolve(frame, p, 1, grad)).Where(x => x.IsReliable).ToList();
                // Kalandan plaka cikmasa da leke artik bulunan plakayla temsil edilir (eski davranis)
                results.Remove(r);
                results.AddRange(found);
            }
        }

        /// <summary>
        /// Lekeden (tabla mm) verilen cokgenleri clearMm genisletilmis olarak cikarir, kalani openMm capla acar (ince
        /// artiklar kopsun); MinAreaMm2'den buyuk kalan parcalari dondurur.
        /// </summary>
        private List<Point2[]> Subtract(Point2[] contourMm, IEnumerable<Point2[]> holesMm, double clearMm, double openMm)
        {
            var parts = new List<Point2[]>();
            double s = Scale, H = _cal.TableWidthMm;
            Func<Point2, Point> toPx = p => new Point((int)Math.Round(p.X * s), (int)Math.Round((H - p.Y) * s));
            Point[] px = contourMm.Select(toPx).ToArray();
            Rect box = Cv2.BoundingRect(px);
            int kc = ((int)Math.Round(2 * clearMm * s)) | 1, ko = ((int)Math.Round(openMm * s)) | 1;
            int pad = Math.Max(kc, ko) + 2;
            var off = new Point(pad - box.X, pad - box.Y);
            Func<Point, Point> shift = p => new Point(p.X + off.X, p.Y + off.Y);
            using (var mask = new Mat(box.Height + 2 * pad, box.Width + 2 * pad, MatType.CV_8UC1, Scalar.All(0)))
            using (var holes = new Mat(mask.Size(), MatType.CV_8UC1, Scalar.All(0)))
            using (Mat kClear = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(kc, kc)))
            using (Mat kOpen = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(ko, ko)))
            {
                Cv2.FillPoly(mask, new[] { px.Select(shift).ToArray() }, Scalar.All(255));
                Cv2.FillPoly(holes, holesMm.Select(hole => hole.Select(toPx).Select(shift).ToArray()).ToArray(), Scalar.All(255));
                Cv2.Dilate(holes, holes, kClear);
                using (var notHoles = new Mat())
                {
                    Cv2.BitwiseNot(holes, notHoles);
                    Cv2.BitwiseAnd(mask, notHoles, mask);
                }
                Cv2.MorphologyEx(mask, mask, MorphTypes.Open, kOpen);
                Point[][] cs;
                HierarchyIndex[] h;
                Cv2.FindContours(mask, out cs, out h, RetrievalModes.External, ContourApproximationModes.ApproxNone);
                foreach (Point[] c in cs.Where(c => Cv2.ContourArea(c) >= _opt.MinAreaMm2 * s * s))
                    parts.Add(c.Select(p => new Point2((p.X - off.X) / s, H - (p.Y - off.Y) / s)).ToArray());
            }
            return parts;
        }

        /// <summary>Lekenin koselerini bulur, fotografta oturtur ve plakaya benzeyip benzemedigini sinar. Kose bulunamazsa null.</summary>
        private SlabResult Evaluate(Mat frame, Point2[] contour, Func<EdgePointFit.GradientField> grad)
        {
            SlabResult r = SlabLocator.Locate(contour, ScaledLocator(contour), _cal.SlabThicknessMm, _cal.Camera);
            if (r == null) return null;
            r = RefineInImage(frame, contour, r, grad);
            int used = _refineUsed, snapped = used;
            Assess(r, contour);
            // Maske parcanin bir bolgesinde delindiyse (acik renk parca, acik eskimiş cita) kontur centikli
            // cikiyor; kenarlar akor olup koseler kayiyordu. Disbukey zarf 4 koseli bir kaba veriyorsa
            // kenarlari fotografta gercek gecislere oturtarak kurtar. Kurtarma, oturtma dort kenarin
            // hepsinde tutmadiysa (kaba akor kalan kenar varsa) da denenir. T/L gibi gercekten cok koseli
            // parcalarda zarf dortgen vermez ya da alan uymaz, sonuc eskisi gibi kalir.
            if (!r.IsReliable || (r.Vertices != null && r.Vertices.Length == 4 && used < 4))
            {
                SlabResult rescued = RefineQuadFromHull(frame, contour, grad);
                if (rescued != null && rescued.IsReliable && _refineUsed >= used) { r = rescued; snapped = _refineUsed; }
            }
            r.Method = "renk";
            _snapped[r] = snapped;
            // Leke taranmayan kenar seridine degiyorsa parca tablanin kenarina dayaniyor ya da tasiyor: o kenari
            // serit kesmistir, kose gercek kenara degil serit sinirina oturur (2026-11-23: kenara dayanan beyaz
            // levhalarin ve tasan siyah plakanin koseleri yanlis cikti). Kucuk lekeler kenar gurultusudur, notu degismez.
            // Lekeye kenar seridindeki gurultu (cita uclari) yapismis ama parca seritten uzaksa ya da dort kenari da
            // fotografta gercek gecise oturduysa (serit icinde kalmadan) parca kenara dayanmiyordur (2026-11-23: alt
            // kenari seritten ~45 mm icerideki plaka boyle elendi). Serit tarafindan kesilen kenar gercek gecise oturamaz.
            bool cornersNearBorder = r.Vertices == null || (NearBorder(r.Vertices, EdgeContactCornerMm)
                && !(r.Vertices.Length == 4 && snapped == 4 && !NearBorder(r.Vertices, 0)));
            if (TouchesBorder(contour) && (r.IsReliable ? cornersNearBorder : Math.Abs(PolygonMath.SignedArea(contour)) >= EdgeContactMinAreaMm2))
            {
                // Kenar seride giriyor; o kenar Resolve'da (leke bolunmeye calisildiktan sonra) fotograftan aranir
                r.IsReliable = false;
                r.Note = EdgeContactNote;
            }
            return r;
        }

        /// <summary>
        /// Orijinal fotograftan tabla mm noktalarinin rengi (BGR); tablanin disindaki noktalar da. Verilirse tablanin
        /// kenar seridine giren parca kenarlari fotografta aranir (BorderEdgeFinder); verilmezse o parcalar uyariyla doner.
        /// </summary>
        public Func<Point2[], Vec3b[]> PhotoSampler;
        internal Func<Point2[], Vec3b[]> TopFaceSampler;

        /// <summary>
        /// Dedektorun tablasi gercek tablanin her yandan bu kadar (mm) genisletilmisi (TableMeasure.RunWithLens): gercek
        /// tabla kenari dedektor tablasinin kenarindan TableInsetMm iceridedir. Kenar seridi, kenara dayanma ve tasma
        /// kurallari gercek kenara gore uygulanir.
        /// </summary>
        public double TableInsetMm { get; set; }

        /// <summary>Taranmayan kenar seridinin dedektor tablasinin kenarindan genisligi (mm).</summary>
        private double Band => Math.Max(0, _opt.BorderMarginMm) + TableInsetMm;

        /// <summary>Son olcumun fark maskesi (kenar seridi silinmeden).</summary>
        private Mat _lastMask;

        /// <summary>Tabla mm noktasi fark maskesinde mi (bos tabladan farkli). Referans yoksa hep false.</summary>
        private bool MaskAt(Point2 p)
        {
            if (!_hasRealReference || _lastMask == null) return false;
            int x = (int)(p.X * Scale), y = (int)((_cal.TableWidthMm - p.Y) * Scale);
            x = Math.Min(Math.Max(x, 0), _lastMask.Cols - 1); y = Math.Min(Math.Max(y, 0), _lastMask.Rows - 1);
            return _lastMask.At<byte>(y, x) != 0;
        }

        /// <summary>Kenar bulucunun dortgeninin icinin en az bu orani bos tabladan farkli (maskede) olmali.</summary>
        private const double MinEdgeQuadMaskCoverage = 0.6;
        /// <summary>Kenar seridinde fotograftan bulunan dortgenin (tablanin icinde kalan) icinin en az bu orani maskede olmali.</summary>
        private const double MinBorderQuadMaskCoverage = 0.8;
        /// <summary>Kenar seridine giren lekenin siniri (serit disinda) en az bu oranda bulunan dortgenin kenarlarina yakin olmali.</summary>
        private const double MinBorderContourFit = 0.65;

        /// <summary>
        /// Cokgenin (tabla mm) icinin ne kadari fark maskesinde. Bos tabla referansi yoksa 1 (sinama yapilmaz).
        /// 2026-11-23: kenar bulucu iki cita araligi + plakanin ust kenari + tablanin ust kenarindan 413 x 1255 mm'lik
        /// sahte bir dortgen buluyordu; ici bos tablayla ayni oldugu halde parca gibi gosteriliyordu.
        /// </summary>
        private double MaskCoverage(Point2[] polyMm)
        {
            if (!_hasRealReference || _lastMask == null || polyMm == null || polyMm.Length < 3) return 1;
            double s = Scale, H = _cal.TableWidthMm;
            Point[] px = polyMm.Select(p => new Point((int)Math.Round(p.X * s), (int)Math.Round((H - p.Y) * s))).ToArray();
            Rect box = Cv2.BoundingRect(px).Intersect(new Rect(0, 0, _lastMask.Cols, _lastMask.Rows));
            if (box.Width <= 0 || box.Height <= 0) return 0;
            using (var poly = new Mat(box.Height, box.Width, MatType.CV_8UC1, Scalar.All(0)))
            using (var both = new Mat())
            using (var roi = new Mat(_lastMask, box))
            {
                Cv2.FillPoly(poly, new[] { px.Select(p => new Point(p.X - box.X, p.Y - box.Y)).ToArray() }, Scalar.All(255));
                double inside = Cv2.CountNonZero(poly);
                if (inside <= 0) return 0;
                Cv2.BitwiseAnd(poly, roi, both);
                return Cv2.CountNonZero(both) / inside;
            }
        }

        public const string OverhangNote = "Parça tablanın dışına taşıyor: parçayı tablanın içine çekip yeniden ölçün.";

        /// <summary>
        /// Kenar seridine giren parcanin kenarlarini fotograftan yeniden bulur ve olcer. Kaba dortgen: lekenin (ya da
        /// verilen dortgenin) gurultu cikintilarindan arinmis cekirdeginden. Bulunamazsa null ve operatore not.
        /// </summary>
        private SlabResult BorderRefine(Mat frame, Func<EdgePointFit.GradientField> grad, Point2[] contour, Point2[] roughQuad, out string note)
        {
            note = EdgeContactNote;
            if (PhotoSampler == null || !_opt.RefineEdges) return null;
            bool dbg = Environment.GetEnvironmentVariable("KK_DEBUG") == "1";
            Point2[] quad = roughQuad;
            if (quad == null)
            {
                List<Point2[]> parts = SplitContour(contour, _opt.SplitOpeningMm.Length > 0 ? _opt.SplitOpeningMm[0] : 80);
                Point2[] core = parts.Count > 0 ? parts.OrderByDescending(p => Math.Abs(PolygonMath.SignedArea(p))).First() : contour;
                SlabResult raw = SlabLocator.Locate(core, ScaledLocator(core), 0, null);
                if (raw != null && raw.Vertices.Length == 4) quad = raw.Vertices;
                else
                {
                    RotatedRect box = Cv2.MinAreaRect(core.Select(p => new Point2f((float)p.X, (float)p.Y)));
                    quad = box.Points().Select(p => new Point2(p.X, p.Y)).ToArray();
                }
            }
            double band = Band;
            Func<Point2, bool> differs = MaskAt;
            // Yalniz seride giren kenarlar fotografta (once maskede, sonra renk profilinde) aranir; oteki kenarlar asagida
            // RefineInImage ile gercek gecise oturtulur (renk profili parlama ve cita araliklarinda sasiyordu)
            BorderEdgeFinder.Result found = BorderEdgeFinder.Refine(quad, _cal.TableLengthMm, _cal.TableWidthMm, band, PhotoSampler, true, differs, TableInsetMm);
            if (dbg) Console.WriteLine("  [kenar arama] " + found.Info);
            if (found.Overhangs) { note = OverhangNote; return null; }
            if (found.Quad == null) return null;
            // Kenari tablanin disindan ayirt edilemeyen parca (siyah plaka, siyah sase) tablanin kenarina hizali kabul edilip
            // olculur, uyariyla (kullanici karari 2026-11-23: nesne ve tabla tam gorunuyor, kosede olmasi olcmeye engel
            // olmamali). Bu kenarin dogrulugu kalibrasyondaki tabla kenarina bagli: o gun 10-15 mm kayik bulunuyordu.
            // Birden fazla kenar fotografta bulunamadiysa ortada plaka kenari yok (insan, golge, kenar gurultusu):
            // 2026-11-23 masanin ustundeki kisi 768 x 593, kenar gurultusu 197 x 335 mm "plaka" olcu
            if (found.RoughEdges > 1) { if (dbg) Console.WriteLine("  [kenar arama] " + found.RoughEdges + " kenar bulunamadi, olculmedi"); return null; }
            // Lekenin siniri (kenar seridi disinda) bulunan dortgenin kenarlarina oturmali
            double tol = _opt.FitToleranceMm + _cal.SlabThicknessMm;
            Point2[] free = contour.Where(p => !NearBorder(new[] { p }, 15)).ToArray();
            if (free.Length > 20)
            {
                Point2[] fq = found.Quad;
                int near = free.Count(p => Enumerable.Range(0, 4).Min(i => SegmentDistance(p, fq[i], fq[(i + 1) % 4])) <= tol);
                double fit = (double)near / free.Length;
                // Esik gevsek: kenar seridindeki gurultu lekeye yapisinca gercek levhalarda bile 0,70'e iniyordu; sahte
                // parcalari asil yukaridaki "birden fazla kenar bulunamadi" kurali eler, bu yalniz bambaska sekilleri
                if (fit < MinBorderContourFit) { if (dbg) Console.WriteLine("  [kenar arama] leke dortgene uymuyor (" + fit.ToString("F2") + "), olculmedi"); return null; }
            }
            var dense = new List<Point2>();
            for (int e = 0; e < 4; e++)
            {
                Point2 a = found.Quad[e], b = found.Quad[(e + 1) % 4];
                int k = Math.Max(2, (int)(Point2.Distance(a, b) / 2));
                for (int j = 0; j < k; j++) dense.Add(a + (b - a) * ((double)j / k));
            }
            Point2[] d = dense.ToArray();
            SlabResult r = SlabLocator.Locate(d, ScaledLocator(d), _cal.SlabThicknessMm, _cal.Camera);
            if (r == null || r.Vertices == null || r.Vertices.Length != 4) return null;
            r = RefineInImage(frame, d, r, grad, retryOnTopFace: found.Assumed == null);
            if (r == null || r.Vertices == null || r.Vertices.Length != 4) return null;
            Assess(r, d);
            // Kaba lekeden kalan kenarlar ve oturtma tutmadiysa olcu bozuk olabilir: karsilikli kenarlar esit olmali.
            // Ici bos tabladan farkli olmali: kenar seridindeki gurultu lekesi 197 x 197 mm'lik "kare" olcu (2026-11-23).
            if (r.IsReliable)
            {
                double coverage = MaskCoverage(found.Quad);
                if (_opt.CaptureTraceMask != null)
                    Console.WriteLine(FormattableString.Invariant($"  [kenar arama kontrol] maske_doluluk={coverage:F3} esik={MinBorderQuadMaskCoverage:F3} kesin_oturan={_refineUsed}/4"));
                if (coverage < MinBorderQuadMaskCoverage) { r.IsReliable = false; r.Note = EdgeContactNote; }
            }
            if (r.IsReliable && !IsSplitQuadOk(r))
            {
                // Karsilikli kenarlar tutarsiz: en sik nedeni yanlis girilen kalinlik (0 mm girilince kalin levhanin yan yuzu
                // olcuye katilip dikdortgen yamuk cikiyordu: 603 / 626 mm). Sessizce elemek yerine operatore soylenir.
                r.IsReliable = false;
                double[] e = r.EdgeLengths;
                note = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "Karşılıklı kenarlar tutarsız ({0:F0} / {1:F0} mm ve {2:F0} / {3:F0} mm). Girilen kalınlık parçanın gerçek kalınlığından farklı olabilir; kalınlığı kontrol edip yeniden ölçün.",
                    e[0], e[2], e[1], e[3]);
                r.Note = note;
                _thicknessHint = note;
            }
            if (r.IsReliable && found.Assumed != null)
                r.Note = "Parçanın " + found.Assumed.Replace("sag", "sağ").Replace("ust", "üst") + " kenarı fotoğrafta tablanın dışından (şase, zemin) ayırt edilemedi; tablanın kenarıyla hizalı kabul edildi. Bu kenarın doğruluğu kalibrasyondaki tabla kenarına bağlıdır; parça tablanın dışına taşıyorsa bu kenar yanlıştır.";
            if (dbg) Console.WriteLine("  [kenar arama] sonuc " + (r.IsReliable ? "guvenilir" : r.Note) + ": " + string.Join("/", r.EdgeLengths.Select(x => x.ToString("F0"))));
            return r.IsReliable ? r : null;
        }

        /// <summary>Kenar temasi notunun yazilacagi en kucuk leke alani (mm2); daha kucukleri kenar gurultusu sayilir.</summary>
        private const double EdgeContactMinAreaMm2 = 200 * 200;
        public const string EdgeContactNote = "Parça tablanın kenarına dayanıyor ya da dışına taşıyor; kenarı görülemediği için köşeleri güvenilmez. Parçayı tablanın içine çekip yeniden ölçün.";

        /// <summary>Kenar seridine degen lekede kosesi seridin sinirina bundan (mm) yakin parca kenara dayaniyor sayilir.</summary>
        private const double EdgeContactCornerMm = 30;

        /// <summary>Kontur (tabla mm) taranmayan kenar seridinin sinirina (birkac mm icinde) degiyor mu.</summary>
        private bool TouchesBorder(Point2[] contour) => NearBorder(contour, 3);

        /// <summary>Noktalardan biri taranmayan kenar seridinin sinirina extraMm'den yakin mi.</summary>
        private bool NearBorder(Point2[] pts, double extraMm)
        {
            double m = Band + extraMm, L = _cal.TableLengthMm, W = _cal.TableWidthMm;
            return pts.Any(p => p.X <= m || p.Y <= m || p.X >= L - m || p.Y >= W - m);
        }

        /// <summary>
        /// Lekeyi (tabla mm konturu) verilen capta (mm) acmayla boler; MinAreaMm2'den kucuk kalanlar atilir.
        /// Lekeden yalniz bir parca kalir ve alani neredeyse degismezse (bolunecek koprü yok) bos liste doner.
        /// </summary>
        private List<Point2[]> SplitContour(Point2[] contourMm, double openingMm)
        {
            var parts = new List<Point2[]>();
            double s = Scale, H = _cal.TableWidthMm;
            int kd = ((int)Math.Round(openingMm * s)) | 1;
            Point[] px = contourMm.Select(p => new Point((int)Math.Round(p.X * s), (int)Math.Round((H - p.Y) * s))).ToArray();
            Rect box = Cv2.BoundingRect(px);
            int pad = kd + 2;
            using (var mask = new Mat(box.Height + 2 * pad, box.Width + 2 * pad, MatType.CV_8UC1, Scalar.All(0)))
            using (Mat kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(kd, kd)))
            {
                var off = new Point(pad - box.X, pad - box.Y);
                Cv2.FillPoly(mask, new[] { px.Select(p => new Point(p.X + off.X, p.Y + off.Y)).ToArray() }, Scalar.All(255));
                double before = Cv2.CountNonZero(mask);
                Cv2.MorphologyEx(mask, mask, MorphTypes.Open, kernel);
                Point[][] cs;
                HierarchyIndex[] h;
                Cv2.FindContours(mask, out cs, out h, RetrievalModes.External, ContourApproximationModes.ApproxNone);
                var kept = cs.Where(c => Cv2.ContourArea(c) >= _opt.MinAreaMm2 * s * s).ToArray();
                if (kept.Length == 0) return parts;
                // Kenar seridine yapisan ince bant plaka alaninin yalniz %2-4'u kadar olabilir; esik dar tutulur
                if (kept.Length == 1 && Cv2.ContourArea(kept[0]) > 0.99 * before) return parts;
                foreach (Point[] c in kept)
                    parts.Add(c.Select(p => new Point2((p.X - off.X) / s, H - (p.Y - off.Y) / s)).ToArray());
            }
            return parts;
        }

        /// <summary>
        /// Centikli konturun disbukey zarfi kaba bir dortgen veriyorsa (parca dikdortgenimsi ama maskenin deldigi
        /// bolgeler konturu bozdu) kenarlari orijinal fotografta yeniden oturtur. Oturtma basarisizsa null.
        /// </summary>
        private SlabResult RefineQuadFromHull(Mat frame, Point2[] contour, Func<EdgePointFit.GradientField> grad)
        {
            bool dbg = Environment.GetEnvironmentVariable("KK_DEBUG") == "1";
            if (!_opt.RefineEdges || contour == null || contour.Length < 8) return null;
            Point2f[] hull = Cv2.ConvexHull(contour.Select(p => new Point2f((float)p.X, (float)p.Y)));
            Point2[] hullPts = hull.Select(p => new Point2(p.X, p.Y)).ToArray();
            if (hullPts.Length < 4) return null;
            LocatorOptions loc = ScaledLocator(contour);
            SlabResult rough = SlabLocator.Locate(hullPts, loc, 0, null);
            if (rough == null || rough.Vertices == null || rough.Vertices.Length != 4) { if (dbg) Console.WriteLine("  [kurtarma] zarf dortgen vermedi (" + (rough?.Vertices?.Length ?? -1) + " kose)"); return null; }   // T/L gibi gercekten cok koseli parca
            SlabResult refined = RefineInImage(frame, hullPts, rough, grad);
            if (refined == null || refined.Vertices == null || refined.Vertices.Length != 4) { if (dbg) Console.WriteLine("  [kurtarma] oturtma sonucu gecersiz"); return null; }
            if (ReferenceEquals(refined, rough)) { if (dbg) Console.WriteLine("  [kurtarma] hicbir kenar oturamadi"); return null; }   // oturtma tutmadi
            // Sinama orijinal (centikli) konturla degil oturtulan dortgenin yogun konturuyla yapilir
            var dense = new List<Point2>();
            for (int e = 0; e < 4; e++)
            {
                Point2 a = refined.Vertices[e], b = refined.Vertices[(e + 1) % 4];
                int k = Math.Max(2, (int)(Point2.Distance(a, b) / 2));
                for (int j = 0; j < k; j++) dense.Add(a + (b - a) * ((double)j / k));
            }
            Assess(refined, dense.ToArray());
            // Kurtarilan dortgen gercek konturdan belirgin buyukse parca dikdortgen degildir (L/T gibi):
            // cikintili parcanin zarfi dortgen cikar ama kontur alani onu doldurmaz. Centikli dikdortgende
            // kontur alani dortgenin ~%90'i ve uzeridir.
            double contourArea = Math.Abs(PolygonMath.SignedArea(contour));
            double quadArea = Math.Abs(PolygonMath.SignedArea(refined.Vertices));
            if (contourArea > 0 && (quadArea > 1.15 * contourArea || quadArea < 0.5 * contourArea)) { if (dbg) Console.WriteLine("  [kurtarma] alan uyumsuz: dortgen " + quadArea + " / kontur " + contourArea); return null; }
            if (dbg) Console.WriteLine("  [kurtarma] sonuc: " + (refined.IsReliable ? "guvenilir" : refined.Note));
            return refined;
        }

        /// <summary>Son kenar oturtmanin ozeti (teshis): kac kenar oturtuldu, ortalama kose kaymasi (px).</summary>
        public List<string> RefineLog = new List<string>();

        /// <summary>Son RefineInImage'de gercek gecise oturtulan kenar sayisi (0-4; oturtma yapilmadiysa 0).</summary>
        private int _refineUsed;

        /// <summary>
        /// Dort koseli parcanin kenarlarini orijinal fotografta yeniden bulur. Kalinlik duzeltmesiz siluet dortgeni
        /// fotografa tasinir, her kenara noktalar konup en guclu gecise oturtulur (EdgePointFit), dogrular kesistirilir,
        /// tabla mm'ye geri cevrilir ve kalinlik duzeltmesi ayni SlabLocator ile yeniden uygulanir.
        /// Kalinlik varken kameraya bakan kenarlara dokunulmaz: orada ust yuzey / on yuz / tabla diye iki gecis var ve
        /// en guclusu ust yuzeyin kenari olabilir (siluet alt kenari istenir). Guvenilir sonuc cikmazsa r aynen doner.
        /// </summary>
        private SlabResult RefineInImage(Mat frame, Point2[] contour, SlabResult r,
            Func<EdgePointFit.GradientField> grad = null, bool retryOnTopFace = false)
        {
            _refineUsed = 0;
            if (!_opt.RefineEdges || r.Vertices == null || r.Vertices.Length != 4) return r;
            LocatorOptions loc = ScaledLocator(contour);
            SlabResult raw = SlabLocator.Locate(contour, loc, 0, null);
            if (raw == null || raw.Vertices.Length != 4) return r;
            Homography toImage = _cal.ImageToTable.Inverse();
            Point2[] vMm = raw.Vertices;   // saat yonunun tersine (mm)
            Point2[] vPx = vMm.Select(toImage.Transform).ToArray();

            CameraPose cam = _cal.Camera;
            double t = _cal.SlabThicknessMm;
            bool thick = cam != null && t > 0 && cam.Height > t;
            List<Point2>[] samples = EdgePointFit.Sample(vPx, Math.Max(4, _opt.RefinePointsPerEdge));
            var radius = new double[4];
            var refine = new bool[4];
            var outermost = new bool[4];
            for (int e = 0; e < 4; e++)
            {
                Point2 a = vMm[e], b = vMm[(e + 1) % 4];
                Point2 d = (b - a) * (1.0 / Math.Max(1e-9, Point2.Distance(a, b)));
                var outward = new Point2(d.Y, -d.X);
                Point2 mid = (a + b) * 0.5;
                // Yerel olcek: kenarin ortasinda 1 mm kac piksel
                double pxPerMm = Point2.Distance(toImage.Transform(mid), toImage.Transform(mid + outward));
                radius[e] = Math.Max(3, Math.Min(40, _opt.RefineSearchMm * pxPerMm));
                bool far = thick && Point2.Dot(mid - new Point2(cam.X, cam.Y), outward) > 0;
                refine[e] = !thick || far || _opt.RefineFacingEdges;
                outermost[e] = thick && !far;   // kameraya bakan kenar: ust yuzey kenari degil siluet
            }
            double rMax = radius.Max();
            // Yinelemeli oturtma: kaba dortgenin bir kenari bir ucunda arama yaricapindan fazla kayik olabilir. Her turda
            // gecise oturan noktalardan (kenarin en az dortte birine yayilmis en az 4 nokta) cizgi yeniden kurulur ve
            // noktalar yeni cizgiden alinir; kenar boylece iyi ucundan baslayarak gercek gecise yuruyerek oturur.
            // Kesin kabul icin noktalarin yarisi oturmali ve kenar duz olmali. Kameraya bakan kenarda en distaki gecis
            // ilk turdan sonra aranir (kaba cizgi yanlis bir bandin uzerindeyse ona takilmasin).
            Point2[] cur = vPx;
            Point2[] cornersPx = null;
            int used = 0;
            var none = new bool[4];
            var confirmed = retryOnTopFace && TopFaceSampler != null ? new bool[4] : null;
            // Gradyan (Sobel) tum turlar icin bir kez: 4K karede her turda yeniden hesaplamak olcumu belirgin yavaslatir
            EdgePointFit.GradientField g = null;
            for (int pass = 0; pass < 4; pass++)
            {
                List<Point2>[] pts = pass == 0 ? samples : EdgePointFit.Sample(cur, Math.Max(4, _opt.RefinePointsPerEdge));
                if (g == null) g = grad != null ? grad() : EdgePointFit.Gradients(frame);
                EdgeFitResult snapped = EdgePointFit.Fit(frame, pts, rMax, pass == 0 ? none : outermost, g);
                EdgeFitResult plain = EdgePointFit.Fit(frame, pts, 0);
                if (snapped.Edges == null || plain.Edges == null) return r;
                var edges = new FittedEdge[4];
                used = 0;
                for (int e = 0; e < 4; e++)
                {
                    FittedEdge fe = snapped.Edges[e];
                    int[] ok = Enumerable.Range(0, fe.Points.Length).Where(i => fe.Snapped[i] && fe.Inlier[i]
                        && Point2.Distance(fe.Points[i], pts[e][i]) <= radius[e]).ToArray();
                    bool spread = ok.Length >= 4 && ok.Max() - ok.Min() >= fe.Points.Length / 4;
                    bool final = ok.Length * 2 >= fe.Points.Length && fe.Straight;
                    if (confirmed != null) confirmed[e] = refine[e] && final;
                    if (refine[e] && (final || (pass < 3 && spread))) { edges[e] = fe; if (final) used++; }
                    else edges[e] = plain.Edges[e];
                    if (Environment.GetEnvironmentVariable("KK_DEBUG") == "1")
                    {
                        // Iz: her noktanin kaba cizgiden kaymasi (mm, + disari), oturdu mu (o), dogruya uydu mu (aykiri x)
                        Point2 d0 = pts[e][pts[e].Count - 1] - pts[e][0];
                        var nrm = new Point2(d0.Y, -d0.X) * (1 / Math.Max(1e-9, d0.Length));
                        Point2 ctr = new Point2(cur.Average(c => c.X), cur.Average(c => c.Y));
                        if (Point2.Dot(nrm, pts[e][0] - ctr) < 0) nrm = nrm * -1;
                        double mmPerPx = Point2.Distance(_cal.ImageToTable.Transform(pts[e][0]), _cal.ImageToTable.Transform(pts[e][0] + nrm));
                        string marks = string.Join(" ", Enumerable.Range(0, fe.Points.Length).Select(i => !fe.Snapped[i] ? "-"
                            : (Point2.Dot(fe.Points[i] - pts[e][i], nrm) * mmPerPx).ToString("+0.0;-0.0", System.Globalization.CultureInfo.InvariantCulture) + (fe.Inlier[i] ? "" : "x")));
                        Console.WriteLine("    [oturtma tur {0} kenar {1}] oturan {2}/{3} duz {4} -> {5}: {6}", pass, e, ok.Length, fe.Points.Length,
                            fe.Straight ? "evet" : "hayir", ReferenceEquals(edges[e], fe) ? (final ? "kesin" : "gecici") : "kaba", marks);
                        if (_opt.CaptureTraceMask != null)
                        {
                            string Points(IEnumerable<Point2> points) => string.Join(" ", points.Select(point =>
                            {
                                Point2 mm = _cal.ImageToTable.Transform(point);
                                return FormattableString.Invariant($"({mm.X - TableInsetMm:F2},{mm.Y - TableInsetMm:F2})");
                            }));
                            Console.WriteLine($"      [profil t{pass} k{e} tabla_mm] arama_merkezleri={Points(pts[e])} gecisler={Points(ok.Select(i => fe.Points[i]))}");
                        }
                    }
                }
                var next = new Point2[4];
                for (int i = 0; i < 4; i++)
                {
                    FittedEdge p = edges[(i + 3) % 4], q = edges[i];
                    double den = p.A * q.B - q.A * p.B;
                    if (Math.Abs(den) < 1e-6) return r;
                    next[i] = new Point2((p.B * q.C - q.B * p.C) / den, (q.A * p.C - p.A * q.C) / den);
                    // Kose kaba konumdan arama yaricapinin dort katindan fazla kaydiysa oturtma baska bir gecise atlamis
                    if (Point2.Distance(next[i], vPx[i]) > 4 * rMax) return r;
                }
                double moved = Enumerable.Range(0, 4).Max(i => Point2.Distance(next[i], cur[i]));
                cur = next;
                cornersPx = next;
                if (moved < 0.3 && pass > 0) break;
            }
            if (used == 0) return r;

            Point2[] cornersMm = cornersPx.Select(_cal.ImageToTable.Transform).ToArray();
            // Duz kenarli yogun kontur: kalinlik duzeltmesi ve olcu aciklamasi ayni yoldan gecsin
            var dense = new List<Point2>();
            for (int e = 0; e < 4; e++)
            {
                Point2 a = cornersMm[e], b = cornersMm[(e + 1) % 4];
                for (int k = 0; k < 40; k++) dense.Add(a + (b - a) * (k / 40.0));
            }
            SlabResult refined = SlabLocator.Locate(dense, loc, t, cam);
            if (refined == null || refined.Vertices.Length != 4) return r;
            _refineUsed = used;
            if (confirmed != null && used == 3)
                refined = RetryTopFaceEdge(refined, cornersMm, Array.IndexOf(confirmed, false), loc);
            RefineLog.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0}/4 kenar, kose kaymasi {1:F1} px", used, Enumerable.Range(0, 4).Average(i => Point2.Distance(cornersPx[i], vPx[i]))));
            return refined;
        }

        private SlabResult RetryTopFaceEdge(SlabResult slab, Point2[] silhouette, int missing, LocatorOptions options)
        {
            Point2[] v = (Point2[])slab.Vertices.Clone();
            Point2 mid = (silhouette[missing] + silhouette[(missing + 1) % 4]) * 0.5;
            // Kalinlik duzeltmesi sonrasi baslangic kosesi degisebilir. Kenari merkezinden eslestir.
            int i = Enumerable.Range(0, 4).OrderBy(n => Point2.Distance(mid, (v[n] + v[(n + 1) % 4]) * 0.5)).First();
            int j = (i + 1) % 4, prev = (i + 3) % 4, next = (i + 2) % 4;
            double across = Math.Min(Point2.Distance(v[prev], v[i]), Point2.Distance(v[j], v[next]));
            bool found = BorderEdgeFinder.TryRefineTopEdge(v[i], v[j], across, TopFaceSampler, out Point2 p, out Point2 d, out string why);
            bool debug = Environment.GetEnvironmentVariable("KK_DEBUG") == "1";
            if (debug) Console.WriteLine("  [ust yuzey] kenar " + i + " " + why + (found ? " destekli" : " yetersiz"));
            if (!found) return slab;

            Point2 startSide = v[i] - v[prev], endSide = v[j] - v[next];
            double startCross = Point2.Cross(startSide, d), endCross = Point2.Cross(endSide, d);
            if (Math.Abs(startCross) < 0.1 * startSide.Length || Math.Abs(endCross) < 0.1 * endSide.Length) return slab;
            Point2 a = v[prev] + startSide * (Point2.Cross(p - v[prev], d) / startCross);
            Point2 b = v[next] + endSide * (Point2.Cross(p - v[next], d) / endCross);
            if (Point2.Distance(a, v[i]) > 30 || Point2.Distance(b, v[j]) > 30) return slab;
            double inset = TableInsetMm - BorderEdgeFinder.OverhangMm;
            bool WithinMeasureBoundary(Point2 q) => q.X >= inset && q.Y >= inset
                && q.X <= _cal.TableLengthMm - inset && q.Y <= _cal.TableWidthMm - inset;
            if (!WithinMeasureBoundary(a) || !WithinMeasureBoundary(b)) return slab;
            v[i] = a;
            v[j] = b;
            if (PolygonMath.SignedArea(v) <= 0 || !PolygonMath.IsConvex(v)) return slab;
            _refineUsed++;
            return SlabLocator.Describe(v, options);
        }

        /// <summary>
        /// Bulunan cokgenin gercekten duz kenarli bir plaka olup olmadigini sinar.
        /// Mermer plaka: siluet doluluk orani yuksek, kontur noktalari cokgen kenarlarina yakin,
        /// kose sayisi az, kenarlar birbirini kesmiyor. Aletler, kablolar, makine parcalari ve
        /// ust uste yiginlar bu sinamalardan birine takilir.
        /// </summary>
        private void Assess(SlabResult r, Point2[] contour)
        {
            var notes = new List<string>();
            Point2[] v = r.Vertices;
            if (v.Length > _opt.MaxVertices) notes.Add(v.Length + " kose");
            if (SelfIntersects(v)) notes.Add("kenarlari kesisiyor");

            double area = Math.Abs(PolygonMath.SignedArea(contour));
            Point2f[] hull = Cv2.ConvexHull(contour.Select(p => new Point2f((float)p.X, (float)p.Y)));
            double hullArea = Math.Abs(PolygonMath.SignedArea(hull.Select(p => new Point2(p.X, p.Y)).ToList()));
            double solidity = hullArea > 0 ? area / hullArea : 0;
            if (solidity < _opt.MinSolidity) notes.Add("sekli girintili cikintili");

            // Kalinlik duzeltmesi kenarlari bir miktar kaydirir; tolerans buna gore
            double tol = _opt.FitToleranceMm + _cal.SlabThicknessMm;
            int near = 0;
            foreach (Point2 p in contour)
            {
                double best = double.MaxValue;
                for (int i = 0; i < v.Length; i++) best = Math.Min(best, SegmentDistance(p, v[i], v[(i + 1) % v.Length]));
                if (best <= tol) near++;
            }
            double fit = contour.Length > 0 ? (double)near / contour.Length : 0;
            if (fit < _opt.MinEdgeFit) notes.Add("kenarlari duz degil");

            if (_opt.CaptureTraceMask != null)
                Console.WriteLine(FormattableString.Invariant($"  [sekil kontrol] kose={v.Length} doluluk={solidity:F3} min_doluluk={_opt.MinSolidity:F3} kontur_uyumu={fit:F3} min_uyum={_opt.MinEdgeFit:F3} uyum_toleransi_mm={tol:F2}"));
            r.IsReliable = notes.Count == 0;
            r.Note = notes.Count == 0 ? null : "Mermer gibi gorunmuyor: " + string.Join(", ", notes);
        }

        /// <summary>
        /// Kenar tabanli bulucuyu calistirir ve sonuclarini renk sonuclariyla birlestirir.
        /// Yedek: ayni yerde guvenilir renk sonucu varsa kenar sonucu eklenmez. Oncelikli: kenar sonucu
        /// ayni yerdeki renk sonucunun yerine gecer. Yedek kipte renk sonuclari zaten guveniliyse
        /// bulucu hic calistirilmaz (7-8 MP ustten goruntude birkac saniyelik is; bos yere harcanmaz).
        /// </summary>
        private void AddEdgeResults(Mat frame, List<SlabResult> results)
        {
            if (_opt.EdgeMode == EdgeMode.Fallback && results.Count > 0 && results.All(m => m.IsReliable)) return;
            List<Point2[]> quads;
            using (Mat rect = Rectify(frame))
            {
                var eo = _opt.Edges ?? new EdgeRectOptions();
                eo.BorderMm = Math.Max(eo.BorderMm, _opt.BorderMarginMm * 0.5 + TableInsetMm);
                // Bulucu yalniz kaba konum verir; koseler RefineInImage ile tam cozunurlukte, piksel altina oturur.
                // Bu yuzden kucultulmus goruntude aramak yeterli ve birkac kat hizli (LSD ve profil taramasi).
                double findScale = Scale;
                Mat small = rect;
                if (rect.Cols > 2000)
                {
                    findScale = Scale * 2000.0 / rect.Cols;
                    small = new Mat();
                    Cv2.Resize(rect, small, new Size(2000, (int)Math.Round(rect.Rows * 2000.0 / rect.Cols)), 0, 0, InterpolationFlags.Area);
                }
                try { quads = EdgeRectFinder.Find(small, findScale, _cal.TableLengthMm, _cal.TableWidthMm, eo); }
                finally { if (!ReferenceEquals(small, rect)) small.Dispose(); }
            }
            foreach (Point2[] q in quads)
            {
                // Dortgeni yogun kontura cevir; kose bulma + kalinlik duzeltmesi ayni yoldan gecsin
                var contour = new List<Point2>();
                for (int i = 0; i < 4; i++)
                {
                    Point2 a = q[i], b = q[(i + 1) % 4];
                    int k = Math.Max(2, (int)(Point2.Distance(a, b) / 2));
                    for (int j = 0; j < k; j++) contour.Add(a + (b - a) * ((double)j / k));
                }
                Point2[] c = contour.ToArray();
                SlabResult r = SlabLocator.Locate(c, ScaledLocator(c), _cal.SlabThicknessMm, _cal.Camera);
                if (r == null) continue;
                EdgePointFit.GradientField g = null;
                r = RefineInImage(frame, c, r, () => g ?? (g = EdgePointFit.Gradients(frame)));
                Assess(r, c);
                r.Method = "kenar";
                if (MaskCoverage(q) < MinEdgeQuadMaskCoverage) continue;   // ici bos tablayla ayni: cita araliklarindan sahte dortgen
                var overlap = results.Where(m => PolygonMath.Contains(q, m.Centroid) || PolygonMath.Contains(m.Vertices, r.Centroid)).ToList();
                // Kenar seridine dayanan dortgen: bir kenari serit siniridir (bkz. Evaluate); yerinde baska sonuc
                // yoksa uyariyla raporlanir ki parca hic gorunmez olmasin
                if (r.IsReliable && NearBorder(r.Vertices, 0))
                {
                    string note;
                    SlabResult b = BorderRefine(frame, () => g ?? (g = EdgePointFit.Gradients(frame)), c, q, out note);
                    if (b != null)
                    {
                        b.Method = "kenar";
                        if (overlap.Any(m => m.IsReliable)) continue;
                        foreach (SlabResult m in overlap)
                            if (m.AreaMm2 <= 1.3 * b.AreaMm2) results.Remove(m);
                        results.Add(b);
                        continue;
                    }
                    r.IsReliable = false;
                    r.Note = note;
                    if (overlap.Count == 0) results.Add(r);
                    continue;
                }
                if (!r.IsReliable) continue;
                if (_opt.EdgeMode == EdgeMode.Fallback && overlap.Any(m => m.IsReliable)) continue;
                // Plakadan belirgin buyuk guvenilmez leke baska parcalari da icerebilir: silinmez, Peel kalanini olcer
                foreach (SlabResult m in overlap)
                    if (m.IsReliable || m.AreaMm2 <= 1.3 * r.AreaMm2) results.Remove(m);
                results.Add(r);
            }
        }

        /// <summary>
        /// Kose bulma toleranslarini parcanin boyuna gore kucultur: secenekteki degerler ust sinirdir.
        /// Buyuk mermerde degismez; kucuk parcada kisa kenarlarin pürüz sayilip atilmasini onler.
        /// </summary>
        private LocatorOptions ScaledLocator(Point2[] contour)
        {
            RotatedRect box = Cv2.MinAreaRect(contour.Select(p => new Point2f((float)p.X, (float)p.Y)));
            double shortSide = Math.Min(box.Size.Width, box.Size.Height);
            LocatorOptions o = _opt.Locator ?? new LocatorOptions();
            return new LocatorOptions
            {
                SimplifyToleranceMm = Math.Min(o.SimplifyToleranceMm, Math.Max(2, 0.06 * shortSide)),
                MinEdgeMm = Math.Min(o.MinEdgeMm, Math.Max(10, 0.3 * shortSide)),
                MinCornerAngleDeg = o.MinCornerAngleDeg,
                RightAngleToleranceDeg = o.RightAngleToleranceDeg,
                SquareSideTolerance = o.SquareSideTolerance
            };
        }

        private static double SegmentDistance(Point2 p, Point2 a, Point2 b)
        {
            Point2 ab = b - a;
            double l2 = Point2.Dot(ab, ab);
            double t = l2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, Point2.Dot(p - a, ab) / l2));
            return Point2.Distance(p, a + ab * t);
        }

        private static bool SelfIntersects(Point2[] v)
        {
            int n = v.Length;
            for (int i = 0; i < n; i++)
                for (int j = i + 2; j < n; j++)
                {
                    if (i == 0 && j == n - 1) continue;   // komsu kenarlar
                    if (SegmentsCross(v[i], v[(i + 1) % n], v[j], v[(j + 1) % n])) return true;
                }
            return false;
        }

        private static bool SegmentsCross(Point2 a, Point2 b, Point2 c, Point2 d)
        {
            double d1 = Point2.Cross(b - a, c - a), d2 = Point2.Cross(b - a, d - a);
            double d3 = Point2.Cross(d - c, a - c), d4 = Point2.Cross(d - c, b - c);
            return ((d1 > 0) != (d2 > 0)) && ((d3 > 0) != (d4 > 0));
        }

        /// <summary>Tabladan farkli gorunen bolgelerin dis konturlari (tabla mm, kalinlik duzeltmesi yok).</summary>
        /// <summary>Golge maskesi (255): referanstan karanlik, rengi parlaklikla olceklenmis referans rengiyle ayni.</summary>
        private static Mat ShadowMask(Mat lab, Mat refLab, double minRatio, double maxRatio, double colorTol)
        {
            Mat[] m = Cv2.Split(lab), r = Cv2.Split(refLab);
            try
            {
                using (var lm = new Mat()) using (var lr = new Mat()) using (var q = new Mat())
                using (var am = new Mat()) using (var ar = new Mat()) using (var bm = new Mat()) using (var br = new Mat())
                using (var t = new Mat()) using (var u = new Mat())
                {
                    m[0].ConvertTo(lm, MatType.CV_32F); r[0].ConvertTo(lr, MatType.CV_32F);
                    Cv2.Divide(lm, lr + new Scalar(1e-3), q);   // parlaklik orani
                    // Renk: a* = a - 128; referansin a* degeri (L + 41) oraninda olceklenir
                    using (var qc = new Mat())
                    {
                        Cv2.Divide(lm + new Scalar(41), lr + new Scalar(41), qc);
                        m[1].ConvertTo(am, MatType.CV_32F, 1, -128); r[1].ConvertTo(ar, MatType.CV_32F, 1, -128);
                        m[2].ConvertTo(bm, MatType.CV_32F, 1, -128); r[2].ConvertTo(br, MatType.CV_32F, 1, -128);
                        Cv2.Multiply(ar, qc, ar); Cv2.Multiply(br, qc, br);
                    }
                    var res = new Mat(lab.Size(), MatType.CV_8UC1, Scalar.All(255));
                    Cv2.InRange(q, new Scalar(minRatio), new Scalar(maxRatio), t); Cv2.BitwiseAnd(res, t, res);
                    Cv2.Absdiff(am, ar, u); Cv2.Threshold(u, u, colorTol, 255, ThresholdTypes.BinaryInv); u.ConvertTo(t, MatType.CV_8U); Cv2.BitwiseAnd(res, t, res);
                    Cv2.Absdiff(bm, br, u); Cv2.Threshold(u, u, colorTol, 255, ThresholdTypes.BinaryInv); u.ConvertTo(t, MatType.CV_8U); Cv2.BitwiseAnd(res, t, res);
                    return res;
                }
            }
            finally { foreach (Mat x in m) x.Dispose(); foreach (Mat x in r) x.Dispose(); }
        }

        /// <summary>
        /// Bos tabla fotografi olcumden saatler once cekilebilir; gun isigi ve kameranin beyaz dengesi bu arada tum
        /// tablayi ayni yonde kaydirir. Olculen (2026-11-24, 11:46 olcum / 12:34 bos tabla): bos tahtada fark L +1,
        /// a +2, b +6; bu kayma tahta lekelerini esigin ustune cikarip sahte parca yapiyor, acik ahsap uzerindeki gri
        /// levhayi ise esigin altina itiyordu (birlesik fark 11 < 12). Referans, tablanin tamamindaki ortanca farkla
        /// kaydirilir (parcalar tablanin yarisindan azini kaplar; ortanca onlardan etkilenmez).
        /// </summary>
        private static Mat AddTo(Mat src, Scalar v, Mat dst) { Cv2.Add(src, v, dst); return dst; }

        private static Mat LightMatchedReference(Mat lab, Mat reference, out Scalar shiftOut)
        {
            var shift = new double[3];
            var samples = new List<int>[] { new List<int>(), new List<int>(), new List<int>() };
            var ia = lab.GetGenericIndexer<Vec3b>();
            var ib = reference.GetGenericIndexer<Vec3b>();
            for (int y = 0; y < lab.Rows; y += 4)
                for (int x = 0; x < lab.Cols; x += 4)
                {
                    Vec3b p = ia[y, x], q = ib[y, x];
                    samples[0].Add(p.Item0 - q.Item0); samples[1].Add(p.Item1 - q.Item1); samples[2].Add(p.Item2 - q.Item2);
                }
            for (int c = 0; c < 3; c++) { samples[c].Sort(); shift[c] = samples[c].Count == 0 ? 0 : samples[c][samples[c].Count / 2]; }
            if (Environment.GetEnvironmentVariable("KK_DEBUG") == "1")
                Console.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "  [isik] bos tablaya gore tabla kaymasi L{0:+0;-0;0} a{1:+0;-0;0} b{2:+0;-0;0}", shift[0], shift[1], shift[2]));
            var res = new Mat();
            shiftOut = new Scalar(shift[0], shift[1], shift[2]);
            Cv2.Add(reference, shiftOut, res);   // 8 bit: 0-255 arasinda kirpilir
            return res;
        }

        public List<Point2[]> FindContoursMm(Mat frame)
        {
            double s = Scale;
            var contoursMm = new List<Point2[]>();
            Scalar lightShift = new Scalar(0, 0, 0);
            using (Mat lab = ToLab(frame))
            using (Mat refLab = _hasRealReference ? LightMatchedReference(lab, _referenceLab, out lightShift) : _referenceLab.Clone())
            using (var diff = new Mat())
            using (var mask = new Mat())
            using (var tmp = new Mat())
            {
                Cv2.Absdiff(lab, refLab, diff);
                Mat[] d = Cv2.Split(diff);
                Mat[] l = Cv2.Split(lab);
                try
                {
                    // Renk farki: a veya b kanali esigi asiyor
                    Cv2.Threshold(d[1], mask, _opt.ColorThreshold, 255, ThresholdTypes.Binary);
                    Cv2.Threshold(d[2], tmp, _opt.ColorThreshold, 255, ThresholdTypes.Binary);
                    Cv2.BitwiseOr(mask, tmp, mask);
                    // Parlaklik: bos tabla referansi varsa fark, yoksa mutlak koyuluk (isik tabla boyunca degistigi icin)
                    if (_hasRealReference) Cv2.Threshold(d[0], tmp, _opt.BrightnessDiffThreshold, 255, ThresholdTypes.Binary);
                    else Cv2.Threshold(l[0], tmp, _opt.DarkThreshold, 255, ThresholdTypes.BinaryInv);
                    Cv2.BitwiseOr(mask, tmp, mask);
                    // Referanssiz ama tabla tek renkli: tablanin ortanca parlakligindan belirgin farkli bolgeler
                    if (!_hasRealReference && _opt.NoReferenceBrightnessDiff > 0)
                    {
                        Cv2.Threshold(d[0], tmp, _opt.NoReferenceBrightnessDiff, 255, ThresholdTypes.Binary);
                        Cv2.BitwiseOr(mask, tmp, mask);
                    }
                    // Referans varken toplam fark: parlaklik ve renk tek tek esigin altinda kalsa da birlikte
                    // belirginse yakalanir (ornek: acik ahsap uzerinde beyaz mermer).
                    if (_hasRealReference && _opt.CombinedDiffThreshold > 0)
                    {
                        using (var sum = new Mat())
                        using (var f = new Mat())
                        {
                            d[0].ConvertTo(f, MatType.CV_32F, 0.5);   // parlaklik yari agirlikta (golge/isik oynamasi)
                            Cv2.Multiply(f, f, sum);
                            for (int i = 1; i < 3; i++)
                            {
                                d[i].ConvertTo(f, MatType.CV_32F);
                                Cv2.Multiply(f, f, f);
                                Cv2.Add(sum, f, sum);
                            }
                            Cv2.Sqrt(sum, sum);
                            Cv2.Threshold(sum, f, _opt.CombinedDiffThreshold, 255, ThresholdTypes.Binary);
                            f.ConvertTo(tmp, MatType.CV_8U);
                            Cv2.BitwiseOr(mask, tmp, mask);
                        }
                    }
                    // Golge: ayni ahsap, daha az isik. Lab'de L* (8 bit: L*2,55) karanliga gore a*, b* de kuculur;
                    // (L + 41) oraninda olceklenen referans rengiyle kiyaslanir (41 = 16 x 2,55, L* tanimindaki kayma).
                    if (_hasRealReference && _opt.SuppressShadows)
                        using (Mat shadow = ShadowMask(lab, refLab, _opt.ShadowMinRatio, _opt.ShadowMaxRatio, _opt.ShadowColorTolerance))
                        using (var notShadow = new Mat())
                        {
                            Cv2.BitwiseNot(shadow, notShadow);
                            Cv2.BitwiseAnd(mask, notShadow, mask);
                        }
                    // Simdi tabla renginde olmayan, bos tablada tabla renginde olan yerler (bkz. SetReference)
                    if (_hasRealReference && _tableColorLab != null)
                    {
                        // Tabla rengi de ayni isik kaymasiyla (yoksa sinirdaki tahta damarlari bos tabladakinden farkli tarafa duser)
                        using (var tableNow = new Mat())
                        using (Mat off = OffColorMask(lab, AddTo(_tableColorLab, lightShift, tableNow)))
                        using (var notEmpty = new Mat())
                        {
                            Cv2.BitwiseNot(_emptyOffColor, notEmpty);
                            Cv2.BitwiseAnd(off, notEmpty, off);
                            Cv2.BitwiseOr(mask, off, mask);
                        }
                    }
                }
                finally
                {
                    foreach (Mat m in d) m.Dispose();
                    foreach (Mat m in l) m.Dispose();
                }

                int k = ((int)(_opt.CleanupMm * s)) | 1;
                using (Mat kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(k, k)))
                {
                    Cv2.MorphologyEx(mask, mask, MorphTypes.Open, kernel);
                    Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);
                }
                // Parca ici yariklar (kontrasti dusuk cita ustu bolgeler): birlestir, ama kucuk parcalari silme
                int kf = ((int)(_opt.FillGapMm * s)) | 1;
                if (kf > k)
                    using (Mat fill = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(kf, kf)))
                        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, fill);
                if (_opt.CaptureTraceMask != null)
                {
                    try { _opt.CaptureTraceMask(mask.ToBytes(".png")); } catch (OpenCVException) { }
                }
                else if (Environment.GetEnvironmentVariable("KK_DEBUG") == "1")
                {
                    try { mask.SaveImage(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kk_maske.png")); } catch (OpenCVException) { }
                }
                // Kenar bulucunun dortgenleri bu maskeyle sinanir (bkz. MaskCoverage); kenar seridi silinmeden saklanir
                _lastMask?.Dispose();
                _lastMask = mask.Clone();
                // Tabla kenarindaki serit yok sayilir (cizgi kalinligi kenarin iki yanina esit dagilir)
                int margin = (int)(Band * s);
                if (margin > 0)
                    Cv2.Rectangle(mask, new Rect(0, 0, mask.Cols, mask.Rows), Scalar.All(0), 2 * margin);

                Point[][] contours;
                HierarchyIndex[] hierarchy;
                Cv2.FindContours(mask, out contours, out hierarchy, RetrievalModes.External, ContourApproximationModes.ApproxNone);
                foreach (Point[] c in contours)
                {
                    if (Cv2.ContourArea(c) < _opt.MinAreaMm2 * s * s) continue;
                    var mm = new Point2[c.Length];
                    for (int i = 0; i < c.Length; i++) mm[i] = new Point2(c[i].X / s, _cal.TableWidthMm - c[i].Y / s);
                    contoursMm.Add(mm);
                }
            }
            return contoursMm;
        }

        private Mat ToLab(Mat frame)
        {
            var lab = new Mat();
            using (Mat rect = Rectify(frame))
            {
                Cv2.GaussianBlur(rect, lab, new Size(5, 5), 0);
                Cv2.CvtColor(lab, lab, ColorConversionCodes.BGR2Lab);
            }
            return lab;
        }

        public void Dispose()
        {
            _warp.Dispose();
            if (_referenceLab != null) _referenceLab.Dispose();
            _tableColorLab?.Dispose();
            _emptyOffColor?.Dispose();
            _lastMask?.Dispose();
        }
    }
}
