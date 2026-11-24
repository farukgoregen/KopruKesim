using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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

    /// <summary>Kamera kalibrasyonu dosyaya yazilip okunur; kamera konumu (C = -R^T t) dogru hesaplanir.</summary>
    static void TestLensCalibrationFile()
    {
        Console.WriteLine("Kamera kalibrasyonu dosyasi:");
        // Kamera tablanin (1500, 900) noktasinin 1300 mm ustunde, 10 derece Z ekseninde donuk
        double a = 10 * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        double[] R = { c, -s, 0, s, c, 0, 0, 0, 1 };
        double[] C = { 1500, 900, -1300 };
        double[] t = { -(R[0] * C[0] + R[1] * C[1] + R[2] * C[2]), -(R[3] * C[0] + R[4] * C[1] + R[5] * C[2]), -(R[6] * C[0] + R[7] * C[1] + R[8] * C[2]) };
        var cal = new LensCalibration
        {
            TableLengthMm = 3000, TableWidthMm = 1800, ImageWidth = 1920, ImageHeight = 1080,
            Fx = 820, Fy = 820, Cx = 960, Cy = 540, Distortion = new[] { -0.31, 0.105, 0, 0, -0.016 },
            Rvec = new[] { 0, 0, a }, Tvec = t, RmsPx = 0.21, MeanMm = 0.4, MaxMm = 1.0, Points = 324, TagType = "normal"
        };
        string path = Path.Combine(Path.GetTempPath(), "kk_lens_test.txt");
        cal.Save(path);
        LensCalibration back = LensCalibration.Load(path);
        File.Delete(path);
        Check(back.Fx == 820 && back.Distortion.Length == 5 && Math.Abs(back.Distortion[4] + 0.016) < 1e-12 && back.TagType == "normal", "kaydet/oku ayni degerler");
        Check(back.MeasuredTagCenters == null && back.MeetsReferenceTolerance, "eski kalibrasyon formati korunuyor");
        Check(double.IsNaN(back.EdgeRmsMm), "damali kalibrasyonda kenar sapmasi yok");
        cal.EdgeRmsMm = 2.25; cal.Save(path);
        Check(LensCalibration.Load(path).EdgeRmsMm == 2.25, "damasiz kalibrasyonun kenar sapmasi saklaniyor");
        cal.EdgeRmsMm = double.NaN;
        cal.MeasuredTagCenters = new[] { new Point2(148.5, 1695), new Point2(1502, 1693), new Point2(2850, 1695),
            new Point2(148.5, 105), new Point2(1530, 105), new Point2(2849, 106) };
        cal.Save(path);
        LensCalibration measured = LensCalibration.Load(path);
        File.Delete(path);
        Check(measured.MeasuredTagCenters.SequenceEqual(cal.MeasuredTagCenters), "olculmus dama merkezleri aynen saklaniyor");
        cal.MeasuredTagCenters = null;
        cal.BoardPlacements = new[] { new BoardPlacement(144.15, 1880.55, -0.483), new BoardPlacement(1647.27, 1878.62, 0.445), new BoardPlacement(3150.96, 1881.48, -0.028),
            new BoardPlacement(148.5, 105, 0), new BoardPlacement(1698.63, 101.56, 0.456), new BoardPlacement(3153.12, 96.03, 0.055) };
        cal.PaperCorners = new[] { new Point2(-3.5, 1986.8), new Point2(2999.5, 1798.4), new Point2(0, 0), new Point2(3001.7, 0) };
        cal.Save(path);
        LensCalibration placed = LensCalibration.Load(path);
        File.Delete(path);
        Check(placed.BoardPlacements.Select(q => (q.X, q.Y, q.RotationDeg)).SequenceEqual(cal.BoardPlacements.Select(q => (q.X, q.Y, q.RotationDeg)))
            && placed.PaperCorners.SequenceEqual(cal.PaperCorners), "dama yerlesimi ve kagit koseleri aynen saklaniyor");
        Check(Math.Abs(placed.PaperCornerDeviationMm() - Point2.Distance(new Point2(-3.5, 1986.8), new Point2(0, 1800))) < 1e-9, "kagit kosesi sapmasi hesaplaniyor");
        Check(placed.CornerSource == LensCalibration.CornerSourcePaper, "kose kaynagi varsayilan olarak kagit");
        // Mavi paspasli kalibrasyon: kose damalari gorunmez (NaN), kose kaynagi "mavi"
        cal.BoardPlacements = Enumerable.Range(0, 6).Select(i => i == 1 || i == 4 ? new BoardPlacement(1650 + i, 100 * i, 0.1 * i) : new BoardPlacement(double.NaN, double.NaN, double.NaN)).ToArray();
        cal.CornerSource = LensCalibration.CornerSourceMats;
        cal.Save(path);
        LensCalibration matCal = LensCalibration.Load(path);
        File.Delete(path);
        Check(matCal.CornerSource == LensCalibration.CornerSourceMats
            && Enumerable.Range(0, 6).All(i => (i == 1 || i == 4) ? matCal.BoardPlacements[i].X == 1650 + i : double.IsNaN(matCal.BoardPlacements[i].X)),
            "mavi paspasli kalibrasyon: kose kaynagi ve gorunmeyen damalar saklaniyor");
        cal.CornerSource = LensCalibration.CornerSourcePaper;
        measured.MaxMm = LensCalibration.MaxReferenceErrorMm;
        Check(measured.MeetsReferenceTolerance, "referans hata siniri dahil");
        measured.MaxMm = 31.21;
        Check(!measured.MeetsReferenceTolerance, "yuksek referans hatasi guvenilir sayilmiyor");
        measured.MaxMm = double.NaN;
        Check(!measured.MeetsReferenceTolerance, "gecersiz referans hatasi guvenilir sayilmiyor");
        CameraPose p = back.CameraPosition();
        Check(Math.Abs(p.X - 1500) < 1e-6 && Math.Abs(p.Y - 900) < 1e-6 && Math.Abs(p.Height - 1300) < 1e-6, $"kamera konumu ({p.X:F1}, {p.Y:F1}, {p.Height:F1})");
    }


    static void TestPlanarBundle()
    {
        Console.WriteLine("Serbest yerlesimli lens cozumu (kagitlar yanlis yerde):");
        // 2026-11-20 15:16 kalibrasyon karesinden cikan gercekci kamera; damalar varsayilan yerden kaymis
        double f = 1020.26, k1 = -0.28329, k2 = 0.09924, k3 = -0.02018;
        double[] rv = { -2.96314, 0.02887, 0.02124 }, tv = { -1710.79, 1105.48, 2020.44 };
        double[] R = Rotations.FromRvec(rv);
        double[,] truth =
        {
            { 144.15, 1880.55, -0.483 }, { 1647.27, 1878.62, 0.445 }, { 3150.96, 1881.48, -0.028 },
            { 148.5, 105.0, 0.0 }, { 1698.63, 101.56, 0.456 }, { 3153.12, 96.03, 0.055 }
        };
        double[,] nominal = { { 148.5, 1895 }, { 1650, 1895 }, { 3151.5, 1895 }, { 148.5, 105 }, { 1650, 105 }, { 3151.5, 105 } };
        var local = new List<Point2>();
        for (int j = 0; j < 6; j++) for (int i = 0; i < 9; i++) local.Add(new Point2((i - 4) * 25.0, (j - 2.5) * 25.0));
        var rnd = new Random(7);
        var bundle = new PlanarBundle { F = 1265.57, Cx = 960, Cy = 540, K1 = -0.4772, K2 = 0.3258, K3 = -0.1212,
            Rvec = new[] { -2.9253, 0.0343, 0.0206 }, Tvec = new[] { -1695.1, 1113.2, 2485.4 } };
        for (int b = 0; b < 6; b++)
        {
            var place = new BoardPlacement(truth[b, 0], truth[b, 1], truth[b, 2]);
            var px = local.Select(l =>
            {
                Point2 q = place.Apply(l);
                Point2 p = PlanarBundle.Project(f, 960, 540, k1, k2, k3, R, tv, q.X, q.Y);
                return new Point2(p.X + (rnd.NextDouble() - 0.5) * 0.3, p.Y + (rnd.NextDouble() - 0.5) * 0.3);
            }).ToArray();
            bundle.Boards.Add(new PlanarBundle.Board { Local = local.ToArray(), Pixels = px, X = nominal[b, 0], Y = nominal[b, 1], Theta = 0, Fixed = b == 3 });
        }
        bool ok = bundle.Solve();
        Check(ok && bundle.RmsPx < 0.2, $"yeniden izdusum hatasi gurultu duzeyinde: {bundle.RmsPx:F3} px");
        Check(Math.Abs(bundle.F - f) / f < 0.01, $"odak uzakligi geri bulundu: {bundle.F:F1} (gercek {f:F1})");
        double worst = Enumerable.Range(0, 6).Max(b => Point2.Distance(new Point2(bundle.Boards[b].X, bundle.Boards[b].Y), new Point2(truth[b, 0], truth[b, 1])));
        Check(worst < 0.5, $"kaymis damalarin yeri geri bulundu (en buyuk fark {worst:F2} mm; orta alt dama 48,6 mm kaymisti)");
        double worstAngle = Enumerable.Range(0, 6).Max(b => Math.Abs(bundle.Boards[b].Theta * 180 / Math.PI - truth[b, 2]));
        Check(worstAngle < 0.05, $"dama acilari geri bulundu (en buyuk fark {worstAngle:F3} derece)");

        // Cerceve degisimi: ayni fiziksel nokta ayni piksele dusmeli
        double[] r0 = Rotations.FromRvec(bundle.Rvec), t0 = (double[])bundle.Tvec.Clone();
        Point2 before = PlanarBundle.Project(bundle.F, 960, 540, bundle.K1, bundle.K2, bundle.K3, r0, t0, 1234, 567);
        double phi = 0.0123; var shift = new Point2(-12.3, 45.6);
        bundle.Reframe(phi, shift);
        double c = Math.Cos(phi), s = Math.Sin(phi);
        Point2 moved = new Point2(c * 1234 - s * 567 + shift.X, s * 1234 + c * 567 + shift.Y);
        Point2 after = PlanarBundle.Project(bundle.F, 960, 540, bundle.K1, bundle.K2, bundle.K3, Rotations.FromRvec(bundle.Rvec), bundle.Tvec, moved.X, moved.Y);
        Check(Point2.Distance(before, after) < 1e-6, $"cerceve degisimi ayni pikseli veriyor ({Point2.Distance(before, after):E1} px)");
        double[] back = Rotations.ToRvec(Rotations.FromRvec(rv));
        Check(Math.Abs(back[0] - rv[0]) + Math.Abs(back[1] - rv[1]) + Math.Abs(back[2] - rv[2]) < 1e-9, "Rodrigues gidis-donus ayni (166 derece)");

        // Geri izdusum izdusumun tersi (genis acili lens, tablanin kosesine yakin nokta)
        Point2 img = PlanarBundle.Project(f, 960, 540, k1, k2, k3, R, tv, 3250, 1950);
        Point2 onTable = PlanarBundle.BackProject(f, 960, 540, k1, k2, k3, R, tv, img);
        Check(Point2.Distance(onTable, new Point2(3250, 1950)) < 1e-6, $"geri izdusum izdusumun tersi ({Point2.Distance(onTable, new Point2(3250, 1950)):E1} mm)");
        // Dogru oturtma ve kesisim
        double[] dev = PlanarBundle.FitLine(new[] { new Point2(0, 1), new Point2(10, 1.2), new Point2(20, 1.4), new Point2(30, 1.6) }, out Point2 nA, out double dA);
        PlanarBundle.FitLine(new[] { new Point2(5, -10), new Point2(5, 0), new Point2(5, 10) }, out Point2 nB, out double dB);
        Point2 cross = PlanarBundle.Intersect(nA, dA, nB, dB);
        Check(dev.All(v => Math.Abs(v) < 1e-9) && Point2.Distance(cross, new Point2(5, 1.1)) < 1e-9, "dogru oturtma ve kesisim");

        // Lens sabit (mavi paspasli kalibrasyon): yalniz iki orta dama gorunur, lens bilinir; kamera konumu ve damalarin yeri cozulur
        var fixedLens = new PlanarBundle { F = f, Cx = 960, Cy = 540, K1 = k1, K2 = k2, K3 = k3, FixIntrinsics = true,
            Rvec = new[] { -2.95, 0.035, 0.03 }, Tvec = new[] { -1690.0, 1090.0, 2050.0 } };
        foreach (int b in new[] { 1, 4 })
        {
            var place = new BoardPlacement(truth[b, 0], truth[b, 1], truth[b, 2]);
            var px = local.Select(l => { Point2 q = place.Apply(l); return PlanarBundle.Project(f, 960, 540, k1, k2, k3, R, tv, q.X, q.Y); }).ToArray();
            fixedLens.Boards.Add(new PlanarBundle.Board { Local = local.ToArray(), Pixels = px, X = b == 4 ? truth[b, 0] : nominal[b, 0], Y = b == 4 ? truth[b, 1] : nominal[b, 1], Theta = b == 4 ? truth[b, 2] * Math.PI / 180 : 0, Fixed = b == 4 });
        }
        bool okFixed = fixedLens.Solve();
        Check(okFixed && fixedLens.F == f && fixedLens.K1 == k1 && fixedLens.K2 == k2 && fixedLens.K3 == k3, "sabit lens cozumde degismiyor");
        double camErr = Point2.Distance(PlanarBundle.Project(f, 960, 540, k1, k2, k3, Rotations.FromRvec(fixedLens.Rvec), fixedLens.Tvec, 3200, 1950),
            PlanarBundle.Project(f, 960, 540, k1, k2, k3, R, tv, 3200, 1950));
        Check(okFixed && fixedLens.RmsPx < 0.01 && camErr < 0.05, $"sabit lensle kamera konumu iki damadan bulundu (kosede {camErr:F3} px)");

        PlanarBundle.RigidFit(new[] { new Point2(0, 0), new Point2(10, 0), new Point2(0, 5) },
            new[] { new Point2(3, 4), new Point2(3, 14), new Point2(-2, 4) }, out double fitPhi, out Point2 fitShift);
        Check(Math.Abs(fitPhi - Math.PI / 2) < 1e-9 && Point2.Distance(fitShift, new Point2(3, 4)) < 1e-9, "rijit oturtma");
    }

    static void TestTableCorrection()
    {
        Console.WriteLine("Tabla duzeltmesi:");
        // Gercek tabla: seritle olculen kenarlar + kosegen (sol ust, sag ust, sol alt, sag alt)
        Point2[] truth = { new Point2(-3.0, 1991.0), new Point2(3297.0, 1988.0), new Point2(0, 0), new Point2(3296, 0) };
        // Modelin olctugu tabla: kalan lens hatasi (merkezden uzaklastikca buyuyen olcek, r^3) + kamera konumu hatasi
        // (hafif donme, egiklik, olcek)
        var mid = new Point2(1600, 1050);
        var H0 = new Homography(new[] { 1.004, 0.006, -5.0, -0.004, 0.997, 3.0, 2e-6, -1e-6, 1.0 });
        Func<Point2, Point2> warp = p => { Point2 d = p - mid; double r2 = (d.X * d.X + d.Y * d.Y) / 1e6; return H0.Transform(mid + d * (1 - 0.006 * r2)); };
        Point2[] measured = truth.Select(warp).ToArray();
        // Kalan bukulme kameranin bilinen ozelligi (kamera-lens.txt), koseler paspaslardan + seritle olculen tabladan
        var corr = new TableCorrection(measured, truth, warp(mid), 6e-9);
        var cornersOnly = new TableCorrection(measured, truth, warp(mid), 0);
        Check(Enumerable.Range(0, 4).All(i => Near(corr.Apply(measured[i]), truth[i], 1e-6)), "koseler gercek koselere gider");
        double maxCorners = 0;
        for (int i = 0; i <= 10; i++)
            for (int j = 0; j <= 10; j++) { Point2 p = new Point2(3296 * i / 10.0, 1990 * j / 10.0); maxCorners = Math.Max(maxCorners, Point2.Distance(cornersOnly.Apply(warp(p)), p)); }
        double maxErr = 0, maxRaw = 0;
        for (int i = 0; i <= 10; i++)
            for (int j = 0; j <= 10; j++)
            {
                Point2 p = new Point2(3296 * i / 10.0, 1990 * j / 10.0);
                maxErr = Math.Max(maxErr, Point2.Distance(corr.Apply(warp(p)), p));
                maxRaw = Math.Max(maxRaw, Point2.Distance(warp(p), p));
            }
        Console.WriteLine("    duzeltmesiz en buyuk hata {0:F1} mm, yalniz koselerle {1:F1} mm, kalan bukulmeyle {2:F2} mm", maxRaw, maxCorners, maxErr);
        Check(maxRaw > 10 && maxCorners > 5 && maxErr < 2.5, "kalan bukulme tablanin icindeki hatayi da giderir (yalniz koseler yetmez)");
        var lc = new LensCalibration { ImageWidth = 10, ImageHeight = 10, Fx = 1, Fy = 1, ResidualRadial = -1.64e-9 };
        string tmp = Path.Combine(Path.GetTempPath(), "kk_kalan_bukulme.txt");
        lc.Save(tmp);
        Check(LensCalibration.Load(tmp).ResidualRadial == -1.64e-9, "kalan bukulme kamera dosyasinda saklaniyor");
        File.Delete(tmp);
        Point2 q0 = new Point2(1234.5, 678.9);
        Check(Near(corr.Apply(corr.Invert(q0)), q0, 1e-6), "ters donusum");
        var re = TableCorrection.FromValues(corr.ToValues());
        Check(Near(re.Apply(warp(q0)), corr.Apply(warp(q0)), 1e-9), "kayit / okuma");
        Point2 q = q0;
        var plain = new TableCorrection(truth, truth, mid, 0);
        Check(Near(plain.Apply(q), q, 1e-9) && Near(plain.Invert(q), q, 1e-9), "kayma yoksa nokta degismez");

        // 6 paspas: kalan bukulme bilinmiyor; alt ve ust kenarin ortasindaki paspaslarin dis koselerinden bulunur
        Point2[] mids = { new Point2(1430, 0), new Point2(1865, 0), new Point2(1432, 1989.6), new Point2(1867, 1989.4) };
        Point2[] all = truth.Concat(mids).ToArray();
        var fit = TableCorrection.Fit(all.Select(warp).ToArray(), all, warp(mid), out double rms);
        double maxFit = 0;
        for (int i = 0; i <= 10; i++)
            for (int j = 0; j <= 10; j++) { Point2 p = new Point2(3296 * i / 10.0, 1990 * j / 10.0); maxFit = Math.Max(maxFit, Point2.Distance(fit.Apply(warp(p)), p)); }
        Console.WriteLine("    6 paspas: bulunan kalan bukulme {0:E2}, nokta rms {1:F2} mm, tablada en buyuk hata {2:F2} mm (yalniz koselerle {3:F1})", fit.E, rms, maxFit, maxCorners);
        Check(maxFit < 3 && fit.E > 0, "6 paspas: kenar ortalari kalan bukulmeyi belirler");
        var re6 = TableCorrection.FromValues(fit.ToValues());
        Check(re6.Measured.Length == 8 && Near(re6.Apply(warp(q0)), fit.Apply(warp(q0)), 1e-9), "6 paspas: kayit / okuma");
    }

    static int Main()
    {
        TestTableCorrection();
        TestHomography();
        TestLensCalibrationFile();
        TestPlanarBundle();
        TestCameraPose();
        TestShapes();
        TestThickness();
        TestRealContour();
        Console.WriteLine(_failed == 0 ? "\nTum testler gecti." : "\n" + _failed + " test basarisiz.");
        return _failed == 0 ? 0 : 1;
    }
}
