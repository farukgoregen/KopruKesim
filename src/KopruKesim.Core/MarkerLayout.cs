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

        public void Save(string path)
        {
            var c = CultureInfo.InvariantCulture;
            var lines = new List<string>
            {
                "# Kopru kesim isaret yerlesimi. Isaret=numara,X,Y (isaret merkezi, tabla mm).",
                "# Isaretler yerinden oynarsa yeniden olculmelidir.",
                "TableLengthMm=" + TableLengthMm.ToString("R", c),
                "TableWidthMm=" + TableWidthMm.ToString("R", c),
                "MarkerHeightMm=" + MarkerHeightMm.ToString("R", c),
                "MarkerSizeMm=" + MarkerSizeMm.ToString("R", c)
            };
            foreach (MarkerPoint m in Markers)
                lines.Add("Isaret=" + m.Id.ToString(c) + "," + m.X.ToString("R", c) + "," + m.Y.ToString("R", c));
            System.IO.File.WriteAllLines(path, lines);
        }

        public static MarkerLayout Load(string path)
        {
            var c = CultureInfo.InvariantCulture;
            var layout = new MarkerLayout();
            var seen = new HashSet<int>();
            foreach (string raw in System.IO.File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq < 0) throw new FormatException("Isaret dosyasi satiri anlasilamadi: " + line);
                string key = line.Substring(0, eq).Trim(), val = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "TableLengthMm": layout.TableLengthMm = double.Parse(val, c); break;
                    case "TableWidthMm": layout.TableWidthMm = double.Parse(val, c); break;
                    case "MarkerHeightMm": layout.MarkerHeightMm = double.Parse(val, c); break;
                    case "MarkerSizeMm": layout.MarkerSizeMm = double.Parse(val, c); break;
                    case "Isaret":
                        string[] p = val.Split(',');
                        if (p.Length != 3) throw new FormatException("Isaret satiri 'numara,X,Y' olmali: " + line);
                        var m = new MarkerPoint(int.Parse(p[0], c), double.Parse(p[1], c), double.Parse(p[2], c));
                        if (!seen.Add(m.Id)) throw new FormatException("Ayni isaret numarasi iki kez: " + m.Id);
                        layout.Markers.Add(m);
                        break;
                    default: throw new FormatException("Bilinmeyen anahtar: " + key);
                }
            }
            if (layout.Markers.Count < 4) throw new FormatException("En az 4 isaret tanimlanmali: " + path);
            return layout;
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
