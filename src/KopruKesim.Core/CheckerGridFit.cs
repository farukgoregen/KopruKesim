using System;
using System.Collections.Generic;
using System.Linq;

namespace KopruKesim.Core
{
    /// <summary>
    /// Kose dama etiketlerinin tum ic koselerini kullanarak kalibrasyonu inceltir.
    ///
    /// Yalniz dort etiket merkeziyle hesaplanan homografi tam belirlidir (4 nokta, 8 bilinmeyen): merkezlerden
    /// birindeki kucuk bir hata dogrudan olcume gecer ve hata hic gorunmez. Her etikette (ornek 4x3) ic kose
    /// vardir ve aralarindaki uzaklik bilinir (kare kenari); dort etiketle ~48 nokta, en kucuk kareler.
    ///
    /// Etiketler cogu zaman "sayfaya sigdir" ile %94-97 olcekte basilir; kare kenari ve etiket merkezinin
    /// koseye uzakligi ayni oranda kuculur. Bu oran (baski olcegi) noktalardan birlikte tahmin edilir:
    /// tablanin olcusu genel olcegi, karelerin kendisi yerel bicimi verir.
    /// </summary>
    public static class CheckerGridFit
    {
        /// <summary>Bir etiketin izgaraya oturmasi en cok bu kadar sapabilir (kare kenarina oranla).</summary>
        private const double SnapTolerance = 0.4;

        /// <param name="fit">Merkezlerden hesaplanmis sonuc (MarkerSolver.Solve); basariliysa yerinde guncellenir.</param>
        /// <param name="corners">Etiket numarasi -> ic koseler (piksel), dedektorun sirasiyla (satir satir, cols x rows).</param>
        public static void Refine(MarkerLayout layout, MarkerFit fit, IDictionary<int, Point2[]> corners, int imageWidth, int imageHeight)
        {
            if (fit == null || fit.Calibration == null || layout.SquareMm <= 0 || corners == null) return;
            int cols = layout.PatternCols, rows = layout.PatternRows;
            var boards = new List<int>();
            foreach (int id in fit.Used)
                if (corners.ContainsKey(id) && corners[id] != null && corners[id].Length == cols * rows) boards.Add(id);
            if (boards.Count < 3) return;

            var markers = layout.Markers.ToDictionary(m => m.Id, m => new Point2(m.X, m.Y));
            Homography h = fit.Calibration.ImageToTable;
            double s = 1;
            Dictionary<int, Point2[]> ideal = null;   // etiket -> her ic kosenin ideal yeri (s=1 icin merkeze gore, mm)

            for (int iter = 0; iter < 20; iter++)
            {
                // 1) Her ic koseyi izgaradaki yerine eslestir (o anki homografiyle)
                var current = new Dictionary<int, Point2[]>();
                foreach (int id in boards)
                {
                    Point2 center = Center(layout, markers[id], s);
                    bool? along = layout.StrictOrientation ? !layout.Rotated.Contains(id) : (bool?)null;
                    Point2[] off = Snap(corners[id].Select(p => h.Transform(p) - center).ToArray(), cols, rows, layout.SquareMm * s, along);
                    if (off != null) current[id] = off.Select(o => o * (1 / s)).ToArray();
                }
                if (current.Count < 3) break;   // izgara oturmadi: merkezlerle yetin
                ideal = current;

                // 2) Tum noktalardan homografi
                var src = new List<Point2>();
                var dst = new List<Point2>();
                foreach (var kv in ideal)
                {
                    Point2 center = Center(layout, markers[kv.Key], s);
                    for (int n = 0; n < kv.Value.Length; n++) { src.Add(corners[kv.Key][n]); dst.Add(center + kv.Value[n] * s); }
                }
                try { h = Homography.FromPoints(src, dst); }
                catch (ArgumentException) { return; }

                // 3) Baski olcegi: yeni homografiyle etiket ici aralik / ideal aralik
                double num = 0, den = 0;
                foreach (var kv in ideal)
                {
                    Point2[] mm = corners[kv.Key].Select(p => h.Transform(p)).ToArray();
                    var mean = new Point2(mm.Average(p => p.X), mm.Average(p => p.Y));
                    for (int n = 0; n < mm.Length; n++) { num += Point2.Dot(mm[n] - mean, kv.Value[n]); den += Point2.Dot(kv.Value[n], kv.Value[n]); }
                }
                double sNew = den > 0 ? num / den : s;
                if (sNew < 0.8 || sNew > 1.2) return;   // makul degil: etiket turu yanlis secilmis olabilir
                bool done = Math.Abs(sNew - s) < 1e-5;
                s = sNew;
                if (done) break;
            }
            if (ideal == null) return;

            // Sonuc ve uyum: her noktanin hesaplanan ve ideal yeri arasindaki fark
            double sum = 0, worst = 0, ox = 0, oxx = 0, oy = 0, oyy = 0; int count = 0;
            foreach (var kv in ideal)
            {
                Point2 center = Center(layout, markers[kv.Key], s);
                Point2[] mm = corners[kv.Key].Select(p => h.Transform(p)).ToArray();
                var mean = new Point2(mm.Average(p => p.X), mm.Average(p => p.Y));
                for (int n = 0; n < mm.Length; n++)
                {
                    double e = Point2.Distance(mm[n], center + kv.Value[n] * s);
                    sum += e * e; worst = Math.Max(worst, e); count++;
                    ox += (mm[n].X - mean.X) * kv.Value[n].X; oxx += kv.Value[n].X * kv.Value[n].X;
                    oy += (mm[n].Y - mean.Y) * kv.Value[n].Y; oyy += kv.Value[n].Y * kv.Value[n].Y;
                }
            }
            Homography inv = h.Inverse();
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
            fit.PrintScale = s;
            fit.GridPoints = count;
            fit.RmsMm = Math.Sqrt(sum / Math.Max(1, count));
            fit.MaxMm = worst;
            // Kareler X ve Y yonunde farkli olcekte gorunuyorsa girilen tabla olcusu (en/boy orani) tutmuyordur
            fit.AxisRatio = oxx > 0 && oyy > 0 && oy > 0 ? (ox / oxx) / (oy / oyy) : double.NaN;
        }

