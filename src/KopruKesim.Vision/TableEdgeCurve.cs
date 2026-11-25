using System;
using System.Collections.Generic;
using System.Linq;
using KopruKesim.Core;
using OpenCvSharp;

namespace KopruKesim.Vision
{
    /// <summary>
    /// L kose kalibrasyonunda tablanin kenarlarini koselerden ibaret birakmaz: dort kose seritle olculen koselere
    /// homografiyle oturtulduktan sonra kenarlar fotografta bulunan kenarlardan hala sapabilir (2026-11-25 canli
    /// kalibrasyon: cizilen cerceve L kollarinin yonunden ~1,4 derece, kenar ortasinda 10-39 mm disarida). Her kenar
    /// icin kenara dik sapma f(s) = s (1 - s) (a + b s) oturtulur (TableCorrection.EdgeCurve):
    ///  - L kollarinin dis kenar noktalari (LCornerFinder): kenarin iki ucunda, kenarin yonu;
    ///  - sol ve sag kenarda dis citanin uzun kenari (tablanin tam boyunda duz ahsap kenari): citanin kenari L'nin dis
    ///    kenarindan sabit bir pay (L dudagi) iceride oldugu icin bu pay ayrica cozulur.
    /// Ust ve alt kenarda cita uclari duz degil (ortadakiler kose citalarindan ~17 mm geride): orada yalniz L kollari.
    /// </summary>
    public static class TableEdgeCurve
    {
        public sealed class EdgeReport
        {
            public string Name;
            /// <summary>Kullanilan / alinan L kolu noktasi ve cita kenari noktasi.</summary>
            public int ArmUsed, ArmTotal, SlatUsed, SlatTotal;
            /// <summary>Oturtmadan once ve sonra noktalarin kenar egrisinden sapmasi (mm, rms).</summary>
            public double RmsBeforeMm, RmsAfterMm;
            /// <summary>Kenar ortasinda (s = 0,5) duzeltme (mm, disari +).</summary>
            public double MidShiftMm;
            /// <summary>Cita kenarinin L dis kenarindan iceride kalma payi (mm); cita kullanilmadiysa NaN.</summary>
            public double SlatInsetMm = double.NaN;
        }

        public sealed class Result
        {
            /// <summary>8 deger (TableCorrection.EdgeCurve); oturtulamadiysa null.</summary>
            public double[] Curve;
            public EdgeReport[] Edges = new EdgeReport[4];
        }

        public static readonly string[] EdgeNames = { "alt", "sağ", "üst", "sol" };

