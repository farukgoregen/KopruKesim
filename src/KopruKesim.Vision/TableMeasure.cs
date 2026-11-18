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
        /// <summary>Ayirmada bos tabla referansi kullanildi.</summary>
        public bool UsedReference;
        /// <summary>Isaret yerlesimi tanimliysa isaretlerden kalibrasyon sonucu; degilse null.</summary>
        public MarkerFit Markers;
        /// <summary>
        /// Yalnizca isaret yokken: kalibrasyondaki tabla kenarlari bu fotografta gercek kenarin uzerinde
        /// degil (kamera kurulumdan farkli konumda). Koordinatlar kayar; kurulum yenilenmeli.
        /// </summary>
        public bool CameraMoved;
        /// <summary>Kamera kurulumdan farkli yerdeydi; tabla bu fotografta yeniden bulundu ve olcum onunla yapildi.</summary>
        public bool Relocated;
        /// <summary>Kenarlarin bu fotograftaki / referanstaki gorunurluk orani (bilgi icin).</summary>
        public double EdgeRatio = double.NaN;
        /// <summary>Olcum yapilamadiysa nedeni (Turkce). Doluysa Slabs bostur.</summary>
        public string Error;
        /// <summary>Tek fotografta bulunan tablanin kenar destegi (0-1); kose guvenilirligi icin.</summary>
        public double TableSupport = double.NaN;
        /// <summary>Koseler kayitli tabla profilinden (desen eslestirmesiyle) tasindi.</summary>
        public bool ProfileMatched;

        public void Dispose() { if (Rectified != null) Rectified.Dispose(); }
    }

    /// <summary>
    /// Olcum akisi. Isaret yerlesimi varsa kalibrasyon HER fotografta isaretlerden yeniden hesaplanir;
    /// kamera oynasa da olcum kaymaz, yeterli isaret yoksa olcum verilmez. Isaret yoksa kurulumdaki
    /// kalibrasyon kullanilir ve kameranin yerinde olup olmadigi tabla kenarlarindan denetlenir.
    /// Web ve konsol ayni akisi kullanir.
    /// </summary>
    public static class TableMeasure
    {
        /// <summary>Kenar gorunurlugu referansin bu oraninin altina duserse kamera oynamis sayilir.</summary>
        public const double MovedRatio = 0.6;
        /// <summary>Fotografta yeniden bulunan tablanin kenarlari en az bu oranda gercek kenar uzerinde olmali.</summary>
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

        /// <param name="cal">Kurulum kalibrasyonu (isaret yoksa kullanilir; isaret varsa null olabilir).</param>
        /// <param name="reference">Kurulumdaki bos tabla fotografi; yoksa null.</param>
        /// <param name="layout">Isaret yerlesimi; yoksa null.</param>
        /// <summary>
        /// Etiketlerin kendisi (beyaz sayfa, dama tahtasi) olcum sonucuna parca olarak girmesin: merkezi bir
        /// etiketin kapladigi alanin icinde kalan parcalar atilir.
        /// </summary>
        private static void RemoveTags(List<SlabResult> slabs, MarkerLayout layout)
        {
            if (layout.Kind != "dama")
            {
                double h = layout.MarkerSizeMm * 0.8;
                slabs.RemoveAll(s => layout.Markers.Any(m => Math.Abs(s.Centroid.X - m.X) < h && Math.Abs(s.Centroid.Y - m.Y) < h));
                return;
            }
            // Dama etiketi tabla kosesine dayali: kosesinden merkezine olan uzakligin iki kati etiketin kendisi.
            // Etiketin kareleri, kenari ya da yanindaki tabla disi bolgeyle birlesen leke etiket alanina tasar;
            // kosesinin cogu (ya da agirlik merkezi) etiket alaninda kalan parca etikettir.
            const double pad = 25;
            var zones = layout.Markers.Select(m =>
            {
                double cx = m.X < layout.TableLengthMm / 2 ? 0 : layout.TableLengthMm;
                double cy = m.Y < layout.TableWidthMm / 2 ? 0 : layout.TableWidthMm;
                double ex = 2 * m.X - cx, ey = 2 * m.Y - cy;
                return new[] { Math.Min(cx, ex) - pad, Math.Min(cy, ey) - pad, Math.Max(cx, ex) + pad, Math.Max(cy, ey) + pad };
            }).ToList();
            Func<Point2, bool> inTag = p => zones.Any(z => p.X >= z[0] && p.X <= z[2] && p.Y >= z[1] && p.Y <= z[3]);
            slabs.RemoveAll(s =>
            {
                if (s.Vertices == null || s.Vertices.Length == 0) return inTag(s.Centroid);
                Point2 c = PolygonMath.Centroid(s.Vertices);
                return inTag(c) || s.Vertices.Count(inTag) * 2 >= s.Vertices.Length;
            });
        }

        /// <summary>Etiket turune gore etiket merkezleri: numarali kare kodlar ya da koselerdeki dama tahtalari.</summary>
        private static Dictionary<int, Point2> FindTags(Mat image, MarkerLayout layout, out Dictionary<int, Point2[]> corners)
        {
            corners = null;
            return layout.Kind == "dama"
                ? CheckerFinder.Find(image, layout.PatternCols, layout.PatternRows, out corners)
                : MarkerFinder.Find(image);
        }

        /// <summary>Etiketleri bulur, merkezlerden kalibrasyonu hesaplar; dama etiketinde tum ic koselerle inceltir.</summary>
        private static MarkerFit SolveTags(Mat image, MarkerLayout layout, out int found)
        {
            Dictionary<int, Point2[]> corners;
            Dictionary<int, Point2> tags = FindTags(image, layout, out corners);
            found = tags.Count;
            MarkerFit fit = MarkerSolver.Solve(layout, tags, image.Cols, image.Rows);
            if (fit.Calibration != null && layout.Kind == "dama") CheckerGridFit.Refine(layout, fit, corners, image.Cols, image.Rows);
            return fit;
        }

        public static MeasureOutput Run(TableCalibration cal, Mat frame, Mat reference, bool compareWithReference,
            DetectorOptions options, MarkerLayout layout = null, double thicknessMm = double.NaN)
        {
            var output = new MeasureOutput();
            bool refUsable = reference != null && !reference.Empty();
            TableCalibration refCal = null;

            if (layout != null)
            {
                int found;
                output.Markers = SolveTags(frame, layout, out found);
                if (output.Markers.Calibration == null)
                {
                    // Dama etiketleri dortten azsa numarasiz doner; iletide gercekten bulunan sayi yazsin
                    output.Error = layout.Kind == "dama" && found < MarkerSolver.MinMarkers
                        ? string.Format("Yeterli isaret gorunmuyor: {0} bulundu, en az {1} gerekli.", found, MarkerSolver.MinMarkers)
                        : output.Markers.Error;
                    return output;
                }
                output.Calibration = output.Markers.Calibration;
                if (refUsable)
                {
                    MarkerFit rf = SolveTags(reference, layout, out found);
                    refCal = rf.Calibration;   // bos tabla fotografinda isaretler yoksa karsilastirma yapilmaz
                }
            }
            else
            {
                if (cal == null) { output.Error = "Kalibrasyon yok: once kurulum yapin."; return output; }
                if (frame.Cols != cal.ImageWidth || frame.Rows != cal.ImageHeight)
                {
                    output.Error = string.Format("Bu fotograf {0}x{1} piksel, kurulum ise {2}x{3}. Ayni kameradan cekilmis bir fotograf yukleyin ya da kurulumu yenileyin.",
                        frame.Cols, frame.Rows, cal.ImageWidth, cal.ImageHeight);
                    return output;
                }
                output.Calibration = cal;
                if (refUsable && reference.Cols == frame.Cols && reference.Rows == frame.Rows)
                {
                    // Tabla kenari parcalarla kismen ortulebilir; referansta iyi gorunen kenarlarin toplam destegi karsilastirilir
                    var corners = new[] { cal.CornerOrigin, cal.CornerYEnd, cal.CornerFar, cal.CornerXEnd };
                    double[] sr = TableFinder.EdgeSupport(reference, corners);
                    double[] sc = TableFinder.EdgeSupport(frame, corners);
                    double a = 0, b = 0;
                    for (int i = 0; i < 4; i++) if (sr[i] >= 0.4) { a += sr[i]; b += sc[i]; }
                    if (a > 0)
                    {
                        output.EdgeRatio = b / a;
                        output.CameraMoved = output.EdgeRatio < MovedRatio;
                    }
                    if (!output.CameraMoved) refCal = cal;
                }
                if (output.CameraMoved)
                {
                    // Kamera kurulumdan farkli yerde: tablayi bu fotografta yeniden bul. Tabla her fotografta
                    // gorundugu surece kamera oynasa da olcum kaymaz. Bulunamazsa uyari kalir.
                    Point2[] c = TableFinder.FindCorners(frame, cal.TableLengthMm, cal.TableWidthMm);
                    if (c != null && TableFinder.EdgeSupport(frame, c).Average() >= RelocateMinSupport)
                    {
                        var moved = new TableCalibration
                        {
                            TableLengthMm = cal.TableLengthMm, TableWidthMm = cal.TableWidthMm,
                            SlabThicknessMm = cal.SlabThicknessMm, ManualCamera = cal.ManualCamera,
                            ImageWidth = frame.Cols, ImageHeight = frame.Rows,
                            CornerOrigin = c[0], CornerYEnd = c[1], CornerFar = c[2], CornerXEnd = c[3]
                        };
                        moved.Build();
                        output.Calibration = moved;
                        output.Relocated = true;
                        output.CameraMoved = false;
                        refCal = cal;   // bos tabla fotografi kendi (kurulum) kalibrasyonuyla ustten gorunume cevrilir
                    }
                }
            }

            if (!double.IsNaN(thicknessMm)) output.Calibration.SlabThicknessMm = thicknessMm;
            using (var detector = new SlabDetector(output.Calibration, options))
            {
                if (compareWithReference && refCal != null) { detector.SetReference(reference, refCal); output.UsedReference = true; }
                else detector.SetTableColorAuto(frame);
                output.Slabs = detector.Detect(frame);
                if (layout != null) RemoveTags(output.Slabs, layout);
                output.Rectified = detector.Rectify(frame);
            }
            return output;
        }
    }
}
