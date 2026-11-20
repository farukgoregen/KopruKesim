using System;
using System.Collections.Generic;
using System.Globalization;

namespace KopruKesim.Core
{
    /// <summary>Makineye sabitlenmis bir isaretin numarasi ve merkezinin tabla koordinati (mm).</summary>
    public struct MarkerPoint
    {
        public int Id;
        public double X;
        public double Y;

        public MarkerPoint(int id, double x, double y) { Id = id; X = x; Y = y; }
    }

    /// <summary>
    /// Isaret yerlesimi: hangi numarali isaret tablanin hangi noktasinda. Isaretler kesim alaninin
    /// disina (sasi, tabla kenari) sabitlenir; yerleri makine koordinatinda bir kez olculur.
    /// Kamera oynasa bile her fotograftaki isaretlerden kalibrasyon yeniden hesaplanir.
    /// </summary>
    public sealed class MarkerLayout
    {
        public double TableLengthMm = 3300;
        public double TableWidthMm = 2000;
        /// <summary>
        /// Isaretlerin tabla ust yuzeyine gore yuksekligi (mm). Isaretler tabladan alcaktaysa eksi
        /// (ornek: tahtanin altindaki sasi yuzeyi -60). Kamera eğik baktigi icin hesaba katilir.
        /// </summary>
        public double MarkerHeightMm = 0;
        /// <summary>Basili isaretin kenar uzunlugu (mm); yalnizca bilgi ve basma icin.</summary>
        public double MarkerSizeMm = 100;
        public List<MarkerPoint> Markers = new List<MarkerPoint>();
        /// <summary>Etiket turu: "aruco" (numarali kare kod) ya da "dama" (koselerde ayni dama tahtasi deseni).</summary>
        public string Kind = "aruco";
        /// <summary>Dama tahtasinin ic kose sayisi (sutun x satir); 10x7 kareli desen icin 9x6.</summary>
        public int PatternCols = 9, PatternRows = 6;
        /// <summary>Dama karesinin tasarim kenar uzunlugu (mm); 0 = bilinmiyor (yalniz merkezler kullanilir).</summary>
        public double SquareMm = 0;
        /// <summary>
        /// Etiket kesim cizgisinden kesilmis (true) ya da A4 sayfanin tamami (false). Kesik etikette baski olcegi
        /// merkezin koseye uzakligini da kucultur; tam sayfada desen sayfanin ortasinda kalir.
        /// </summary>
        public bool CutTag = false;
        /// <summary>
        /// Deseni 90 derece cevrik konan etiketler (ic kose sutunlari tablanin Y yonunde). Kisa kenarin ortasindaki
        /// etiketler boyle konur; koseler ve ortalar boylece birbirinden ayrilir (iki fotografli olcum).
        /// </summary>
        public HashSet<int> Rotated = new HashSet<int>();
        /// <summary>True ise her etiketin yonu zorunludur (Rotated'a gore); false ise iki yon de kabul edilir.</summary>
        public bool StrictOrientation = false;

        /// <summary>
        /// Etiketin dayandigi tabla noktasi: kosedeki etiket icin kose, kenarin ortasindaki etiket icin kenar
        /// uzerindeki nokta. Bir eksende tabla kenarina ortadan yakinsa o kenar, degilse etiketin kendi koordinati.
        /// </summary>
        public Point2 AnchorOf(double x, double y)
        {
            double ax = Math.Min(x, TableLengthMm - x) < Math.Abs(x - TableLengthMm / 2) ? (x < TableLengthMm / 2 ? 0 : TableLengthMm) : x;
            double ay = Math.Min(y, TableWidthMm - y) < Math.Abs(y - TableWidthMm / 2) ? (y < TableWidthMm / 2 ? 0 : TableWidthMm) : y;
            return new Point2(ax, ay);
        }
    }

    /// <summary>Isaretlerden hesaplanan kalibrasyonun sonucu.</summary>
    public sealed class MarkerFit
    {
        /// <summary>Basariliysa kalibrasyon; yeterli guvenilir isaret yoksa null.</summary>
        public TableCalibration Calibration;
        /// <summary>Hesapta kullanilan isaretler.</summary>
        public List<int> Used = new List<int>();
        /// <summary>Yerlesimde tanimli ama fotografta bulunamayan isaretler.</summary>
        public List<int> Missing = new List<int>();
        /// <summary>Bulundu ama digerleriyle uyusmadigi icin atilan isaretler (yerinden oynamis olabilir).</summary>
        public List<int> Rejected = new List<int>();
        /// <summary>Kullanilan isaretlerin hesaplanan ve tanimli yerleri arasindaki fark (mm).</summary>
        public double RmsMm, MaxMm;
        /// <summary>Dama ic koseleriyle inceltildiyse: kullanilan nokta sayisi (0 = yalniz merkezler).</summary>
        public int GridPoints;
        /// <summary>Tahmin edilen baski olcegi (1 = %100); inceltme yapilmadiysa NaN.</summary>
        public double PrintScale = double.NaN;
        /// <summary>Karelerin X yonundeki olcegi / Y yonundeki olcegi; 1'den uzaksa tabla olcusu tutmuyor olabilir.</summary>
        public double AxisRatio = double.NaN;
        /// <summary>Basarisizsa nedeni (Turkce, kullaniciya gosterilebilir).</summary>
        public string Error;
    }

