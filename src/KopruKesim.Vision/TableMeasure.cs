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
        /// <summary>Olcum yapilamadiysa nedeni (Turkce). Doluysa Slabs bostur.</summary>
        public string Error;
        /// <summary>Parcalar bos tabla fotografiyla karsilastirilarak bulundu (kalibrasyonlu sabit kamera).</summary>
        public bool UsedReference;

        public void Dispose() { if (Rectified != null) Rectified.Dispose(); }
    }

    /// <summary>
    /// Olcum akisi: fotograf bir kez yapilmis kalibrasyonla (damali ya da damasiz; lens + tabla) duz ustten
    /// gorunume cevrilir ve parcalar orada bulunur (RunWithLens).
    /// </summary>
    public static class TableMeasure
    {
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
            // Yalniz TEK bir etiket alanina sigan parca etikettir: kosesinin cogu ayni etiket alaninda kalmali ve
            // parca etiketten belirgin buyuk olmamali. (2026-11-23: koseler farkli etiket alanlarina dusunce sayiliyordu;
            // tablanin alt kenarina dayanan 1488 x 595 mm plakanin iki kosesi alt-orta ve sag-alt etiket alanina dustu,
            // plaka etiket sanilip silindi.)
            Func<double[], Point2, bool> inZone = (z, p) => p.X >= z[0] && p.X <= z[2] && p.Y >= z[1] && p.Y <= z[3];
            slabs.RemoveAll(s =>
            {
                if (s.Vertices == null || s.Vertices.Length == 0) return zones.Any(z => inZone(z, s.Centroid));
                Point2 c = PolygonMath.Centroid(s.Vertices);
                return zones.Any(z =>
                {
                    double zoneArea = (z[2] - z[0]) * (z[3] - z[1]);
                    if (s.AreaMm2 > 1.5 * zoneArea) return false;
                    return inZone(z, c) || s.Vertices.Count(p => inZone(z, p)) * 2 >= s.Vertices.Length;
                });
            });
        }


        /// <summary>
        /// Kalibrasyonlu sabit kamera: fotograf bir kez yapilmis lens + tabla kalibrasyonuyla dogrudan duz ustten
        /// gorunume cevrilir (lens bukulmesi giderilir) ve olcum orada yapilir. Kalinlik duzeltmesi kalibrasyondaki
        /// kamera konumunu kullanir. Etiketler tablada kalabilir; etiket alanlari parca sayilmaz.
        /// </summary>
        /// <param name="emptyTable">Bos tablanin ayni kameradan fotografi (damalar kaldirildiktan sonra); varsa yalniz bos
        /// tabladan farkli yerler parca sayilir (ahsaptaki lekeler, cita aralari karismaz). Yoksa null.</param>
        public static MeasureOutput RunWithLens(Mat frame, LensCalibration lens, MarkerLayout tags, DetectorOptions options, double thicknessMm, Mat emptyTable = null)
        {
            var output = new MeasureOutput();
            if (frame.Cols != lens.ImageWidth || frame.Rows != lens.ImageHeight)
            {
                output.Error = string.Format("Bu fotograf {0}x{1} piksel, kalibrasyon {2}x{3} piksel fotografla yapildi. Ayni kameranin ayni akisindan cekin ya da kalibrasyonu yenileyin.",
                    frame.Cols, frame.Rows, lens.ImageWidth, lens.ImageHeight);
                return output;
            }
            double L = lens.TableLengthMm, W = lens.TableWidthMm;
            // Olcum alani tablanin kenarindan OuterMarginMm disari tasar: cita uclari duz degil, ortadaki citalar kose
            // citalarindan gecen kenar cizgisinin 15-20 mm disina uzanabiliyor (2026-11-24 bos tabla); o citalarin
            // ustundeki parca da bulunmali. Dedektor genisletilmis tablada calisir, sonuc gercek tablaya geri kaydirilir.
            double m = Math.Max(0, options.OuterMarginMm);
            LensCalibration work = lens.WithTableMargin(m);
            double Lw = work.TableLengthMm, Ww = work.TableWidthMm;
            // Olcek: tablanin ortasindaki dogal cozunurluk, piksel siniri icinde
            double k = Math.Min(LensCalibrator.NativeScale(work), Math.Sqrt(options.MaxRectifiedPixels / (Lw * Ww)));
            Mat canvas = LensCalibrator.TopView(frame, work, k);
            int cw = canvas.Cols, ch = canvas.Rows;
            CameraPose camera = lens.CameraPosition();
            var cal = new TableCalibration
            {
                TableLengthMm = Lw, TableWidthMm = Ww, SlabThicknessMm = thicknessMm,
                ImageWidth = cw, ImageHeight = ch,
                ManualCamera = new CameraPose(camera.X + m, camera.Y + m, camera.Height) { FocalPx = camera.FocalPx },
                CornerOrigin = new Point2(0, Ww * k), CornerYEnd = new Point2(0, 0),
                CornerFar = new Point2(Lw * k, 0), CornerXEnd = new Point2(Lw * k, Ww * k)
            };
            cal.Build();
            double oldScale = options.PxPerMm;
            options.PxPerMm = k;   // ustten gorunum zaten bu olcekte
            try
            {
                using (var detector = new SlabDetector(cal, options) { TableInsetMm = m })
                {
                    if (emptyTable != null && !emptyTable.Empty() && emptyTable.Cols == frame.Cols && emptyTable.Rows == frame.Rows)
                    {
                        using (Mat emptyTop = LensCalibrator.TopView(emptyTable, work, k)) detector.SetReference(emptyTop);
                        output.UsedReference = true;
                    }
                    else detector.SetTableColorAuto(canvas);
                    // Tablanin kenarina dayanan parcanin kenari orijinal fotografta aranir (tablanin disi da gorunur)
                    detector.PhotoSampler = pts => BorderEdgeFinder.SamplePhoto(frame, work, pts);
                    detector.TopFaceSampler = thicknessMm > 0
                        ? pts => BorderEdgeFinder.SamplePhoto(frame, work, pts, thicknessMm)
                        : detector.PhotoSampler;
                    output.Slabs = detector.Detect(canvas);
                    foreach (SlabResult s in output.Slabs) Shift(s, -m);
                    // Ust yuze oturtma (TopFaceSnapper) 2026-11-24'te kaldirildi: 5 mm plakada yan yuz gorunmuyor, adim tahta
                    // araligina ve parlamaya kanip kenarlari 5-10 mm kaydiriyordu (sol ust 1471 -> 1459). Bkz. gelistirme notlari.
                }
            }
            finally { options.PxPerMm = oldScale; }
            if (tags != null) RemoveTags(output.Slabs, tags);
            // Disariya verilen kalibrasyon gercek tablanin: genisletilmis ustten gorunumde koseleri m mm iceride
            var real = new TableCalibration
            {
                TableLengthMm = L, TableWidthMm = W, SlabThicknessMm = thicknessMm,
                ImageWidth = cw, ImageHeight = ch, ManualCamera = camera,
                CornerOrigin = new Point2(m * k, (W + m) * k), CornerYEnd = new Point2(m * k, m * k),
                CornerFar = new Point2((L + m) * k, m * k), CornerXEnd = new Point2((L + m) * k, (W + m) * k)
            };
            real.Build();
            output.Calibration = real;
            output.Rectified = canvas;
            return output;
        }

        /// <summary>Parcanin tum koordinatlarini (mm) her iki eksende d kadar kaydirir.</summary>
        private static void Shift(SlabResult s, double d)
        {
            if (d == 0) return;
            var v = new Point2(d, d);
            if (s.Vertices != null) s.Vertices = s.Vertices.Select(p => p + v).ToArray();
            s.Centroid = s.Centroid + v;
            s.MinX += d; s.MaxX += d; s.MinY += d; s.MaxY += d;
        }
    }
}
