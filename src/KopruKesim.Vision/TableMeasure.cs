using System;
using System.Collections.Generic;
using System.Linq;
using KopruKesim.Core;
using OpenCvSharp;

namespace KopruKesim.Vision
{
    /// <summary>Bir olcumun sonucu.</summary>
    public sealed class MeasureOutput : IDisposable
    {
        public List<SlabResult> Slabs = new List<SlabResult>();
        /// <summary>Ustten (duzeltilmis) gorunum; cizim icin. Olcum yapilamadiysa null.</summary>
        public Mat Rectified;
        /// <summary>Bu fotograf icin kullanilan kalibrasyon.</summary>
        public TableCalibration Calibration;
        /// <summary>Etiketli olcumde etiketlerden kalibrasyon sonucu; degilse null.</summary>
        public MarkerFit Markers;
        /// <summary>Olcum yapilamadiysa nedeni (Turkce). Doluysa Slabs bostur.</summary>
        public string Error;
        /// <summary>Tek fotografta bulunan tablanin kenar destegi (0-1); kose guvenilirligi icin.</summary>
        public double TableSupport = double.NaN;
        /// <summary>Koseler kayitli tabla profilinden (desen eslestirmesiyle) tasindi.</summary>
        public bool ProfileMatched;

        public void Dispose() { if (Rectified != null) Rectified.Dispose(); }
    }

    /// <summary>
    /// Olcum akisi. Kalibrasyon her fotografin kendisinden kurulur: ya tablanin dort kosesindeki dama
    /// etiketlerinden (RunWithTags) ya da tabla koselerinden (otomatik: RunSingle, elle: RunWithCorners,
    /// kayitli tabladan: RunWithProfile). Kamera fotograftan fotografa yer degistirebilir.
    /// </summary>
    public static class TableMeasure
    {
        /// <summary>Fotografta bulunan tablanin kenarlari en az bu oranda gercek kenar uzerinde olmali.</summary>
        public const double RelocateMinSupport = 0.6;

        /// <summary>
        /// Tek fotograf modu: ayri bir bos tabla kurulumu yoktur. Tabla olcusu bilinir; tabla bu
        /// fotografin kendisinde bulunur ve olcum ayni fotografla yapilir. Iki fotograf arasinda kamera
        /// oynamasi sorunu bu sekilde hic olusmaz. Tabla bulunamazsa Error doldurulur.
        /// </summary>
        public static MeasureOutput RunSingle(Mat frame, double lengthMm, double widthMm, DetectorOptions options, double thicknessMm)
        {
            var output = new MeasureOutput();
            Point2[] c = TableFinder.FindCorners(frame, lengthMm, widthMm);
            double support = c == null ? 0 : TableFinder.EdgeSupport(frame, c).Average();
            output.TableSupport = support;
            if (c == null || support < RelocateMinSupport)
            {
                output.Error = "Tabla bu fotografta bulunamadi. Tablanin tamami gorunecek sekilde yeniden cekin.";
                return output;
            }
            return MeasureWith(output, frame, c, lengthMm, widthMm, options, thicknessMm);
        }

        /// <summary>
        /// Koseler disaridan verilir (kullanicinin onayladigi / surukledigi ya da profilden tasinan).
        /// refine: koseler kenarlara piksel alti oturtulmaya calisilir; kenar destegi artmazsa verilen koseler kalir.
        /// </summary>
        public static MeasureOutput RunWithCorners(Mat frame, Point2[] corners, double lengthMm, double widthMm, DetectorOptions options, double thicknessMm, bool refine, double maxSnapPx = -1)
        {
            var output = new MeasureOutput();
            Point2[] c = refine ? SnapCorners(frame, corners, maxSnapPx) : corners;
            output.TableSupport = TableFinder.EdgeSupport(frame, c).Average();
            try { return MeasureWith(output, frame, c, lengthMm, widthMm, options, thicknessMm); }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
            {
                output.Error = "Bu koselerle gecerli bir tabla olusmuyor. Koseleri sirasiyla yerlestirin.";
                return output;
            }
        }

