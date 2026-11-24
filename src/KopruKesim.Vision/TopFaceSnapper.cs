using System;
using System.Collections.Generic;
using System.Linq;
using KopruKesim.Core;
using OpenCvSharp;

namespace KopruKesim.Vision
{
    /// <summary>
    /// Son adim: parcanin her kenari, fotograf plakanin ust yuzeyi yuksekliginde (z = kalinlik) orneklenerek ust yuzeyin
    /// renginin bittigi yere oturtulur. Kalin plakada kenarin disinda kameraya bakan yan yuz (gri serit) ve golge
    /// (koyu serit) vardir; onceki adimlar kenari bazen bu seritlerin dis sinirina oturtuyordu (5-10 mm). Ust yuzey
    /// kenari gercek X, Y'dedir (prizma), ekrandaki goruntu de ayni yukseklikte duzeltildigi icin cizgi kenarda durur.
    /// </summary>
    public static class TopFaceSnapper
    {
        // Kenar en fazla bu kadar kaydirilir; daha fazlasi yanlis gecis sayilir. 10 mm azdi: yan yana iki levhanin ust
        // kenari bir ucta 11 mm iceride kaliyordu ve hic duzeltilmiyordu.
        public const double MaxShiftMm = 15;
        const double InsideMm = 25, OutsideMm = 18, StepMm = 0.5, RefEndMm = 16, MinEdgeMm = 100;
        const int Samples = 30;
        const double ColorThreshold = 22;

        /// <summary>sampler: tabla mm noktalarinin ust yuzey duzlemindeki rengi.</summary>
        public static void Snap(List<SlabResult> slabs, Func<Point2[], Vec3b[]> sampler, LocatorOptions opt)
        {
            if (Environment.GetEnvironmentVariable("KK_UST_YUZEY") == "0") return;   // karsilastirma icin kapatilabilir
            for (int s = 0; s < slabs.Count; s++)
            {
                SlabResult r = slabs[s];
                if (!r.IsReliable || r.Vertices == null || r.Vertices.Length < 3) continue;
                SlabResult snapped = SnapOne(r, sampler, opt);
                if (snapped != null) slabs[s] = snapped;
            }
        }

        static SlabResult SnapOne(SlabResult r, Func<Point2[], Vec3b[]> sampler, LocatorOptions opt)
        {
            Point2[] v = r.Vertices.ToArray();
            if (PolygonMath.SignedArea(v) < 0) Array.Reverse(v);
            int n = v.Length;
            var lineP = new Point2[n]; var lineD = new Point2[n];
            int moved = 0;
            var log = new List<string>();
            for (int i = 0; i < n; i++)
            {
                Point2 a = v[i], b = v[(i + 1) % n];
                double len = Point2.Distance(a, b);
                Point2 dir = (b - a) * (1 / len);
                lineP[i] = a; lineD[i] = dir;
                if (len < MinEdgeMm) { log.Add("kisa"); continue; }
                Point2 outward = new Point2(dir.Y, -dir.X);   // saat yonunun tersi sirada dis normal
                if (!FitEdge(a, dir, outward, len, sampler, out Point2 p, out Point2 d, out string why)) { log.Add(why); continue; }
                // Kenarin iki ucunun yeni cizgiye uzakligi kucuk olmali
                Point2 nrm = new Point2(-d.Y, d.X);
                double da = Point2.Dot(a - p, nrm), db = Point2.Dot(b - p, nrm);
                if (Math.Abs(da) > MaxShiftMm || Math.Abs(db) > MaxShiftMm) { log.Add(string.Format("kayma buyuk ({0:F1}/{1:F1})", da, db)); continue; }
                lineP[i] = p; lineD[i] = d; moved++;
                log.Add(string.Format("{0:+0.0;-0.0}/{1:+0.0;-0.0}", -da, -db));
            }
            if (Debug) Console.WriteLine("  [ust yuzey] once " + string.Join("/", r.EdgeLengths.Select(x => x.ToString("F0"))) + ": " + string.Join(" | ", log));
            if (moved == 0) return null;
            var nv = new Point2[n];
            for (int i = 0; i < n; i++)
            {
                int prev = (i + n - 1) % n;
                if (!Intersect(lineP[prev], lineD[prev], lineP[i], lineD[i], out Point2 x) || Point2.Distance(x, v[i]) > 1.5 * MaxShiftMm) return null;
                nv[i] = x;
            }
            if (PolygonMath.SignedArea(nv) <= 0) return null;
            SlabResult res = SlabLocator.Describe(nv, opt);
            res.IsReliable = r.IsReliable; res.Note = r.Note; res.Method = r.Method;
            return res;
        }

