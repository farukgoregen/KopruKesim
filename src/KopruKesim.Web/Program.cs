using System.Globalization;
using KopruKesim.Core;
using KopruKesim.Vision;
using OpenCvSharp;

// Yerel deneme arayuzu. Calistirma: dotnet run --project src/KopruKesim.Web
// Kalibrasyon ve bos tabla referansi "veri" klasorunde tutulur.
// Ag modu (--ag): ayni Wi-Fi'deki telefon/tablet https ile baglanir (tarayici kameraya ancak https ile izin verir).
bool networkMode = args.Contains("--ag");
var builder = WebApplication.CreateBuilder(args.Where(a => a != "--ag").ToArray());
string[] lanUrls = Array.Empty<string>();
if (networkMode)
{
    string certDir = Path.Combine(builder.Environment.ContentRootPath, "veri");
    Directory.CreateDirectory(certDir);
    var (cert, ips) = LanCertificate.LoadOrCreate(certDir);
    builder.WebHost.ConfigureKestrel(o =>
    {
        // Yalnizca https: normal moddaki sunucu (5080) ayni anda acik olsa da cakismaz
        o.ListenAnyIP(5443, l => l.UseHttps(cert));
    });
    lanUrls = ips.Select(ip => $"https://{ip}:5443").ToArray();
}
var app = builder.Build();
if (networkMode)
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        Console.WriteLine();
        Console.WriteLine("=== AG MODU ===");
        Console.WriteLine("Bu bilgisayarda:  https://localhost:5443");
        foreach (string u in lanUrls) Console.WriteLine("Telefon/tablet:   " + u);
        Console.WriteLine("Telefon ayni Wi-Fi'de olmali. Ilk acilista 'baglanti guvenli degil' uyarisi cikar:");
        Console.WriteLine("sertifika bu bilgisayarda uretildigi icindir; 'Gelismis > Devam et' ile gecin.");
        Console.WriteLine("Windows guvenlik duvari izin sorarsa yalnizca 'Ozel agler' icin izin verin.");
        Console.WriteLine();
    });
app.UseDefaultFiles();
app.UseStaticFiles();

var inv = CultureInfo.InvariantCulture;
var gate = new object();
string dataDir = Path.Combine(app.Environment.ContentRootPath, "veri");
Directory.CreateDirectory(dataDir);
string calPath = Path.Combine(dataDir, "kalibrasyon.txt");
string refPath = Path.Combine(dataDir, "kalibrasyon_referans.png");
string layoutPath = Path.Combine(dataDir, "isaretler.txt");
MarkerLayout LoadLayout() => File.Exists(layoutPath) ? MarkerLayout.Load(layoutPath) : null;

// Ornek gorseller depo kokundeki "ornek" klasorunden gelir
string repoRoot = app.Environment.ContentRootPath;
while (repoRoot != null && !File.Exists(Path.Combine(repoRoot, "KopruKesim.sln"))) repoRoot = Path.GetDirectoryName(repoRoot);
var samples = new Dictionary<string, string> { { "bos", "bos_tabla.webp" }, { "mermer", "mermer.webp" } };

IResult Fail(string message) => Results.Json(new { hata = message }, statusCode: 400);

object State()
{
    MarkerLayout layout = LoadLayout();
    var isaretli = layout == null ? null : new
    {
        sayi = layout.Markers.Count,
        numaralar = layout.Markers.Select(m => m.Id),
        yukseklik = layout.MarkerHeightMm
    };
    if (!File.Exists(calPath))
    {
        if (layout == null) return new { kurulu = false };
        return new
        {
            kurulu = true,
            tabla = new { uzunluk = layout.TableLengthMm, genislik = layout.TableWidthMm },
            isaretli,
            referansVar = File.Exists(refPath)
        };
    }
    TableCalibration cal = TableCalibration.Load(calPath);
    return new
    {
        kurulu = true,
        tabla = new { uzunluk = layout?.TableLengthMm ?? cal.TableLengthMm, genislik = layout?.TableWidthMm ?? cal.TableWidthMm },
        goruntu = new { w = cal.ImageWidth, h = cal.ImageHeight },
        koseler = new[] { P(cal.CornerOrigin), P(cal.CornerYEnd), P(cal.CornerFar), P(cal.CornerXEnd) },
        tutarlilik = cal.Camera == null ? (double?)null : Math.Round(cal.Camera.Consistency, 2),
        isaretli,
        referansVar = File.Exists(refPath)
    };
}

