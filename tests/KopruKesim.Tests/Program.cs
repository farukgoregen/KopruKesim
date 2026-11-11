using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using KopruKesim.Core;

// NuGet gerektirmeyen basit test kosucusu: "dotnet run --project tests/KopruKesim.Tests"
static class Program
{
    static int _failed;

    static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "  OK    " : "  HATA  ") + name);
        if (!ok) _failed++;
    }

    static bool Near(Point2 a, Point2 b, double tol) { return Point2.Distance(a, b) <= tol; }

    /// <summary>Beklenen koselerin her biri bulunan koselerden birine tol kadar yakin mi.</summary>
    static bool SameVertices(Point2[] found, Point2[] expected, double tol)
    {
        if (found.Length != expected.Length) return false;
        foreach (var e in expected)
        {
            bool hit = false;
            foreach (var f in found) if (Near(e, f, tol)) hit = true;
            if (!hit) return false;
        }
        return true;
    }

    static List<Point2> Sample(Point2[] poly, double step, double noise, Random rnd)
    {
        var r = new List<Point2>();
        for (int i = 0; i < poly.Length; i++)
        {
            Point2 a = poly[i], b = poly[(i + 1) % poly.Length];
            int n = Math.Max(1, (int)(Point2.Distance(a, b) / step));
            for (int j = 0; j < n; j++)
            {
                Point2 p = a + (b - a) * ((double)j / n);
                r.Add(new Point2(p.X + (rnd.NextDouble() - 0.5) * 2 * noise, p.Y + (rnd.NextDouble() - 0.5) * 2 * noise));
            }
        }
        return r;
    }

    static Point2[] Rotate(Point2[] p, double deg, Point2 shift)
    {
        double c = Math.Cos(deg * Math.PI / 180), s = Math.Sin(deg * Math.PI / 180);
        var r = new Point2[p.Length];
        for (int i = 0; i < p.Length; i++) r[i] = new Point2(p[i].X * c - p[i].Y * s + shift.X, p[i].X * s + p[i].Y * c + shift.Y);
        return r;
    }

    static List<Point2> ConvexHull(List<Point2> pts)
    {
        pts.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        var h = new List<Point2>();
        for (int pass = 0; pass < 2; pass++)
        {
            int start = h.Count;
            foreach (var p in pts)
            {
                while (h.Count >= start + 2 && Point2.Cross(h[h.Count - 1] - h[h.Count - 2], p - h[h.Count - 1]) <= 0) h.RemoveAt(h.Count - 1);
                h.Add(p);
            }
            h.RemoveAt(h.Count - 1);
            pts.Reverse();
        }
        return h;
    }

    static void TestHomography()
    {
        Console.WriteLine("Homografi");
        var src = new[] { new Point2(665, 129), new Point2(1557, 122), new Point2(1818, 1510), new Point2(342, 1530) };
        var dst = new[] { new Point2(0, 0), new Point2(0, 2000), new Point2(3500, 2000), new Point2(3500, 0) };
        var h = Homography.FromPoints(src, dst);
        bool ok = true;
        for (int i = 0; i < 4; i++) ok &= Near(h.Transform(src[i]), dst[i], 1e-6);
        Check(ok, "4 nokta tam eslesiyor");
        var q = new Point2(1000, 800);
        Check(Near(h.Inverse().Transform(h.Transform(q)), q, 1e-6), "ters donusum");

        // Fazla noktayla en kucuk kareler: bilinen bir donusumden uretilen 9 nokta
        var s9 = new List<Point2>(); var d9 = new List<Point2>();
        for (int x = 0; x <= 3500; x += 1750) for (int y = 0; y <= 2000; y += 1000)
        { var t = new Point2(x, y); d9.Add(t); s9.Add(h.Inverse().Transform(t)); }
        var h9 = Homography.FromPoints(s9, d9);
        Check(Near(h9.Transform(q), h.Transform(q), 1e-4), "9 noktali cozum ayni sonucu veriyor");
    }

    static Homography SyntheticCamera(double f, double cx, double cy, double[] camPos, double[] lookAt)
    {
        // Kamera eksenleri (tabla koordinatlarinda): z = bakis yonu, x = saga, y = asagi
        double[] z = Normalize(new[] { lookAt[0] - camPos[0], lookAt[1] - camPos[1], lookAt[2] - camPos[2] });
        double[] x = Normalize(CrossV(z, new[] { 0.0, 0.0, 1.0 }));
        double[] y = CrossV(z, x);
        double[][] rows = { x, y, z };
        var t = new double[3];
        for (int i = 0; i < 3; i++) t[i] = -(rows[i][0] * camPos[0] + rows[i][1] * camPos[1] + rows[i][2] * camPos[2]);
        // tabla(z=0) -> goruntu: K [r1 r2 t]
        var m = new double[9];
        for (int c = 0; c < 3; c++)
        {
            double a = c < 2 ? rows[0][c] : t[0], b = c < 2 ? rows[1][c] : t[1], w = c < 2 ? rows[2][c] : t[2];
            m[c] = f * a + cx * w; m[3 + c] = f * b + cy * w; m[6 + c] = w;
        }
        return new Homography(m).Inverse(); // goruntu -> tabla
    }

    static double[] Normalize(double[] v) { double n = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]); return new[] { v[0] / n, v[1] / n, v[2] / n }; }
    static double[] CrossV(double[] a, double[] b) { return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] }; }

    static void TestCameraPose()
    {
        Console.WriteLine("Kamera konumu tahmini");
        double[] cam = { 4600, 1300, 3100 };
        var h = SyntheticCamera(1900, 1280, 853, cam, new[] { 1750.0, 1000, 0 });
        var pose = CameraPose.Estimate(h, 2560, 1706);
        Check(pose != null && Math.Abs(pose.X - cam[0]) < 1 && Math.Abs(pose.Y - cam[1]) < 1 && Math.Abs(pose.Height - cam[2]) < 1,
            "egik kamera konumu 1 mm icinde: " + (pose == null ? "null" : pose.X.ToString("F1") + ", " + pose.Y.ToString("F1") + ", " + pose.Height.ToString("F1")));
        Check(pose != null && Math.Abs(pose.FocalPx - 1900) < 1 && Math.Abs(pose.Consistency - 1) < 1e-6, "odak uzakligi ve tutarlilik");
    }

    static void TestShapes()
    {
        Console.WriteLine("Sekil ve kose bulma (sentetik, +-0.5 mm gurultu)");
        var rnd = new Random(7);
        var opt = new LocatorOptions();

        var rect = Rotate(new[] { new Point2(0, 0), new Point2(1200, 0), new Point2(1200, 600), new Point2(0, 600) }, 12, new Point2(900, 400));
        var r = SlabLocator.Locate(Sample(rect, 2, 0.5, rnd), opt, 0, null);
        Check(r != null && SameVertices(r.Vertices, rect, 1.0) && r.Shape == SlabShape.Dikdortgen, "donuk dikdortgen, koseler 1 mm icinde");
        Check(r != null && Math.Abs(r.RotationDeg - 12) < 0.1 && Math.Abs(r.AreaMm2 - 720000) < 1500, "donme acisi ve alan");

        var sq = Rotate(new[] { new Point2(0, 0), new Point2(500, 0), new Point2(500, 500), new Point2(0, 500) }, -30, new Point2(600, 900));
        r = SlabLocator.Locate(Sample(sq, 2, 0.5, rnd), opt, 0, null);
        Check(r != null && SameVertices(r.Vertices, sq, 1.0) && r.Shape == SlabShape.Kare, "kare");

        var tri = new[] { new Point2(300, 200), new Point2(1500, 350), new Point2(700, 1100) };
        r = SlabLocator.Locate(Sample(tri, 2, 0.5, rnd), opt, 0, null);
        Check(r != null && SameVertices(r.Vertices, tri, 1.5) && r.Shape == SlabShape.Ucgen, "ucgen");

        var cut = new[] { new Point2(200, 200), new Point2(1400, 200), new Point2(1400, 600), new Point2(1100, 900), new Point2(200, 900) };
        r = SlabLocator.Locate(Sample(cut, 2, 0.5, rnd), opt, 0, null);
        Check(r != null && SameVertices(r.Vertices, cut, 1.5) && r.Shape == SlabShape.Cokgen && r.IsConvex, "kosesi capraz kesilmis dikdortgen (5 kose)");

        var lshape = new[] { new Point2(200, 200), new Point2(1400, 200), new Point2(1400, 600), new Point2(800, 600), new Point2(800, 1000), new Point2(200, 1000) };
        r = SlabLocator.Locate(Sample(lshape, 2, 0.5, rnd), opt, 0, null);
        Check(r != null && SameVertices(r.Vertices, lshape, 1.5) && !r.IsConvex, "L seklinde (ic bukey) parca");
        Check(r != null && PolygonMath.Contains(r.Vertices, new Point2(500, 800)) && !PolygonMath.Contains(r.Vertices, new Point2(1100, 800)), "nokta icinde mi kontrolu");

        // Saat yonunde verilen kontur da ayni sonucu vermeli
        var cw = Sample(rect, 2, 0.5, rnd); cw.Reverse();
        r = SlabLocator.Locate(cw, opt, 0, null);
        Check(r != null && SameVertices(r.Vertices, rect, 1.0) && r.AreaMm2 > 0, "ters yonlu kontur");

        // Yuvarlak / kirik kose ucu: 20 mm pah, kose yine iki kenarin kesisimi olarak bulunmali
        var chamfer = new[] { new Point2(200, 200), new Point2(1380, 200), new Point2(1400, 220), new Point2(1400, 900), new Point2(200, 900) };
        var full = new[] { new Point2(200, 200), new Point2(1400, 200), new Point2(1400, 900), new Point2(200, 900) };
        r = SlabLocator.Locate(Sample(chamfer, 2, 0.5, rnd), opt, 0, null);
        Check(r != null && SameVertices(r.Vertices, full, 1.5), "kucuk kose kirigi yok sayiliyor");
    }

    static void TestThickness()
    {
        Console.WriteLine("Kalinlik duzeltmesi");
        var rnd = new Random(3);
        var cam = new CameraPose(4600, 1300, 3100);
        double h = 30, k = cam.Height / (cam.Height - h);
        var bottom = Rotate(new[] { new Point2(0, 0), new Point2(1500, 0), new Point2(1500, 800), new Point2(0, 800) }, 8, new Point2(700, 500));
        // Kameranin gordugu siluet: alt yuzey + tabla duzlemine izdusen ust yuzey
        var all = new List<Point2>(bottom);
        var foot = new Point2(cam.X, cam.Y);
        foreach (var p in bottom) all.Add(foot + (p - foot) * k);
        var silhouette = Sample(ConvexHull(all).ToArray(), 2, 0.3, rnd);

        var raw = SlabLocator.Locate(silhouette, new LocatorOptions(), 0, null);
        double worst = 0;
        foreach (var e in bottom) { double best = 1e9; foreach (var f in raw.Vertices) best = Math.Min(best, Point2.Distance(e, f)); worst = Math.Max(worst, best); }
        var fixedR = SlabLocator.Locate(silhouette, new LocatorOptions(), h, cam);
        Check(worst > 10, "duzeltmesiz hata buyuk: " + worst.ToString("F1") + " mm");
        Check(fixedR != null && SameVertices(fixedR.Vertices, bottom, 1.0), "duzeltme sonrasi koseler 1 mm icinde");
    }

    static void TestRealContour()
    {
        Console.WriteLine("Gercek fotograftan cikan kontur (acik renkli plaka)");
        var pts = new List<Point2>();
        foreach (string line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "data", "beyaz_plaka_kontur.txt")))
        {
            string[] p = line.Split(' ');
            pts.Add(new Point2(double.Parse(p[0], CultureInfo.InvariantCulture), double.Parse(p[1], CultureInfo.InvariantCulture)));
        }
        var r = SlabLocator.Locate(pts, new LocatorOptions(), 0, null);
        Check(r != null && r.Vertices.Length == 4, "4 kose bulundu");
        if (r == null) return;
        Console.WriteLine("    sekil: " + r.Shape + ", alan: " + (r.AreaMm2 / 100).ToString("F0") + " cm2, donme: " + r.RotationDeg.ToString("F2") + " derece");
        for (int i = 0; i < r.Vertices.Length; i++)
            Console.WriteLine("    K" + (i + 1) + " " + r.Vertices[i] + "  aci " + r.AnglesDeg[i].ToString("F1") + "  kenar " + r.EdgeLengths[i].ToString("F0") + " mm");
    }

    static int Main()
    {
        TestHomography();
        TestCameraPose();
        TestShapes();
        TestThickness();
        TestRealContour();
        Console.WriteLine(_failed == 0 ? "\nTum testler gecti." : "\n" + _failed + " test basarisiz.");
        return _failed == 0 ? 0 : 1;
    }
}