        /// <summary>
        /// Tabla profiliyle olcum: referans fotograftaki onayli koseler, yuzey deseni eslestirilerek
        /// bu fotografa tasinir, sonra kenarlara oturtulur. Eslesme olmazsa Error doludur
        /// (cagiran isterse RunSingle'a duser). match her durumda doldurulur.
        /// </summary>
        public static MeasureOutput RunWithProfile(Mat frame, TableProfile profile, Mat profileImage, DetectorOptions options, double thicknessMm, out TableMatch match)
        {
            match = TableMatcher.Locate(profileImage, profile.Corners, frame);
            if (match.Corners == null)
            {
                var failed = new MeasureOutput { Error = match.Error ?? "Kayitli tabla bu fotografta bulunamadi." };
                return failed;
            }
            // Tasinan koseler zaten piksel alti (homografi hatasi ~1 px); oturtma yalniz birkac piksel duzeltebilir.
            // Genis pencere, golgeli ya da dusuk kontrastli kenarda iceri dogru yanlis cizgiye atlayabiliyor
            // (2026-11-18 ofis masasi: sol ust kose 17 px iceri kaymisti).
            double maxSnap = Math.Max(3.0, 3.0 * (double.IsNaN(match.RmsPx) ? 1.0 : match.RmsPx));
            MeasureOutput output = RunWithCorners(frame, match.Corners, profile.TableLengthMm, profile.TableWidthMm, options, thicknessMm, true, maxSnap);
            if (output.Error != null) return output;
            // Desen eslestirmesi yanlis ama makul bir homografi verebilir (tekrarlayan delikler, testere kolu);
            // tasinan koseler gercek tabla kenarlarina oturmuyorsa profil sonucu kullanilmaz.
            if (double.IsNaN(output.TableSupport) || output.TableSupport < RelocateMinSupport)
            {
                string reason = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "Kayitli tabladan tasinan koseler tabla kenarlarina oturmadi (kenar destegi {0:F2}).", output.TableSupport);
                output.Dispose();
                return new MeasureOutput { Error = reason, TableSupport = output.TableSupport };
            }
            output.ProfileMatched = true;
            return output;
        }

        /// <summary>
        /// Koseleri kenarlara kucuk bir pencerede oturtur. Kenar destegi belirgin artmazsa ya da bir kose
        /// beklenenden cok kayarsa (yanlis paralel cizgiye atlama) verilen koseler korunur.
        /// </summary>
        public static Point2[] SnapCorners(Mat frame, Point2[] corners, double maxMovePx = -1)
        {
            int win = Math.Max(6, Math.Max(frame.Cols, frame.Rows) / 250);
            double limit = maxMovePx > 0 ? maxMovePx : win * 2.5;
            Point2[] refined;
            try { refined = TableFinder.RefineCorners(frame, corners, win); }
            catch (Exception) { return corners; }
            if (refined == null) return corners;
            for (int i = 0; i < 4; i++)
            {
                double dx = refined[i].X - corners[i].X, dy = refined[i].Y - corners[i].Y;
                if (Math.Sqrt(dx * dx + dy * dy) > limit) return corners;
            }
            double before = TableFinder.EdgeSupport(frame, corners).Average();
            double after = TableFinder.EdgeSupport(frame, refined).Average();
            return after >= before + 0.02 ? refined : corners;
        }

        private static MeasureOutput MeasureWith(MeasureOutput output, Mat frame, Point2[] c, double lengthMm, double widthMm, DetectorOptions options, double thicknessMm)
        {
            var cal = new TableCalibration
            {
                TableLengthMm = lengthMm, TableWidthMm = widthMm, SlabThicknessMm = thicknessMm,
                ImageWidth = frame.Cols, ImageHeight = frame.Rows,
                CornerOrigin = c[0], CornerYEnd = c[1], CornerFar = c[2], CornerXEnd = c[3]
            };
            cal.Build();
            output.Calibration = cal;
            using (var detector = new SlabDetector(cal, options))
            {
                detector.SetTableColorAuto(frame);
                output.Slabs = detector.Detect(frame);
                output.Rectified = detector.Rectify(frame);
            }
            return output;
        }