static double[] P(Point2 p) => new[] { Math.Round(p.X, 1), Math.Round(p.Y, 1) };

Point2[] ParseCorners(string text)
{
    if (string.IsNullOrWhiteSpace(text)) return null;
    string[] parts = text.Split(';');
    if (parts.Length != 4) return null;
    var c = new Point2[4];
    for (int i = 0; i < 4; i++)
    {
        string[] xy = parts[i].Split(',');
        c[i] = new Point2(double.Parse(xy[0], inv), double.Parse(xy[1], inv));
    }
    return c;
}

void SaveCalibration(Point2[] corners, double length, double width, int w, int h)
{
    var cal = new TableCalibration
    {
        TableLengthMm = length, TableWidthMm = width, ImageWidth = w, ImageHeight = h,
        CornerOrigin = corners[0], CornerYEnd = corners[1], CornerFar = corners[2], CornerXEnd = corners[3]
    };
    cal.Build();
    cal.Save(calPath);
}

async Task<byte[]> ReadFile(IFormCollection form)
{
    IFormFile f = form.Files.GetFile("dosya");
    if (f == null || f.Length == 0) return null;
    using var ms = new MemoryStream();
    await f.CopyToAsync(ms);
    return ms.ToArray();
}

app.MapGet("/api/durum", () => { lock (gate) return Results.Json(State()); });

app.MapGet("/api/ornek/{ad}", (string ad) =>
{
    if (repoRoot == null || !samples.TryGetValue(ad, out string file)) return Results.NotFound();
    string path = Path.Combine(repoRoot, "ornek", file);
    return File.Exists(path) ? Results.File(path, "image/webp") : Results.NotFound();
});

app.MapGet("/api/referans", () =>
{
    lock (gate)
    {
        if (!File.Exists(refPath)) return Results.NotFound();
        using Mat m = Cv2.ImRead(refPath);
        return Results.File(m.ToBytes(".jpg"), "image/jpeg");
    }
});

// Kurulum: bos tabla fotografi. Koseler verilmezse otomatik bulunur.
app.MapPost("/api/kur", async (HttpRequest req) =>
{
    IFormCollection form = await req.ReadFormAsync();
    byte[] bytes = await ReadFile(form);
    if (bytes == null) return Fail("Fotoğraf gelmedi.");
    double length = double.TryParse(form["uzunluk"], NumberStyles.Float, inv, out double l) ? l : 3300;
    double width = double.TryParse(form["genislik"], NumberStyles.Float, inv, out double w) ? w : 2000;
    if (length <= 0 || width <= 0) return Fail("Tabla ölçüleri sıfırdan büyük olmalı.");
    lock (gate)
    {
        using Mat frame = Cv2.ImDecode(bytes, ImreadModes.Color);
        if (frame.Empty()) return Fail("Dosya bir görüntü olarak okunamadı.");
        Point2[] corners = TableFinder.FindCorners(frame, length, width);
        bool found = corners != null;
        if (found)
        {
            try { SaveCalibration(corners, length, width, frame.Cols, frame.Rows); }
            catch (ArgumentException) { found = false; }   // bulunan koselerle kalibrasyon kurulamadi
        }
        if (!found)
        {
            // Bulunamadi: kullanici surukleyip duzeltsin diye iceride bir dortgen oner
            double mx = frame.Cols * 0.2, my = frame.Rows * 0.15;
            corners = new[] { new Point2(mx, my), new Point2(frame.Cols - mx, my), new Point2(frame.Cols - mx, frame.Rows - my), new Point2(mx, frame.Rows - my) };
            SaveCalibration(corners, length, width, frame.Cols, frame.Rows);
        }
        Cv2.ImWrite(refPath, frame);
        return Results.Json(new { otomatikBulundu = found, durum = State() });
    }
});

