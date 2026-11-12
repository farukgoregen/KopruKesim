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
        Console.WriteLine("Kullanim:");
        Console.WriteLine("  KopruKesim.Demo kur <bos_tabla.jpg> [--kal dosya] [--uzunluk mm] [--genislik mm]");
        Console.WriteLine("                      [--koseler x,y x,y x,y x,y] [--tikla] [--referanssiz]");
        Console.WriteLine("  KopruKesim.Demo olc <fotograf.jpg> [kalinlik_mm] [--kal dosya] [--kenar mm] [--referanssiz]");
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
                case "--koseler":
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
        string image = args[1], calPath = "kalibrasyon.txt";
        double thickness = 20, margin = 40;
        bool useReference = true;
        double cleanup = 25, rough = 8, minEdge = 40;
        for (int i = 2; i < args.Length; i++)
        {
            double t;
            if (args[i] == "--kal") calPath = args[++i];
            else if (args[i] == "--kenar") margin = double.Parse(args[++i], Inv);
            else if (args[i] == "--referanssiz") useReference = false;
            else if (args[i] == "--temizlik") cleanup = double.Parse(args[++i], Inv);
            else if (args[i] == "--puruz") rough = double.Parse(args[++i], Inv);
            else if (args[i] == "--minkenar") minEdge = double.Parse(args[++i], Inv);
            else if (double.TryParse(args[i].Replace(',', '.'), NumberStyles.Float, Inv, out t)) thickness = t;
            else { Console.WriteLine("Bilinmeyen secenek: " + args[i]); return 1; }
        }
        if (!File.Exists(calPath)) { Console.WriteLine("Kalibrasyon yok: " + calPath + ". Once 'kur' calistirin."); return 1; }
        if (thickness < 10 || thickness > 30) Console.WriteLine("UYARI: kalinlik {0} mm, beklenen aralik 10-30 mm.", thickness);

        TableCalibration cal = TableCalibration.Load(calPath);
        cal.SlabThicknessMm = thickness;

        using (Mat frame = Cv2.ImRead(image))
        {
            if (frame.Empty()) { Console.WriteLine("Goruntu okunamadi: " + image); return 1; }
            if (frame.Cols != cal.ImageWidth || frame.Rows != cal.ImageHeight)
            {
                Console.WriteLine("Goruntu boyutu ({0}x{1}) kalibrasyonla ({2}x{3}) uyusmuyor; kamera ayni degil.",
                    frame.Cols, frame.Rows, cal.ImageWidth, cal.ImageHeight);
                return 1;
            }
            Console.WriteLine("Tabla {0:F0} x {1:F0} mm, mermer kalinligi {2} mm", cal.TableLengthMm, cal.TableWidthMm, thickness);
            PrintCamera(cal);

            using (var detector = new SlabDetector(cal, new DetectorOptions { BorderMarginMm = margin, CleanupMm = cleanup, Locator = new LocatorOptions { SimplifyToleranceMm = rough, MinEdgeMm = minEdge } }))
            {
                string refPath = SidePath(calPath, "_referans.png");
                bool hasRef = false;
                if (useReference && File.Exists(refPath))
                {
                    using (Mat empty = Cv2.ImRead(refPath))
                    {
                        if (!empty.Empty() && empty.Cols == frame.Cols && empty.Rows == frame.Rows) { detector.SetReference(empty); hasRef = true; }
                    }
                }
                if (!hasRef) detector.SetTableColorAuto(frame);
                Console.WriteLine(hasRef ? "Ayirma: bos tabla referansiyla" : "Ayirma: referanssiz (tabla rengi tahmini)");

                List<SlabResult> slabs = detector.Detect(frame);
                using (Mat view = detector.Rectify(frame))
                {
                    for (int n = 0; n < slabs.Count; n++)
                    {
                        SlabResult r = slabs[n];
                        Console.WriteLine("\nParca {0}: {1}, {2} kose, alan {3:F0} cm2, donme {4:F1} derece",
                            n + 1, r.Shape, r.Vertices.Length, r.AreaMm2 / 100, r.RotationDeg);
                        var poly = new List<Point>();
                        for (int i = 0; i < r.Vertices.Length; i++)
                        {
                            Console.WriteLine("  K{0}: X={1:F1}  Y={2:F1}   (kenar {3:F0} mm, aci {4:F1})",
                                i + 1, r.Vertices[i].X, r.Vertices[i].Y, r.EdgeLengths[i], r.AnglesDeg[i]);
                            poly.Add(detector.ToRectifiedPixel(r.Vertices[i]));
                        }
                        Cv2.Polylines(view, new[] { poly }, true, new Scalar(0, 0, 255), 2);
                    }
                    if (slabs.Count == 0) Console.WriteLine("\nParca bulunamadi.");
                    Cv2.ImWrite("sonuc.jpg", view);
                }
            }
        }
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