        /// <summary>
        /// Etiketlerin kendisi (beyaz sayfa, dama tahtasi) olcum sonucuna parca olarak girmesin: merkezi bir
        /// etiketin kapladigi alanin icinde kalan parcalar atilir.
        /// </summary>
        private static void RemoveTags(List<SlabResult> slabs, MarkerLayout layout)
        {
            // Dama etiketi tabla kosesine dayali: kosesinden merkezine olan uzakligin iki kati etiketin kendisi.
            // Etiketin kareleri, kenari ya da yanindaki tabla disi bolgeyle birlesen leke etiket alanina tasar;
            // kosesinin cogu (ya da agirlik merkezi) etiket alaninda kalan parca etikettir.
            const double pad = 25;
            // Kenarin ortasindaki etikette dayanilan nokta kenar uzerindedir; o eksende etiketin yari boyu
            // (kose etiketinden cikarilan) kullanilir.
            double halfAlong = layout.MarkerSizeMm > 0 ? layout.MarkerSizeMm / 2 : 100;
            var zones = layout.Markers.Select(m =>
            {
                Point2 a = layout.AnchorOf(m.X, m.Y);
                double hx = Math.Abs(m.X - a.X) > 1e-6 ? Math.Abs(m.X - a.X) : halfAlong;
                double hy = Math.Abs(m.Y - a.Y) > 1e-6 ? Math.Abs(m.Y - a.Y) : halfAlong;
                return new[] { m.X - hx - pad, m.Y - hy - pad, m.X + hx + pad, m.Y + hy + pad };
            }).ToList();
            Func<Point2, bool> inTag = p => zones.Any(z => p.X >= z[0] && p.X <= z[2] && p.Y >= z[1] && p.Y <= z[3]);
            slabs.RemoveAll(s =>
            {
                if (s.Vertices == null || s.Vertices.Length == 0) return inTag(s.Centroid);
                Point2 c = PolygonMath.Centroid(s.Vertices);
                return inTag(c) || s.Vertices.Count(inTag) * 2 >= s.Vertices.Length;
            });
        }

