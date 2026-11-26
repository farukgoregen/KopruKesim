using System;
using System.Collections.Generic;
using System.Linq;
using KopruKesim.Core;
using OpenCvSharp;

namespace KopruKesim.Vision
{
    /// <summary>
    /// Parcanin ust yuzeyinin kenari (2026-11-26). Kalinlikli parcada fotograftaki dis sinir kenara gore degisir:
    /// kameradan uzak tarafta ust kenar, kameraya bakan tarafta yan yuz gorunuyorsa alt kenar, golgede kalip
    /// gorunmuyorsa yine ust kenar. SlabLocator kameraya bakan kenarda her zaman alt kenari varsayiyordu: 1208 x 600 x 20
    /// kopukte (28 konum) o kenar 0-10 mm iceride kaliyordu (olcuye uyan katsayi uzak kenar 1,02, bakan kenar 0,75).
    ///
    /// Yontem: fotograf ust yuzey yuksekliginde (z = kalinlik) duz gorunume cevrilmis gibi orneklenir; bu duzlemde ust
    /// yuzey gercek yerinde ve olcusunde durur, ayrica kalinlik duzeltmesi gerekmez. Kaba siluet bos tabladan renk
    /// farkiyla (parca rengine mi bos tablanin o noktadaki rengine mi yakin) bulunur; her kenarda dik profillerde,
    /// beklenen yan yuz seridi (kalinlik x kamera tabanina uzaklik / (yukseklik - kalinlik)) kadar genis pencerede icten
    /// disa ust yuzey renginden ilk belirgin ayrilma ust kenar sayilir (TopOffset). Yan yuz ust yuzeyle tam ayni renkte
    /// gorunurse ust kenar ayirt edilemez, dis sinir alinir.
    ///
    /// Python denemesi (ayni 27 kopuk fotografi): uygulamanin onceki sonucu ort. |hata| 5,7 mm, 3 mm icinde %49, en buyuk
    /// 27 mm; bu yontem 2,6 mm, %65, 11,7 mm.
    /// </summary>
    public static class TopEdgeRefiner
    {
        const double StepMm = 0.25, ProfileEveryMm = 4, InsideMarginMm = 8, OutsideMarginMm = 12, MaxShiftMm = 40;

