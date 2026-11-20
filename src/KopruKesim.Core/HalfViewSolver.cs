using System;
using System.Collections.Generic;
using System.Linq;

namespace KopruKesim.Core
{
    /// <summary>
    /// Tablanin yalniz bir yarisini goren fotografin kalibrasyonu (iki fotografli olcum, 6 etiket).
    ///
    /// Fotografta 4 etiket gorunur: o yarinin iki kose etiketi ve kisa kenarlarin ortasindaki iki etiket. Etiketler
    /// birbirinin aynisi oldugu icin hangisinin hangisi oldugu bilinmez; olasi eslestirmeler denenir:
    /// fotograftaki dortgenin koseleri sirayla yarinin dort etiketine (4 donus x 2 yon), fazla etiket bulunduysa
    /// her 4'lu secim. Her eslestirme icin merkezlerden homografi, sonra tum ic koselerle inceltme
    /// (CheckerGridFit) yapilir. Dogru eslestirme:
    ///  - butun ic koseler izgaraya oturur; ortadaki etiketler 90 derece cevrik kondugu icin koseyle ortayi
    ///    karistiran eslestirme oturmaz,
    ///  - tabla ustten bakildigi gibi gorunur (ayna goruntusu degil; kamera goruntusu aynalanamaz),
    ///  - izgara hatasi en kucuktur.
    /// </summary>
    public static class HalfViewSolver
    {
        /// <param name="cycle">Yarinin 4 etiketinin numarasi, tabladaki dortgen boyunca sirayla.</param>
        /// <param name="centers">Fotografta bulunan etiket merkezleri (piksel).</param>
        /// <param name="corners">Ayni sirayla her etiketin ic koseleri (piksel).</param>
        /// <param name="side">-1: fotograf Y = 0 tarafindan, +1: Y = genislik tarafindan cekildi, 0: bilinmiyor. Alti etiketin
        /// hepsi gorunurse 6 etiketli yerlesim 180 derece simetrik oldugu icin fotograf diger yariya da oturur; kamera
        /// konumu (homografiden) dogru tarafta olan eslestirme secilir.</param>
        public static MarkerFit Solve(MarkerLayout layout, int[] cycle, IList<Point2> centers, IList<Point2[]> corners, int imageWidth, int imageHeight, int side = 0)
        {
            var fail = new MarkerFit();
            if (cycle == null || cycle.Length != 4) throw new ArgumentException("Yarida 4 etiket olmali.");
            if (centers.Count < 4)
            {
                fail.Error = string.Format("Yeterli isaret gorunmuyor: {0} bulundu, en az 4 gerekli.", centers.Count);
                return fail;
            }
            int perBoard = layout.PatternCols * layout.PatternRows;
            var valid = new List<MarkerFit>();
            foreach (int[] pick in Choose(centers.Count, 4))
            {
                int[] ring = Ring(pick.Select(i => centers[i]).ToArray()).Select(k => pick[k]).ToArray();
                for (int dir = -1; dir <= 1; dir += 2)
                    for (int rot = 0; rot < 4; rot++)
                    {
                        var seen = new Dictionary<int, Point2>();
                        var cornerMap = new Dictionary<int, Point2[]>();
                        for (int i = 0; i < 4; i++)
                        {
                            int id = cycle[((rot + dir * i) % 4 + 4) % 4];
                            seen[id] = centers[ring[i]];
                            cornerMap[id] = corners[ring[i]];
                        }
                        MarkerFit fit = MarkerSolver.Solve(layout, seen, imageWidth, imageHeight);
                        if (fit.Calibration == null) continue;
                        CheckerGridFit.Refine(layout, fit, cornerMap, imageWidth, imageHeight);
                        if (fit.GridPoints != 4 * perBoard) continue;          // izgara her etikette oturmadi
                        if (!SeenFromAbove(fit.Calibration, layout)) continue;  // ayna goruntusu
                        valid.Add(fit);
                    }
            }
            // Kamera dogru tarafta mi (kamera konumu tahmin edilemediyse bu eleme yapilmaz)
            if (side != 0)
            {
                double mid = layout.TableWidthMm / 2;
                var onSide = valid.Where(f => f.Calibration.Camera != null && (side < 0 ? f.Calibration.Camera.Y <= mid : f.Calibration.Camera.Y >= mid)).ToList();
                if (onSide.Count > 0) valid = onSide;
            }
            MarkerFit best = valid.OrderBy(f => f.RmsMm).FirstOrDefault();
            if (best == null)
            {
                fail.Error = "Etiketler bu yarinin yerlesimine uymuyor. Ortadaki iki etiket 90 derece cevrik (uzun kenari tablanin kisa kenari boyunca) konmali; fotografta bu yarinin iki kose etiketi ve iki orta etiketi gorunmeli.";
                return fail;
            }
            return best;
        }

        /// <summary>
        /// Tabla ustten gorulur gibi mi: goruntude (y asagi) X ve Y yonlerinin vektorel carpimi negatif
        /// (tek fotografli olcumdeki kurala uyar: orijin sol ustte, X asagi, Y saga).
        /// </summary>
        public static bool SeenFromAbove(TableCalibration cal, MarkerLayout layout)
        {
            Homography toImage = cal.ImageToTable.Inverse();
            var c = new Point2(layout.TableLengthMm / 2, layout.TableWidthMm / 2);
            Point2 p0 = toImage.Transform(c), px = toImage.Transform(c + new Point2(50, 0)), py = toImage.Transform(c + new Point2(0, 50));
            return Point2.Cross(px - p0, py - p0) < 0;
        }

        /// <summary>Dort noktayi agirlik merkezi cevresinde aci sirasina dizer (fotograftaki dortgenin kenarlari boyunca).</summary>
        private static int[] Ring(Point2[] p)
        {
            double cx = p.Average(q => q.X), cy = p.Average(q => q.Y);
            return Enumerable.Range(0, p.Length).OrderBy(i => Math.Atan2(p[i].Y - cy, p[i].X - cx)).ToArray();
        }

        private static IEnumerable<int[]> Choose(int n, int k)
        {
            var idx = Enumerable.Range(0, k).ToArray();
            while (true)
            {
                yield return (int[])idx.Clone();
                int i = k - 1;
                while (i >= 0 && idx[i] == n - k + i) i--;
                if (i < 0) yield break;
                idx[i]++;
                for (int j = i + 1; j < k; j++) idx[j] = idx[j - 1] + 1;
            }
        }
    }
}