// Koseleri elle duzeltme (surukleme) ya da tabla olcusunu degistirme
app.MapPost("/api/koseler", async (HttpRequest req) =>
{
    IFormCollection form = await req.ReadFormAsync();
    lock (gate)
    {
        if (!File.Exists(calPath)) return Fail("Önce kurulum yapın.");
        TableCalibration old = TableCalibration.Load(calPath);
        Point2[] corners;
        try { corners = ParseCorners(form["koseler"]); } catch (FormatException) { corners = null; }
        if (corners == null) corners = new[] { old.CornerOrigin, old.CornerYEnd, old.CornerFar, old.CornerXEnd };
        double length = double.TryParse(form["uzunluk"], NumberStyles.Float, inv, out double l) ? l : old.TableLengthMm;
        double width = double.TryParse(form["genislik"], NumberStyles.Float, inv, out double w) ? w : old.TableWidthMm;
        if (length <= 0 || width <= 0) return Fail("Tabla ölçüleri sıfırdan büyük olmalı.");
        // Surüklenen koseler kurulum fotografindaki kenarlara piksel alti hassasiyetle oturtulur
        if (form["incelt"] == "1" && File.Exists(refPath))
        {
            using Mat setupFrame = Cv2.ImRead(refPath);
            if (!setupFrame.Empty()) corners = TableFinder.RefineCorners(setupFrame, corners, Math.Max(8, setupFrame.Cols / 100));
        }
        try { SaveCalibration(corners, length, width, old.ImageWidth, old.ImageHeight); }
        catch (Exception) { return Fail("Bu köşelerle geçerli bir tabla oluşmuyor. Köşeleri sırasıyla yerleştirin."); }
        return Results.Json(State());
    }
});

// Olcum: tabladaki parcalarin koseleri (mm) ve ustten gorunum
app.MapPost("/api/olc", async (HttpRequest req) =>
{
    IFormCollection form = await req.ReadFormAsync();
    byte[] bytes = await ReadFile(form);
    if (bytes == null) return Fail("Fotoğraf gelmedi.");
    double thickness = double.TryParse(form["kalinlik"], NumberStyles.Float, inv, out double t) && t >= 0 ? t : 20;
    bool useReference = form["referans"] == "1";
    bool uniformTable = form["tekrenk"] == "1";
    // Tek fotograf: kurulum yok; tabla olcusu formdan, tabla bu fotografta bulunur
    bool singlePhoto = form["tekfoto"] == "1";
    double formLength = double.TryParse(form["uzunluk"], NumberStyles.Float, inv, out double fl) && fl > 0 ? fl : 0;
    double formWidth = double.TryParse(form["genislik"], NumberStyles.Float, inv, out double fw) && fw > 0 ? fw : 0;
    double margin = double.TryParse(form["kenar"], NumberStyles.Float, inv, out double m) ? m : 40;
    lock (gate)
    {
        MarkerLayout layout = LoadLayout();
        TableCalibration cal = File.Exists(calPath) ? TableCalibration.Load(calPath) : null;
        if (singlePhoto && (formLength <= 0 || formWidth <= 0)) return Fail("Tek fotoğraf modunda tablanın uzunluğunu ve genişliğini girin.");
        if (!singlePhoto && layout == null && cal == null) return Fail("Önce kurulum yapın ya da tek fotoğraf modunu açın.");
        using Mat frame = Cv2.ImDecode(bytes, ImreadModes.Color);
        if (frame.Empty()) return Fail("Dosya bir görüntü olarak okunamadı.");

        // Kenar puruzu toleransi genis: kameraya bakan kenarda yan yuzey ve golge kenari dalgalandirir
        var options = new DetectorOptions
        {
            BorderMarginMm = margin,
            MinAreaMm2 = 60 * 60,
            NoReferenceBrightnessDiff = uniformTable ? 35 : 0,
            CleanupMm = uniformTable ? 8 : 25,   // tek renkli tablada gurultu az; kucuk parcalarin koseleri yuvarlanmasin
            Locator = new LocatorOptions { SimplifyToleranceMm = 30, MinEdgeMm = 150 }
        };
        using Mat empty = File.Exists(refPath) ? Cv2.ImRead(refPath) : null;
        using MeasureOutput result = singlePhoto && layout == null
            ? TableMeasure.RunSingle(frame, formLength, formWidth, options, thickness)
            : TableMeasure.Run(cal, frame, empty, useReference, options, layout, thickness);
        var isaretler = result.Markers == null ? null : new
        {
            kullanilan = result.Markers.Used,
            gorunmeyen = result.Markers.Missing,
            atilan = result.Markers.Rejected,
            ortalamaMm = Math.Round(result.Markers.RmsMm, 1),
            enKotuMm = Math.Round(result.Markers.MaxMm, 1)
        };
        if (result.Error != null)
            return Results.Json(new { hata = TurkceHata(result.Error), isaretler }, statusCode: 400);
        List<SlabResult> slabs = result.Slabs;
        TableCalibration used = result.Calibration;
        string image = "data:image/jpeg;base64," + Convert.ToBase64String(result.Rectified.ToBytes(".jpg"));
        return Results.Json(new
        {
            tabla = new { uzunluk = used.TableLengthMm, genislik = used.TableWidthMm },
            kalinlik = thickness,
            tekFoto = singlePhoto && layout == null,
            goruntuBoyutu = new { w = used.ImageWidth, h = used.ImageHeight },
            tablaKoseleri = new[] { P(used.CornerOrigin), P(used.CornerYEnd), P(used.CornerFar), P(used.CornerXEnd) },
            tutarlilik = used.Camera == null ? (double?)null : Math.Round(used.Camera.Consistency, 2),
            referansKullanildi = result.UsedReference,
            isaretler,
            kameraOynamis = result.CameraMoved,
            tablaYenidenBulundu = result.Relocated,
            kenarGorunurlugu = double.IsNaN(result.EdgeRatio) ? (double?)null : Math.Round(result.EdgeRatio, 2),
            goruntu = image,
            parcalar = slabs.Select(s => new
            {
                sekil = s.Shape.ToString(),
                alanCm2 = Math.Round(s.AreaMm2 / 100),
                donme = Math.Round(s.RotationDeg, 1),
                koseler = s.Vertices.Select(v => new[] { Math.Round(v.X, 1), Math.Round(v.Y, 1) }),
                kenarlar = s.EdgeLengths.Select(e => Math.Round(e)),
                acilar = s.AnglesDeg.Select(a => Math.Round(a, 1)),
                guvenilir = s.IsReliable,
                yontem = s.Method,
                not = s.Note
            })
        });
    }
});

