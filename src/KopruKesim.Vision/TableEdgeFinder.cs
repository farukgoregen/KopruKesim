using System;
using System.Collections.Generic;
using System.Linq;
using KopruKesim.Core;
using OpenCvSharp;

namespace KopruKesim.Vision
{
    /// <summary>
    /// Bos tabla fotografinda tablanin dort kenarini (ahsabin bittigi yer) bulur. Her kenar boyunca ~100 mm arayla kenara
    /// dik parlaklik profili alinir, en guclu gecis kenar noktasidir; noktalara aykirilari atan bir dogru oturtulur ve
    /// komsu dogrularin kesisimi tabla kosesidir. Ikinci turda yalniz ilk dogrunun +-15 mm yakininda aranir (ust kenardaki
    /// yesil serit, kenardaki talas gibi ikinci gecisler karismasin).
    ///
    /// Kameranin isi tablayi gormektir: bulunan koseler seritle olculen tabla koselerine oturtulur (TableCorrection).
    /// Paspas koseleri bu is icin yeterince kesin degildi (paspas boylari 429-439 mm, kose tespiti +-5-15 mm); kenar
    /// dogrusu kenarin tum boyundaki 15-30 noktadan kurulur.
    /// </summary>
    public static class TableEdgeFinder
    {
        public sealed class Result
        {
            /// <summary>Tabla koseleri (modelin mm'si, z = 0): sol ust, sag ust, sol alt, sag alt. Hata varsa null.</summary>
            public Point2[] Corners;
            public string Error;
            /// <summary>Kenar basina uyan / toplam nokta: 0 alt, 1 ust, 2 sol, 3 sag.</summary>
            public int[] Inliers = new int[4], Samples = new int[4];
            /// <summary>Kenar basina uyan noktalarin dogrudan en buyuk sapmasi (mm).</summary>
            public double[] MaxDevMm = new double[4];
        }

        /// <param name="lens">Duzeltmesiz kamera modeli (WithoutCorrection).</param>
        /// <param name="expected">Koselerin modeldeki tahmini (sol ust, sag ust, sol alt, sag alt).</param>
        public static Result Find(Mat frame, LensCalibration lens, Point2[] expected, double stepMm = 100, double searchMm = 80)
        {
            var res = new Result();
            using (var gray = new Mat())
            {
                Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
                Cv2.GaussianBlur(gray, gray, new Size(0, 0), 1.5);
                gray.GetArray(out byte[] g);
                int w = gray.Cols, h = gray.Rows;
                Point2 tl = expected[0], tr = expected[1], bl = expected[2], br = expected[3];
                Point2 center = (tl + tr + bl + br) * 0.25;
                // 0 alt, 1 ust, 2 sol, 3 sag
                var ends = new[] { new[] { bl, br }, new[] { tl, tr }, new[] { bl, tl }, new[] { br, tr } };
                var lines = new Point2[4][];
                var dbg = new List<string>();
                for (int e = 0; e < 4; e++)
                {
                    Point2 a = ends[e][0], b = ends[e][1];
                    double len = Point2.Distance(a, b);
                    Point2 u = (b - a) * (1 / len);
                    var n = new Point2(-u.Y, u.X);
                    if (Point2.Dot(n, (a + b) * 0.5 - center) < 0) n = n * -1;   // disari
                    int count = Math.Max(5, (int)(len * 0.9 / stepMm));
                    var bases = Enumerable.Range(0, count).Select(i => a + u * (len * (0.05 + 0.9 * i / (count - 1.0)))).ToArray();
                    // 1. tur: tahmini kenarin +-searchMm'si; 2. tur: ilk dogrunun +-15 mm'si
                    List<Point2> pts = Scan(g, w, h, lens, bases, n, Enumerable.Repeat(0.0, count).ToArray(), searchMm);
                    if (!FitRobust(pts, out Point2 p0, out Point2 d0, out _)) { res.Error = "Tablanin " + Name(e) + " kenari bulunamadi."; return res; }
                    var off = bases.Select(q => SignedOffsetToLine(q, p0, d0, n)).ToArray();
                    pts = Scan(g, w, h, lens, bases, n, off, 15);
                    res.Samples[e] = count;
                    if (!FitRobust(pts, out Point2 p, out Point2 d, out bool[] keep) || keep.Count(k => k) < Math.Max(5, 0.6 * count))
                    {
                        res.Error = "Tablanin " + Name(e) + " kenari duz bir cizgi olarak bulunamadi (" + pts.Count + "/" + count + " nokta).";
                        return res;
                    }
                    res.Inliers[e] = keep.Count(k => k);
                    var nn = new Point2(-d.Y, d.X);
                    res.MaxDevMm[e] = pts.Where((q, i) => keep[i]).Max(q => Math.Abs(Point2.Dot(q - p, nn)));
                    lines[e] = new[] { p, d };
                    dbg.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} {1}/{2} nokta, en buyuk sapma {3:F1} mm", Name(e), res.Inliers[e], count, res.MaxDevMm[e]));
                }
                Point2 X(int i, int j)
                {
                    Point2[] p = lines[i], q = lines[j];
                    double den = p[1].X * q[1].Y - p[1].Y * q[1].X;
                    double t = ((q[0].X - p[0].X) * q[1].Y - (q[0].Y - p[0].Y) * q[1].X) / den;
                    return p[0] + p[1] * t;
                }
                res.Corners = new[] { X(1, 2), X(1, 3), X(0, 2), X(0, 3) };
                if (Environment.GetEnvironmentVariable("KK_DEBUG") == "1")
                    Console.WriteLine("  [tabla kenari] " + string.Join("; ", dbg) + " -> koseler " + string.Join(" ", res.Corners.Select(c => c.ToString())));
                for (int i = 0; i < 4; i++)
                    if (Point2.Distance(res.Corners[i], expected[i]) > 2 * searchMm)
                    {
                        res.Error = "Bulunan tabla kosesi beklenen yerden cok uzak; kenarlar yanlis bulunmus olabilir.";
                        res.Corners = null;
                        return res;
                    }
            }
            return res;
        }