        /// <param name="lens">Kalibrasyon (duzeltmeli); koseler ayni tabla cercevesinde doner.</param>
        /// <param name="approx">Dedektorun buldugu 4 kose (tabla mm); hangi parcanin oldugunu ve guvenlik siniri icin.</param>
        /// <returns>Ust yuzeyin 4 kosesi (tabla mm) ya da bulunamazsa null (why: neden).</returns>
        public static Point2[] Refine(Mat frame, Mat empty, LensCalibration lens, double thicknessMm, Point2[] approx, out string why)
        {
            why = null;
            if (approx == null || approx.Length != 4 || empty == null || empty.Empty() || empty.Size() != frame.Size())
            { why = "kaba dortgen ya da bos tabla yok"; return null; }
            LensCalibration raw = lens.WithoutCorrection();
            Point2[] approxRaw = approx.Select(p => ToModel(lens, p)).ToArray();
            double t = Math.Max(0, thicknessMm);

            // --- ilgi bolgesi (fotograf) ---
            Point2[] approxPx = LensCalibrator.TableToImage(raw, Grow(approxRaw, 60), 0)
                .Concat(LensCalibrator.TableToImage(raw, Grow(approxRaw, 60), t)).ToArray();
            int x0 = Clamp((int)approxPx.Min(p => p.X) - 40, 0, frame.Cols - 1), y0 = Clamp((int)approxPx.Min(p => p.Y) - 40, 0, frame.Rows - 1);
            int x1 = Clamp((int)approxPx.Max(p => p.X) + 40, 1, frame.Cols), y1 = Clamp((int)approxPx.Max(p => p.Y) + 40, 1, frame.Rows);
            if (x1 - x0 < 20 || y1 - y0 < 20) { why = "ilgi bolgesi kucuk"; return null; }
            var roi = new Rect(x0, y0, x1 - x0, y1 - y0);

            using (var img = new Mat(frame, roi))
            using (var emp = new Mat(empty, roi))
            using (Mat lab2 = Lab(img, 2.0)) using (Mat labE2 = Lab(emp, 2.0))
            using (Mat lab1 = Lab(img, 1.0)) using (Mat labE1 = Lab(emp, 1.0))
            using (Mat lab12 = Lab(img, 1.2)) using (Mat labE12 = Lab(emp, 1.2))
            {
                int w = roi.Width, h = roi.Height;
                labE12.GetArray(out Vec3f[] profE);
                lab2.GetArray(out Vec3f[] a2); labE2.GetArray(out Vec3f[] e2);
                lab1.GetArray(out Vec3f[] a1); labE1.GetArray(out Vec3f[] e1);
                lab12.GetArray(out Vec3f[] prof);
                Point2 cPx = LensCalibrator.TableToImage(raw, new[] { Centroid(approxRaw) }, 0)[0] - new Point2(x0, y0);
                int cx = Clamp((int)cPx.X, 0, w - 1), cy = Clamp((int)cPx.Y, 0, h - 1);

                // 1) tohum: bos tabladan renkce (a, b) farkli bolge; parcanin rengi tohumun icinden
                var seedArr = new byte[w * h];
                for (int i = 0; i < seedArr.Length; i++)
                {
                    double da = a2[i].Item1 - e2[i].Item1, db = a2[i].Item2 - e2[i].Item2;
                    seedArr[i] = (byte)(da * da + db * db > 100 ? 255 : 0);
                }
                Vec3f color;
                using (var seed = new Mat(h, w, MatType.CV_8UC1))
                {
                    seed.SetArray(seedArr);
                    using (var k = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(31, 31))) Cv2.MorphologyEx(seed, seed, MorphTypes.Open, k);
                    using (Mat comp = Component(seed, cx, cy))
                    {
                        if (comp == null) { why = "parca bos tabladan ayrilmadi"; return null; }
                        using (var k = new Mat(31, 31, MatType.CV_8UC1, Scalar.All(1))) Cv2.Erode(comp, comp, k);
                        comp.GetArray(out byte[] inside);
                        var vals = Enumerable.Range(0, inside.Length).Where(i => inside[i] != 0).Select(i => a2[i]).ToArray();
                        if (vals.Length < 100) { why = "parca rengi alinamadi"; return null; }
                        color = new Vec3f(Median(vals.Select(v => v.Item0)), Median(vals.Select(v => v.Item1)), Median(vals.Select(v => v.Item2)));
                    }
                }

                // 2) siluet: her piksel parca rengine mi bos tablanin rengine mi yakin (parlaklik golgeyle degisir, az agirlik)
                var cls = new byte[w * h];
                for (int i = 0; i < cls.Length; i++)
                {
                    double dE = D(a1[i], e1[i]), dC = D(a1[i], color);
                    cls[i] = (byte)(dE - dC > 0 ? 255 : 0);
                }
                Point2[] contourPx;
                using (var mask = new Mat(h, w, MatType.CV_8UC1))
                {
                    mask.SetArray(cls);
                    using (var k = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(21, 21))) Cv2.MorphologyEx(mask, mask, MorphTypes.Open, k);
                    using (Mat comp = Component(mask, cx, cy))
                    {
                        if (comp == null) { why = "siluet bulunamadi"; return null; }
                        using (var k = new Mat(41, 41, MatType.CV_8UC1, Scalar.All(255))) Cv2.MorphologyEx(comp, comp, MorphTypes.Close, k);
                        Cv2.FindContours(comp, out Point[][] cs, out _, RetrievalModes.External, ContourApproximationModes.ApproxNone);
                        if (cs.Length == 0) { why = "siluet bulunamadi"; return null; }
                        contourPx = cs.OrderByDescending(c => c.Length).First().Select(p => new Point2(p.X + x0, p.Y + y0)).ToArray();
                    }
                }
                Point2[] contourMm = LensCalibrator.ImageToTable(raw, contourPx);
                Point2[] sil = Quad(contourMm);
                if (sil == null) { why = "siluet dortgen degil"; return null; }