        /// <summary>
        /// Iki fotografli olcum (6 etiket): <paramref name="lowHalf"/> tablanin Y = 0 tarafindaki yarisini,
        /// <paramref name="highHalf"/> Y = genislik tarafindaki yarisini gorur. Her fotograf kendi 4 etiketiyle
        /// (iki kose + kisa kenarlarin ortasindaki iki etiket) ayri kalibre edilir; iki fotograf mm izgarasinda
        /// (ustten gorunum) birlestirilir. Dikis tablanin orta cizgisidir (Y = genislik / 2); orta etiketler iki
        /// fotografta da ayni yerde oldugu icin dikis onlara kilitlidir. Olcum birlesik gorunumde yapilir; dikisten
        /// gecen parca tek parca bulunur. Kalinlik duzeltmesinde her parca kendi yarisinin kamerasini kullanir.
        /// </summary>
        /// <param name="lowIds">Y = 0 yarisinin 4 etiketi, tabladaki dortgen boyunca sirayla.</param>
        /// <param name="highIds">Y = genislik yarisinin 4 etiketi, sirayla.</param>
        public static MeasureOutput RunTwoViews(Mat lowHalf, Mat highHalf, MarkerLayout layout, int[] lowIds, int[] highIds,
            DetectorOptions options, double thicknessMm, out MarkerFit lowFit, out MarkerFit highFit)
        {
            var output = new MeasureOutput();
            lowFit = SolveHalf(lowHalf, layout, lowIds, -1);
            highFit = SolveHalf(highHalf, layout, highIds, +1);
            if (lowFit.Calibration == null) { output.Error = "1. fotograf (Y = 0 tarafi): " + lowFit.Error; output.Markers = lowFit; return output; }
            if (highFit.Calibration == null) { output.Error = "2. fotograf (Y = genislik tarafi): " + highFit.Error; output.Markers = highFit; return output; }

            double L = layout.TableLengthMm, W = layout.TableWidthMm;
            // Olcek: iki fotograftan dogal cozunurlugu yuksek olan (ayrinti kaybolmasin), piksel siniri icinde
            double k = Math.Max(SlabDetector.AutoScale(lowFit.Calibration, options.MaxRectifiedPixels),
                                SlabDetector.AutoScale(highFit.Calibration, options.MaxRectifiedPixels));
            k = Math.Min(k, Math.Sqrt(options.MaxRectifiedPixels / (L * W)));
            int cw = (int)Math.Round(L * k), ch = (int)Math.Round(W * k);
            var size = new Size(cw, ch);
            Mat canvas = new Mat(size, MatType.CV_8UC3, Scalar.All(0));
            using (var lowTop = new Mat())
            using (var highTop = new Mat())
            using (var lowMask = new Mat())
            using (var highMask = new Mat())
            {
                Warp(lowHalf, lowFit.Calibration, k, size, lowTop, lowMask);
                Warp(highHalf, highFit.Calibration, k, size, highTop, highMask);
                // Ustten gorunumde Y yukari: Y = W/2 cizgisi satir ch/2. Ustteki satirlar (Y > W/2) 2. fotograftan.
                int mid = ch / 2;
                using (var pick = new Mat(size, MatType.CV_8UC1, Scalar.All(0)))
                {
                    Cv2.Rectangle(pick, new Rect(0, mid, cw, ch - mid), Scalar.All(255), -1);   // alt yari: 1. fotograf
                    using (var useLow = new Mat())
                    using (var useHigh = new Mat())
                    using (var notPick = new Mat())
                    using (var notHighMask = new Mat())
                    using (var notLowMask = new Mat())
                    using (var tmp = new Mat())
                    {
                        // Secilen fotograf o noktayi gormuyorsa digeri kullanilir
                        Cv2.BitwiseNot(pick, notPick);
                        Cv2.BitwiseNot(highMask, notHighMask);
                        Cv2.BitwiseNot(lowMask, notLowMask);
                        Cv2.BitwiseAnd(pick, lowMask, useLow);
                        Cv2.BitwiseAnd(notPick, notHighMask, tmp);
                        Cv2.BitwiseAnd(tmp, lowMask, tmp);
                        Cv2.BitwiseOr(useLow, tmp, useLow);
                        Cv2.BitwiseNot(useLow, useHigh);
                        Cv2.BitwiseAnd(useHigh, highMask, useHigh);
                        lowTop.CopyTo(canvas, useLow);
                        highTop.CopyTo(canvas, useHigh);
                    }
                }
            }

            // Birlesik gorunumun kendi kalibrasyonu: piksel = mm x k (orijin sol altta, Y yukari)
            Func<CameraPose, TableCalibration> canvasCal = cam =>
            {
                var c = new TableCalibration
                {
                    TableLengthMm = L, TableWidthMm = W, SlabThicknessMm = thicknessMm,
                    ImageWidth = cw, ImageHeight = ch, ManualCamera = cam,
                    CornerOrigin = new Point2(0, W * k), CornerYEnd = new Point2(0, 0),
                    CornerFar = new Point2(L * k, 0), CornerXEnd = new Point2(L * k, W * k)
                };
                c.Build();
                return c;
            };
            // Ustten gorunum zaten k olceginde: dedektor yeniden orneklemesin (secenek cagirana geri verilir)
            double oldScale = options.PxPerMm;
            options.PxPerMm = k;
            try
            {
                output.Calibration = canvasCal(lowFit.Calibration.Camera);
                using (var detector = new SlabDetector(output.Calibration, options))
                {
                    detector.SetTableColorAuto(canvas);
                    output.Slabs = detector.Detect(canvas);
                }
                // Kalinlik duzeltmesi kameraya baglidir: Y = W/2'nin ustundeki parcalar 2. fotografin kamerasiyla
                if (thicknessMm > 0 && highFit.Calibration.Camera != null)
                {
                    using (var detector = new SlabDetector(canvasCal(highFit.Calibration.Camera), options))
                    {
                        detector.SetTableColorAuto(canvas);
                        List<SlabResult> high = detector.Detect(canvas);
                        output.Slabs = output.Slabs.Where(s => s.Centroid.Y < W / 2).Concat(high.Where(s => s.Centroid.Y >= W / 2)).ToList();
                    }
                }
            }
            finally { options.PxPerMm = oldScale; }
            RemoveTags(output.Slabs, layout);
            output.Rectified = canvas;
            output.Markers = lowFit.RmsMm >= highFit.RmsMm ? lowFit : highFit;   // kotu olani goster
            return output;
        }