        static string Name(int e) => e == 0 ? "alt" : e == 1 ? "ust" : e == 2 ? "sol" : "sag";

        static double SignedOffsetToLine(Point2 q, Point2 p, Point2 d, Point2 n)
        {
            // q'dan n yonunde dogruya uzaklik
            double den = n.X * d.Y - n.Y * d.X;
            return Math.Abs(den) < 1e-9 ? 0 : ((p.X - q.X) * d.Y - (p.Y - q.Y) * d.X) / den;
        }

        /// <summary>Her taban noktasinda n yonunde [off-r, off+r] mm araliginda en guclu parlaklik gecisi (1 mm adim, parabolle ince).</summary>
        static List<Point2> Scan(byte[] g, int w, int h, LensCalibration lens, Point2[] bases, Point2 n, double[] off, double r)
        {
            var found = new List<Point2>();
            int m = (int)Math.Round(2 * r) + 1;
            for (int i = 0; i < bases.Length; i++)
            {
                var prof = new Point2[m];
                for (int k = 0; k < m; k++) prof[k] = bases[i] + n * (off[i] - r + k);
                Point2[] px = LensCalibrator.TableToImage(lens, prof, 0);
                var v = new double[m];
                bool ok = true;
                for (int k = 0; k < m; k++)
                {
                    double x = px[k].X, y = px[k].Y;
                    if (x < 0 || y < 0 || x > w - 2 || y > h - 2) { ok = false; break; }
                    int x0 = (int)x, y0 = (int)y; double fx = x - x0, fy = y - y0; int j = y0 * w + x0;
                    v[k] = g[j] * (1 - fx) * (1 - fy) + g[j + 1] * fx * (1 - fy) + g[j + w] * (1 - fx) * fy + g[j + w + 1] * fx * fy;
                }
                if (!ok) continue;
                var dvs = new double[m];
                double max = 0;
                for (int k = 1; k < m - 1; k++) { dvs[k] = Math.Abs(v[k + 1] - v[k - 1]); max = Math.Max(max, dvs[k]); }
                // Profil tablanin icinden disariya: icten ilk guclu gecis (en gucluun yarisindan buyuk yerel tepe) tablanin
                // kenaridir; disaridaki yesil serit, talas, zemin catlagi ikinci gecislerdir
                int best = -1; double bv = 0;
                for (int k = 2; k < m - 2; k++)
                    if (dvs[k] >= 0.5 * max && dvs[k] >= dvs[k - 1] && dvs[k] >= dvs[k + 1]) { best = k; bv = dvs[k]; break; }
                if (best < 2 || best > m - 3 || bv < 8) continue;   // zayif gecis ya da aralik sinirinda
                double l0 = Math.Abs(v[best] - v[best - 2]), r0 = Math.Abs(v[best + 2] - v[best]);
                double den = l0 - 2 * bv + r0, sub = Math.Abs(den) > 1e-9 ? Math.Max(-0.5, Math.Min(0.5, 0.5 * (l0 - r0) / den)) : 0;
                found.Add(bases[i] + n * (off[i] - r + best + sub));
            }
            return found;
        }

        /// <summary>Toplam en kucuk kareler dogrusu; medyan mutlak sapmanin 3 katindan (en az 3 mm) uzak noktalar atilir.</summary>
        static bool FitRobust(List<Point2> pts, out Point2 p, out Point2 d, out bool[] keepOut)
        {
            p = d = new Point2(0, 0);
            bool[] keep = Enumerable.Repeat(true, pts.Count).ToArray();
            keepOut = keep;
            if (pts.Count < 5) return false;
            for (int it = 0; it < 6; it++)
            {
                var use = pts.Where((q, i) => keep[i]).ToList();
                if (use.Count < 3) { keepOut = keep; return false; }
                double mx = use.Average(q => q.X), my = use.Average(q => q.Y), sxx = 0, syy = 0, sxy = 0;
                foreach (Point2 q in use) { double dx = q.X - mx, dy = q.Y - my; sxx += dx * dx; syy += dy * dy; sxy += dx * dy; }
                double ang = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
                p = new Point2(mx, my); d = new Point2(Math.Cos(ang), Math.Sin(ang));
                var nn = new Point2(-d.Y, d.X);
                Point2 pp = p;
                double[] r = pts.Select(q => Math.Abs(Point2.Dot(q - pp, nn))).ToArray();
                double mad = r.Where((x, i) => keep[i]).OrderBy(x => x).ElementAt(use.Count / 2) * 1.4826;
                double lim = Math.Max(3, 3 * mad);
                bool[] next = r.Select(x => x <= lim).ToArray();
                if (next.SequenceEqual(keep)) break;
                keep = next;
            }
            keepOut = keep;
            return keep.Count(k => k) >= 3;
        }
    }
}
