using System;
using System.Globalization;
using System.IO;

namespace KopruKesim.Core
{
    /// <summary>
    /// Tabla profili: bir tablanin olcusu ve bir referans fotografta onaylanmis dort kosesi.
    /// Referans fotograf ayri dosyada tutulur. Sonraki fotograflarda tabla yuzeyinin deseni
    /// referansla eslestirilir ve koseler oradan tasinir; kamera sabit olmasa da her fotografta
    /// koseleri yeniden secmek gerekmez.
    /// Kose sirasi TableCalibration ile ayni: 0 noktasi, Y sonu, uzak kose, X sonu.
    /// </summary>
    public sealed class TableProfile
    {
        public string Name = "";
        public double TableLengthMm;
        public double TableWidthMm;
        public int ImageWidth;
        public int ImageHeight;
        public Point2[] Corners = new Point2[4];

        public void Save(string path)
        {
            var c = CultureInfo.InvariantCulture;
            var lines = new[]
            {
                "# Kopru kesim tabla profili. Referans fotograf ayni klasorde referans.jpg.",
                "Name=" + (Name ?? "").Replace("\r", " ").Replace("\n", " "),
                "TableLengthMm=" + TableLengthMm.ToString("R", c),
                "TableWidthMm=" + TableWidthMm.ToString("R", c),
                "ImageWidth=" + ImageWidth.ToString(c),
                "ImageHeight=" + ImageHeight.ToString(c),
                "CornerOrigin=" + Text(Corners[0]),
                "CornerYEnd=" + Text(Corners[1]),
                "CornerFar=" + Text(Corners[2]),
                "CornerXEnd=" + Text(Corners[3])
            };
            File.WriteAllLines(path, lines);
        }

        public static TableProfile Load(string path)
        {
            var c = CultureInfo.InvariantCulture;
            var p = new TableProfile();
            int found = 0;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq < 0) throw new FormatException("Profil satiri anlasilamadi: " + line);
                string key = line.Substring(0, eq).Trim(), val = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "Name": p.Name = val; break;
                    case "TableLengthMm": p.TableLengthMm = double.Parse(val, c); found |= 1; break;
                    case "TableWidthMm": p.TableWidthMm = double.Parse(val, c); found |= 2; break;
                    case "ImageWidth": p.ImageWidth = int.Parse(val, c); found |= 4; break;
                    case "ImageHeight": p.ImageHeight = int.Parse(val, c); found |= 8; break;
                    case "CornerOrigin": p.Corners[0] = Parse(val); found |= 16; break;
                    case "CornerYEnd": p.Corners[1] = Parse(val); found |= 32; break;
                    case "CornerFar": p.Corners[2] = Parse(val); found |= 64; break;
                    case "CornerXEnd": p.Corners[3] = Parse(val); found |= 128; break;
                    default: throw new FormatException("Bilinmeyen profil anahtari: " + key);
                }
            }
            if (found != 255) throw new FormatException("Profil dosyasi eksik: " + path);
            return p;
        }

        private static string Text(Point2 p)
        {
            var c = CultureInfo.InvariantCulture;
            return p.X.ToString("R", c) + "," + p.Y.ToString("R", c);
        }

        private static Point2 Parse(string s)
        {
            var c = CultureInfo.InvariantCulture;
            string[] xy = s.Split(',');
            if (xy.Length != 2) throw new FormatException("Kose anlasilamadi: " + s);
            return new Point2(double.Parse(xy[0], c), double.Parse(xy[1], c));
        }
    }
}
