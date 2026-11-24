using System;
using System.Collections.Generic;
using System.Linq;
using KopruKesim.Core;
using OpenCvSharp;

namespace KopruKesim.Vision
{
    /// <summary>
    /// Tablanin kenarina dayanan parcanin kenarlarini dogrudan orijinal fotograftan bulur.
    /// Ustten goruntude tablanin kenar seridi taranmaz (cita uclari, sase, zemin gurultusu); seride giren kenar
    /// maskede kesilir ve koseler serit sinirina oturur. Burada o kenar boyunca kenara dik cizgiler cizilir, her
    /// cizgide parcanin icinden disari dogru parcanin renginin bittigi yer aranir (tablanin disina da tasabilir:
    /// fotografta tabla siniri yoktur) ve noktalara duz cizgi oturtulur (2026-11-23: tablanin kosesine dayali
    /// siyah plaka ve kenara dayali beyaz levhalar olculemiyordu).
    /// </summary>
    public static class BorderEdgeFinder
    {
        /// <summary>Kenar basina profil cizgisi.</summary>
        public const int Samples = 24;
        /// <summary>Renk farki esigi (Lab, parlaklik yari agirlikli; 8 bit olcek).</summary>
        public const double ColorThreshold = 22;
        /// <summary>Kenar tablanin sinirindan bu kadar (mm) disarida bulunursa parca tasiyor sayilir.</summary>
        public const double OverhangMm = 20;

        public sealed class Result
        {
            /// <summary>Yeniden bulunan dortgen (tabla mm, saat yonunun tersine). Bulunamadiysa null.</summary>
            public Point2[] Quad;
            /// <summary>Parca tablanin disina tasiyor (kenari sinirin OverhangMm'den fazla disinda).</summary>
            public bool Overhangs;
            /// <summary>Teshis.</summary>
            public string Info;
            /// <summary>
            /// Fotografta gorulemeyen (parca ile tablanin disi ayni renkte) ama parcanin tablanin kenarina kadar uzandigi
            /// bos tabla karsilastirmasindan belli olan kenarlar: tablanin kenarina hizali kabul edildi. Bos degilse uyari.
            /// </summary>
            public string Assumed;
            /// <summary>Fotografta bulunamayip kaba konumunda kalan (seride girmeyen) kenar sayisi.</summary>
            public int RoughEdges;
        }

