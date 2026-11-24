using System;
using System.Collections.Generic;
using System.Linq;
using KopruKesim.Core;
using OpenCvSharp;

namespace KopruKesim.Vision
{
    /// <summary>
    /// Paspasli kalibrasyonda tablanin dort kenarini fotografta bulur ve seritle olculen tablaya gore duzeltmeyi
    /// (TableCorrection.Fit) kurar.
    ///
    /// Kenar: paspaslarin arasinda ahsabin kenari. Kenar boyunca her 40 mm'de kenara dik bir profil (iceriden 50 mm,
    /// disaridan 50 mm) fotografta orneklenir; ahsap turuncu-sari (R - B yuksek), disarisi gri beton, koyu celik ya da
    /// beyaz kapak (R - B dusuk). Profildeki en sert dusus kenardir. Paspaslarin dis kenar noktalari (kalibrasyonda zaten
    /// bulunur) kenarin uclarini verir.
    /// </summary>
    public static class TableEdgeFit
    {
        public sealed class Report
        {
            /// <summary>Kenar basina bulunan ahsap ve paspas noktasi: alt, sag, ust, sol.</summary>
            public int[] WoodPoints = new int[4], MatPoints = new int[4];
            public TableCorrection.FitReport Fit;
        }

        static bool Debug => Environment.GetEnvironmentVariable("KK_DEBUG") == "1";

