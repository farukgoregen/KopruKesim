using System;
using System.Collections.Generic;

namespace KopruKesim.Core
{
    /// <summary>
    /// Mermerin dis konturundan (tabla duzlemine izdusurulmus, mm) kose koordinatlarini cikarir.
    /// Adimlar: 1) kontur sadelestirilip kose adaylari bulunur, 2) her kenara en kucuk kareler
    /// ile dogru oturtulur, 3) kalinlik kaymasi duzeltilir, 4) komsu dogrular kesistirilip
    /// koseler elde edilir. Dogru oturtma sayesinde koseler piksel altı hassasiyette cikar ve
    /// yuvarlak / hafif kirik kose uclari sonucu bozmaz.
    /// </summary>
    public static class SlabLocator
    {
        private struct Line { public Point2 P; public Point2 D; public Point2 FallbackVertex; }

        /// <param name="contourMm">Kapali kontur noktalari, sirali (tabla mm).</param>
        /// <param name="thicknessMm">Mermer kalinligi. 0 ise veya camera null ise duzeltme yapilmaz.</param>
        /// <param name="camera">Kamera konumu (CameraPose.Estimate ya da elle olcum).</param>
        /// <returns>Gecerli bir cokgen cikmazsa null.</returns>
        public static SlabResult Locate(IList<Point2> contourMm, LocatorOptions opt, double thicknessMm, CameraPose camera)
        {
            if (contourMm == null || contourMm.Count < 3) return null;
            if (opt == null) opt = new LocatorOptions();

            // Konturu saat yonunun tersine cevir; boylece kenarin sag tarafi her zaman dis taraf olur.
            var pts = new List<Point2>(contourMm);
            if (PolygonMath.SignedArea(pts) < 0) pts.Reverse();

            List<int> corners = SimplifyClosed(pts, opt.SimplifyToleranceMm);
            MergeCollinear(pts, corners, opt.MinCornerAngleDeg);
            if (corners.Count < 3) return null;

            // Kisa kenarlari atla (en az 3 kenar kalacak sekilde, en kisadan baslayarak).
            int n = corners.Count;
            var keep = new bool[n];
            int kept = n;
            for (int i = 0; i < n; i++) keep[i] = true;
            while (kept > 3)
            {
                int shortest = -1; double best = opt.MinEdgeMm;
                for (int i = 0; i < n; i++)
                {
                    if (!keep[i]) continue;
                    double len = Point2.Distance(pts[corners[i]], pts[corners[(i + 1) % n]]);
                    if (len < best) { best = len; shortest = i; }
                }
                if (shortest < 0) break;
                keep[shortest] = false; kept--;
            }

            // Kenarlara dogru oturt. Oturtulan iki komsu dogru neredeyse ayni dogrultudaysa
            // (sadelestirmenin kenar puruzunden urettigi sahte kose) tek kenar olarak yeniden oturt.
            var edgeFrom = new List<int>();
            var edgeTo = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if (!keep[i]) continue;
                edgeFrom.Add(corners[i]); edgeTo.Add(corners[(i + 1) % n]);
            }
            var lines = new List<Line>();
            for (int i = 0; i < edgeFrom.Count; i++) lines.Add(FitLine(pts, edgeFrom[i], edgeTo[i]));
            double minSin = Math.Sin(opt.MinCornerAngleDeg * Math.PI / 180);
            for (int i = 0; i < lines.Count && lines.Count > 3; i++)
            {
                int j = (i + 1) % lines.Count;
                if (Math.Abs(Point2.Cross(lines[i].D, lines[j].D)) >= minSin || Point2.Dot(lines[i].D, lines[j].D) <= 0) continue;
                edgeTo[i] = edgeTo[j];
                lines[i] = FitLine(pts, edgeFrom[i], edgeTo[i]);
                edgeFrom.RemoveAt(j); edgeTo.RemoveAt(j); lines.RemoveAt(j);
                i = -1; // bastan kontrol et
            }

