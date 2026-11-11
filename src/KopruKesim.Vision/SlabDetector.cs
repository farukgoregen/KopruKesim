using System;
using System.Collections.Generic;
using KopruKesim.Core;
using OpenCvSharp;

namespace KopruKesim.Vision
{
    public sealed class DetectorOptions
    {
        /// <summary>Duzeltilmis goruntunun olcegi (piksel/mm). 0.5 = 2 mm/piksel.</summary>
        public double PxPerMm = 0.5;
        /// <summary>Tabla rengine gore renk (Lab a/b) farki esigi.</summary>
        public int ColorThreshold = 10;
        /// <summary>Referans goruntusu yokken: parlaklik (Lab L, 0-255) bunun altindaysa koyu tas sayilir.</summary>
        public int DarkThreshold = 60;
        /// <summary>Referans goruntusu varken: parlaklik farki esigi.</summary>
        public int BrightnessDiffThreshold = 40;
        /// <summary>Referans goruntusu varken: parlaklik (yari agirlik) + renk toplam farki esigi. 0 = kapali.</summary>
        public double CombinedDiffThreshold = 16;
        /// <summary>Bundan kucuk lekeler / bosluklar temizlenir (mm).</summary>
        public double CleanupMm = 25;
        /// <summary>Tabla kenarindan bu kadar iceride kalan bolge taranir (mm).</summary>
        public double BorderMarginMm = 40;
        /// <summary>Bundan kucuk nesneler mermer sayilmaz (mm2).</summary>
        public double MinAreaMm2 = 200 * 200;
        public LocatorOptions Locator = new LocatorOptions();
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
        private Mat _referenceLab;
        private bool _hasRealReference;

        public SlabDetector(TableCalibration calibration, DetectorOptions options)
        {
            _cal = calibration;
            _opt = options ?? new DetectorOptions();
            double[] h = _cal.ImageToRectified(_opt.PxPerMm).ToArray();
            _warp = new Mat(3, 3, MatType.CV_64FC1);
            for (int i = 0; i < 9; i++) _warp.Set<double>(i / 3, i % 3, h[i]);
            _rectSize = new Size((int)Math.Round(_cal.TableLengthMm * _opt.PxPerMm), (int)Math.Round(_cal.TableWidthMm * _opt.PxPerMm));
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
        /// Referans yerine tek bir tabla rengi kullanir: verilen goruntude, bos oldugu bilinen
        /// tabla bolgesinin (mm) ortalama rengini olcer.
        /// </summary>
        public void SetTableColor(Mat frame, double minXmm, double minYmm, double maxXmm, double maxYmm)
        {
            using (Mat lab = ToLab(frame))
            {
                double s = _opt.PxPerMm;
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
                SlabResult r = SlabLocator.Locate(contour, _opt.Locator, _cal.SlabThicknessMm, _cal.Camera);
                if (r != null) results.Add(r);
            }
            results.Sort((a, b) => b.AreaMm2.CompareTo(a.AreaMm2));
            return results;
        }

        /// <summary>Tabladan farkli gorunen bolgelerin dis konturlari (tabla mm, kalinlik duzeltmesi yok).</summary>
        public List<Point2[]> FindContoursMm(Mat frame)
        {
            double s = _opt.PxPerMm;
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
            return new Point((int)Math.Round(mm.X * _opt.PxPerMm), (int)Math.Round((_cal.TableWidthMm - mm.Y) * _opt.PxPerMm));
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
