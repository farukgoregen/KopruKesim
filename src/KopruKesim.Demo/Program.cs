using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using KopruKesim.Core;
using KopruKesim.Vision;
using OpenCvSharp;

// Iki komut:
//   kur <bos_tabla.jpg>   Kurulum (bir kez): tabla koselerini bulur, kalibrasyonu ve bos tabla referansini kaydeder.
//   olc <fotograf.jpg> [kalinlik_mm]   Olcum: tabladaki parcalarin koselerini (mm) yazar, "sonuc.jpg" kaydeder.
static class Program
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "kur") return Setup(args);
        if (args.Length >= 2 && args[0] == "olc") return Measure(args);
        if (args.Length >= 2 && args[0] == "isaret-uret") return MakeMarkers(args);
        Console.WriteLine("Kullanim:");
        Console.WriteLine("  KopruKesim.Demo kur <bos_tabla.jpg> [--kal dosya] [--uzunluk mm] [--genislik mm]");
        Console.WriteLine("                      [--koseler x,y x,y x,y x,y] [--tikla] [--referanssiz]");
        Console.WriteLine("  KopruKesim.Demo olc <fotograf.jpg> [kalinlik_mm] [--kal dosya] [--isaret isaretler.txt] [--kenar mm] [--referanssiz]");
        Console.WriteLine("  KopruKesim.Demo isaret-uret <klasor> [adet=8] [piksel=1000]   basilacak ArUco isaretleri");
        Console.WriteLine("                      [--temizlik mm] [--puruz mm] [--minkenar mm]");
        Console.WriteLine("Koseler goruntudeki sirayla verilir: sol ust (0 noktasi), sag ust, sag alt, sol alt.");
        return 1;
    }

    // ---------- kur ----------
    static int Setup(string[] args)
    {
        string image = args[1], calPath = "kalibrasyon.txt";
        double length = 3300, width = 2000;
        bool click = false, saveReference = true;
        bool manualCorners = false;
        int refine = 15;   // elle verilen koseleri kenara oturtma penceresi (piksel), 0 = kapali
        Point2[] corners = null;
        for (int i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--kal": calPath = args[++i]; break;
                case "--uzunluk": length = double.Parse(args[++i], Inv); break;
                case "--genislik": width = double.Parse(args[++i], Inv); break;
                case "--tikla": click = true; break;
                case "--referanssiz": saveReference = false; break;
                case "--incelt": refine = int.Parse(args[++i], Inv); break;
                case "--koseler":
                    manualCorners = true;
                    corners = new Point2[4];
                    for (int k = 0; k < 4; k++)
                    {
                        string[] p = args[++i].Split(',');
                        corners[k] = new Point2(double.Parse(p[0], Inv), double.Parse(p[1], Inv));
                    }
                    break;
                default: Console.WriteLine("Bilinmeyen secenek: " + args[i]); return 1;
            }
        }

        using (Mat frame = Cv2.ImRead(image))
        {
            if (frame.Empty()) { Console.WriteLine("Goruntu okunamadi: " + image); return 1; }

            if (click) corners = ClickCorners(frame);
            else if (corners == null)
            {
                corners = TableFinder.FindCorners(frame, length, width);
                if (corners == null)
                {
                    Console.WriteLine("Tabla koseleri otomatik bulunamadi. --tikla ya da --koseler ile elle verin.");
                    return 2;
                }
                Console.WriteLine("Tabla koseleri otomatik bulundu.");
            }
            if (corners == null) { Console.WriteLine("Kurulum iptal edildi."); return 2; }
            if ((click || manualCorners) && refine > 0)
            {
                Point2[] before = corners;
                corners = TableFinder.RefineCorners(frame, corners, refine);
                double moved = 0;
                for (int i = 0; i < 4; i++) moved = Math.Max(moved, Point2.Distance(before[i], corners[i]));
                Console.WriteLine("Koseler kenarlara oturtuldu (en buyuk duzeltme {0:F1} piksel).", moved);
            }

            var cal = new TableCalibration
            {
                TableLengthMm = length,
                TableWidthMm = width,
                ImageWidth = frame.Cols,
                ImageHeight = frame.Rows,
                CornerOrigin = corners[0],
                CornerYEnd = corners[1],
                CornerFar = corners[2],
                CornerXEnd = corners[3]
            };
            cal.Build();
            cal.Save(calPath);

            string[] names = { "0 noktasi (sol ust)", "Y sonu (sag ust)", "uzak kose (sag alt)", "X sonu (sol alt)" };
            for (int i = 0; i < 4; i++)
                Console.WriteLine("  {0}: piksel ({1:F1}, {2:F1})", names[i], corners[i].X, corners[i].Y);
            PrintCamera(cal);

            // Onizleme: bulunan dortgen ve eksenler, gozle onay icin
            using (Mat preview = frame.Clone())
            {
                var poly = new List<Point>();
                foreach (Point2 c in corners) poly.Add(new Point((int)Math.Round(c.X), (int)Math.Round(c.Y)));
                int th = Math.Max(2, frame.Cols / 500);
                Cv2.Polylines(preview, new[] { poly }, true, new Scalar(0, 0, 255), th);
                Cv2.Circle(preview, poly[0], 6 * th, new Scalar(0, 255, 0), -1);
                Cv2.PutText(preview, "0", new Point(poly[0].X + 8 * th, poly[0].Y + 14 * th), HersheyFonts.HersheySimplex, 0.5 * th, new Scalar(0, 255, 0), th);
                Cv2.PutText(preview, "X", new Point(poly[3].X + 8 * th, poly[3].Y - 8 * th), HersheyFonts.HersheySimplex, 0.5 * th, new Scalar(0, 255, 0), th);
                Cv2.PutText(preview, "Y", new Point(poly[1].X - 16 * th, poly[1].Y + 14 * th), HersheyFonts.HersheySimplex, 0.5 * th, new Scalar(0, 255, 0), th);
                Cv2.ImWrite(SidePath(calPath, "_onizleme.jpg"), preview);
            }

            string refPath = SidePath(calPath, "_referans.png");
            if (saveReference) Cv2.ImWrite(refPath, frame);
            else if (File.Exists(refPath)) Console.WriteLine("UYARI: eski referans duruyor: " + refPath);

            Console.WriteLine("Kalibrasyon kaydedildi: " + calPath);
            Console.WriteLine("Onizleme: " + SidePath(calPath, "_onizleme.jpg") + (saveReference ? ", bos tabla referansi: " + refPath : ""));
        }
        return 0;
    }

    /// <summary>Pencerede dort koseye tiklatir: sol ust (0 noktasi), sag ust, sag alt, sol alt. Esc iptal.</summary>
    static Point2[] ClickCorners(Mat frame)
    {
        const string win = "Tabla koseleri: sol ust (0), sag ust, sag alt, sol alt - Esc iptal";
        double scale = Math.Min(1.0, Math.Min(1400.0 / frame.Cols, 800.0 / frame.Rows));
        var picked = new List<Point2>();
        using (var shown = new Mat())
        {
            Cv2.Resize(frame, shown, new Size(), scale, scale);
            Cv2.NamedWindow(win);
            Cv2.SetMouseCallback(win, (ev, x, y, flags, data) =>
            {
                if (ev != MouseEventTypes.LButtonDown || picked.Count >= 4) return;
                picked.Add(new Point2(x / scale, y / scale));
                Cv2.Circle(shown, new Point(x, y), 5, new Scalar(0, 0, 255), -1);
                Cv2.PutText(shown, picked.Count.ToString(), new Point(x + 8, y - 8), HersheyFonts.HersheySimplex, 0.7, new Scalar(0, 0, 255), 2);
            });
            while (picked.Count < 4)
            {
                Cv2.ImShow(win, shown);
                if (Cv2.WaitKey(30) == 27) { Cv2.DestroyAllWindows(); return null; }
            }
            Cv2.ImShow(win, shown);
            Cv2.WaitKey(600);
            Cv2.DestroyAllWindows();
        }
        return picked.ToArray();
    }

    // ---------- olc ----------
    static int Measure(string[] args)
    {
        string image = args[1], calPath = "kalibrasyon.txt", layoutPath = null;
        double thickness = 20, margin = 40;
        bool useReference = true;
        double cleanup = 25, rough = 8, minEdge = 40, minArea = 200 * 200, scale = 0;
        int brightness = 0;
        EdgeMode edgeMode = EdgeMode.Fallback;
        for (int i = 2; i < args.Length; i++)
        {
            double t;
            if (args[i] == "--kal") calPath = args[++i];
            else if (args[i] == "--isaret") layoutPath = args[++i];
            else if (args[i] == "--kenar") margin = double.Parse(args[++i], Inv);
            else if (args[i] == "--referanssiz") useReference = false;
            else if (args[i] == "--temizlik") cleanup = double.Parse(args[++i], Inv);
            else if (args[i] == "--puruz") rough = double.Parse(args[++i], Inv);
            else if (args[i] == "--minkenar") minEdge = double.Parse(args[++i], Inv);
            else if (args[i] == "--minalan") minArea = double.Parse(args[++i], Inv);
            else if (args[i] == "--olcek") scale = double.Parse(args[++i], Inv);
            else if (args[i] == "--parlaklik") brightness = int.Parse(args[++i], Inv);
            else if (args[i] == "--kenar-yontemi") edgeMode = (EdgeMode)Enum.Parse(typeof(EdgeMode), args[++i], true);
            else if (double.TryParse(args[i].Replace(',', '.'), NumberStyles.Float, Inv, out t)) thickness = t;
            else { Console.WriteLine("Bilinmeyen secenek: " + args[i]); return 1; }
        }
        MarkerLayout layout = layoutPath != null ? MarkerLayout.Load(layoutPath) : null;
        TableCalibration cal = File.Exists(calPath) ? TableCalibration.Load(calPath) : null;
        if (layout == null && cal == null) { Console.WriteLine("Kalibrasyon yok: " + calPath + ". Once 'kur' calistirin ya da --isaret verin."); return 1; }
        if (thickness < 0) { Console.WriteLine("Kalinlik eksi olamaz."); return 1; }

        using (Mat frame = Cv2.ImRead(image))
        {
            if (frame.Empty()) { Console.WriteLine("Goruntu okunamadi: " + image); return 1; }
            var options = new DetectorOptions { BorderMarginMm = margin, CleanupMm = cleanup, MinAreaMm2 = minArea, PxPerMm = scale, NoReferenceBrightnessDiff = brightness, EdgeMode = edgeMode, Locator = new LocatorOptions { SimplifyToleranceMm = rough, MinEdgeMm = minEdge } };
            string refPath = SidePath(calPath, "_referans.png");
            using (Mat empty = File.Exists(refPath) ? Cv2.ImRead(refPath) : null)
            using (MeasureOutput m = TableMeasure.Run(cal, frame, empty, useReference, options, layout, thickness))
            {
                if (m.Markers != null)
                {
                    Console.WriteLine("Isaretler: {0} kullanildi{1}{2}", m.Markers.Used.Count,
                        m.Markers.Missing.Count > 0 ? ", gorunmeyen: " + string.Join(" ", m.Markers.Missing) : "",
                        m.Markers.Rejected.Count > 0 ? ", uyusmadigi icin atilan: " + string.Join(" ", m.Markers.Rejected) : "");
                    if (m.Markers.Calibration != null)
                        Console.WriteLine("Isaret uyumu: ortalama {0:F1} mm, en kotu {1:F1} mm", m.Markers.RmsMm, m.Markers.MaxMm);
                }
                if (m.Error != null) { Console.WriteLine("OLCUM YAPILAMADI: " + m.Error); return 2; }
                Console.WriteLine("Tabla {0:F0} x {1:F0} mm, mermer kalinligi {2} mm", m.Calibration.TableLengthMm, m.Calibration.TableWidthMm, thickness);
                PrintCamera(m.Calibration);
                if (m.Relocated) Console.WriteLine("Kamera kurulumdan farkli yerde: tabla bu fotografta yeniden bulundu, olcum onunla yapildi.");
                if (m.CameraMoved)
                    Console.WriteLine("UYARI: tabla kenarlari kurulumdaki yerinde gorunmuyor (gorunurluk %{0:F0}); kamera oynamis olabilir. Koordinatlar kayar, kurulumu yenileyin.", m.EdgeRatio * 100);
                else if (!m.Relocated && !double.IsNaN(m.EdgeRatio)) Console.WriteLine("Kamera konumu: kurulumla uyumlu (kenar gorunurlugu %{0:F0})", m.EdgeRatio * 100);
                Console.WriteLine(m.UsedReference ? "Ayirma: bos tabla referansiyla" : "Ayirma: referanssiz (tabla rengi tahmini)");

                using (var detector = new SlabDetector(m.Calibration, options))
                {
                    for (int n = 0; n < m.Slabs.Count; n++)
                    {
                        SlabResult r = m.Slabs[n];
                        Console.WriteLine("\nParca {0}: {1}, {2} kose, alan {3:F0} cm2, donme {4:F1} derece{5}",
                            n + 1, r.Shape, r.Vertices.Length, r.AreaMm2 / 100, r.RotationDeg, r.IsReliable ? "" : "  [" + r.Note + "]", r.Method);
                        var poly = new List<Point>();
                        for (int i = 0; i < r.Vertices.Length; i++)
                        {
                            if (r.IsReliable)
                                Console.WriteLine("  K{0}: X={1:F1}  Y={2:F1}   (kenar {3:F0} mm, aci {4:F1})",
                                    i + 1, r.Vertices[i].X, r.Vertices[i].Y, r.EdgeLengths[i], r.AnglesDeg[i]);
                            poly.Add(detector.ToRectifiedPixel(r.Vertices[i]));
                        }
                        Cv2.Polylines(m.Rectified, new[] { poly }, true, r.IsReliable ? new Scalar(0, 0, 255) : new Scalar(150, 150, 150), 2);
                    }
                }
                if (m.Slabs.Count == 0) Console.WriteLine("\nParca bulunamadi.");
                Cv2.ImWrite("sonuc.jpg", m.Rectified);
            }
        }
        return 0;
    }

    // ---------- isaret-uret ----------
    /// <summary>Basilacak isaretleri PNG olarak uretir: isaret_0.png, isaret_1.png ...</summary>
    static int MakeMarkers(string[] args)
    {
        string dir = args[1];
        int count = args.Length >= 3 ? int.Parse(args[2], Inv) : 8;
        int px = args.Length >= 4 ? int.Parse(args[3], Inv) : 1000;
        if (count < 4 || count > 50) { Console.WriteLine("Isaret sayisi 4-50 arasinda olmali."); return 1; }
        Directory.CreateDirectory(dir);
        for (int id = 0; id < count; id++)
            using (Mat img = MarkerFinder.Render(id, px)) Cv2.ImWrite(Path.Combine(dir, "isaret_" + id + ".png"), img);
        Console.WriteLine("{0} isaret yazildi: {1}", count, dir);
        Console.WriteLine("Siyah kare bolgesi tam olarak ayni boyda basilmali (ornek 100 mm); numaralar isaret dosyasindaki numaralardir.");
        return 0;
    }

    static void PrintCamera(TableCalibration cal)
    {
        if (cal.Camera == null) return;
        Console.WriteLine("Kamera tahmini (mm): X={0:F0} Y={1:F0} yukseklik={2:F0}, tutarlilik={3:F2} (ideal 1)",
            cal.Camera.X, cal.Camera.Y, cal.Camera.Height, cal.Camera.Consistency);
    }

    /// <summary>Kalibrasyon dosyasinin yanindaki yardimci dosyanin yolu (ayni ad + ek).</summary>
    static string SidePath(string calPath, string suffix)
    {
        string dir = Path.GetDirectoryName(calPath);
        string name = Path.GetFileNameWithoutExtension(calPath) + suffix;
        return string.IsNullOrEmpty(dir) ? name : Path.Combine(dir, name);
    }
}