        /// <summary>
        /// Etiket merkezi: etiket tabla kosesine (ya da kenarin ortasindaysa kenara) dayali; kesik etikette baski
        /// olcegiyle dayandigi noktaya uzakligi kuculur.
        /// </summary>
        private static Point2 Center(MarkerLayout layout, Point2 nominal, double s)
        {
            if (!layout.CutTag) return nominal;
            Point2 anchor = layout.AnchorOf(nominal.X, nominal.Y);
            return anchor + (nominal - anchor) * s;
        }

        /// <summary>
        /// Merkeze gore konumlari (mm) en yakin izgara noktasina oturtur. Desenin uzun yonu tablanin X ya da Y
        /// yonunde olabilir; ikisi de denenir. Her nokta ayri bir izgara noktasina oturmazsa null.
        /// </summary>
        /// <param name="along">Desen yonu biliniyorsa: true = sutunlar X yonunde, false = Y yonunde; null = ikisi de denenir.</param>
        private static Point2[] Snap(Point2[] rel, int cols, int rows, double square, bool? along = null)
        {
            Point2[] best = null; double bestErr = double.MaxValue;
            foreach (bool colsAlongX in new[] { true, false })
            {
                if (along.HasValue && along.Value != colsAlongX) continue;
                int nx = colsAlongX ? cols : rows, ny = colsAlongX ? rows : cols;
                var used = new HashSet<int>();
                var res = new Point2[rel.Length];
                double err = 0; bool ok = true;
                for (int n = 0; n < rel.Length && ok; n++)
                {
                    int i = (int)Math.Round(rel[n].X / square + (nx - 1) / 2.0);
                    int j = (int)Math.Round(rel[n].Y / square + (ny - 1) / 2.0);
                    if (i < 0 || i >= nx || j < 0 || j >= ny || !used.Add(j * nx + i)) { ok = false; break; }
                    res[n] = new Point2((i - (nx - 1) / 2.0) * square, (j - (ny - 1) / 2.0) * square);
                    double e = Point2.Distance(res[n], rel[n]);
                    if (e > SnapTolerance * square) ok = false;
                    err += e;
                }
                if (ok && err < bestErr) { bestErr = err; best = res; }
            }
            return best;
        }
    }
}