        static bool FitEdge(Point2 a, Point2 dir, Point2 outward, double len, Func<Point2[], Vec3b[]> sampler,
            out Point2 lineP, out Point2 lineD, out string why)
        {
            lineP = a; lineD = dir; why = null;
            int k = (int)Math.Round((InsideMm + OutsideMm) / StepMm) + 1, refEnd = (int)Math.Round((InsideMm - RefEndMm) / StepMm);
            var pts = new Point2[Samples * k];
            var along = new double[Samples];
            for (int i = 0; i < Samples; i++)
            {
                along[i] = len * (0.1 + 0.8 * i / (Samples - 1));
                Point2 basePt = a + dir * along[i];
                for (int j = 0; j < k; j++) pts[i * k + j] = basePt + outward * (j * StepMm - InsideMm);
            }
            double[][] lab = BorderEdgeFinder.ToLab(sampler(pts));
            var refL = new List<double>(); var refA = new List<double>(); var refB = new List<double>();
            for (int i = 0; i < Samples; i++)
                for (int j = 0; j < refEnd; j++) { double[] c = lab[i * k + j]; refL.Add(c[0]); refA.Add(c[1]); refB.Add(c[2]); }
            double rl = BorderEdgeFinder.Median(refL), ra = BorderEdgeFinder.Median(refA), rb = BorderEdgeFinder.Median(refB);
            var found = new List<Point2>();
            for (int i = 0; i < Samples; i++)
            {
                int run = 0;
                for (int j = refEnd; j < k; j++)
                {
                    double[] c = lab[i * k + j];
                    double dl = 0.5 * (c[0] - rl), dA = c[1] - ra, dB = c[2] - rb;
                    if (Math.Sqrt(dl * dl + dA * dA + dB * dB) > ColorThreshold) run++; else run = 0;
                    if (run == 3)   // 1,5 mm boyunca farkli: ust yuzey bitti
                    {
                        double t = (j - 2) * StepMm - InsideMm - StepMm / 2;
                        found.Add(a + dir * along[i] + outward * t);
                        break;
                    }
                }
            }
            if (Debug) Console.WriteLine("    [ust yuzey profil] ref L{0:F0} a{1:F0} b{2:F0}: {3}", rl, ra, rb,
                string.Join(" ", found.Select(f => Point2.Dot(f - a, outward).ToString("+0.0;-0.0"))));
            if (found.Count < Samples * 0.6) { why = string.Format("{0}/{1} profil", found.Count, Samples); return false; }
            // Kenar onceki adimlardan dogru yonde gelir ve en fazla MaxShiftMm kayar: egim kucuk tutulur (2026-11-24 sag alt
            // plaka: parlama kumesinin parcasini da yakalayan 0,7 derece egik cizgi kisa kenari 572'den 559'a bozuyordu)
            bool ok = BorderEdgeFinder.FitRobust(found, Samples, dir, out lineP, out lineD, out why, 0.3);
            if (Debug && ok)
            {
                Point2 lp = lineP, ld = lineD;
                Func<Point2, double> off = x => { Point2 q = lp + ld * Point2.Dot(x - lp, ld); return Point2.Dot(q - x, outward); };
                Console.WriteLine("    [ust yuzey cizgi] {0}; uclarda {1:+0.0;-0.0} / {2:+0.0;-0.0} mm", why, off(a), off(a + dir * len));
            }
            return ok;
        }

        static bool Intersect(Point2 p1, Point2 d1, Point2 p2, Point2 d2, out Point2 x)
        {
            double den = d1.X * d2.Y - d1.Y * d2.X;
            x = p2;
            if (Math.Abs(den) < 1e-6) return false;
            double t = ((p2.X - p1.X) * d2.Y - (p2.Y - p1.Y) * d2.X) / den;
            x = p1 + d1 * t;
            return true;
        }

        static bool Debug => Environment.GetEnvironmentVariable("KK_DEBUG") == "1";
    }
}