    /// <summary>
    /// Fotografta bulunan isaret merkezlerinden tabla kalibrasyonunu hesaplar. Dort ve ustu isaretle
    /// en kucuk kareler homografi; digerleriyle uyusmayan isaret (yerinden oynamis, yanlis okunmus)
    /// atilir. Isaretler tabla yuzeyinden farkli yukseklikteyse kamera konumu tahmin edilerek
    /// yinelemeli duzeltilir.
    /// </summary>
    public static class MarkerSolver
    {
        public const int MinMarkers = 4;

        /// <param name="seen">Isaret numarasi -> goruntudeki merkezi (piksel).</param>
        /// <param name="toleranceMm">Bundan fazla uyusmayan isaret atilir.</param>
        public static MarkerFit Solve(MarkerLayout layout, IDictionary<int, Point2> seen, int imageWidth, int imageHeight, double toleranceMm = 8)
        {
            var fit = new MarkerFit();
            var ids = new List<int>();
            foreach (MarkerPoint m in layout.Markers)
            {
                if (seen.ContainsKey(m.Id)) ids.Add(m.Id); else fit.Missing.Add(m.Id);
            }
            var target = new Dictionary<int, Point2>();
            foreach (MarkerPoint m in layout.Markers) target[m.Id] = new Point2(m.X, m.Y);

            CameraPose camera = null;
            Homography h = null;
            for (int pass = 0; pass < 6; pass++)
            {
                if (ids.Count < MinMarkers)
                {
                    fit.Error = string.Format("Yeterli isaret gorunmuyor: {0} bulundu, en az {1} gerekli.", ids.Count, MinMarkers);
                    return fit;
                }
                // Isaret yuksekligi: kameradan bakinca tabla duzlemindeki gorunur yeri
                Func<int, Point2> apparent = id =>
                {
                    Point2 p = target[id];
                    if (camera == null || Math.Abs(layout.MarkerHeightMm) < 1e-9 || camera.Height <= layout.MarkerHeightMm) return p;
                    var foot = new Point2(camera.X, camera.Y);
                    return foot + (p - foot) * (camera.Height / (camera.Height - layout.MarkerHeightMm));
                };

                // Yukseklik varsa kamera tahminini birkac kez yenile
                for (int iter = 0; iter < (Math.Abs(layout.MarkerHeightMm) < 1e-9 ? 1 : 4); iter++)
                {
                    var src = new List<Point2>();
                    var dst = new List<Point2>();
                    foreach (int id in ids) { src.Add(seen[id]); dst.Add(apparent(id)); }
                    try { h = Homography.FromPoints(src, dst); }
                    catch (ArgumentException) { fit.Error = "Isaretler gecerli bir kalibrasyon vermiyor (ayni dogru uzerinde olabilirler)."; return fit; }
                    camera = CameraPose.Estimate(h, imageWidth, imageHeight);
                }

                // Uyusmayan isareti ayikla (en kotusu, tolerans disindaysa ve yeterli isaret kaliyorsa)
                int worst = -1; double worstErr = 0, sum = 0;
                foreach (int id in ids)
                {
                    double e = Point2.Distance(h.Transform(seen[id]), apparent(id));
                    sum += e * e;
                    if (e > worstErr) { worstErr = e; worst = id; }
                }
                fit.RmsMm = Math.Sqrt(sum / ids.Count);
                fit.MaxMm = worstErr;
                if (worstErr <= toleranceMm) break;
                if (ids.Count <= MinMarkers)
                {
                    fit.Error = string.Format("Isaretler birbiriyle uyusmuyor (en buyuk fark {0:F0} mm). Bir isaret yerinden oynamis olabilir; daha fazla isaret gorunur olmali.", worstErr);
                    return fit;
                }
                ids.Remove(worst);
                fit.Rejected.Add(worst);
            }
            if (fit.MaxMm > toleranceMm)
            {
                fit.Error = string.Format("Isaretler birbiriyle uyusmuyor (en buyuk fark {0:F0} mm).", fit.MaxMm);
                return fit;
            }

            Homography inv = h.Inverse();   // tabla -> goruntu
            var cal = new TableCalibration
            {
                TableLengthMm = layout.TableLengthMm,
                TableWidthMm = layout.TableWidthMm,
                ImageWidth = imageWidth,
                ImageHeight = imageHeight,
                CornerOrigin = inv.Transform(new Point2(0, 0)),
                CornerYEnd = inv.Transform(new Point2(0, layout.TableWidthMm)),
                CornerFar = inv.Transform(new Point2(layout.TableLengthMm, layout.TableWidthMm)),
                CornerXEnd = inv.Transform(new Point2(layout.TableLengthMm, 0))
            };
            cal.Build();
            fit.Calibration = cal;
            fit.Used.AddRange(ids);
            return fit;
        }
    }
}