        /// <summary>
        /// raw: duzeltmesiz kamera modeli; found: L kose tespiti (raw'in mm'sinde); tape: seritle olculen koseler (sol ust,
        /// sag ust, sol alt, sag alt); center: optik eksenin tablayi kestigi nokta (TableCorrection.Center).
        /// </summary>
        /// <param name="corners">Tabla koseleri (modelin mm'si; LCornerFinder.InsetCorners).</param>
        /// <param name="table">Bu koselerin gercek tabla mm'si (sol ust, sag ust, sol alt, sag alt).</param>
        /// <param name="wallMm">L'nin dis kenarinin tabla kenarindan disarida kalma payi: L kolu noktalari kenarin bu kadar
        /// disindadir.</param>
        public static Result Fit(Mat frame, LensCalibration raw, LCornerFinder.Result found, Point2[] corners, Point2[] table, Point2 center, double wallMm)
        {
            var res = new Result();
            var baseCorr = new TableCorrection(corners, table, center, 0);
            // Kenar basina gozlemler: (s, sapma mm, cita mi)
            var obs = Enumerable.Range(0, 4).Select(_ => new List<(double s, double off, bool slat)>()).ToArray();
            // Kose i'nin uzun kolu (X boyunca) ve kisa kolu hangi kenarda (EdgeEnds sirasi: 0 alt, 1 sag, 2 ust, 3 sol)
            int[] longEdge = { 2, 2, 0, 0 }, shortEdge = { 3, 1, 3, 1 };
            for (int i = 0; i < 4; i++)
            {
                AddArm(obs[longEdge[i]], found.Fits[i].PointsX, baseCorr, longEdge[i], wallMm);
                AddArm(obs[shortEdge[i]], found.Fits[i].PointsY, baseCorr, shortEdge[i], wallMm);
            }
            int[] armTotal = obs.Select(o => o.Count).ToArray();
            // Sol ve sag kenar: dis citanin uzun kenari
            using (var blur = new Mat())
            using (var lab = new Mat())
            {
                Cv2.GaussianBlur(frame, blur, new Size(0, 0), 1.0);
                Cv2.CvtColor(blur, lab, ColorConversionCodes.BGR2Lab);
                lab.GetArray(out Vec3b[] px);
                foreach (int e in new[] { 1, 3 })
                    AddSlat(obs[e], px, lab.Cols, lab.Rows, raw, baseCorr, e);
            }

            var curve = new double[8];
            for (int e = 0; e < 4; e++)
            {
                var rep = new EdgeReport { Name = EdgeNames[e], ArmTotal = armTotal[e], SlatTotal = obs[e].Count - armTotal[e] };
                res.Edges[e] = rep;
                var o = obs[e];
                bool withSlat = o.Count(x => x.slat) >= 20;
                var use = withSlat ? o : o.Where(x => !x.slat).ToList();
                // Iki uctan da yeterli L noktasi yoksa bu kenarin egrisi kurulamaz (duz birakilir)
                if (use.Count(x => !x.slat && x.s < 0.5) < 10 || use.Count(x => !x.slat && x.s > 0.5) < 10) continue;
                rep.RmsBeforeMm = Math.Sqrt(use.Where(x => !x.slat).Average(x => x.off * x.off));
                if (!FitEdge(use, withSlat, out double a, out double b, out double c, out bool[] keep, out double rms)) continue;
                curve[2 * e] = a; curve[2 * e + 1] = b;
                rep.RmsAfterMm = rms;
                rep.ArmUsed = use.Where((x, i) => keep[i] && !x.slat).Count();
                rep.SlatUsed = use.Where((x, i) => keep[i] && x.slat).Count();
                rep.MidShiftMm = TableCorrection.CurveValue(a, b, 0.5);
                if (withSlat) rep.SlatInsetMm = -c;
            }
            res.Curve = curve;
            return res;
        }

        static void AddArm(List<(double, double, bool)> list, List<Point2> pts, TableCorrection corr, int edge, double wallMm)
        {
            if (pts == null) return;
            TableCorrection.EdgeEnds(corr.True, edge, out Point2 a, out Point2 b);
            Point2 d = b - a, n = corr.OutwardNormal(edge);
            double L2 = Point2.Dot(d, d);
            foreach (Point2 p in pts)
            {
                Point2 t = corr.Apply(p);
                // L'nin dis kenari tabla kenarinin wallMm disinda: kenarin kendisi wallMm iceride
                list.Add((Point2.Dot(t - a, d) / L2, Point2.Dot(t - a, n) - wallMm, false));
            }
        }

        /// <summary>
        /// Citanin uzun kenari: kenar boyunca 25 mm arayla (uclardaki L'lerden uzakta), kenara dik profil (gercek tabla
        /// mm'sinde +25 disari ... -45 iceri, 0,5 mm adim). Ahsap rengi profilin ic ucundan (-45..-30 mm, Lab ortancasi);
        /// iceriden disari ilk kez renk farki 25'i gecip 1,5 mm boyunca ustunde kaldigi yer citanin kenari.
        /// </summary>
        static void AddSlat(List<(double, double, bool)> list, Vec3b[] px, int w, int h, LensCalibration raw, TableCorrection corr, int edge)
        {
            TableCorrection.EdgeEnds(corr.True, edge, out Point2 a, out Point2 b);
            Point2 dir = b - a, n = corr.OutwardNormal(edge);
            double len = dir.Length;
            const double step = 0.5, dOut = 25, dIn = -45;
            int m = (int)((dOut - dIn) / step) + 1;
            for (double along = 250; along <= len - 250; along += 25)
            {
                double s = along / len;
                Point2 basePt = a + dir * s;
                var table = new Point2[m];
                for (int j = 0; j < m; j++) table[j] = basePt + n * (dIn + j * step);   // iceriden disari
                Point2[] img = LensCalibrator.TableToImage(raw, table.Select(corr.Invert).ToArray());
                var col = new double[m][];
                bool ok = true;
                for (int j = 0; j < m && ok; j++)
                {
                    int x = (int)Math.Round(img[j].X), y = (int)Math.Round(img[j].Y);
                    if (x < 0 || y < 0 || x >= w || y >= h) { ok = false; break; }
                    Vec3b v = px[y * w + x];
                    col[j] = new double[] { v.Item0, v.Item1, v.Item2 };
                }
                if (!ok) continue;
                int refN = (int)(15 / step) + 1;
                double[] refc = Enumerable.Range(0, 3).Select(k => Median(col.Take(refN).Select(c => c[k]))).ToArray();
                double Diff(int j) => Math.Sqrt(Enumerable.Range(0, 3).Sum(k => (col[j][k] - refc[k]) * (col[j][k] - refc[k])));
                int start = (int)((-30 - dIn) / step);
                for (int j = start; j + 3 < m; j++)
                {
                    if (Diff(j) > 25 && Diff(j + 1) > 25 && Diff(j + 2) > 25 && Diff(j + 3) > 25)
                    {
                        double off = dIn + (j - 0.5) * step;   // son ahsap ile ilk ahsap olmayan arasi
                        if (off < dOut - 3) list.Add((s, off, true));
                        break;
                    }
                }
            }
        }

