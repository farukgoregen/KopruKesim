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
        /// <summary>Referans goruntusu varken: parlaklik farki esigi.</summary>
        public int BrightnessDiffThreshold = 40;
        /// <summary>
        /// Referans yokken: tablanin ortanca parlakligindan bu kadar farkli bolgeler de parca sayilir.
        /// 0 = kapali. Yalnizca tek renkli, duzgun aydinlatilmis tablada acilmali (ahsap tablada damarlar karisir).
        /// </summary>
        public int NoReferenceBrightnessDiff = 0;
        /// <summary>Referans goruntusu varken: parlaklik (yari agirlik) + renk toplam farki esigi. 0 = kapali.</summary>
        public double CombinedDiffThreshold = 16;
        /// <summary>Bundan kucuk lekeler / bosluklar temizlenir (mm).</summary>
        public double CleanupMm = 25;
        /// <summary>Tabla kenarindan bu kadar iceride kalan bolge taranir (mm).</summary>
        public double BorderMarginMm = 40;
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
    /// Tabla ya bos tabla referans goruntusuyle (onerilen, SetReference) ya da tek bir
    /// tabla rengiyle (SetTableColor) tanitilir; tabladan farkli gorunen bolgeler mermer adayidir.
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

        /// <summary>Bos tablanin goruntusunu referans olarak kaydeder. Isik degisince yenilenmelidir.</summary>
        public void SetReference(Mat emptyTableFrame)
        {
            if (_referenceLab != null) _referenceLab.Dispose();
            _referenceLab = ToLab(emptyTableFrame);
            _hasRealReference = true;
        }

        /// <summary>
        /// Bos tabla goruntusunu, o goruntunun kendi kalibrasyonuyla (ornek: isaretlerden) ustten
        /// gorunume cevirip referans yapar. Kamera iki cekim arasinda oynasa da karsilastirma dogru kalir.
        /// </summary>
        public void SetReference(Mat emptyTableFrame, TableCalibration emptyCalibration)
        {
            double[] h = emptyCalibration.ImageToRectified(Scale).ToArray();
            using (var warp = new Mat(3, 3, MatType.CV_64FC1))
            using (var rect = new Mat())
            {
                for (int i = 0; i < 9; i++) warp.Set<double>(i / 3, i % 3, h[i]);
                Cv2.WarpPerspective(emptyTableFrame, rect, warp, _rectSize);
                if (_referenceLab != null) _referenceLab.Dispose();
                _referenceLab = new Mat();
                Cv2.GaussianBlur(rect, _referenceLab, new Size(5, 5), 0);
                Cv2.CvtColor(_referenceLab, _referenceLab, ColorConversionCodes.BGR2Lab);
                _hasRealReference = true;
            }
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
            if (_referenceLab == null) throw new InvalidOperationException("Once SetReference veya SetTableColor cagrilmali.");
            var results = new List<SlabResult>();
            foreach (Point2[] contour in FindContoursMm(frame))
            {
                SlabResult r = SlabLocator.Locate(contour, ScaledLocator(contour), _cal.SlabThicknessMm, _cal.Camera);
                if (r == null) continue;
                Assess(r, contour);
                r.Method = "renk";
                results.Add(r);
            }
            if (_opt.EdgeMode != EdgeMode.Off) AddEdgeResults(frame, results);
            // Once guvenilir parcalar, sonra alana gore
            results.Sort((a, b) => a.IsReliable != b.IsReliable ? (a.IsReliable ? -1 : 1) : b.AreaMm2.CompareTo(a.AreaMm2));
            return results;
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

            r.IsReliable = notes.Count == 0;
            r.Note = notes.Count == 0 ? null : "Mermer gibi gorunmuyor: " + string.Join(", ", notes);
        }

        /// <summary>
        /// Kenar tabanli bulucuyu calistirir ve sonuclarini renk sonuclariyla birlestirir.
        /// Yedek: ayni yerde guvenilir renk sonucu varsa kenar sonucu eklenmez. Oncelikli: kenar sonucu
        /// ayni yerdeki renk sonucunun yerine gecer.
        /// </summary>
        private void AddEdgeResults(Mat frame, List<SlabResult> results)
        {
            List<Point2[]> quads;
            using (Mat rect = Rectify(frame))
            {
                var eo = _opt.Edges ?? new EdgeRectOptions();
                eo.BorderMm = Math.Max(eo.BorderMm, _opt.BorderMarginMm * 0.5);
                quads = EdgeRectFinder.Find(rect, Scale, _cal.TableLengthMm, _cal.TableWidthMm, eo);
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
                Assess(r, c);
                r.Method = "kenar";
                if (!r.IsReliable) continue;
                var overlap = results.Where(m => PolygonMath.Contains(q, m.Centroid) || PolygonMath.Contains(m.Vertices, r.Centroid)).ToList();
                if (_opt.EdgeMode == EdgeMode.Fallback && overlap.Any(m => m.IsReliable)) continue;
                foreach (SlabResult m in overlap) results.Remove(m);
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
        public List<Point2[]> FindContoursMm(Mat frame)
        {
            double s = Scale;
            var contoursMm = new List<Point2[]>();
            using (Mat lab = ToLab(frame))
            using (var diff = new Mat())
            using (var mask = new Mat())
            using (var tmp = new Mat())
            {
                Cv2.Absdiff(lab, _referenceLab, diff);
                Mat[] d = Cv2.Split(diff);
                Mat[] l = Cv2.Split(lab);
                try
                {
                    // Renk farki: a veya b kanali esigi asiyor
                    Cv2.Threshold(d[1], mask, _opt.ColorThreshold, 255, ThresholdTypes.Binary);
                    Cv2.Threshold(d[2], tmp, _opt.ColorThreshold, 255, ThresholdTypes.Binary);
                    Cv2.BitwiseOr(mask, tmp, mask);
                    // Parlaklik: referans varsa fark, yoksa mutlak koyuluk (isik tabla boyunca degistigi icin)
                    if (_hasRealReference) Cv2.Threshold(d[0], tmp, _opt.BrightnessDiffThreshold, 255, ThresholdTypes.Binary);
                    else Cv2.Threshold(l[0], tmp, _opt.DarkThreshold, 255, ThresholdTypes.BinaryInv);
                    Cv2.BitwiseOr(mask, tmp, mask);
                    // Referanssiz ama tabla tek renkli: tablanin ortanca parlakligindan belirgin farkli bolgeler
                    if (!_hasRealReference && _opt.NoReferenceBrightnessDiff > 0)
                    {
                        Cv2.Threshold(d[0], tmp, _opt.NoReferenceBrightnessDiff, 255, ThresholdTypes.Binary);
                        Cv2.BitwiseOr(mask, tmp, mask);
                    }
                    // Referans varken toplam fark: parlaklik ve renk tek tek esigin altinda kalsa da
                    // birlikte belirginse yakalanir (ornek: acik ahsap uzerinde beyaz mermer).
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
                // Tabla kenarindaki serit yok sayilir (cizgi kalinligi kenarin iki yanina esit dagilir)
                int margin = (int)(_opt.BorderMarginMm * s);
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

        /// <summary>Tabla koordinatini (mm) duzeltilmis goruntudeki piksele cevirir; ekrana cizim icin.</summary>
        public Point ToRectifiedPixel(Point2 mm)
        {
            return new Point((int)Math.Round(mm.X * Scale), (int)Math.Round((_cal.TableWidthMm - mm.Y) * Scale));
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
        }
    }
}
