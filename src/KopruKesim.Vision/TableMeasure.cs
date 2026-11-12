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
        public static MeasureOutput Run(TableCalibration cal, Mat frame, Mat reference, bool compareWithReference,
            DetectorOptions options, MarkerLayout layout = null, double thicknessMm = double.NaN)
        {
            var output = new MeasureOutput();
            bool refUsable = reference != null && !reference.Empty();
            TableCalibration refCal = null;

            if (layout != null)
            {
                output.Markers = MarkerSolver.Solve(layout, MarkerFinder.Find(frame), frame.Cols, frame.Rows);
                if (output.Markers.Calibration == null) { output.Error = output.Markers.Error; return output; }
                output.Calibration = output.Markers.Calibration;
                if (refUsable)
                {
                    MarkerFit rf = MarkerSolver.Solve(layout, MarkerFinder.Find(reference), reference.Cols, reference.Rows);
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
                output.Rectified = detector.Rectify(frame);
            }
            return output;
        }
    }
}