        private static MarkerFit SolveHalf(Mat image, MarkerLayout layout, int[] ids, int side)
        {
            List<KeyValuePair<Point2, Point2[]>> boards = CheckerFinder.FindAll(image, layout.PatternCols, layout.PatternRows);
            return HalfViewSolver.Solve(layout, ids, boards.Select(b => b.Key).ToList(), boards.Select(b => b.Value).ToList(), image.Cols, image.Rows, side);
        }

        /// <summary>Fotografi ustten gorunume cevirir; mask fotografin gercekten gordugu yerlerdir.</summary>
        private static void Warp(Mat image, TableCalibration cal, double k, Size size, Mat top, Mat mask)
        {
            Homography h = cal.ImageToRectified(k);
            double[] a = h.ToArray();
            using (var m = new Mat(3, 3, MatType.CV_64FC1))
            using (var ones = new Mat(image.Size(), MatType.CV_8UC1, Scalar.All(255)))
            {
                for (int i = 0; i < 9; i++) m.Set<double>(i / 3, i % 3, a[i]);
                Cv2.WarpPerspective(image, top, m, size, InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
                Cv2.WarpPerspective(ones, mask, m, size, InterpolationFlags.Nearest, BorderTypes.Constant, Scalar.All(0));
            }
            // Kenar pikselleri karisik: maskeyi biraz daralt
            using (var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5)))
                Cv2.Erode(mask, mask, kernel);
        }

        /// <summary>Koselerdeki dama etiketlerini bulur, merkezlerden kalibrasyonu hesaplar, tum ic koselerle inceltir.</summary>
        private static MarkerFit SolveTags(Mat image, MarkerLayout layout, out int found)
        {
            Dictionary<int, Point2[]> corners;
            Dictionary<int, Point2> tags = CheckerFinder.Find(image, layout.PatternCols, layout.PatternRows, out corners);
            found = tags.Count;
            MarkerFit fit = MarkerSolver.Solve(layout, tags, image.Cols, image.Rows);
            if (fit.Calibration != null) CheckerGridFit.Refine(layout, fit, corners, image.Cols, image.Rows);
            return fit;
        }

        /// <summary>
        /// Dama etiketli olcum: kalibrasyon bu fotograftaki etiketlerden hesaplanir (tum ic koselerle
        /// inceltilir). Yeterli etiket yoksa Error doludur.
        /// </summary>
        public static MeasureOutput RunWithTags(Mat frame, MarkerLayout layout, DetectorOptions options, double thicknessMm)
        {
            var output = new MeasureOutput();
            int found;
            output.Markers = SolveTags(frame, layout, out found);
            if (output.Markers.Calibration == null)
            {
                // Dama etiketleri dortten azsa numarasiz doner; iletide gercekten bulunan sayi yazsin
                output.Error = found < MarkerSolver.MinMarkers
                    ? string.Format("Yeterli isaret gorunmuyor: {0} bulundu, en az {1} gerekli.", found, MarkerSolver.MinMarkers)
                    : output.Markers.Error;
                return output;
            }
            output.Calibration = output.Markers.Calibration;
            output.Calibration.SlabThicknessMm = thicknessMm;
            using (var detector = new SlabDetector(output.Calibration, options))
            {
                detector.SetTableColorAuto(frame);
                output.Slabs = detector.Detect(frame);
                RemoveTags(output.Slabs, layout);
                output.Rectified = detector.Rectify(frame);
            }
            return output;
        }
    }
}