        /// <summary>
        /// Kaba dortgenin (siluet, tabla mm) tablanin kenar seridine giren kenarlarini fotografta yeniden bulur.
        /// </summary>
        /// <param name="quad">Kaba dortgen (4 kose).</param>
        /// <param name="bandMm">Taranmayan kenar seridinin genisligi (mm).</param>
        /// <param name="sampler">Tabla mm noktalarinin fotograftaki rengi (BGR); tablanin disindaki noktalar da.</param>
        /// <param name="allEdges">Seride girmeyen kenarlar da fotografta aranir (kaba dortgen iyiyse daha dogru).</param>
        /// <param name="differs">Tabla mm noktasi bos tabladan farkli mi (fark maskesi); null ise kenar hizalama yapilmaz.</param>
        /// <param name="insetMm">Gercek tabla kenari verilen tablanin kenarindan bu kadar iceride (olcum alani tablanin disina
        /// tasiyorsa). Arama verilen tablanin kenarina kadar; tasma ve kenara hizalama gercek kenara gore.</param>
        public static Result Refine(Point2[] quad, double tableLengthMm, double tableWidthMm, double bandMm, Func<Point2[], Vec3b[]> sampler,
            bool allEdges = true, Func<Point2, bool> differs = null, double insetMm = 0)
        {
            var res = new Result();
            if (quad == null || quad.Length != 4 || sampler == null) { res.Info = "dortgen yok"; return res; }
            Point2[] q = PolygonMath.SignedArea(quad) < 0 ? quad.Reverse().ToArray() : quad.ToArray();
            double L = tableLengthMm, W = tableWidthMm;
            var lineP = new Point2[4];
            var lineD = new Point2[4];
            var info = new List<string>();
            bool any = false;
            for (int e = 0; e < 4; e++)
            {
                Point2 a = q[e], b = q[(e + 1) % 4];
                double len = Point2.Distance(a, b);
                Point2 dir = (b - a) * (1.0 / Math.Max(1e-9, len));
                var outward = new Point2(dir.Y, -dir.X);   // saat yonunun tersine cokgende disari
                Point2 mid = (a + b) * 0.5;
                // Kenar, disari dogru tablanin sinirina seridin biraz otesinden daha yakinsa seride girer
                double toBorder = new[] { a, mid, b }.Min(x => RayToBorder(x, outward, L, W));
                lineP[e] = a; lineD[e] = dir;
                // Dort kenar da fotografta aranir (kaba dortgen gurultulu lekeden gelir); seride girmeyen kenarda arama
                // dar, seride girende tablanin disina kadar
                bool inBand = toBorder <= bandMm + 30;
                if (inBand) any = true;
                if (!inBand && !allEdges) continue;
                // Parcanin karsi kenarina olan uzaklik: icerideki renk orneklemesi parcanin disina tasmasin
                Point2 c = q[(e + 2) % 4], d = q[(e + 3) % 4];
                double across = Math.Min(Math.Abs(Point2.Dot(c - a, outward)), Math.Abs(Point2.Dot(d - a, outward)));
                double inward = Math.Max(15, Math.Min(100, 0.45 * across));
                double outRange = inBand ? Math.Max(30, Math.Min(200, RayToBorder(mid, outward, L, W) + 60)) : 60;
                Point2 p = a, dd = dir;
                string why = null;
                // Once fark maskesinden (golge bastirilmis, kenar seridi silinmemis): kenar tablanin icindeyse renk
                // profilinden daha dogru (renk profili golgeyi ve yan yuzu parcaya katiyordu: 1460 mm plaka 1487 olcu).
                // Maske tablanin kenarina kadar uzaniyorsa kenar tablanin disindadir: renk profiline gecilir.
                bool ok = inBand && differs != null && FitEdgeFromMask(a, dir, outward, len, inward, L, W, differs, out p, out dd, out why);
                if (!ok) ok = FitEdge(a, dir, outward, len, inward, outRange, sampler, out p, out dd, out why);
                bool overhang = false;
                if (ok)
                {
                    // Bulunan kenar tablanin sinirinin belirgin disindaysa parca tasiyor (ya da kenar gorulemedi)
                    double outA = OutsideBy(ProjectOnLine(a, p, dd), L, W, insetMm), outB = OutsideBy(ProjectOnLine(b, p, dd), L, W, insetMm);
                    overhang = Math.Max(outA, outB) > OverhangMm;
                }
                if (!ok || overhang)
                {
                    if (Debug) Console.WriteLine("    [kenar " + e + "] " + (ok ? "tasiyor gibi (" + why + ")" : "bulunamadi: " + why) + (inBand ? ", seritte" : ""));
                    // Parca ile tablanin disi ayni renkteyse (siyah plaka, siyah sase) kenar fotografta gorulemez. Parca bos
                    // tablaya gore tablanin kenarina kadar uzaniyorsa ve kenari tablanin kenarina paralelse oraya hizali say.
                    Point2 sp, sd;
                    if (inBand && differs != null && ReachesSide(a, b, dir, outward, len, L, W, insetMm, differs, out sp, out sd))
                    {
                        lineP[e] = sp; lineD[e] = sd;
                        res.Assumed = (res.Assumed == null ? "" : res.Assumed + ", ") + SideName(outward);
                        info.Add("kenar " + e + ": gorulemedi, tablanin kenarina hizali kabul edildi");
                        continue;
                    }
                    // Seride girmeyen kenar bulunamadiysa kaba konumunda kalir (cagiran fotografta oturtur)
                    if (!inBand && !ok) { info.Add("kenar " + e + ": " + why + ", kaba kaldi"); res.RoughEdges++; continue; }
                    if (Debug) DumpProfile(a, dir, outward, len, inward, outRange, sampler);
                    if (!ok) { res.Info = "kenar " + e + (inBand ? " (serit)" : "") + ": " + why; return res; }
                    res.Overhangs = true; res.Info = string.Join("; ", info) + " kenar " + e + " -> tasiyor";
                    return res;
                }
                lineP[e] = p; lineD[e] = dd;
                info.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "kenar {0} {1}: {2:F0} mm kaydi", e, why,
                    Point2.Dot(ProjectOnLine(mid, p, dd) - mid, outward)));
            }
            if (!any) { res.Info = "seride giren kenar yok"; return res; }
            var corners = new Point2[4];
            for (int i = 0; i < 4; i++)
            {
                Point2 p1 = lineP[(i + 3) % 4], d1 = lineD[(i + 3) % 4], p2 = lineP[i], d2 = lineD[i];
                double den = d1.X * d2.Y - d1.Y * d2.X;
                if (Math.Abs(den) < 1e-6) { res.Info = "kenarlar paralel"; return res; }
                double t = ((p2.X - p1.X) * d2.Y - (p2.Y - p1.Y) * d2.X) / den;
                corners[i] = p1 + d1 * t;
                if (Point2.Distance(corners[i], q[i]) > 250) { res.Info = "kose cok kaydi"; return res; }
            }
            if (PolygonMath.SignedArea(corners) <= 0) { res.Info = "dortgen ters"; return res; }
            res.Quad = corners;
            res.Info = string.Join("; ", info);
            return res;
        }

        private static bool Debug => Environment.GetEnvironmentVariable("KK_DEBUG") == "1";

        /// <summary>Kenar tablanin kenarina bu acidan (derece) daha paralelse hizalanabilir.</summary>
        private const double AlignMaxAngleDeg = 3;

        /// <summary>
        /// Kenar tablanin bir kenarina paralel mi ve parca (bos tablaya gore farkli bolge) kaba kenardan o kenara kadar
        /// kesintisiz uzaniyor mu. Evetse tablanin o kenari (nokta + yon) doner.
        /// </summary>
        private static bool ReachesSide(Point2 a, Point2 b, Point2 dir, Point2 outward, double len, double L, double W, double inset,
            Func<Point2, bool> differs, out Point2 sideP, out Point2 sideD)
        {
            sideP = a; sideD = dir;
            Point2 n; double offset;   // gercek tablanin kenarinin disari normali ve konumu
            if (Math.Abs(outward.X) > Math.Abs(outward.Y)) { n = new Point2(Math.Sign(outward.X), 0); offset = outward.X > 0 ? L - inset : inset; }
            else { n = new Point2(0, Math.Sign(outward.Y)); offset = outward.Y > 0 ? W - inset : inset; }
            if (Point2.Dot(outward, n) < Math.Cos(AlignMaxAngleDeg * Math.PI / 180)) return false;
            int good = 0, total = 0;
            for (int i = 0; i < Samples; i++)
            {
                Point2 basePt = a + dir * (len * (0.1 + 0.8 * i / (Samples - 1)));
                double toSide = n.X != 0 ? (offset - basePt.X) * n.X : (offset - basePt.Y) * n.Y;   // kenara kalan (mm)
                total++;
                if (toSide < -OverhangMm) continue;
                bool all = true;
                for (double t = 0; t <= toSide - 3; t += 4)
                    if (!differs(basePt + n * t)) { all = false; break; }
                if (all && differs(basePt + n * Math.Max(0, toSide - 3))) good++;
            }
            if (good < 0.8 * total) return false;
            sideP = n.X != 0 ? new Point2(offset, a.Y) : new Point2(a.X, offset);
            sideD = n.X != 0 ? new Point2(0, Math.Sign(dir.Y) == 0 ? 1 : Math.Sign(dir.Y)) : new Point2(Math.Sign(dir.X) == 0 ? 1 : Math.Sign(dir.X), 0);
            return true;
        }

        private static string SideName(Point2 outward) =>
            Math.Abs(outward.X) > Math.Abs(outward.Y) ? (outward.X > 0 ? "sag" : "sol") : (outward.Y > 0 ? "ust" : "alt");

        /// <summary>Teshis: kenarin ortasindaki profilin Lab degerleri (her 5 mm).</summary>
        private static void DumpProfile(Point2 a, Point2 dir, Point2 outward, double len, double inward, double outRange, Func<Point2[], Vec3b[]> sampler)
        {
            foreach (double f in new[] { 0.3, 0.7 })
            {
                var pts = new List<Point2>();
                for (double t = -inward; t <= outRange; t += 5) pts.Add(a + dir * (len * f) + outward * t);
                double[][] lab = ToLab(sampler(pts.ToArray()));
                Console.WriteLine("    profil %" + (int)(f * 100) + " (t=-" + (int)inward + " mm'den 5 mm adim, L/a/b): " +
                    string.Join(" ", lab.Select(c => (int)c[0] + "/" + (int)c[1] + "/" + (int)c[2])));
            }
        }

        private static Point2 ProjectOnLine(Point2 x, Point2 p, Point2 d) => p + d * Point2.Dot(x - p, d);

        /// <summary>Nokta gercek tablanin (verilen tablanin inset kadar ici) disinda ne kadar (mm; icerideyse 0).</summary>
        private static double OutsideBy(Point2 p, double L, double W, double inset) =>
            Math.Max(Math.Max(inset - p.X, p.X - (L - inset)), Math.Max(Math.Max(inset - p.Y, p.Y - (W - inset)), 0));

        /// <summary>p'den o yonunde tablanin sinirina uzaklik (mm). Disaridaysa 0.</summary>
        private static double RayToBorder(Point2 p, Point2 o, double L, double W)
        {
            if (p.X < 0 || p.Y < 0 || p.X > L || p.Y > W) return 0;
            double t = double.MaxValue;
            if (o.X > 1e-9) t = Math.Min(t, (L - p.X) / o.X);
            if (o.X < -1e-9) t = Math.Min(t, -p.X / o.X);
            if (o.Y > 1e-9) t = Math.Min(t, (W - p.Y) / o.Y);
            if (o.Y < -1e-9) t = Math.Min(t, -p.Y / o.Y);
            return t;
        }

        /// <summary>
        /// Kenar boyunca profiller: icerideki (parca) rengin ortancasi alinir, her profilde disari dogru o renkten
        /// uc ardisik adim boyunca belirgin farkli olunan ilk yer kenardir; noktalara aykirilari atarak cizgi oturtulur.
        /// </summary>
        private static bool FitEdge(Point2 a, Point2 dir, Point2 outward, double len, double inward, double outRange,
            Func<Point2[], Vec3b[]> sampler, out Point2 lineP, out Point2 lineD, out string why)
        {
            lineP = a; lineD = dir; why = null;
            int n = Samples, k = (int)Math.Ceiling(inward + outRange) + 1;
            var pts = new Point2[n * k];
            var along = new double[n];
            for (int i = 0; i < n; i++)
            {
                along[i] = len * (0.1 + 0.8 * i / (n - 1));
                Point2 basePt = a + dir * along[i];
                for (int j = 0; j < k; j++) pts[i * k + j] = basePt + outward * (j - inward);
            }
            Vec3b[] bgr = sampler(pts);
            double[][] lab = ToLab(bgr);
            // Parcanin rengi: tum profillerin iceride kalan ilk kismi (kenara en fazla inward/3 yaklasan)
            int refEnd = Math.Max(3, (int)(inward * 2 / 3));
            var refL = new List<double>(); var refA = new List<double>(); var refB = new List<double>();
            for (int i = 0; i < n; i++)
                for (int j = 0; j < refEnd; j++) { double[] c = lab[i * k + j]; refL.Add(c[0]); refA.Add(c[1]); refB.Add(c[2]); }
            double rl = Median(refL), ra = Median(refA), rb = Median(refB);
            var found = new List<Point2>();
            for (int i = 0; i < n; i++)
            {
                int run = 0;
                for (int j = refEnd; j < k; j++)
                {
                    double[] c = lab[i * k + j];
                    double dl = 0.5 * (c[0] - rl), da = c[1] - ra, db = c[2] - rb;
                    if (Math.Sqrt(dl * dl + da * da + db * db) > ColorThreshold) run++;
                    else run = 0;
                    if (run == 3)
                    {
                        double t = (j - 2) - inward - 0.5;
                        found.Add(a + dir * along[i] + outward * t);
                        break;
                    }
                }
            }
            if (Debug) Console.WriteLine("    renk profili: ic renk L{0:F0} a{1:F0} b{2:F0}, kenar (mm, disari): {3}", rl, ra, rb,
                string.Join(" ", found.Select(f => Point2.Dot(f - a, outward).ToString("F0"))));
            if (found.Count < n * 0.6) { why = string.Format("profillerin {0}/{1} kenari buldu", found.Count, n); return false; }
            return FitRobust(found, n, dir, out lineP, out lineD, out why);
        }

        /// <summary>
        /// Kenari fark maskesinden bulur: her profilde parcanin icinden disari, maskenin bittigi (uc ardisik adim bos)
        /// ilk yer. Maske tablanin kenarina kadar suruyorsa o profilde kenar yok (tablanin disinda).
        /// </summary>
        private static bool FitEdgeFromMask(Point2 a, Point2 dir, Point2 outward, double len, double inward, double L, double W,
            Func<Point2, bool> differs, out Point2 lineP, out Point2 lineD, out string why)
        {
            lineP = a; lineD = dir; why = null;
            int n = Samples;
            var found = new List<Point2>();
            for (int i = 0; i < n; i++)
            {
                Point2 basePt = a + dir * (len * (0.1 + 0.8 * i / (n - 1)));
                double start = -inward / 3, end = RayToBorder(basePt + outward * start, outward, L, W) + start - 2;
                if (!differs(basePt + outward * start)) continue;   // iceride bile maske yok: bu profil guvenilmez
                int run = 0;
                for (double t = start; t <= end; t += 1)
                {
                    if (!differs(basePt + outward * t)) run++; else run = 0;
                    if (run == 3) { found.Add(basePt + outward * (t - 2.5)); break; }
                }
            }
            if (found.Count < n * 0.6) { why = string.Format("maskede {0}/{1} profil kenari buldu", found.Count, n); return false; }
            bool ok = FitRobust(found, n, dir, out lineP, out lineD, out why);
            if (ok) why = "maske " + why;
            return ok;
        }

        /// <summary>Noktalara aykirilari atarak (8 mm, sonra 3 mm) cizgi oturtur; noktalarin yarisi cizgide kalmali.</summary>
        /// <param name="maxAngleDeg">Kume aramasinda cizginin kaba kenara gore en buyuk egimi: genis acida egik bir cizgi iki kumenin
        /// parcalarini birlikte yakalayip dogru (duz) kumeyi gecebiliyor; kaba kenar zaten dogru yondeyse kucuk verilmeli.</param>
        internal static bool FitRobust(List<Point2> found, int n, Point2 dir, out Point2 lineP, out Point2 lineD, out string why, double maxAngleDeg = 10)
        {
            why = null;
            List<Point2> cur = found;
            lineP = found[0]; lineD = dir;
            // Once en cok noktanin 3 mm icinde kaldigi cizgi (nokta ciftlerinden): parcadaki parlama ya da leke bazi
            // profillerde kenari parcanin icinde gosteriyor; hepsine birden oturtulan cizgi iki kumenin arasina dusup
            // gercek kenar da reddediliyordu (2026-11-24: sol alttaki siyah plaka, 24 profilin 7-10'u parlamada).
            bool consensus = false;
            if (found.Count >= 4)
            {
                int bestCount = 0; List<Point2> bestSet = null;
                double cosMax = Math.Cos(maxAngleDeg * Math.PI / 180);
                for (int i = 0; i < found.Count; i++)
                    for (int j = i + 1; j < found.Count; j++)
                    {
                        Point2 d = found[j] - found[i]; double l = d.Length;
                        if (l < 1e-6) continue;
                        d = d * (1 / l);
                        if (Math.Abs(Point2.Dot(d, dir)) < cosMax) continue;
                        Point2 nr = new Point2(-d.Y, d.X), pi = found[i];
                        List<Point2> inl = found.Where(p => Math.Abs(Point2.Dot(p - pi, nr)) <= 3).ToList();
                        if (inl.Count > bestCount) { bestCount = inl.Count; bestSet = inl; }
                    }
                if (bestSet != null && bestSet.Count >= 2) { cur = bestSet; consensus = true; }
            }
            List<Point2> pool = cur;
            for (int pass = 0; pass < 3; pass++)
            {
                FitLine(cur, out lineP, out lineD);
                Point2 nrm = new Point2(-lineD.Y, lineD.X);
                Point2 lp = lineP;
                // Kume secildiyse genis (8 mm) tur atlanir: parlamadaki noktalarin bir kismi geri alinip cizgiyi egiyordu
                // (2026-11-24 sag alt plaka: ust kenar bir ucta 13 mm kaydi, kisa kenar 572 yerine 559)
                // Kume bulunduysa inceltme yalniz kumenin icinde: tum noktalardan yeniden secmek egik cizginin uzantisina
                // dusen parlama noktalarini tur tur geri aliyordu
                List<Point2> keep = (consensus ? pool : found).Where(p => Math.Abs(Point2.Dot(p - lp, nrm)) <= (pass == 0 && !consensus ? 8 : 3)).ToList();
                if (keep.Count < n * 0.5) { why = string.Format("kenar duz degil ({0}/{1} nokta cizgide)", keep.Count, n); return false; }
                cur = keep;
            }
            FitLine(cur, out lineP, out lineD);
            if (Point2.Dot(lineD, dir) < 0) lineD = lineD * -1;
            if (Point2.Dot(lineD, dir) < Math.Cos(10 * Math.PI / 180)) { why = "kenar yonu kaba kenardan 10 dereceden fazla farkli"; return false; }
            why = string.Format("{0}/{1} nokta", cur.Count, n);
            return true;
        }

        private static void FitLine(List<Point2> pts, out Point2 p, out Point2 d)
        {
            double mx = pts.Average(q => q.X), my = pts.Average(q => q.Y);
            double sxx = 0, sxy = 0, syy = 0;
            foreach (Point2 q in pts) { double dx = q.X - mx, dy = q.Y - my; sxx += dx * dx; sxy += dx * dy; syy += dy * dy; }
            double ang = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
            p = new Point2(mx, my);
            d = new Point2(Math.Cos(ang), Math.Sin(ang));
        }

        internal static double[][] ToLab(Vec3b[] bgr)
        {
            var res = new double[bgr.Length][];
            using (var m = new Mat(1, bgr.Length, MatType.CV_8UC3))
            using (var lab = new Mat())
            {
                m.SetArray(bgr);
                Cv2.CvtColor(m, lab, ColorConversionCodes.BGR2Lab);
                var outArr = new Vec3b[bgr.Length];
                lab.GetArray(out outArr);
                for (int i = 0; i < bgr.Length; i++) res[i] = new double[] { outArr[i].Item0, outArr[i].Item1, outArr[i].Item2 };
            }
            return res;
        }

        internal static double Median(List<double> v)
        {
            if (v.Count == 0) return 0;
            var s = v.OrderBy(x => x).ToList();
            return s[s.Count / 2];
        }

        /// <summary>Orijinal fotograftan tabla mm noktalarinin rengi (iki dogrusal ara deger; fotograf disi siyah).</summary>
        public static Vec3b[] SamplePhoto(Mat frame, LensCalibration lens, Point2[] tableMm) => SamplePhoto(frame, lens, tableMm, 0);

        /// <summary>Tabla yuzeyinden zMm yukaridaki duzlemde (ornegin plakanin ust yuzeyi) noktalarin fotograftaki rengi.</summary>
        public static Vec3b[] SamplePhoto(Mat frame, LensCalibration lens, Point2[] tableMm, double zMm)
        {
            Point2[] img = LensCalibrator.TableToImage(lens, tableMm, zMm);
            var res = new Vec3b[tableMm.Length];
            var idx = frame.GetGenericIndexer<Vec3b>();
            int w = frame.Cols, h = frame.Rows;
            for (int i = 0; i < img.Length; i++)
            {
                double x = img[i].X, y = img[i].Y;
                if (double.IsNaN(x) || x < 0 || y < 0 || x > w - 2 || y > h - 2) continue;
                int x0 = (int)x, y0 = (int)y;
                double fx = x - x0, fy = y - y0;
                Vec3b c00 = idx[y0, x0], c10 = idx[y0, x0 + 1], c01 = idx[y0 + 1, x0], c11 = idx[y0 + 1, x0 + 1];
                Func<byte, byte, byte, byte, byte> mix = (v00, v10, v01, v11) =>
                    (byte)Math.Round((v00 * (1 - fx) + v10 * fx) * (1 - fy) + (v01 * (1 - fx) + v11 * fx) * fy);
                res[i] = new Vec3b(mix(c00.Item0, c10.Item0, c01.Item0, c11.Item0), mix(c00.Item1, c10.Item1, c01.Item1, c11.Item1),
                    mix(c00.Item2, c10.Item2, c01.Item2, c11.Item2));
            }
            return res;
        }
    }
}