        static double Median(IEnumerable<double> v)
        {
            double[] a = v.OrderBy(x => x).ToArray();
            return a.Length == 0 ? 0 : a[a.Length / 2];
        }

        /// <summary>
        /// off = s (1 - s) (a + b s) [+ c cita noktalarinda] en kucuk karelerle; 3 x MAD (en az 1,5 mm) disindakiler
        /// atilarak yinelenir. Uc bilinmeyenli (cita varsa) ya da iki bilinmeyenli dogrusal denklem.
        /// </summary>
        static bool FitEdge(List<(double s, double off, bool slat)> o, bool withSlat, out double a, out double b, out double c, out bool[] keep, out double rms)
        {
            a = b = c = 0; rms = double.NaN;
            int n = o.Count, k = withSlat ? 3 : 2;
            bool[] kp = Enumerable.Repeat(true, n).ToArray();
            keep = kp;
            for (int it = 0; it < 6; it++)
            {
                var ata = new double[k, k]; var atb = new double[k];
                for (int i = 0; i < n; i++)
                {
                    if (!kp[i]) continue;
                    double s = o[i].s, g = s * (1 - s);
                    double[] row = withSlat ? new[] { g, g * s, o[i].slat ? 1.0 : 0.0 } : new[] { g, g * s };
                    for (int p = 0; p < k; p++) { atb[p] += row[p] * o[i].off; for (int q = 0; q < k; q++) ata[p, q] += row[p] * row[q]; }
                }
                double[] x = Solve(ata, atb);
                if (x == null) return false;
                a = x[0]; b = x[1]; c = withSlat ? x[2] : 0;
                double aa = a, bb = b, cc = c;
                double[] r = o.Select(q => Math.Abs(q.off - (TableCorrection.CurveValue(aa, bb, q.s) + (q.slat ? cc : 0)))).ToArray();
                double mad = r.Where((v, i) => kp[i]).OrderBy(v => v).ElementAt(kp.Count(t => t) / 2) * 1.4826;
                double lim = Math.Max(1.5, 3 * mad);
                bool changed = false;
                for (int i = 0; i < n; i++) { bool nk = r[i] <= lim; if (nk != kp[i]) { kp[i] = nk; changed = true; } }
                rms = Math.Sqrt(r.Where((v, i) => kp[i]).Select(v => v * v).DefaultIfEmpty(0).Average());
                if (kp.Count(t => t) < 2 * k) return false;
                if (!changed) break;
            }
            return true;
        }

        static double[] Solve(double[,] m, double[] v)
        {
            int n = v.Length;
            var a = new double[n, n + 1];
            for (int i = 0; i < n; i++) { for (int j = 0; j < n; j++) a[i, j] = m[i, j]; a[i, n] = v[i]; }
            for (int col = 0; col < n; col++)
            {
                int piv = col;
                for (int r = col + 1; r < n; r++) if (Math.Abs(a[r, col]) > Math.Abs(a[piv, col])) piv = r;
                if (Math.Abs(a[piv, col]) < 1e-12) return null;
                for (int j = 0; j <= n; j++) { double t = a[col, j]; a[col, j] = a[piv, j]; a[piv, j] = t; }
                for (int r = 0; r < n; r++)
                {
                    if (r == col) continue;
                    double f = a[r, col] / a[col, col];
                    for (int j = col; j <= n; j++) a[r, j] -= f * a[col, j];
                }
            }
            return Enumerable.Range(0, n).Select(i => a[i, n] / a[i, i]).ToArray();
        }
    }
}