                // 3) siluet z = t duzleminde: ayni isin kameraya dogru (taban noktasina (H - t) / H)
                CameraPose cam = raw.CameraPosition();
                var foot = new Point2(cam.X, cam.Y);
                double H = cam.Height;
                if (!(H > t + 100)) { why = "kamera yuksekligi gecersiz"; return null; }
                Point2[] top = sil.Select(p => foot + (p - foot) * ((H - t) / H)).ToArray();
                Point2 center = Centroid(top);

                // 4) her kenarda profiller: en icteki guclu gecis
                var lines = new (Point2 P, Point2 D)[4];
                for (int e = 0; e < 4; e++)
                {
                    Point2 pa = top[e], pb = top[(e + 1) % 4];
                    double len = Point2.Distance(pa, pb);
                    Point2 dir = (pb - pa) * (1 / len), n = new Point2(-dir.Y, dir.X);
                    if (Point2.Dot(center - pa, n) > 0) n = n * -1;   // disari
                    double sd = Point2.Dot((pa + pb) * 0.5 - foot, n);  // > 0 uzak kenar, < 0 kameraya bakan
                    double band = Math.Max(0, -sd) * t / (H - t);
                    int m = (int)Math.Round((band + InsideMarginMm + OutsideMarginMm) / StepMm) + 1;
                    var bases = new List<Point2>();
                    for (double u = 0.1 * len; u < 0.9 * len; u += ProfileEveryMm) bases.Add(pa + dir * u);
                    var q = new Point2[bases.Count * m];
                    for (int b = 0; b < bases.Count; b++)
                        for (int j = 0; j < m; j++) q[b * m + j] = bases[b] + n * (-(band + InsideMarginMm) + j * StepMm);
                    Point2[] qPx = LensCalibrator.TableToImage(raw, q, t);
                    var found = new List<Point2>();
                    var g = new double[m - 1];
                    var vals = new Vec3f[m];
                    var valsE = new Vec3f[m];
                    for (int b = 0; b < bases.Count; b++)
                    {
                        bool ok = true;
                        for (int j = 0; j < m && ok; j++) ok = Sample(prof, w, h, qPx[b * m + j].X - x0, qPx[b * m + j].Y - y0, out vals[j]);
                        if (!ok) continue;
                        for (int j = 0; j < m - 1; j++) g[j] = D3(vals[j + 1], vals[j]);
                        double[] gs = Smooth(g);
                        double off = TopOffset(gs, vals, band);
                        if (double.IsNaN(off)) continue;
                        // Uzak kenarda yan yuz gorunmez: ust yuzey renginden ayrilan ama bos tablaya da benzemeyen serit
                        // (isik alan kenar pahi, 172546 sag kenar: gri-yesil ust yuz, 5-7 mm beyaz serit, sonra tabla)
                        // parcanin ust yuzudur; sinir bos tablanin rengine donulen yer.
                        if (sd > 0)
                        {
                            bool okE = true;
                            for (int j = 0; j < m && okE; j++) okE = Sample(profE, w, h, qPx[b * m + j].X - x0, qPx[b * m + j].Y - y0, out valsE[j]);
                            if (okE) off = ExtendToTable(vals, valsE, band, off);
                        }
                        found.Add(bases[b] + n * off);
                    }
                    if (!FitLine(found, dir, len, out lines[e].P, out lines[e].D))
                    {
                        why = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "kenar {0}: yeterli gecis yok ({1}/{2} profil, serit {3:F1} mm, siluet {4})", e, found.Count, bases.Count, band,
                            string.Join(" ", sil.Select(p => p.X.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + "," + p.Y.ToString("F0", System.Globalization.CultureInfo.InvariantCulture))));
                        return null;
                    }
                }