        /// <param name="raw">Duzeltmesiz kalibrasyon (model).</param>
        /// <param name="photo">Paspaslarin dis koselerinin model mm yeri: sol ust, sag ust, sol alt, sag alt.</param>
        /// <param name="table">Seritle olculen kenar ve kosegenden kurulan gercek koseler (ayni sira).</param>
        /// <param name="matEdgeMm">Paspaslarin dis kenar noktalari (model mm, paspas yuzeyi yuksekliginde).</param>
        /// <param name="mats">Her paspas: modelin olctugu 4 kose ve gercek 4 kose.</param>
        public static TableCorrection Build(Mat frame, LensCalibration raw, Point2[] photo, Point2[] table, MatSpec spec,
            IList<Point2> matEdgeMm, IList<Tuple<Point2[], Point2[]>> mats, out Report report)
        {
            report = new Report();
            float[] wood; int w = frame.Cols, h = frame.Rows;
            using (var f = new Mat())
            {
                frame.ConvertTo(f, MatType.CV_32FC3);
                Mat[] ch = Cv2.Split(f);
                using (var rb = new Mat())
                {
                    Cv2.Subtract(ch[2], ch[0], rb);
                    Cv2.GaussianBlur(rb, rb, new Size(5, 5), 0);
                    rb.GetArray(out wood);
                }
                foreach (var c in ch) c.Dispose();
            }
            Func<Point2, double> sample = p =>
            {
                double x = p.X, y = p.Y;
                if (double.IsNaN(x) || x < 0 || y < 0 || x >= w - 1 || y >= h - 1) return double.NaN;
                int x0 = (int)x, y0 = (int)y; double fx = x - x0, fy = y - y0;
                int i = y0 * w + x0;
                return (wood[i] * (1 - fx) + wood[i + 1] * fx) * (1 - fy) + (wood[i + w] * (1 - fx) + wood[i + w + 1] * fx) * fy;
            };

            Point2 mid = new Point2(photo.Average(p => p.X), photo.Average(p => p.Y));
            var edges = new List<TableCorrection.EdgeSample>[4];
            var debugPts = new List<Point2>();
            for (int e = 0; e < 4; e++)
            {
                edges[e] = new List<TableCorrection.EdgeSample>();
                TableCorrection.EdgeEnds(photo, e, out Point2 a, out Point2 b);
                Point2 dir = b - a; double len = dir.Length; dir = dir * (1 / len);
                var n = new Point2(-dir.Y, dir.X);
                if (Point2.Dot(n, a - mid) < 0) n = n * -1;   // tablanin disina
                double margin = (e % 2 == 0 ? spec.LongMm : spec.ShortMm) + 60;

                const int half = 50;
                var prof = new Point2[2 * half + 1];
                for (double d = margin; d <= len - margin; d += 40)
                {
                    Point2 c = a + dir * d;
                    for (int k = -half; k <= half; k++) prof[k + half] = c + n * k;
                    double[] v = LensCalibrator.TableToImage(raw, prof, 0).Select(sample).ToArray();
                    if (v.Any(double.IsNaN)) continue;
                    int best = -1; double bestD = 0;
                    for (int k = 3; k < v.Length - 3; k++)
                    {
                        double dd = v[k + 2] - v[k - 2];
                        if (dd < bestD) { bestD = dd; best = k; }
                    }
                    if (best < 8 || best > v.Length - 9 || bestD > -15) continue;
                    // Iceride ahsap, disarida degil
                    double inside = v.Skip(Math.Max(0, best - 30)).Take(Math.Min(25, best - 5)).Average();
                    double outside = v.Skip(best + 6).Take(25).Average();
                    if (!(inside - outside > 20)) continue;
                    double y0 = v[best + 1] - v[best - 3], y1 = bestD, y2 = v[best + 3] - v[best - 1];
                    double den = y0 - 2 * y1 + y2, sub = Math.Abs(den) > 1e-9 ? 0.5 * (y0 - y2) / den : 0;
                    double s = best - half + Math.Max(-1, Math.Min(1, sub));
                    edges[e].Add(new TableCorrection.EdgeSample(c + n * s, false));
                    if (Debug) debugPts.Add(LensCalibrator.TableToImage(raw, new[] { c + n * s }, 0)[0]);
                }
                report.WoodPoints[e] = edges[e].Count;
                if (matEdgeMm != null)
                    foreach (Point2 p in matEdgeMm)
                    {
                        double t = Point2.Dot(p - a, dir) / len, s = Point2.Dot(p - a, n), along = t * len;
                        if (t < -0.02 || t > 1.02 || Math.Abs(s) > 20) continue;
                        if (along > margin - 40 && along < len - margin + 40) continue;
                        edges[e].Add(new TableCorrection.EdgeSample(p, true));
                    }
                report.MatPoints[e] = edges[e].Count - report.WoodPoints[e];
            }
            Point2 center = LensCalibrator.ImageToTable(raw, new[] { new Point2(raw.Cx, raw.Cy) })[0];
            TableCorrection corr = TableCorrection.Fit(photo, table, center, edges, mats, out report.Fit);
            if (Debug)
            {
                using (Mat vis = frame.Clone())
                {
                    foreach (Point2 p in debugPts) Cv2.Circle(vis, new Point((int)p.X, (int)p.Y), 4, Scalar.Red, -1);
                    vis.SaveImage(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kk_tabla_kenar.jpg"));
                }
                var fr = report.Fit;
                Console.WriteLine("tabla duzeltmesi: merkez {0}, E {1:E2}, kenar noktalari ahsap {2} paspas {3}", center, fr.E,
                    string.Join("/", report.WoodPoints), string.Join("/", report.MatPoints));
                Console.WriteLine("  kenar duzlugu (alt/sag/ust/sol) once {0} sonra {1} mm",
                    string.Join("/", fr.StraightBefore.Select(v => v.ToString("F2"))), string.Join("/", fr.StraightAfter.Select(v => v.ToString("F2"))));
                if (fr.MatBefore != null)
                    for (int m = 0; m < fr.MatBefore.Length; m++)
                        Console.WriteLine("  paspas {0}: once {1} sonra {2} (beklenen {3})", m,
                            string.Join("/", fr.MatBefore[m].Select(v => v.ToString("F0"))), string.Join("/", fr.MatAfter[m].Select(v => v.ToString("F0"))),
                            string.Join("/", fr.MatExpected[m].Select(v => v.ToString("F0"))));
            }
            return corr;
        }
    }
}
