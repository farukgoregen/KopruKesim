using System;

namespace KopruKesim.Core
{
    /// <summary>
    /// Tabla ve kamera kalibrasyonu. Koordinat sistemi: orijin tablanin secilen kosesi,
    /// X tablanin uzun kenari boyunca, Y kisa kenari boyunca (yatay ekranda X saga, Y yukari).
    /// </summary>
    public sealed class TableCalibration
    {
        /// <summary>Tablanin X yonundeki (uzun kenar) olcusu, mm.</summary>
        public double TableLengthMm = 3300;
        /// <summary>Tablanin Y yonundeki (kisa kenar) olcusu, mm.</summary>
        public double TableWidthMm = 2000;
        /// <summary>Mermer kalinligi, mm. Parcaya gore degisir (10-30 mm); her Detect oncesi guncellenebilir.</summary>
        public double SlabThicknessMm = 20;

        /// <summary>Kamera goruntusunun boyutu (piksel).</summary>
        public int ImageWidth;
        public int ImageHeight;

        /// <summary>Tabla koselerinin kamera goruntusundeki piksel konumlari.</summary>
        public Point2 CornerOrigin;   // (0, 0)
        public Point2 CornerYEnd;     // (0, Width)
        public Point2 CornerFar;      // (Length, Width)
        public Point2 CornerXEnd;     // (Length, 0)

        /// <summary>Kamera konumu elle olculduyse buraya yazilir; null ise homografiden tahmin edilir.</summary>
        public CameraPose ManualCamera;

        public Homography ImageToTable { get; private set; }
        public CameraPose Camera { get; private set; }

        /// <summary>Koseler veya olculer degistikten sonra cagrilir.</summary>
        public void Build()
        {
            if (ImageWidth <= 0 || ImageHeight <= 0) throw new InvalidOperationException("Goruntu boyutu girilmemis.");
            ImageToTable = Homography.FromPoints(
                new[] { CornerOrigin, CornerYEnd, CornerFar, CornerXEnd },
                new[]
                {
                    new Point2(0, 0), new Point2(0, TableWidthMm),
                    new Point2(TableLengthMm, TableWidthMm), new Point2(TableLengthMm, 0)
                });
            Camera = ManualCamera ?? CameraPose.Estimate(ImageToTable, ImageWidth, ImageHeight);
        }

        /// <summary>Kalibrasyonu duz metin dosyasina yazar (anahtar=deger). Kalinlik kaydedilmez; parcaya gore girilir.</summary>
        public void Save(string path)
        {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            var lines = new[]
            {
                "# Kopru kesim tabla kalibrasyonu. Kamera yerinden oynarsa yeniden kurulmalidir.",
                "TableLengthMm=" + TableLengthMm.ToString("R", c),
                "TableWidthMm=" + TableWidthMm.ToString("R", c),
                "ImageWidth=" + ImageWidth.ToString(c),
                "ImageHeight=" + ImageHeight.ToString(c),
                "CornerOrigin=" + CornerText(CornerOrigin),
                "CornerYEnd=" + CornerText(CornerYEnd),
                "CornerFar=" + CornerText(CornerFar),
                "CornerXEnd=" + CornerText(CornerXEnd)
            };
            System.IO.File.WriteAllLines(path, lines);
        }

        /// <summary>Save ile yazilan dosyayi okur ve Build cagirir.</summary>
        public static TableCalibration Load(string path)
        {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            var cal = new TableCalibration();
            int found = 0;
            foreach (string raw in System.IO.File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq < 0) throw new FormatException("Kalibrasyon satiri anlasilamadi: " + line);
                string key = line.Substring(0, eq).Trim(), val = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "TableLengthMm": cal.TableLengthMm = double.Parse(val, c); found |= 1; break;
                    case "TableWidthMm": cal.TableWidthMm = double.Parse(val, c); found |= 2; break;
                    case "ImageWidth": cal.ImageWidth = int.Parse(val, c); found |= 4; break;
                    case "ImageHeight": cal.ImageHeight = int.Parse(val, c); found |= 8; break;
                    case "CornerOrigin": cal.CornerOrigin = ParseCorner(val); found |= 16; break;
                    case "CornerYEnd": cal.CornerYEnd = ParseCorner(val); found |= 32; break;
                    case "CornerFar": cal.CornerFar = ParseCorner(val); found |= 64; break;
                    case "CornerXEnd": cal.CornerXEnd = ParseCorner(val); found |= 128; break;
                    default: throw new FormatException("Bilinmeyen kalibrasyon anahtari: " + key);
                }
            }
            if (found != 255) throw new FormatException("Kalibrasyon dosyasi eksik: " + path);
            cal.Build();
            return cal;
        }

        private static string CornerText(Point2 p)
        {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            return p.X.ToString("F2", c) + "," + p.Y.ToString("F2", c);
        }

        private static Point2 ParseCorner(string text)
        {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            string[] p = text.Split(',');
            if (p.Length != 2) throw new FormatException("Kose 'x,y' biciminde olmali: " + text);
            return new Point2(double.Parse(p[0], c), double.Parse(p[1], c));
        }

        /// <summary>
        /// Kamera goruntusunu yatay tabla gorunumune ceviren donusum. Cikti goruntusunde
        /// orijin sol altta, X saga, Y yukari; olcek pxPerMm piksel/mm.
        /// </summary>
        public Homography ImageToRectified(double pxPerMm)
        {
            if (ImageToTable == null) Build();
            var tableToRect = new Homography(new[] { pxPerMm, 0, 0, 0, -pxPerMm, TableWidthMm * pxPerMm, 0, 0, 1 });
            return tableToRect.Multiply(ImageToTable);
        }
    }
}