            bool correct = camera != null && thicknessMm > 0 && camera.Height > thicknessMm;
            Point2 foot = correct ? new Point2(camera.X, camera.Y) : new Point2();
            double k = correct ? (camera.Height - thicknessMm) / camera.Height : 1;
            var isFar = new bool[lines.Count];
            if (correct)
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    Line l = lines[i];
                    // Dis normal kameradan uzaga bakiyorsa gorunen kenar ust yuzeyin kenaridir ve
                    // kameradan uzaga dogru kaymistir; kamera izdusumune dogru k oraninda geri cekilir.
                    // Kameraya bakan kenarlarda yan yuzey gorundugu icin siluet alt kenardir, duzeltme gerekmez.
                    var outward = new Point2(l.D.Y, -l.D.X);
                    isFar[i] = Point2.Dot(l.P - foot, outward) > 0;
                    if (isFar[i]) { l.P = foot + (l.P - foot) * k; lines[i] = l; }
                }
            }

            var verts = new List<Point2>();
            for (int i = 0; i < lines.Count; i++)
            {
                Line a = lines[i], b = lines[(i + 1) % lines.Count];
                double cross = Point2.Cross(a.D, b.D);
                Point2 v;
                if (Math.Abs(cross) < 0.05) // ~3 dereceden kucuk aci: kesisim guvenilmez
                {
                    v = a.FallbackVertex;
                    if (correct && isFar[i]) v = foot + (v - foot) * k;
                }
                else
                {
                    double t = Point2.Cross(b.P - a.P, b.D) / cross;
                    v = a.P + a.D * t;
                }
                verts.Add(v);
            }
            if (verts.Count < 3 || Math.Abs(PolygonMath.SignedArea(verts)) < 1e-6) return null;
            return Describe(verts, opt);
        }

        /// <summary>Kose listesinden olcu ve sekil bilgilerini cikarir.</summary>
        public static SlabResult Describe(IList<Point2> vertices, LocatorOptions opt)
        {
            if (opt == null) opt = new LocatorOptions();
            var v = new List<Point2>(vertices);
            if (PolygonMath.SignedArea(v) < 0) v.Reverse();

            // Orijine en yakin koseden baslat: cikti her olcumde ayni sirada gelsin.
            int start = 0;
            for (int i = 1; i < v.Count; i++) if (v[i].Length < v[start].Length) start = i;
            var ordered = new Point2[v.Count];
            for (int i = 0; i < v.Count; i++) ordered[i] = v[(start + i) % v.Count];

            var r = new SlabResult();
            r.Vertices = ordered;
            r.AreaMm2 = PolygonMath.SignedArea(ordered);
            r.Centroid = PolygonMath.Centroid(ordered);
            r.IsConvex = PolygonMath.IsConvex(ordered);
            r.AnglesDeg = PolygonMath.InteriorAngles(ordered);
            r.EdgeLengths = new double[ordered.Length];
            r.MinX = r.MaxX = ordered[0].X; r.MinY = r.MaxY = ordered[0].Y;
            int longest = 0;
            for (int i = 0; i < ordered.Length; i++)
            {
                r.EdgeLengths[i] = Point2.Distance(ordered[i], ordered[(i + 1) % ordered.Length]);
                if (r.EdgeLengths[i] > r.EdgeLengths[longest]) longest = i;
                r.MinX = Math.Min(r.MinX, ordered[i].X); r.MaxX = Math.Max(r.MaxX, ordered[i].X);
                r.MinY = Math.Min(r.MinY, ordered[i].Y); r.MaxY = Math.Max(r.MaxY, ordered[i].Y);
            }
            Point2 d = ordered[(longest + 1) % ordered.Length] - ordered[longest];
            double rot = Math.Atan2(d.Y, d.X) * 180 / Math.PI;
            if (rot > 90) rot -= 180; else if (rot <= -90) rot += 180;
            r.RotationDeg = rot;
            r.Shape = Classify(r, opt);
            return r;
        }

        private static SlabShape Classify(SlabResult r, LocatorOptions opt)
        {
            if (r.Vertices.Length == 3) return SlabShape.Ucgen;
            if (r.Vertices.Length != 4) return SlabShape.Cokgen;
            foreach (double a in r.AnglesDeg)
                if (Math.Abs(a - 90) > opt.RightAngleToleranceDeg) return SlabShape.Dortgen;
            double ratio = r.EdgeLengths[0] / r.EdgeLengths[1];
            return Math.Abs(ratio - 1) <= opt.SquareSideTolerance ? SlabShape.Kare : SlabShape.Dikdortgen;
        }

        /// <summary>Kapali kontur icin Douglas-Peucker. Kose adaylarinin indekslerini sirali dondurur.</summary>
        internal static List<int> SimplifyClosed(IList<Point2> pts, double tolerance)
        {
            int n = pts.Count;
            // Iki sabit baslangic noktasi: agirlik merkezine en uzak nokta ve ona en uzak nokta.
            double cx = 0, cy = 0;
            for (int i = 0; i < n; i++) { cx += pts[i].X; cy += pts[i].Y; }
            var c = new Point2(cx / n, cy / n);
            int a = 0;
            for (int i = 1; i < n; i++) if (Point2.Distance(pts[i], c) > Point2.Distance(pts[a], c)) a = i;
            int b = a;
            for (int i = 0; i < n; i++) if (Point2.Distance(pts[i], pts[a]) > Point2.Distance(pts[b], pts[a])) b = i;

            var keep = new bool[n];
            keep[a] = keep[b] = true;
            var stack = new Stack<int[]>();
            stack.Push(new[] { a, b }); stack.Push(new[] { b, a });
            while (stack.Count > 0)
            {
                int[] seg = stack.Pop();
                int s = seg[0], e = seg[1];
                Point2 p0 = pts[s], d = pts[e] - p0;
                double len = d.Length;
                double max = 0; int idx = -1;
                for (int i = (s + 1) % n; i != e; i = (i + 1) % n)
                {
                    double dist = len < 1e-9 ? Point2.Distance(pts[i], p0) : Math.Abs(Point2.Cross(pts[i] - p0, d)) / len;
                    if (dist > max) { max = dist; idx = i; }
                }
                if (idx >= 0 && max > tolerance)
                {
                    keep[idx] = true;
                    stack.Push(new[] { s, idx }); stack.Push(new[] { idx, e });
                }
            }
            var result = new List<int>();
            for (int i = 0; i < n; i++) if (keep[i]) result.Add(i);
            return result;
        }

        /// <summary>Ayni dogrultuda devam eden ardisik kenarlari birlestirir.</summary>
        private static void MergeCollinear(IList<Point2> pts, List<int> corners, double minAngleDeg)
        {
            bool changed = true;
            while (changed && corners.Count > 3)
            {
                changed = false;
                for (int i = 0; i < corners.Count && corners.Count > 3; i++)
                {
                    int m = corners.Count;
                    Point2 prev = pts[corners[(i + m - 1) % m]], cur = pts[corners[i]], next = pts[corners[(i + 1) % m]];
                    Point2 d1 = cur - prev, d2 = next - cur;
                    double turn = Math.Abs(Math.Atan2(Point2.Cross(d1, d2), Point2.Dot(d1, d2))) * 180 / Math.PI;
                    if (turn < minAngleDeg) { corners.RemoveAt(i); changed = true; i--; }
                }
            }
        }

        /// <summary>Konturun iki kose arasindaki noktalarina toplam en kucuk kareler ile dogru oturtur.</summary>
        private static Line FitLine(IList<Point2> pts, int from, int to)
        {
            int n = pts.Count;
            int count = ((to - from + n) % n) + 1;
            // Kose yakinlari (yuvarlak kose, golge) dogruyu bozmasin diye uclardan %15 kirpilir.
            int trim = count >= 10 ? (int)(count * 0.15) : 0;
            int used = count - 2 * trim;
            double mx = 0, my = 0;
            for (int j = trim; j < count - trim; j++) { Point2 p = pts[(from + j) % n]; mx += p.X; my += p.Y; }
            mx /= used; my /= used;
            double sxx = 0, sxy = 0, syy = 0;
            for (int j = trim; j < count - trim; j++)
            {
                Point2 p = pts[(from + j) % n];
                double dx = p.X - mx, dy = p.Y - my;
                sxx += dx * dx; sxy += dx * dy; syy += dy * dy;
            }
            Point2 chord = pts[to] - pts[from];
            Point2 dir;
            if (used < 2 || sxx + syy < 1e-12)
            {
                dir = chord;
            }
            else
            {
                double theta = 0.5 * Math.Atan2(2 * sxy, sxx - syy); // ana eksen yonu
                dir = new Point2(Math.Cos(theta), Math.Sin(theta));
                if (Point2.Dot(dir, chord) < 0) dir = dir * -1; // kontur yonunu koru
            }
            double len = dir.Length;
            Line line;
            line.P = new Point2(mx, my);
            line.D = len < 1e-12 ? new Point2(1, 0) : dir * (1 / len);
            line.FallbackVertex = pts[to];
            return line;
        }
    }
}