                // 5) koseler
                var corners = new Point2[4];
                for (int e = 0; e < 4; e++)
                {
                    var l1 = lines[(e + 3) % 4]; var l2 = lines[e];
                    double cr = Point2.Cross(l1.D, l2.D);
                    if (Math.Abs(cr) < 0.3) { why = "kenarlar kesismiyor"; return null; }
                    double s = Point2.Cross(l2.P - l1.P, l2.D) / cr;
                    corners[e] = l1.P + l1.D * s;
                }
                if (!PolygonMath.IsConvex(corners)) { why = "dortgen dis bukey degil"; return null; }
                // Dedektorun sonucundan cok uzaklasan sonuc baska bir parcaya ya da hataya isaret eder
                foreach (Point2 c in corners)
                    if (approxRaw.Min(p => Point2.Distance(p, c)) > MaxShiftMm) { why = "kose dedektorden " + MaxShiftMm + " mm'den fazla uzak"; return null; }
                return corners.Select(p => ToTable(lens, p)).ToArray();
            }
        }

        /// <summary>
        /// Bir profilde ust kenarin yeri (siluete gore mm, disari +); bulunamazsa NaN. gs[j]: j ile j+1 ornekleri arasi
        /// renk degisimi; ornek j'nin konumu -(band + InsideMarginMm) + j * StepMm.
        /// Kural: icten disa, ust yuzey renginden belirgin (esik: en az 8, ust yuzey gurultusunun 5 kati) ve en az 1,5 mm
        /// suren ilk ayrilma ust kenardir. Gecisin gucune bakilmaz: isik alan yan yuz (ust yuzeyden aydinlik), golgede
        /// kalan yan yuz (koyu) ve gorunmeyen yan yuz (tabla) ayni kuralla. 2026-11-26 kopuk 172546: isik alan yan yuz
        /// L 200-205, ust yuz L 176-180, tahta araligi L 75-125; ust yuz -> yan yuz degisimi aralik gecisinin %13-23'u
        /// oldugundan "guclu gecis" kurallari disi (aralik / yan yuz sinirini) seciyordu, kisa kenar 608-614 (dogrusu 600,
        /// operator koseleri elle tasiyarak 2672,5 / 2675 buldu; bu kurala gore 2672-2674).
        /// </summary>
        static double TopOffset(double[] gs, Vec3f[] vals, double band)
        {
            double start = -(band + InsideMarginMm);
            // Ust yuzey rengi: profilin ic kismi (siluetin band + 2 .. band + 8 mm icerisi; siluet ust kenarda da olsa
            // alt kenarda da olsa burasi ust yuzeydir)
            int n0 = (int)((InsideMarginMm - 2) / StepMm);
            if (n0 < 4 || vals.Length <= n0 + 8) return double.NaN;
            var top = new Vec3f(Median(vals.Take(n0).Select(v => v.Item0)), Median(vals.Take(n0).Select(v => v.Item1)), Median(vals.Take(n0).Select(v => v.Item2)));
            double[] dev = vals.Select(v => D3(v, top)).ToArray();
            double noise = Median(dev.Take(n0).Select(x => (float)x));
            double thr = Math.Max(8, 5 * noise);
            int hold = (int)Math.Round(1.5 / StepMm);
            for (int j = n0; j < dev.Length; j++)
            {
                if (dev[j] <= thr) continue;
                bool sustained = true;
                for (int k = j; k < Math.Min(dev.Length, j + hold) && sustained; k++) sustained = dev[k] > thr;
                if (!sustained) continue;
                // Kenar gecisin ortasi: ayrilmanin basladigi yerden sonraki ~2,5 mm'deki en buyuk sapmanin yarisi
                // (esigin asildigi ilk yer bulaniklik yuzunden kenarin 1-1,5 mm icinde kaliyordu; kopukte her olcu 2-4 mm kucuk)
                int to = Math.Min(dev.Length - 1, j + hold + (int)Math.Round(1.0 / StepMm));
                double plateau = 0;
                for (int k = j; k <= to; k++) plateau = Math.Max(plateau, dev[k]);
                double level = Math.Max(thr, 0.5 * plateau);
                int c = j;
                while (c < to && dev[c] < level) c++;
                double f = dev[c] - dev[c - 1] > 1e-9 ? (level - dev[c - 1]) / (dev[c] - dev[c - 1]) : 0;
                return start + (c - 1 + Math.Max(0, Math.Min(1, f))) * StepMm;
            }
            return double.NaN;
        }

        /// <summary>
        /// Uzak kenar: off'tan (ust yuzey renginden ayrilma) disari, ornek ust yuzey rengine mi bos tablanin o noktadaki
        /// rengine mi yakin (D, parlaklik az agirlikli: tabladaki golge tabla sayilir); en az 1,5 mm suren ilk "tabla"
        /// gecisi sinirdir. En fazla MaxExtendMm uzatilir; tabla bulunamazsa off degismez.
        /// </summary>
        static double ExtendToTable(Vec3f[] vals, Vec3f[] valsE, double band, double off)
        {
            const double MaxExtendMm = 10;
            double start = -(band + InsideMarginMm);
            int n0 = (int)((InsideMarginMm - 2) / StepMm);
            var top = new Vec3f(Median(vals.Take(n0).Select(v => v.Item0)), Median(vals.Take(n0).Select(v => v.Item1)), Median(vals.Take(n0).Select(v => v.Item2)));
            // s > 0: tabla, s <= 0: parca
            double S(int j) => D(vals[j], top) - D(vals[j], valsE[j]);
            int hold = (int)Math.Round(1.5 / StepMm);
            int j0 = Math.Max(1, (int)Math.Floor((off - start) / StepMm));
            int j1 = Math.Min(vals.Length - hold, (int)Math.Ceiling((off + MaxExtendMm - start) / StepMm));
            for (int j = j0; j <= j1; j++)
            {
                bool table = true;
                for (int k = j; k < j + hold && table; k++) table = S(k) > 0;
                if (!table) continue;
                if (j == j0) return off;   // ayrilma zaten tablaya geciste
                double a = S(j - 1), c = S(j);
                double f = c - a > 1e-9 ? -a / (c - a) : 1;
                return Math.Max(off, start + (j - 1 + Math.Max(0, Math.Min(1, f))) * StepMm);
            }
            return off;
        }

        // ---- yardimcilar ----

        static Point2 ToModel(LensCalibration lens, Point2 table)
        {
            Point2 p = lens.MetricCorrection == null ? table : lens.MetricCorrection.Invert(table);
            return lens.Correction == null ? p : lens.Correction.Invert(p);
        }

        static Point2 ToTable(LensCalibration lens, Point2 model)
        {
            Point2 p = lens.Correction == null ? model : lens.Correction.Apply(model);
            return lens.MetricCorrection == null ? p : lens.MetricCorrection.Apply(p);
        }

        static Point2[] Grow(Point2[] quad, double mm)
        {
            Point2 c = Centroid(quad);
            return quad.Select(p => p + (p - c) * (mm / Math.Max(1, Point2.Distance(p, c)))).ToArray();
        }

        static Point2 Centroid(Point2[] p) => new Point2(p.Average(q => q.X), p.Average(q => q.Y));

        static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

        static Mat Lab(Mat bgr, double sigma)
        {
            var lab = new Mat();
            Cv2.CvtColor(bgr, lab, ColorConversionCodes.BGR2Lab);
            lab.ConvertTo(lab, MatType.CV_32FC3);
            Cv2.GaussianBlur(lab, lab, new Size(0, 0), sigma);
            return lab;
        }

        static float Median(IEnumerable<float> v)
        {
            float[] a = v.OrderBy(x => x).ToArray();
            return a[a.Length / 2];
        }

        /// <summary>Renk uzakligi; parlaklik golgeyle degistigi icin az agirlikli.</summary>
        static double D(Vec3f a, Vec3f b)
        {
            double l = 0.3 * (a.Item0 - b.Item0), x = a.Item1 - b.Item1, y = a.Item2 - b.Item2;
            return Math.Sqrt(l * l + x * x + y * y);
        }

        static double D3(Vec3f a, Vec3f b)
        {
            double l = a.Item0 - b.Item0, x = a.Item1 - b.Item1, y = a.Item2 - b.Item2;
            return Math.Sqrt(l * l + x * x + y * y);
        }

        static double[] Smooth(double[] g)
        {
            var s = new double[g.Length];
            for (int i = 0; i < g.Length; i++)
            {
                double sum = g[i]; int c = 1;
                if (i > 0) { sum += g[i - 1]; c++; }
                if (i < g.Length - 1) { sum += g[i + 1]; c++; }
                s[i] = sum / c;
            }
            return s;
        }

        static bool Sample(Vec3f[] img, int w, int h, double x, double y, out Vec3f v)
        {
            v = default(Vec3f);
            if (!(x >= 0 && y >= 0 && x < w - 1 && y < h - 1)) return false;
            int ix = (int)x, iy = (int)y; float fx = (float)(x - ix), fy = (float)(y - iy);
            Vec3f p00 = img[iy * w + ix], p10 = img[iy * w + ix + 1], p01 = img[(iy + 1) * w + ix], p11 = img[(iy + 1) * w + ix + 1];
            float W(float a, float b, float c, float d) => (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy;
            v = new Vec3f(W(p00.Item0, p10.Item0, p01.Item0, p11.Item0), W(p00.Item1, p10.Item1, p01.Item1, p11.Item1), W(p00.Item2, p10.Item2, p01.Item2, p11.Item2));
            return true;
        }

        /// <summary>Tohum noktasini iceren bilesen (yoksa en buyugu); null: bos.</summary>
        static Mat Component(Mat mask, int x, int y)
        {
            using (var labels = new Mat()) using (var stats = new Mat()) using (var cents = new Mat())
            {
                int n = Cv2.ConnectedComponentsWithStats(mask, labels, stats, cents);
                if (n < 2) return null;
                int id = labels.At<int>(y, x);
                if (id == 0) id = Enumerable.Range(1, n - 1).OrderByDescending(i => stats.At<int>(i, 4)).First();
                var comp = new Mat();
                Cv2.Compare(labels, new Scalar(id), comp, CmpType.EQ);
                // Delikleri doldur: disaridan erisilemeyen bos pikseller parcaya katilir
                using (var filled = comp.Clone())
                using (var fm = new Mat(comp.Rows + 2, comp.Cols + 2, MatType.CV_8UC1, Scalar.All(0)))
                {
                    var corners = new[] { new Point(0, 0), new Point(comp.Cols - 1, 0), new Point(0, comp.Rows - 1), new Point(comp.Cols - 1, comp.Rows - 1) };
                    foreach (Point c in corners)
                        if (filled.At<byte>(c.Y, c.X) == 0) Cv2.FloodFill(filled, fm, c, new Scalar(128));
                    using (var holes = new Mat()) { Cv2.Compare(filled, new Scalar(0), holes, CmpType.EQ); Cv2.BitwiseOr(comp, holes, comp); }
                }
                return comp;
            }
        }

        /// <summary>
        /// Tabla duzlemindeki konturdan 4 koseli kaba sekil. Sadelestirmenin koseleri maskenin yuvarlanmis koselerinde
        /// kalir ve aralarindaki kiris gercek kenarin birkac mm icinden gecer (2026-11-26: kopukte 9 mm, ust kenar
        /// arama penceresinin disinda kaliyordu). Bu yuzden her kenara, kirisin orta %70'indeki kontur noktalarindan
        /// dogru oturtulur ve koseler dogrularin kesisimidir.
        /// </summary>
        static Point2[] Quad(Point2[] contourMm)
        {
            var pts = contourMm.Select(p => new Point2f((float)p.X, (float)p.Y)).ToArray();
            if (pts.Length < 10) return null;
            Point2f[] q = Cv2.ApproxPolyDP(pts, 15, true);
            if (q.Length != 4) q = Cv2.MinAreaRect(pts).Points();
            Point2[] r = q.Select(p => new Point2(p.X, p.Y)).ToArray();
            if (PolygonMath.SignedArea(r) < 0) r = r.Reverse().ToArray();
            var lines = new (Point2 P, Point2 D)[4];
            for (int e = 0; e < 4; e++)
            {
                Point2 a = r[e], b = r[(e + 1) % 4];
                double len = Point2.Distance(a, b);
                Point2 d = (b - a) * (1 / len), n = new Point2(-d.Y, d.X);
                Point2[] near = contourMm.Where(p =>
                {
                    double s = Point2.Dot(p - a, d), o = Point2.Dot(p - a, n);
                    return s > 0.15 * len && s < 0.85 * len && Math.Abs(o) < 25;
                }).ToArray();
                if (near.Length < 10) { lines[e] = (a, d); continue; }
                PlanarBundle.FitLine(near, out Point2 nn, out _);
                Point2 dd = new Point2(nn.Y, -nn.X);
                if (Point2.Dot(dd, d) < 0) dd = dd * -1;
                lines[e] = (new Point2(near.Average(p => p.X), near.Average(p => p.Y)), dd);
            }
            var c = new Point2[4];
            for (int e = 0; e < 4; e++)
            {
                var l1 = lines[(e + 3) % 4]; var l2 = lines[e];
                double cr = Point2.Cross(l1.D, l2.D);
                if (Math.Abs(cr) < 0.3) return r;
                c[e] = l1.P + l1.D * (Point2.Cross(l2.P - l1.P, l2.D) / cr);
            }
            return c;
        }

        /// <summary>Medyan / MAD ayiklamali dogru; noktalar kenarin en az yarisina yayilmali.</summary>
        static bool FitLine(List<Point2> found, Point2 dir0, double len, out Point2 p, out Point2 d)
        {
            p = default(Point2); d = dir0;
            if (found.Count < 10) return false;
            var pts = found.ToList();
            Point2 n = new Point2(-d.Y, d.X);
            for (int it = 0; it < 10; it++)
            {
                Point2 c = new Point2(Med(pts.Select(q => q.X)), Med(pts.Select(q => q.Y)));
                double[] res = pts.Select(q => Point2.Dot(q - c, n)).ToArray();
                double med = Med(res), mad = Med(res.Select(r => Math.Abs(r - med))) * 1.4826;
                var keep = pts.Where((q, i) => Math.Abs(res[i] - med) < Math.Max(3 * mad, 0.4)).ToList();
                if (keep.Count == pts.Count || keep.Count < 10) break;
                pts = keep;
                PlanarBundle.FitLine(pts.ToArray(), out Point2 nn, out _);
                if (Math.Abs(Point2.Dot(nn, n)) > 0.9) n = nn;
                d = new Point2(n.Y, -n.X);
            }
            double[] along = pts.Select(q => Point2.Dot(q, dir0)).ToArray();
            if (along.Max() - along.Min() < 0.4 * len) return false;
            PlanarBundle.FitLine(pts.ToArray(), out Point2 normal, out double offset);
            d = new Point2(normal.Y, -normal.X);
            if (Point2.Dot(d, dir0) < 0) d = d * -1;
            p = new Point2(pts.Average(q => q.X), pts.Average(q => q.Y));
            return true;
        }

        static double Med(IEnumerable<double> v)
        {
            double[] a = v.OrderBy(x => x).ToArray();
            return a[a.Length / 2];
        }
    }
}