// Isaret yerlesim dosyasi: yuklenince her olcum kalibrasyonu fotograftaki isaretlerden hesaplar
app.MapPost("/api/isaretler", async (HttpRequest req) =>
{
    IFormCollection form = await req.ReadFormAsync();
    byte[] bytes = await ReadFile(form);
    if (bytes == null) return Fail("Dosya gelmedi.");
    string tmp = Path.Combine(dataDir, "isaretler.yeni.txt");
    lock (gate)
    {
        File.WriteAllBytes(tmp, bytes);
        try { MarkerLayout.Load(tmp); }
        catch (Exception e) when (e is FormatException || e is OverflowException) { File.Delete(tmp); return Fail("İşaret dosyası okunamadı: " + e.Message); }
        File.Copy(tmp, layoutPath, true);
        File.Delete(tmp);
        return Results.Json(State());
    }
});

app.MapPost("/api/isaretler/kaldir", () =>
{
    lock (gate)
    {
        if (File.Exists(layoutPath)) File.Delete(layoutPath);
        return Results.Json(State());
    }
});

// Cekirdek kutuphanenin ASCII mesajlarini arayuz icin Turkce harflere cevirir
static string TurkceHata(string m) => m
    .Replace("Yeterli isaret gorunmuyor", "Yeterli işaret görünmüyor").Replace("bulundu", "bulundu")
    .Replace("Isaretler birbiriyle uyusmuyor", "İşaretler birbiriyle uyuşmuyor").Replace("en buyuk fark", "en büyük fark")
    .Replace("Bir isaret yerinden oynamis olabilir; daha fazla isaret gorunur olmali.", "Bir işaret yerinden oynamış olabilir; daha fazla işaret görünür olmalı.")
    .Replace("Isaretler gecerli bir kalibrasyon vermiyor (ayni dogru uzerinde olabilirler).", "İşaretler geçerli bir kalibrasyon vermiyor (aynı doğru üzerinde olabilirler).")
    .Replace("Bu fotograf", "Bu fotoğraf").Replace("Ayni kameradan cekilmis bir fotograf yukleyin ya da kurulumu yenileyin.", "Aynı kameradan çekilmiş bir fotoğraf yükleyin ya da kurulumu yenileyin.")
    .Replace("Kalibrasyon yok: once kurulum yapin.", "Kalibrasyon yok: önce kurulum yapın.")
    .Replace("Tabla bu fotografta bulunamadi. Tablanin tamami gorunecek sekilde yeniden cekin.", "Tabla bu fotoğrafta bulunamadı. Tablanın tamamı görünecek şekilde yeniden çekin.");

app.Run();
