using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using KopruKesim.Core;
using KopruKesim.Vision;
using OpenCvSharp;

// Yerel deneme arayuzu. Calistirma: dotnet run --project src/KopruKesim.Web
// Kalibrasyonlar ve olcum kayitlari D:\SMO\photos altinda tutulur (D diski).
// Ag modu (--ag): ayni Wi-Fi'deki telefon/tablet https ile baglanir (tarayici kameraya ancak https ile izin verir).
bool networkMode = args.Contains("--ag");
bool openBrowser = args.Contains("--tarayici");
var builder = WebApplication.CreateBuilder(args.Where(a => a != "--ag" && a != "--tarayici").ToArray());

static int ReadPort(string value, string name)
{
    if (int.TryParse(value, out int port) && port is >= 1 and <= 65535) return port;
    throw new ArgumentException($"{name} 1 ile 65535 arasında bir sayı olmalı.");
}

int? panelPort = null;
int lanPort = 5443;
try
{
    if (builder.Configuration["panel-port"] is string panelValue) panelPort = ReadPort(panelValue, "Yerel port");
    if (networkMode && builder.Configuration["lan-port"] is string lanValue) lanPort = ReadPort(lanValue, "Ağ portu");
    if (networkMode && panelPort == lanPort) throw new ArgumentException("Yerel port ile ağ portu farklı olmalı.");
}
catch (ArgumentException error)
{
    Console.Error.WriteLine(error.Message);
    Environment.ExitCode = 2;
    return;
}

if (panelPort is int port)
    builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(port));

string[] lanUrls = Array.Empty<string>();
if (networkMode)
{
    // Sertifika fotograflarin arasina karismasin: calisma klasorundeki veri altinda kalir
    string certDir = Path.Combine(builder.Environment.ContentRootPath, "veri");
    Directory.CreateDirectory(certDir);
    var (cert, ips) = LanCertificate.LoadOrCreate(certDir);
    builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(lanPort, listener => listener.UseHttps(cert)));
    lanUrls = ips.Select(ip => $"https://{ip}:{lanPort}").ToArray();
}

var app = builder.Build();
MeasureTrace.Install();   // Acikca istenen olcumlerde cekirdek izini yakalar
if (panelPort.HasValue || networkMode)
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        string panelUrl = panelPort is int localPort ? $"http://localhost:{localPort}" : $"https://localhost:{lanPort}";
        Console.WriteLine($"Bu bilgisayarda: {panelUrl}");
        if (networkMode)
        {
            foreach (string url in lanUrls) Console.WriteLine($"Telefon/tablet: {url}");
            if (lanUrls.Length == 0) Console.WriteLine("Ağ adresi bulunamadı. Wi-Fi veya Ethernet bağlantısını kontrol edin.");
            else Console.WriteLine("Telefon aynı yerel ağda olmalı. Ağ HTTPS sertifikası yerel olarak imzalanır.");
        }
        if (!openBrowser) return;
        try
        {
            using var browser = Process.Start(new ProcessStartInfo(panelUrl) { UseShellExecute = true });
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"Tarayıcı açılamadı. Adresi kendiniz açabilirsiniz: {panelUrl}");
        }
    });
app.UseDefaultFiles();
// Arayuz dosyalari onbellege alinmasin: tarayici eski index.html'i gostermesin (yerel arayuz, boyutu kucuk)
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers["Cache-Control"] = "no-cache"
});

var inv = CultureInfo.InvariantCulture;
var gate = new object();
// Veri koku; deneme icin "--veri <klasor>" ile degistirilebilir (canli kalibrasyon ve olcumlere dokunmadan)
string dataDir = app.Configuration["veri"] is string veri && veri.Length > 0 ? veri : @"D:\SMO\photos";
// Olcum goruntusu (ekran ve olcumler klasoru): tablanin 3300 x 2000 mm'lik alani, 1 piksel = 1 mm
const double DisplayLengthMm = 3300, DisplayWidthMm = 2000;
// Kamera merceginden tablaya dik mesafe: kullanici olcumu 2030-2035 mm (yeni kamera, 2026-11-26)
const double DefaultCameraHeightMm = 2032.5;
var tr = CultureInfo.GetCultureInfo("tr-TR");
Directory.CreateDirectory(dataDir);

IResult Fail(string message) => Results.Json(new { hata = message }, statusCode: 400);

static double[] P(Point2 p) => new[] { Math.Round(p.X, 1), Math.Round(p.Y, 1) };

async Task<byte[]> ReadFile(IFormCollection form)
{
    IFormFile f = form.Files.GetFile("dosya");
    if (f == null || f.Length == 0) return null;
    using var ms = new MemoryStream();
    await f.CopyToAsync(ms);
    return ms.ToArray();
}

IResult SaveCalibrationArchive(string folder, string modelPath, string photoPath, params string[] optionalFiles)
{
    lock (gate)
    {
        if (!File.Exists(modelPath) || !File.Exists(photoPath))
            return Fail("Kaydedilecek kalibrasyon ve fotoğraf bulunamadı.");

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string file in new[] { modelPath, photoPath }.Concat(optionalFiles))
            {
                if (File.Exists(file))
                    archive.CreateEntryFromFile(file, $"veri/{folder}/{Path.GetFileName(file)}", CompressionLevel.Fastest);
            }
        }
        return Results.File(output.ToArray(), "application/zip", $"{folder}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
    }
}

// ---------- IP kamera (Dahua, RTSP) ----------
// Arayuz kameradan taze bir kare alir (/api/kamera/kare) ve onu fotograf gibi /api/olc'ye gonderir; boylece
// cizim, kayit ve tum olcum yollari ayni kalir. Sifre yalniz bellekte (IpCamera), hicbir yere yazilmaz.
var camera = new IpCamera();
app.Lifetime.ApplicationStopping.Register(() => camera.Dispose());

app.MapGet("/api/kamera", () => Results.Json(camera.Status()));

app.MapPost("/api/kamera", async (HttpRequest req) =>
{
    IFormCollection form = await req.ReadFormAsync();
    int port = int.TryParse(form["port"], out int pp) && pp > 0 && pp < 65536 ? pp : 554;
    int channel = int.TryParse(form["kanal"], out int ch) && ch > 0 ? ch : 1;
    int subtype = form["akis"] == "1" ? 1 : 0;
    try { camera.Configure(form["adres"], port, form["kullanici"], form["sifre"], channel, subtype); }
    catch (ArgumentException e) { return Fail(e.Message); }
    // Ilk kareyi kisa bir sure bekle: arayuz hemen "bagli" ya da hatayi gostersin
    using (Mat first = camera.FreshFrame(TimeSpan.FromSeconds(8))) { }
    return Results.Json(camera.Status());
});

app.MapPost("/api/kamera/kapat", () => { camera.Stop(); return Results.Json(camera.Status()); });

// Taze kare (JPEG). Olcum icin kalite yuksek; onizleme icin "onizleme=1" kucuk ve hizli.
app.MapGet("/api/kamera/kare", (HttpRequest req) =>
{
    using Mat frame = camera.FreshFrame(TimeSpan.FromSeconds(5));
    if (frame == null) return Results.Json(new { hata = "Kameradan kare alınamadı. Kamera bağlı mı?" }, statusCode: 503);
    bool preview = req.Query["onizleme"] == "1";
    if (preview && frame.Cols > 960)
    {
        using var small = new Mat();
        Cv2.Resize(frame, small, new Size(), 960.0 / frame.Cols, 960.0 / frame.Cols, InterpolationFlags.Area);
        return Results.File(small.ImEncode(".jpg", new ImageEncodingParam(ImwriteFlags.JpegQuality, 80)), "image/jpeg");
    }
    return Results.File(frame.ImEncode(".jpg", new ImageEncodingParam(ImwriteFlags.JpegQuality, preview ? 80 : 95)), "image/jpeg");
});

// ---------- Kamera kalibrasyonu (lens + tabla, bir kez) ----------
// 6 kalibrasyon etiketi (4 kose + uzun kenarlarin ortasi) ile lens bukulmesi ve kameranin tablaya gore konumu
// hesaplanir, veri/kalibrasyon/lens.txt dosyasina yazilir. Olcumde ("kalibrasyon=1") fotograf bu modelle duz
// ustten gorunume cevrilir. Kamera, odak ya da yerlesim degisirse yeniden yapilir.
string lensDir = Path.Combine(dataDir, "kalibrasyon");
string lensPath = Path.Combine(lensDir, "lens.txt");
string calibrationPhotoPath = Path.Combine(lensDir, "kalibrasyon.jpg");
string emptyPath = Path.Combine(lensDir, "bos.jpg");
// Kameranin lensi (odak, bukulme) ve konumu: koselerin de gorundugu son 6 damali kalibrasyondan. Mavi paspasli
// kalibrasyonda kose damalari paspasin altinda kalir ve lens o fotograftan cozulemez; buradan alinir. "Sil" bunu silmez
// (lens kameranin ozelligidir; tabla kalibrasyonu silinse de gecerli).
string cameraLensPath = Path.Combine(lensDir, "kamera-lens.txt");
// L kose kalibrasyonunun inceleme goruntusu (dort kose buyutulmus, bulunan kenarlar cizili)
string lCornerViewPath = Path.Combine(lensDir, "l-koseler.jpg");
LensCalibration LoadCameraLens()
{
    try { return File.Exists(cameraLensPath) ? LensCalibration.Load(cameraLensPath) : null; }
    catch (Exception e) when (e is FormatException || e is IOException || e is OverflowException) { return null; }
}
LensCalibration lensCache = null;
DateTime lensCacheTime = DateTime.MinValue;
LensCalibration LoadLens()
{
    if (!File.Exists(lensPath)) { lensCache = null; return null; }
    DateTime w = File.GetLastWriteTimeUtc(lensPath);
    if (lensCache == null || w != lensCacheTime)
    {
        try { lensCache = LensCalibration.Load(lensPath); lensCacheTime = w; }
        catch (Exception e) when (e is FormatException || e is IOException || e is OverflowException) { lensCache = null; }
    }
    return lensCache;
}
// Tabla kenari boyunca noktalar (mm): kose sirasi (0,0) -> (0,W) -> (L,W) -> (L,0), kenar basina n parca
static Point2[] TableBorder(double l, double w, int n)
{
    var c = new[] { new Point2(0, 0), new Point2(0, w), new Point2(l, w), new Point2(l, 0) };
    var pts = new List<Point2>();
    for (int e = 0; e < 4; e++)
        for (int i = 0; i < n; i++) pts.Add(c[e] + (c[(e + 1) % 4] - c[e]) * ((double)i / n));
    return pts.ToArray();
}
// Fotografta cizilecek tabla siniri: L kose kalibrasyonunda kalibrasyon alani (alt ve sol kenardan, CalibrationArea),
// digerlerinde duzeltmenin oturttugu koseler.
static Point2[] TableOutline(LensCalibration c, int n)
{
    if (c.Correction == null || c.Correction.True.Length < 4)
        return TableBorder(c.TableLengthMm, c.TableWidthMm, n);
    Point2[] t = c.CornerSource == LensCalibration.CornerSourceL ? CalibrationArea(c.Correction.True) : c.Correction.True;
    Point2[] k = { t[2], t[0], t[1], t[3] };   // sol alt, sol ust, sag ust, sag alt
    var pts = new List<Point2>();
    for (int e = 0; e < 4; e++)
        for (int i = 0; i < n; i++) pts.Add(k[e] + (k[(e + 1) % 4] - k[e]) * ((double)i / n));
    return pts.ToArray();
}
static CalibrationTag LensTag(string name) => CalibrationTag.All.FirstOrDefault(t => t.Name == name) ?? CalibrationTag.Normal;
// Damalarin varsayilan (ya da elle olculmus) merkezleri
static Point2[] ExpectedCenters(LensCalibration c) => c.MeasuredTagCenters ?? LensCalibrator.TagCenters(c.TableLengthMm, c.TableWidthMm, LensTag(c.TagType));
// Olcumde damalarin kapladigi alanlar: bulunan gercek yerleri (varsa)
static Point2[] TagCentersForMasking(LensCalibration c) =>
    c.BoardPlacements?.Select((p, i) => double.IsNaN(p.X) ? ExpectedCenters(c)[i] : p.Center).ToArray() ?? c.MeasuredTagCenters;
object LensStatus(LensCalibration c) => c == null ? new { var = false } as object : new
{
    var = true,
    tarih = c.Created.ToString("dd.MM.yyyy HH:mm"),
    tabla = new { uzunluk = c.TableLengthMm, genislik = c.TableWidthMm },
    goruntu = new { w = c.ImageWidth, h = c.ImageHeight },
    hataPx = Math.Round(c.RmsPx, 2),
    ortalamaMm = Math.Round(c.MeanMm, 2),
    enKotuMm = Math.Round(c.MaxMm, 2),
    referansUygun = c.MeetsReferenceTolerance,
    olculenDamaMerkezleri = c.MeasuredTagCenters?.Select(p => new[] { p.X, p.Y }),
    nokta = c.Points,
    etiket = c.TagType,
    kareMm = LensTag(c.TagType).SquareMm,
    // Damalarin bulunan gercek yeri: beklenen yerden fark (mm) ve aci (derece), ust uc ve alt uc soldan saga
    damaYerlesimi = c.BoardPlacements == null ? null : c.BoardPlacements.Select((p, i) => (p, i)).Where(q => !double.IsNaN(q.p.X)).Select(q => new
    {
        no = q.i + 1,
        dx = Math.Round(q.p.X - ExpectedCenters(c)[q.i].X, 1),
        dy = Math.Round(q.p.Y - ExpectedCenters(c)[q.i].Y, 1),
        aci = Math.Round(q.p.RotationDeg, 2)
    }),
    // Tabla koseleri: "L" (L kose parcalarinin dis koseleri), "kagit" (kose kagitlari) ya da eski "mavi" (paspas)
    koseKaynagi = c.CornerSource,
    // Seritle girilen tabla kenarlari (L koseleri arasi)
    seritTabla = c.Correction == null || c.PaperCorners == null || c.PaperCorners.Length != 4 ? null : new
    {
        alt = Math.Round(Point2.Distance(c.PaperCorners[2], c.PaperCorners[3]), 1),
        ust = Math.Round(Point2.Distance(c.PaperCorners[0], c.PaperCorners[1]), 1),
        sol = Math.Round(Point2.Distance(c.PaperCorners[2], c.PaperCorners[0]), 1),
        sag = Math.Round(Point2.Distance(c.PaperCorners[3], c.PaperCorners[1]), 1),
        kosegen = Math.Round(Point2.Distance(c.PaperCorners[2], c.PaperCorners[1]), 1)
        // sol ust -> sag alt kosegen yalniz karsilastirma icin girilir, saklanmaz (arayuzdeki deger korunur)
    },
    kameraLensi = File.Exists(cameraLensPath) ? File.GetLastWriteTime(cameraLensPath).ToString("dd.MM.yyyy HH:mm") : null,
    // Kose kagitlarinin dis koselerinin olusturdugu tabla (mm)
    kagitTabla = c.PaperCorners == null ? null : new
    {
        alt = Math.Round(Point2.Distance(c.PaperCorners[2], c.PaperCorners[3]), 1),
        ust = Math.Round(Point2.Distance(c.PaperCorners[0], c.PaperCorners[1]), 1),
        sol = Math.Round(Point2.Distance(c.PaperCorners[2], c.PaperCorners[0]), 1),
        sag = Math.Round(Point2.Distance(c.PaperCorners[3], c.PaperCorners[1]), 1),
        kosegenFarki = Math.Round(Math.Abs(Point2.Distance(c.PaperCorners[2], c.PaperCorners[1]) - Point2.Distance(c.PaperCorners[3], c.PaperCorners[0])), 1),
        enBuyukSapma = Math.Round(c.PaperCornerDeviationMm(), 1),
        koseler = c.PaperCorners.Select(P)
    },
    bosTabla = File.Exists(emptyPath),
    bosTablaTarih = File.Exists(emptyPath) ? File.GetLastWriteTime(emptyPath).ToString("dd.MM.yyyy HH:mm") : null,
    kamera = new { x = Math.Round(c.CameraPosition().X), y = Math.Round(c.CameraPosition().Y), z = Math.Round(c.CameraPosition().Height) }
};

// Duz ustten gorunum icin sentetik "kamera": tabla duzlemine tam yukaridan bakan, bukulmesiz, 1 piksel = 1 mm
LensCalibration FlatTopCalibration(double lengthMm, double widthMm) => new LensCalibration
{
    TableLengthMm = lengthMm, TableWidthMm = widthMm, ImageWidth = (int)lengthMm, ImageHeight = (int)widthMm,
    Fx = 5000, Fy = 5000, Cx = 0, Cy = 0, Distortion = new double[5],
    Rvec = new[] { Math.PI, 0, 0 }, Tvec = new[] { 0, widthMm, 5000.0 }, TagType = "duz"
};

// Kalibrasyonlu olcum: bos tabla fotografi varsa karsilastirmayla
MeasureOutput RunLens(Mat frame, LensCalibration c, MarkerLayout tags, DetectorOptions options, double thickness, string emptyFile = null)
{
    emptyFile ??= emptyPath;
    using Mat empty = File.Exists(emptyFile) ? Cv2.ImRead(emptyFile) : null;
    return TableMeasure.RunWithLens(frame, c, tags, options, thickness, empty);
}

app.MapGet("/api/kalibrasyon", () => { lock (gate) return Results.Json(LensStatus(LoadLens())); });

// Canli kalibrasyon ekrani icin: kayitli kalibrasyona gore tablanin kenari ve ortasi kamera pikselinde (kamera ve
// tablayi hizalamak icin; kalibrasyon yoksa yalniz goruntunun ortasi cizilir)
app.MapGet("/api/kalibrasyon/cerceve", () =>
{
    lock (gate)
    {
        LensCalibration c = LoadLens();
        if (c == null) return Results.Json(new { var = false });
        double L = c.TableLengthMm, W = c.TableWidthMm;
        return Results.Json(new
        {
            var = true,
            goruntu = new { w = c.ImageWidth, h = c.ImageHeight },
            tablaKenari = LensCalibrator.TableToImage(c, TableBorder(L, W, 40)).Select(P),
            tablaMerkezi = P(LensCalibrator.TableToImage(c, new[] { new Point2(L / 2, W / 2) })[0]),
            tabla = new { uzunluk = L, genislik = W }
        });
    }
});

app.MapGet("/api/kalibrasyon/kaydet", () =>
    SaveCalibrationArchive("kalibrasyon", lensPath, calibrationPhotoPath, cameraLensPath, emptyPath));

app.MapGet("/api/kalibrasyon/incele", () =>
{
    lock (gate)
    {
        LensCalibration cal = LoadLens();
        // L kose kalibrasyonunda kalibrasyon fotografi (L'ler gorunur: cerceve L'lerin dis koselerinden gecmeli);
        // eski yontemlerde varsa bos tabla fotografi
        bool fromEmpty = File.Exists(emptyPath) && !(cal?.CornerSource == LensCalibration.CornerSourceL && File.Exists(calibrationPhotoPath));
        string shown = fromEmpty ? emptyPath : calibrationPhotoPath;
        if (cal == null || !File.Exists(shown))
            return Fail("İncelenecek kayıtlı kalibrasyon fotoğrafı bulunamadı.");

        using Mat frame = Cv2.ImRead(shown);
        if (frame.Empty() || frame.Cols != cal.ImageWidth || frame.Rows != cal.ImageHeight)
            return Fail("Kayıtlı fotoğraf bu kalibrasyonun görüntü boyutuyla uyuşmuyor.");

        double length = cal.TableLengthMm, width = cal.TableWidthMm;
        double scale = 1200 / Math.Max(length, width);
        if (!double.IsFinite(scale) || length * scale < 1 || width * scale < 1)
            return Fail("Kalibrasyondaki tabla ölçüleriyle üstten görünüm oluşturulamıyor.");

        using Mat top = LensCalibrator.TopView(frame, cal, scale);
        double[] ValidPoint(Point2 p) => double.IsFinite(p.X) && double.IsFinite(p.Y) ? P(p) : null;
        string Jpeg(Mat image) => "data:image/jpeg;base64," + Convert.ToBase64String(image.ToBytes(".jpg", new ImageEncodingParam(ImwriteFlags.JpegQuality, 88)));
        return Results.Json(new
        {
            kalibrasyon = LensStatus(cal),
            fotoKaynagi = fromEmpty ? "bos" : "kalibrasyon",
            foto = Jpeg(frame),
            // L kose kalibrasyonunun inceleme goruntusu (dort kose buyutulmus, bulunan kenarlar cizili)
            lKoseler = cal.CornerSource == LensCalibration.CornerSourceL && File.Exists(lCornerViewPath)
                ? "data:image/jpeg;base64," + Convert.ToBase64String(File.ReadAllBytes(lCornerViewPath)) : null,
            ustten = Jpeg(top),
            cerceve = LensCalibrator.TableToImage(cal, TableOutline(cal, 40)).Select(ValidPoint),
            sifir = ValidPoint(LensCalibrator.TableToImage(cal, new[] { new Point2(0, 0) })[0]),
            // Tabla kosesi kabul edilen noktalar: fotografta ve tablada (L kose kalibrasyonunda tablanin gercek koseleri)
            kagitKoseleri = (cal.Correction?.True.Take(4).ToArray() ?? cal.PaperCorners) is Point2[] corners ? new
            {
                foto = LensCalibrator.TableToImage(cal, corners).Select(ValidPoint),
                tabla = corners.Select(ValidPoint)
            } : null
        });
    }
});

app.MapPost("/api/kalibrasyon", async (HttpRequest req) =>
{
    IFormCollection form = await req.ReadFormAsync();
    byte[] bytes = await ReadFile(form);
    if (bytes == null) return Fail("Fotoğraf gelmedi.");
    double Num(string key) => double.TryParse(form[key].ToString().Replace(',', '.'), NumberStyles.Float, inv, out double v) && double.IsFinite(v) ? v : double.NaN;
    // L kose parcalariyla kalibrasyon: tablanin dort kenari ve kosegenler (L'lerin dis koseleri arasi) ayri ayri girilir
    if (form.ContainsKey("alt"))
    {
        // Kamera merceginden tablaya dik mesafe (kullanici olcumu 2030-2035 mm, 2026-11-26)
        double measuredHeight = form.ContainsKey("kameraYukseklikMm") ? Num("kameraYukseklikMm") : DefaultCameraHeightMm;
        if (!(measuredHeight >= 100 && measuredHeight <= 20000))
            return Fail("Kamera ile tabla arasındaki dik mesafeyi mm olarak girin.");
        return CalibrateWithLCorners(bytes, SidesFromForm(Num), measuredHeight);
    }
    double length = double.TryParse(form["uzunluk"], NumberStyles.Float, inv, out double l) && l > 0 ? l : 0;
    double width = double.TryParse(form["genislik"], NumberStyles.Float, inv, out double w) && w > 0 ? w : 0;
    if (!double.IsFinite(length) || !double.IsFinite(width) || length <= 0 || width <= 0)
        return Fail("Tablanın uzunluğunu ve genişliğini girin.");
    Point2[] measuredCenters = null;
    string geometry = form["damaMerkezleri"].ToString().Trim();
    if (geometry.Length > 0)
    {
        string[] rows = geometry.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (rows.Length != 6) return Fail("Dama merkezlerini 1–6 sırasıyla, her satıra bir X;Y çifti olarak girin.");
        measuredCenters = new Point2[6];
        bool Coordinate(string s, out double value) =>
            double.TryParse(s.Trim().Replace(',', '.'), NumberStyles.Float, inv, out value) && double.IsFinite(value);
        for (int i = 0; i < rows.Length; i++)
        {
            string[] pair = rows[i].Split(';');
            if (pair.Length != 2 || !Coordinate(pair[0], out double x) || !Coordinate(pair[1], out double y)
                || x <= 0 || x >= length || y <= 0 || y >= width)
                return Fail($"{i + 1} numaralı dama için tabla içinde bir X;Y merkezi girin (mm).");
            measuredCenters[i] = new Point2(x, y);
        }
        if (new[] { 0, 1, 3, 4 }.Any(i => measuredCenters[i].X >= measuredCenters[i + 1].X)
            || measuredCenters.Take(3).Min(p => p.Y) <= measuredCenters.Skip(3).Max(p => p.Y))
            return Fail("Dama merkezlerini önce üst sırada 1–3, sonra alt sırada 4–6, her sırada soldan sağa girin. Y sol alttan yukarı artar.");
    }
    double? cameraHeight = form.ContainsKey("kameraYukseklikMm") ? Num("kameraYukseklikMm") : null;
    if (cameraHeight.HasValue && !(cameraHeight.Value >= 100 && cameraHeight.Value <= 20000))
        return Fail("Kamera ile tabla arasındaki dik mesafeyi mm olarak girin.");
    string type = form["etiket"].ToString();
    CalibrationTag[] tags = type == "otomatik" ? CalibrationTag.All : new[] { LensTag(type) };
    lock (gate)
    {
        using Mat frame = Cv2.ImDecode(bytes, ImreadModes.Color);
        if (frame.Empty()) return Fail("Dosya bir görüntü olarak okunamadı.");
        LensCalibrator.Result best = null;
        foreach (CalibrationTag tag in tags)
        {
            LensCalibrator.Result r = LensCalibrator.Calibrate(frame, length, width, tag, measuredCenters, cameraHeight);
            if (r.Calibration != null && r.Calibration.MeetsReferenceTolerance) { best = r; break; }
            if (best == null || (r.Calibration != null
                ? best.Calibration == null || r.Calibration.MaxMm < best.Calibration.MaxMm
                : best.Calibration == null && r.TagsFound > best.TagsFound))
                best = r;
        }
        if (best.Calibration == null)
            return Fail(best.Error.Replace("Kalibrasyon icin 6 etiket gerekli", "Kalibrasyon için 6 etiket gerekli").Replace(" bulundu. Dort koseye ve iki uzun kenarin ortasina birer etiket koyun; hepsi fotografta net gorunmeli.", " bulundu. Dört köşeye ve iki uzun kenarın ortasına birer etiket koyun; hepsi fotoğrafta net görünmeli.")
                .Replace("Etiketler iki sira (ust ve alt uzun kenar) olarak gorunmuyor. Kamera tablayi uzun kenari yatay gorecek sekilde bakmali.", "Etiketler iki sıra (üst ve alt uzun kenar) olarak görünmüyor. Kamera tablayı uzun kenarı yatay görecek şekilde bakmalı."));
        if (!best.Calibration.MeetsReferenceTolerance)
        {
            int worstTag = Array.IndexOf(best.TagErrorMm, best.TagErrorMm.Where(double.IsFinite).DefaultIfEmpty(double.NaN).Max()) + 1;
            return Fail($"Kalibrasyon kaydedilmedi. Ort. hata {best.Calibration.MeanMm:F2} mm, en büyük nokta hatası {best.Calibration.MaxMm:F2} mm (sınır {LensCalibration.MaxReferenceErrorMm:F0} mm). "
                + $"Önce {worstTag} numaralı damayı kontrol edin (üst sıra 1–3, alt sıra 4–6). "
                + "Baskı boyutunu, tabla ölçülerini ve dama merkezlerini doğrulayın. Varsa önceki kayıt korundu.");
        }
        Directory.CreateDirectory(lensDir);
        best.Calibration.Save(lensPath);
        // Koseler de gorundu (6 dama): lens ve kamera konumu ileride L kose kalibrasyonunda kullanilmak uzere saklanir
        if (best.Calibration.CornerSource == LensCalibration.CornerSourcePaper && measuredCenters == null)
            best.Calibration.Save(cameraLensPath);
        File.WriteAllBytes(calibrationPhotoPath, bytes);
        SaveCalibrationTopView(frame, best.Calibration);
        // Yeni kalibrasyon: kamera oynamis olabilir, eski bos tabla fotografi artik ayni goruntuyu vermez
        if (File.Exists(emptyPath)) File.Delete(emptyPath);
        LensCalibration saved = LoadLens();
        var uyarilar = new List<string>();
        double cornerDev = saved.PaperCornerDeviationMm();
        if (!double.IsNaN(cornerDev) && cornerDev > LensCalibration.MaxReferenceErrorMm)
        {
            Point2[] pc = saved.PaperCorners;
            uyarilar.Add($"Köşe kâğıtlarının dış köşeleri {length:F0} × {width:F0} mm dikdörtgen vermiyor (en büyük fark {cornerDev:F1} mm): "
                + $"alt {Point2.Distance(pc[2], pc[3]):F1}, üst {Point2.Distance(pc[0], pc[1]):F1}, sol {Point2.Distance(pc[2], pc[0]):F1}, sağ {Point2.Distance(pc[3], pc[1]):F1} mm. "
                + "Sıfır noktası sol alt kâğıdın köşesi, X ekseni sağ alt kâğıdın köşesine doğru alındı; parça ölçüleri bundan etkilenmez. "
                + "Konumların da tam olması için köşe kâğıtlarını tabla köşesine tam dayayın ya da tablanın gerçek ölçüsünü girin.");
        }
        return Results.Json(new { kalibrasyon = LensStatus(saved), etiketHatasiMm = best.TagErrorMm.Select(e => double.IsFinite(e) ? (double?)Math.Round(e, 2) : null), uyarilar });
    }
});

// Kalibrasyonun duzeltilmis (ustten) goruntusu: kalibrasyon\ustten.jpg, olcum goruntusuyle ayni alan ve olcek
// (3300 x 2000 mm, 1 piksel = 1 mm, sol alt 0,0), tabla yuzeyi duzleminde. Disk doluysa kalibrasyonu bozmaz.
void SaveCalibrationTopView(Mat frame, LensCalibration cal)
{
    try
    {
        using Mat top = LensCalibrator.TopViewAt(frame, cal, DisplayLengthMm, DisplayWidthMm, 1.0, 0);
        File.WriteAllBytes(Path.Combine(lensDir, "ustten.jpg"), top.ToBytes(".jpg", new ImageEncodingParam(ImwriteFlags.JpegQuality, 90)));
    }
    catch (IOException) { }
}

// Formdan seritle olculen tabla: dort kenar ve sol alt -> sag ust kosegen (L parcalarinin dis koseleri arasi, mm)
static TableSides SidesFromForm(Func<string, double> num) => new TableSides
{
    Bottom = num("alt"), Top = num("ust"), Left = num("sol"), Right = num("sag"), Diagonal = num("kosegen")
};

// Girilen kenarlarin kontrolu ve tabla koseleri (sol ust, sag ust, sol alt, sag alt); hatada null
static Point2[] TapeCorners(TableSides s, out string error)
{
    foreach (var (v, name) in new[] { (s.Bottom, "alt"), (s.Top, "üst"), (s.Left, "sol"), (s.Right, "sağ") })
        if (double.IsNaN(v) || v < 500 || v > 10000) { error = $"Tablanın {name} kenarını mm olarak girin (L parçalarının dış köşeleri arası)."; return null; }
    if (!double.IsNaN(s.Diagonal) && s.Diagonal <= 0) s.Diagonal = double.NaN;
    return s.Corners(out error);
}

// Kamera modelinin (lens + konum) fotografta L'lerden buldugu dort tabla kosesi (measured, modelin mm'si) gercek tabla
// koselerine oturtulur. tape: seritle olculen L dis koseleri; tablanin koseleri bunlarin wallMm icinde (L duvari).
// edgeCurve: kenar egrileri (artik null: kenarlar koseden koseye duz). Ayni kalibrasyonda serit olculeri degisince
// fotograf gerekmeden yeniden kurulur (koseler ayni kalir).
static LensCalibration WithTape(LensCalibration cal, Point2[] measured, Point2[] tape, double[] edgeCurve, double wallMm)
{
    LensCalibration c = cal.WithoutCorrection();
    Point2 center = LensCalibrator.ImageToTable(c, new[] { new Point2(c.Cx, c.Cy) })[0];
    // Piksel -> mm: dort gercek kose (perspektifi ancak dort kose duzeltir; uc koseyle +-50 mm bozuluyordu)
    Point2[] table = wallMm > 0 ? TableSides.Inset(tape, wallMm) : tape;
    c.Correction = new TableCorrection(measured, table, center, 0, edgeCurve);
    c.ResidualRadial = 0;
    c.LWallMm = wallMm;
    c.PaperCorners = (Point2[])tape.Clone();
    // Kalibrasyon alani (kullanici karari, Oneri 2): alt kenar ve sol kenardan; bkz. CalibrationArea
    Point2[] area = CalibrationArea(table);
    c.TableLengthMm = Math.Round(area[3].X, 1);
    c.TableWidthMm = Math.Round(area[0].Y, 1);
    return c;
}

// Kalibrasyon alani (kullanici karari "Oneri 2", 2026-11-25): alt kenar (sol alt -> sag alt) aynen yukari, sol kenar
// (sol alt -> sol ust) aynen saga tasinir; sag ust kose = sag alt + sol kenar. Tablanin gercek sag ust kosesi
// kullanilmaz: ust ve sag kenara yakin ince serit kalibrasyon alaninin disinda kalabilir. Sira: sol ust, sag ust,
// sol alt, sag alt.
static Point2[] CalibrationArea(Point2[] table)
{
    Point2 tl = table[0], bl = table[2], br = table[3];
    return new[] { tl, br + (tl - bl), bl, br };
}

// L + dama kalibrasyonu: lens ve kamera konumu dort kose damasindan (CheckerLensCalibrator), tabla koseleri bu lensle
// L parcalarinin dis kenarlarindan; seritle olculen dis koseler tabla cercevesini verir.
IResult CalibrateWithLCorners(byte[] bytes, TableSides sides, double heightMm)
{
    Point2[] tape = TapeCorners(sides, out string tapeError);
    if (tape == null) return Fail(tapeError);
    lock (gate)
    {
        using Mat frame = Cv2.ImDecode(bytes, ImreadModes.Color);
        if (frame.Empty()) return Fail("Dosya bir görüntü olarak okunamadı.");
        Directory.CreateDirectory(lensDir);
        File.WriteAllBytes(Path.Combine(lensDir, "son-deneme.jpg"), bytes);
        var fit = CheckerLensCalibrator.Calibrate(frame, tape, heightMm);
        if (fit.Lens == null) return Fail(fit.Error + " Fotoğraf incelenmek üzere kaydedildi.");
        LensCalibration raw = fit.Lens;
        LCornerFinder.Result found = fit.Corners;
        // Tabla koseleri L'nin dis kenarlarinin duvar kalinligi kadar icinde. Kenarlar koseden koseye duz tabla kenari
        // (kullanici karari 2026-11-25: tahtalardaki cikintilara gore kenar egrisi oturtulmaz; TableEdgeCurve kullanilmiyor)
        double wall = LCornerFinder.WallMm;
        Point2[] inner = LCornerFinder.InsetCorners(found, wall);
        LensCalibration cal = WithTape(raw, inner, tape, null, wall);
        // Denetim: damalarin bu lensle olculen kare boyu (gercek 25 mm) ve L dis koselerinin seritten farki
        cal.MeanMm = fit.BoardShapeMm.Average();
        cal.MaxMm = fit.BoardShapeMm.Max();
        cal.RmsPx = raw.RmsPx;
        double[] tapeGap = found.Corners.Select((p, i) => Point2.Distance(p, tape[i])).ToArray();
        cal.CornerSource = LensCalibration.CornerSourceL;
        cal.TagType = "L";
        cal.Created = DateTime.Now;
        cal.Save(lensPath);
        cal.Save(cameraLensPath);
        File.WriteAllBytes(calibrationPhotoPath, bytes);
        SaveCalibrationTopView(frame, cal);
        // Inceleme: dort kosenin buyutulmus goruntusu, bulunan kenar noktalari, oturtulan kenar dogrulari ve kose
        try
        {
            using Mat view = LCornerFinder.DrawCorners(frame, raw, found, wall);
            File.WriteAllBytes(lCornerViewPath, view.ToBytes(".jpg", new ImageEncodingParam(ImwriteFlags.JpegQuality, 92)));
        }
        catch (IOException) { }
        LensCalibration saved = LoadLens();
        Point2[] q = found.Corners;
        string[] names = LCornerFinder.CornerNames;
        Console.WriteLine(string.Format(inv, "L koseleri bulundu (modelde alt/ust {0:F0}/{1:F0}, sol/sag {2:F0}/{3:F0} mm): {4}", Point2.Distance(q[2], q[3]), Point2.Distance(q[0], q[1]),
            Point2.Distance(q[2], q[0]), Point2.Distance(q[3], q[1]),
            string.Join("; ", found.Fits.Select((f, i) => string.Format(inv, "{0} uzun {1}/{2} {3:F2} mm, kisa {4}/{5} {6:F2} mm, aci {7:F2}", names[i], f.InliersX, f.SamplesX, f.RmsX, f.InliersY, f.SamplesY, f.RmsY, f.AngleDeg)))));
        var uyarilar = new List<string>();
        string Mm(IEnumerable<double> v) => string.Join(" / ", v.Select(x => double.IsFinite(x) ? x.ToString("F2", tr) : "-"));
        uyarilar.Add($"Lens dört köşe damasından, {heightMm.ToString("F1", tr)} mm kamera yüksekliğiyle hesaplandı (sapma {raw.RmsPx.ToString("F2", tr)} piksel, optik merkez {raw.Cx.ToString("F0", tr)}, {raw.Cy.ToString("F0", tr)}).");
        uyarilar.Add($"Denetim — damaların bu lensle ölçülen karesi (gerçek 25 mm, sırayla sol üst / sağ üst / sol alt / sağ alt): {Mm(fit.BoardSquareMm)} mm; "
            + $"lens öbür üç damadan çözülünce: {Mm(fit.HoldoutSquareMm)} mm.");
        uyarilar.Add($"L dış köşelerinin şerit köşelerinden farkı (lens ve konum yalnız damalardan; rijit oturtma sonrası): {Mm(tapeGap)} mm.");
        if (tapeGap.Max() > LensCalibration.MaxReferenceErrorMm)
            uyarilar.Add($"Bir L köşesi şerit ölçüsünden {tapeGap.Max().ToString("F1", tr)} mm farklı. Şerit ölçülerini ve L parçalarının köşeye oturduğunu kontrol edin.");
        if (fit.BoardSquareMm.Concat(fit.HoldoutSquareMm).Any(s => !(Math.Abs(s - CheckerLensCalibrator.SquareMm) <= 0.4)))
            uyarilar.Add("Dama kareleri 25 mm'den 0,4 mm'den fazla farklı ölçülüyor. Damaların düz yattığını ve baskının ölçekli olmadığını kontrol edin.");
        var weak =found.Fits.Select((f, i) => (f, i)).Where(x => x.f.RmsX > 2 || x.f.RmsY > 2 || x.f.InliersX < 0.7 * x.f.SamplesX || x.f.InliersY < 0.7 * x.f.SamplesY).ToList();
        if (weak.Count > 0)
            uyarilar.Add("Şu köşelerde L parçasının dış kenarı tam düz bulunamadı: " + string.Join("; ", weak.Select(x =>
                $"{names[x.i]} (uzun kenar {x.f.InliersX}/{x.f.SamplesX} nokta, sapma {x.f.RmsX:F1} mm; kısa kenar {x.f.InliersY}/{x.f.SamplesY} nokta, sapma {x.f.RmsY:F1} mm)"))
                + ". Parçanın köşeye tam oturduğunu ve dış kenarının önünde bir şey olmadığını kontrol edin.");
        uyarilar.Add(File.Exists(emptyPath)
            ? "Boş tabla fotoğrafı önceki kayıttan duruyor. Ölçüm sırasında tabla nasıl duracaksa (L parçaları kalacaksa onlarla) boş tablayı yeniden kaydedin."
            : "Kalibrasyon kaydedildi. Şimdi boş tablayı kaydedin.");
        return Results.Json(new
        {
            kalibrasyon = LensStatus(saved),
            lensKatsayilari = raw.Distortion,
            lensOptikMerkez = new[] { raw.Cx, raw.Cy },
            damaKaresiMm = fit.BoardSquareMm,
            damaKaresiDisaridaMm = fit.HoldoutSquareMm,
            seritFarkiMm = tapeGap,
            lKoseleri = found.Fits.Select((f, i) => new
            {
                kose = names[i], uzunNokta = f.InliersX, uzunProfil = f.SamplesX, uzunSapmaMm = Math.Round(f.RmsX, 2),
                kisaNokta = f.InliersY, kisaProfil = f.SamplesY, kisaSapmaMm = Math.Round(f.RmsY, 2), aci = Math.Round(f.AngleDeg, 2)
            }),
            uyarilar
        });
    }
}

// Serit olculeri degisti: L'lerden bulunan koseler ayni kalir, yalniz yeni tabla olculerine yeniden oturtulur
// (fotograf gerekmez). Arayuz ardindan son olcumu yeniden yapar.
app.MapPost("/api/kalibrasyon/tabla", async (HttpRequest req) =>
{
    IFormCollection form = await req.ReadFormAsync();
    double Num(string key) => double.TryParse(form[key].ToString().Replace(',', '.'), NumberStyles.Float, inv, out double v) && double.IsFinite(v) ? v : double.NaN;
    TableSides sides = SidesFromForm(Num);
    Point2[] tape = TapeCorners(sides, out string tapeError);
    if (tape == null) return Fail(tapeError);
    lock (gate)
    {
        LensCalibration c = LoadLens();
        if (c == null || c.Correction == null || c.CornerSource != LensCalibration.CornerSourceL)
            return Fail("Tabla ölçüleri L köşe kalibrasyonuna uygulanır; önce L parçaları yerindeyken kalibrasyon yapın.");
        LensCalibration cal = WithTape(c, c.Correction.Measured.Take(4).ToArray(), tape, null, c.LWallMm);
        cal.Save(lensPath);
        return Results.Json(new { kalibrasyon = LensStatus(LoadLens()) });
    }
});

// Bos tabla fotografi: L kose kalibrasyonundan sonra ayni kameradan, tabla olcumdeki haliyle bosken. Olcumde yalniz bos
// tabladan farkli yerler parca sayilir (ahsaptaki lekeler, cita aralari, delikler karismaz).
app.MapPost("/api/kalibrasyon/bos", async (HttpRequest req) =>
{
    IFormCollection form = await req.ReadFormAsync();
    byte[] bytes = await ReadFile(form);
    if (bytes == null) return Fail("Fotoğraf gelmedi.");
    lock (gate)
    {
        LensCalibration c = LoadLens();
        if (c == null) return Fail("Önce kalibrasyon yapın.");
        using Mat frame = Cv2.ImDecode(bytes, ImreadModes.Color);
        if (frame.Empty()) return Fail("Dosya bir görüntü olarak okunamadı.");
        if (frame.Cols != c.ImageWidth || frame.Rows != c.ImageHeight)
            return Fail($"Bu fotoğraf {frame.Cols}x{frame.Rows} piksel, kalibrasyon {c.ImageWidth}x{c.ImageHeight}. Aynı kameranın aynı akışından çekin.");
        Directory.CreateDirectory(lensDir);
        File.WriteAllBytes(emptyPath, bytes);
        return Results.Json(new { kalibrasyon = LensStatus(c) });
    }
});

// Tabla kalibrasyonunu ve boş tablayı siler. Kameranin lensi (kamera-lens.txt) kalir: yeni L kose kalibrasyonu lens ve
// kamera konumunu ondan alir.
app.MapPost("/api/kalibrasyon/sil", () =>
{
    lock (gate)
    {
        foreach (string f in new[] { emptyPath, lensPath, calibrationPhotoPath, Path.Combine(lensDir, "ustten.jpg"), lCornerViewPath })
            if (File.Exists(f)) File.Delete(f);
        lensCache = null;
        return Results.Json(LensStatus(null));
    }
});

// ---------- Damasiz kamera kalibrasyonu (sabit kamera, bir kez) ----------
// Kullanici fotografta 4 koseyi (istenirse tablanin ortasini da) isaretler; CornerCalibrator kamerayi cozer. Lens bilgisi
// damali kalibrasyondan (ayni kamera, ayni cozunurluk) alinir: lens kameranin ozelligidir; yalniz koselerden lens tahmini
// tablanin ortasindaki olcegi belirleyemez. Damali kalibrasyonun uzerine yazmamak icin ayri klasorde tutulur.
string cornerDir = Path.Combine(dataDir, "kalibrasyon-damasiz");
string cornerPath = Path.Combine(cornerDir, "lens.txt");
string cornerPhotoPath = Path.Combine(cornerDir, "kalibrasyon.jpg");
string cornerPointsPath = Path.Combine(cornerDir, "noktalar.txt");
string cornerEmptyPath = Path.Combine(cornerDir, "bos.jpg");
LensCalibration cornerCache = null;
DateTime cornerCacheTime = DateTime.MinValue;
LensCalibration LoadCornerCal()
{
    if (!File.Exists(cornerPath)) { cornerCache = null; return null; }
    DateTime t = File.GetLastWriteTimeUtc(cornerPath);
    if (cornerCache == null || t != cornerCacheTime)
    {
        try { cornerCache = LensCalibration.Load(cornerPath); cornerCacheTime = t; }
        catch (Exception e) when (e is FormatException || e is IOException || e is OverflowException) { cornerCache = null; }
    }
    return cornerCache;
}
Point2[] LoadCornerPoints()
{
    if (!File.Exists(cornerPointsPath)) return null;
    try { return ParsePoints(File.ReadAllText(cornerPointsPath)); } catch (FormatException) { return null; }
}
// Noktalar CornerCalibrator.TablePoints sirasinda: 4 kose, orta, alt/sag/ust/sol kenar ortasi; isaretlenmeyen "-"
Point2[] ParsePoints(string text)
{
    string[] parts = text.Split(';', StringSplitOptions.TrimEntries);
    if (parts.Length < 4 || parts.Length > CornerCalibrator.MaxPoints) throw new FormatException("4 ile 9 arası nokta gerekli.");
    return parts.Select(s =>
    {
        if (s == "-" || s == "") return new Point2(double.NaN, double.NaN);
        string[] xy = s.Split(',');
        if (xy.Length != 2 || !double.TryParse(xy[0], NumberStyles.Float, inv, out double x) || !double.TryParse(xy[1], NumberStyles.Float, inv, out double y)
            || !double.IsFinite(x) || !double.IsFinite(y))
            throw new FormatException("Nokta 'x,y' olmali: " + s);
        return new Point2(x, y);
    }).ToArray();
}
// Damasiz kalibrasyon icin lens: damali kalibrasyon (ayni goruntu boyutundaysa)
LensCalibration LensForCorners(int width, int height)
{
    foreach (LensCalibration l in new[] { LoadLens(), LoadCameraLens() })
        if (l != null && l.ImageWidth == width && l.ImageHeight == height) return l;
    return null;
}
object CornerStatus(LensCalibration c) => c == null ? new { var = false, lensVar = LoadLens() != null } as object : new
{
    var = true,
    tarih = c.Created.ToString("dd.MM.yyyy HH:mm"),
    tabla = new { uzunluk = c.TableLengthMm, genislik = c.TableWidthMm },
    goruntu = new { w = c.ImageWidth, h = c.ImageHeight },
    noktaHataPx = Math.Round(c.RmsPx, 1),
    noktaHataMm = Math.Round(c.MeanMm, 1),
    kenarMm = double.IsNaN(c.EdgeRmsMm) ? (double?)null : Math.Round(c.EdgeRmsMm, 1),
    lensKaynagi = c.TagType == "damasiz-lens" ? "damalı kalibrasyon" : "işaretlerden ve kenarlardan",
    lensVar = LoadLens() != null,
    bosTabla = File.Exists(cornerEmptyPath),
    bosTablaTarih = File.Exists(cornerEmptyPath) ? File.GetLastWriteTime(cornerEmptyPath).ToString("dd.MM.yyyy HH:mm") : null,
    kamera = new { x = Math.Round(c.CameraPosition().X), y = Math.Round(c.CameraPosition().Y), z = Math.Round(c.CameraPosition().Height) }
};

app.MapGet("/api/kalibrasyon-damasiz", () => { lock (gate) return Results.Json(CornerStatus(LoadCornerCal())); });

// Kalibre et: fotograf + 4/5 nokta (sol alt, sag alt, sag ust, sol ust, [orta]) + tabla olcusu
app.MapPost("/api/kalibrasyon-damasiz", async (HttpRequest req) =>
{
    IFormCollection form = await req.ReadFormAsync();
    byte[] bytes = await ReadFile(form);
    if (bytes == null) return Fail("Fotoğraf gelmedi.");
    double length = double.TryParse(form["uzunluk"], NumberStyles.Float, inv, out double l) && l > 0 ? l : 0;
    double width = double.TryParse(form["genislik"], NumberStyles.Float, inv, out double w) && w > 0 ? w : 0;
    if (!double.IsFinite(length) || !double.IsFinite(width) || length <= 0 || width <= 0) return Fail("Tablanın uzunluğunu ve genişliğini girin.");
    Point2[] points;
    try { points = ParsePoints(form["noktalar"].ToString()); } catch (FormatException e) { return Fail("Noktalar okunamadı: " + e.Message); }
    lock (gate)
    {
        using Mat frame = Cv2.ImDecode(bytes, ImreadModes.Color);
        if (frame.Empty()) return Fail("Dosya bir görüntü olarak okunamadı.");
        LensCalibration lens = LensForCorners(frame.Cols, frame.Rows);
        // Lens bilinmiyorsa yalniz kose + kenarlar lensi belirleyemez (sanal ve gercek denemede k1 yanlis isaretli, A4 %45
        // buyuk cikti); kenar ortalari sart. Damali kalibrasyondan lens varsa 4 kose yeter.
        if (lens == null && points.Skip(5).Count(q => !double.IsNaN(q.X)) < 4)
            return Fail("Bu kamera için lens bilgisi yok (damalı kalibrasyon yapılmamış), bu yüzden dört kenar ortası da gerekli: tablada her kenarın tam ortasını (iki köşeye eşit uzaklıkta) işaretleyin, 6–9 numaralı yeşil işaretleri oraya sürükleyin ve yeniden Kalibre et’e basın.");
        CornerCalibrator.Result r = CornerCalibrator.Calibrate(frame, length, width, points, lens);
        if (r.Calibration == null) return Fail(r.Error);
        r.Calibration.TagType = r.UsedLens ? "damasiz-lens" : "damasiz";
        r.Calibration.EdgeRmsMm = r.EdgeRmsMm;
        Directory.CreateDirectory(cornerDir);
        r.Calibration.Save(cornerPath);
        File.WriteAllBytes(cornerPhotoPath, bytes);
        File.WriteAllText(cornerPointsPath, string.Join(";", points.Select(p => double.IsNaN(p.X) ? "-" : p.X.ToString("R", inv) + "," + p.Y.ToString("R", inv))));
        if (File.Exists(cornerEmptyPath)) File.Delete(cornerEmptyPath);   // kamera oynamis olabilir
        var uyarilar = new List<string>();
        double worstPx = r.PointErrorPx.Where(v => !double.IsNaN(v)).Max();
        if (worstPx > 8)
            uyarilar.Add($"İşaretlenen noktalardan biri modelden {worstPx:F0} piksel uzakta ({CornerCalibrator.PointNames[Array.IndexOf(r.PointErrorPx, worstPx)]}). Noktayı yakınlaştırıp tam yerine oturtun; kenar ortasıysa tablada gerçekten ortada mı ölçün.");
        return Results.Json(new { kalibrasyon = CornerStatus(LoadCornerCal()), uyarilar });
    }
});

// Inceleme: fotograf, isaretlenen noktalar, modelin bekledigi yerler, bulunan kenar noktalari, ustten gorunum
app.MapGet("/api/kalibrasyon-damasiz/kaydet", () =>
    SaveCalibrationArchive("kalibrasyon-damasiz", cornerPath, cornerPhotoPath, cornerPointsPath, cornerEmptyPath));

app.MapGet("/api/kalibrasyon-damasiz/incele", () =>
{
    lock (gate)
    {
        LensCalibration cal = LoadCornerCal();
        Point2[] points = LoadCornerPoints();
        if (cal == null || points == null || !File.Exists(cornerPhotoPath)) return Fail("Kayıtlı damasız kalibrasyon yok.");
        using Mat frame = Cv2.ImRead(cornerPhotoPath);
        if (frame.Empty() || frame.Cols != cal.ImageWidth || frame.Rows != cal.ImageHeight) return Fail("Kayıtlı fotoğraf kalibrasyonla uyuşmuyor.");
        double length = cal.TableLengthMm, width = cal.TableWidthMm;
        // Kenar noktalari yeniden bulunur (kayitli modelle): ne kadar oturdugu gorulsun
        CornerCalibrator.Result r = CornerCalibrator.Calibrate(frame, length, width, points, cal.TagType == "damasiz-lens" ? cal : null);
        double scale = 1200 / Math.Max(length, width);
        using Mat top = LensCalibrator.TopView(frame, cal, scale);
        double[] V(Point2 p) => double.IsFinite(p.X) && double.IsFinite(p.Y) ? P(p) : null;
        string Jpeg(Mat image) => "data:image/jpeg;base64," + Convert.ToBase64String(image.ToBytes(".jpg", new ImageEncodingParam(ImwriteFlags.JpegQuality, 88)));
        Point2[] model = LensCalibrator.TableToImage(cal, CornerCalibrator.TablePoints(length, width));
        return Results.Json(new
        {
            kalibrasyon = CornerStatus(cal),
            foto = Jpeg(frame),
            ustten = Jpeg(top),
            cerceve = LensCalibrator.TableToImage(cal, TableBorder(length, width, 40)).Select(V),
            noktalar = points.Select((p, i) => new { ad = CornerCalibrator.PointNames[i], isaret = V(p), model = V(model[i]), hataPx = double.IsNaN(p.X) ? (double?)null : Math.Round(Point2.Distance(p, model[i]), 1) }),
            kenarlar = r.Edges?.Select(e => new { p = V(e.Pixel), aykiri = e.Outlier, mm = Math.Round(e.OffsetMm, 1) })
        });
    }
});

// Kayitli kalibrasyon fotografi (noktalari yeniden duzeltmek icin, yeniden sikistirilmadan)
app.MapGet("/api/kalibrasyon-damasiz/foto", () =>
{
    lock (gate)
        return File.Exists(cornerPhotoPath) ? Results.File(File.ReadAllBytes(cornerPhotoPath), "image/jpeg") : Fail("Kayıtlı fotoğraf yok.");
});

app.MapPost("/api/kalibrasyon-damasiz/bos", async (HttpRequest req) =>
{
    IFormCollection form = await req.ReadFormAsync();
    byte[] bytes = await ReadFile(form);
    if (bytes == null) return Fail("Fotoğraf gelmedi.");
    lock (gate)
    {
        LensCalibration c = LoadCornerCal();
        if (c == null) return Fail("Önce damasız kalibrasyon yapın.");
        using Mat frame = Cv2.ImDecode(bytes, ImreadModes.Color);
        if (frame.Empty()) return Fail("Dosya bir görüntü olarak okunamadı.");
        if (frame.Cols != c.ImageWidth || frame.Rows != c.ImageHeight)
            return Fail($"Bu fotoğraf {frame.Cols}x{frame.Rows} piksel, kalibrasyon {c.ImageWidth}x{c.ImageHeight}. Aynı kameranın aynı akışından çekin.");
        File.WriteAllBytes(cornerEmptyPath, bytes);
        return Results.Json(CornerStatus(c));
    }
});

app.MapPost("/api/kalibrasyon-damasiz/sil", () =>
{
    lock (gate)
    {
        foreach (string f in new[] { cornerPath, cornerPhotoPath, cornerPointsPath, cornerEmptyPath }) if (File.Exists(f)) File.Delete(f);
        cornerCache = null;
        return Results.Json(CornerStatus(null));
    }
});

// Olcum: kayitli kalibrasyonla (lens + tabla, bir kez yapilir) duzeltilmis fotografta parcalarin koseleri (mm) ve ustten
// gorunum. "kalibrasyon": "1" damali kalibrasyon (veri/kalibrasyon), "2" damasiz kalibrasyon (veri/kalibrasyon-damasiz).
// Tabla olcusu kalibrasyondan gelir; her yontem kendi bos tabla fotografini kullanir.
app.MapGet("/api/olc/iz/{id}", (string id, bool? maske) =>
{
    if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"\A[0-9]{8}_[0-9]{6}_[0-9]{3}\z"))
        return Results.NotFound();
    string name = id + (maske == true ? "_maske.png" : ".txt");
    string path = Path.Combine(dataDir, "olcumler", "iz", name);
    return File.Exists(path)
        ? Results.File(path, maske == true ? "image/png" : "text/plain; charset=utf-8", name)
        : Results.NotFound();
});

// Operator kenar duzeltmesi (2026-11-25): arayuzde kenari kaydirilan parcalarin yeni koseleri olcum kaydina
// "operatorDuzeltmesi" olarak yazilir; olculen koseler kayitta aynen kalir. Bos liste duzeltmeyi kaldirir.
app.MapPost("/api/olc/duzelt", async (HttpRequest req) =>
{
    System.Text.Json.Nodes.JsonNode body;
    try { body = await System.Text.Json.Nodes.JsonNode.ParseAsync(req.Body); }
    catch (System.Text.Json.JsonException) { return Fail("Düzeltme okunamadı."); }
    string id = body?["kayit"]?.GetValue<string>();
    if (id == null || !System.Text.RegularExpressions.Regex.IsMatch(id, @"\A[0-9]{8}_[0-9]{6}_[0-9]{3}\z")) return Results.NotFound();
    string path = Path.Combine(dataDir, "olcumler", id + ".json");
    lock (gate)
    {
        if (!File.Exists(path)) return Results.NotFound();
        var record = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) as System.Text.Json.Nodes.JsonObject;
        if (record == null) return Fail("Ölçüm kaydı okunamadı.");
        var parts = body["parcalar"] as System.Text.Json.Nodes.JsonArray;
        if (parts == null || parts.Count == 0) record.Remove("operatorDuzeltmesi");
        else record["operatorDuzeltmesi"] = new System.Text.Json.Nodes.JsonObject
        {
            ["zaman"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["parcalar"] = parts.DeepClone()
        };
        File.WriteAllText(path, record.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
    return Results.Ok();
});

app.MapPost("/api/olc", async (HttpRequest req) =>
{
    IFormCollection form = await req.ReadFormAsync();
    byte[] bytes = await ReadFile(form);
    if (bytes == null) return Fail("Fotoğraf gelmedi.");
    double thickness = double.TryParse(form["kalinlik"], NumberStyles.Float, inv, out double t) && t >= 0 ? t : 20;
    bool uniformTable = form["tekrenk"] == "1";
    bool captureTrace = form["olcumIzi"] == "1";
    string mode = form["kalibrasyon"].ToString();
    if (mode != "1" && mode != "2") return Fail("Ölçüm yöntemi seçilmedi: Kalibrasyonlu ya da Damasız kalibrasyon.");
    bool cornerMode = mode == "2";
    double margin = double.TryParse(form["kenar"], NumberStyles.Float, inv, out double m) ? m : 40;
    lock (gate)
    {
        LensCalibration lensCal = cornerMode ? LoadCornerCal() : LoadLens();
        if (lensCal == null) return Fail(cornerMode ? "Damasız kalibrasyon yapılmamış. Önce Damasız kalibrasyon sekmesinde köşeleri işaretleyip kalibre edin."
            : "Kamera kalibrasyonu yapılmamış. Önce 6 kalibrasyon etiketli bir fotoğrafla kalibrasyon yapın.");
        double formLength = lensCal.TableLengthMm, formWidth = lensCal.TableWidthMm;   // tabla olcusu kalibrasyondan
        // Damali kalibrasyonda dama yerleri olcumden cikarilir (damalar tablada unutulduysa parca sanilmasin)
        // Paspasli kalibrasyonda tablada dama yok: hicbir alan olcumden cikarilmaz
        // Dama etiketlerinin alani yalniz damali kalibrasyonda olcumden cikarilir (L / paspas kalibrasyonunda tablada etiket yok)
        MarkerLayout layout = cornerMode || !CalibrationTag.All.Any(t => t.Name == lensCal.TagType) ? null : LensCalibrator.TagLayout(formLength, formWidth, LensTag(lensCal.TagType), TagCentersForMasking(lensCal));
        using Mat frame = Cv2.ImDecode(bytes, ImreadModes.Color);
        if (frame.Empty()) return Fail("Dosya bir görüntü olarak okunamadı.");

        // Kenar puruzu toleransi genis: kameraya bakan kenarda yan yuzey ve golge kenari dalgalandirir
        var options = new DetectorOptions
        {
            BorderMarginMm = margin,
            MinAreaMm2 = 60 * 60,
            NoReferenceBrightnessDiff = uniformTable ? 35 : 0,
            CleanupMm = uniformTable ? 8 : 25,   // tek renkli tablada gurultu az; kucuk parcalarin koseleri yuvarlanmasin
            Locator = new LocatorOptions { SimplifyToleranceMm = 30, MinEdgeMm = 150 },
            RefineEdges = form["parcaIncelt"] != "0",   // parca kenarlarini fotografta cok noktayla oturt (karsilastirma icin kapatilabilir)
            RefineTopEdge = form["ustKenar"] != "0"     // ust yuzey duzleminde kenar (TopEdgeRefiner); karsilastirma icin kapatilabilir
        };
        byte[] traceMask = null;
        if (captureTrace) options.CaptureTraceMask = png => traceMask = png;
        string method = cornerMode ? "damasiz-kalibrasyon" : "kalibrasyon";
        // Daha once kaydedilen duzeltilmis goruntu (3300 x 2000, 1 px = 1 mm, plakanin ust yuzeyi yuksekliginde) yeniden
        // yuklendiyse: zaten duz ustten gorunum, lens duzeltmesi yapilmaz. Bos tabla ayni sekilde duzeltilip referans olur.
        bool flat = !cornerMode && frame.Cols == (int)DisplayLengthMm && frame.Rows == (int)DisplayWidthMm
            && (lensCal.ImageWidth != frame.Cols || lensCal.ImageHeight != frame.Rows);
        LensCalibration geomCal = lensCal;
        MeasureOutput result;
        var sw = Stopwatch.StartNew();
        var trace = captureTrace ? MeasureTrace.Begin() : null;
        try
        {
        if (flat)
        {
            geomCal = FlatTopCalibration(DisplayLengthMm, DisplayWidthMm);
            formLength = DisplayLengthMm; formWidth = DisplayWidthMm;
            using Mat emptyRaw = File.Exists(emptyPath) ? Cv2.ImRead(emptyPath) : null;
            using Mat emptyFlat = emptyRaw != null && !emptyRaw.Empty() && emptyRaw.Cols == lensCal.ImageWidth && emptyRaw.Rows == lensCal.ImageHeight
                ? LensCalibrator.TopViewAt(emptyRaw, lensCal, DisplayLengthMm, DisplayWidthMm, 1.0, thickness) : null;
            result = TableMeasure.RunWithLens(frame, geomCal, null, options, 0, emptyFlat);
        }
        else result = RunLens(frame, lensCal, layout, options, thickness, cornerMode ? cornerEmptyPath : emptyPath);
        }
        finally { trace?.Dispose(); }
        using var resultHolder = result;
        bool calibrationTrusted = cornerMode || lensCal.MeetsReferenceTolerance;
        string traceText = null;
        if (trace != null)
        {
            try { traceText = BuildTrace(); }
            catch (IOException ex) { Console.Error.WriteLine("Ölçüm izi hazırlanamadı: " + ex.Message); }
            catch (UnauthorizedAccessException ex) { Console.Error.WriteLine("Ölçüm izi hazırlanamadı: " + ex.Message); }
        }

        string BuildTrace()
        {
            string usedEmpty = cornerMode ? cornerEmptyPath : emptyPath;
            var head = new System.Text.StringBuilder();
            head.AppendLine("protokol=KK-OI/1 ad=Köprü Kesim Ölçüm İzi");
            head.AppendLine("birimler: tabla_mm=(X sağa,Y yukarı), girdi_px=(X sağa,Y aşağı); iz profilleri tabla düzleminde, son köşeler parça üst yüzeyi düzlemindedir.");
            head.AppendLine("aday/oturtma satırları ara kararlardır; son karar ve köşeler sonuç bölümündedir.");
            head.AppendLine("sure_ms=" + sw.ElapsedMilliseconds);
            head.AppendLine("dosya_adi=" + (form.Files.GetFile("dosya")?.FileName ?? "-"));
            head.AppendLine("foto=" + frame.Cols + "x" + frame.Rows + " md5=" + Md5(bytes));
            head.AppendLine(string.Format(inv, "kalinlik={0} tekrenk={1} kalibrasyon={2} kenar={3} parcaIncelt={4} duz={5}", thickness, uniformTable ? 1 : 0, mode, margin, options.RefineEdges ? 1 : 0, flat ? 1 : 0));
            head.AppendLine(string.Format(inv, "secenek BorderMarginMm={0} MinAreaMm2={1} NoReferenceBrightnessDiff={2} CleanupMm={3} SimplifyToleranceMm={4} MinEdgeMm={5} RefineEdges={6} OuterMarginMm={7}",
                options.BorderMarginMm, options.MinAreaMm2, options.NoReferenceBrightnessDiff, options.CleanupMm, options.Locator.SimplifyToleranceMm, options.Locator.MinEdgeMm, options.RefineEdges, options.OuterMarginMm));
            head.AppendLine("bos_kullanildi=" + result.UsedReference);
            head.AppendLine("bos=" + (File.Exists(usedEmpty) ? usedEmpty + " " + File.GetLastWriteTime(usedEmpty).ToString("yyyy-MM-dd HH:mm:ss") + " md5=" + Md5(File.ReadAllBytes(usedEmpty)) : "yok"));
            string usedLens = cornerMode ? cornerPath : lensPath;
            head.AppendLine("--- kalibrasyon " + usedLens);
            head.AppendLine(File.Exists(usedLens) ? File.ReadAllText(usedLens).TrimEnd() : "yok");
            head.AppendLine("--- adimlar");
            head.Append(trace.Text);
            head.AppendLine("--- sonuc");
            if (result.Error != null) head.AppendLine("hata=" + result.Error);
            for (int i = 0; i < result.Slabs.Count; i++)
            {
                var s = result.Slabs[i];
                head.AppendLine(FormattableString.Invariant($"nesne={i + 1} yontem={s.Method} sekil={s.Shape} alan_mm2={s.AreaMm2:F1} guvenilir={calibrationTrusted && s.IsReliable} tespit_guvenilir={s.IsReliable} kalibrasyon_guvenilir={calibrationTrusted} not={s.Note ?? "-"}"));
                head.AppendLine("  kenarlar_mm=" + string.Join("/", s.EdgeLengths.Select(e => e.ToString("F2", inv))));
                Point2[] pixels = LensCalibrator.TableToImage(geomCal, s.Vertices, flat ? 0 : thickness);
                for (int n = 0; n < s.Vertices.Length; n++)
                    head.AppendLine(FormattableString.Invariant($"  kose={n + 1} tabla_mm=({s.Vertices[n].X:F2},{s.Vertices[n].Y:F2}) girdi_px=({pixels[n].X:F2},{pixels[n].Y:F2})"));
            }
            return head.ToString();
        }
        if (result.Error != null)
        {
            string failedId = SaveMeasurement(bytes, null, thickness, formLength, formWidth, result, new List<string>(), method, true, traceText, traceMask);
            return Results.Json(new { hata = TurkceHata(result.Error), kayit = failedId, iz = captureTrace && TraceExists(failedId) }, statusCode: 400);
        }
        var uyarilar = new List<string>();
        if (cornerMode && lensCal.TagType != "damasiz-lens" && (LoadCornerPoints()?.Count(q => !double.IsNaN(q.X)) ?? 0) < 8)
            uyarilar.Add("Damasız kalibrasyonda kenar ortaları işaretlenmedi: lens kenarlardan tahmin edildi, tablanın ortasındaki ölçüler birkaç yüzde sapabilir. Kalibrasyonu dört kenar ortasıyla (6–9 numaralı işaretler) yenileyin.");
        if (!calibrationTrusted)
            uyarilar.Add($"Kayıtlı kalibrasyonda en büyük referans hatası {lensCal.MaxMm:F2} mm. Ölçüler yalnızca önizlemedir; kullanmadan önce yeniden kalibre edin.");
        if (!result.UsedReference)
            uyarilar.Add(cornerMode ? "Boş tabla fotoğrafı kayıtlı değil: tablanın üzerindeki lekeler, çizikler ya da çıta araları parça sanılabilir. Damasız kalibrasyon bölümünden boş tablayı bir kez kaydedin."
                : "Boş tabla fotoğrafı kayıtlı değil: tablanın üzerindeki lekeler, çizikler ya da çıta araları parça sanılabilir. Damaları kaldırdıktan sonra Kamera kalibrasyonu bölümünden boş tablayı bir kez kaydedin.");
        // Gosterim ve kayit goruntusu: 1 piksel = 1 mm, DisplayLengthMm x DisplayWidthMm, plakanin ust yuzeyi
        // yuksekliginde duzeltilir; boylece kalinlik ne olursa olsun cizilen koseler plakanin gorunen kenarinda durur.
        if (flat) uyarilar.Add("Düzeltilmiş (3300 × 2000) görüntü ölçüldü: lens ve kalınlık düzeltmesi görüntüde zaten uygulanmış, yeniden uygulanmadı. Ham kamera fotoğrafı ölçmek daha doğrudur.");
        using Mat display = flat ? frame.Clone() : LensCalibrator.TopViewAt(frame, lensCal, DisplayLengthMm, DisplayWidthMm, 1.0, thickness);
        byte[] displayJpg = display.ToBytes(".jpg", new ImageEncodingParam(ImwriteFlags.JpegQuality, 90));
        string logId = SaveMeasurement(bytes, displayJpg, thickness, formLength, formWidth, result, uyarilar, method, calibrationTrusted, traceText, traceMask);
        TableCalibration used = result.Calibration;
        string image = "data:image/jpeg;base64," + Convert.ToBase64String(displayJpg);
        return Results.Json(new
        {
            tabla = new { uzunluk = used.TableLengthMm, genislik = used.TableWidthMm },
            kalinlik = thickness,
            // Olcum ustten gorunumde yapilir; arayuz fotografi cizdigi icin boyut ve koseler fotograftaki
            goruntuBoyutu = new { w = frame.Cols, h = frame.Rows },
            tablaKoseleri = LensCalibrator.TableToImage(geomCal, new[] { new Point2(0, 0), new Point2(0, formWidth), new Point2(formLength, formWidth), new Point2(formLength, 0) }).Select(P),
            kalibrasyon = cornerMode ? CornerStatus(lensCal) : LensStatus(lensCal),
            // Tablanin kenari fotografta lens bukulmesiyle egri gorunur: kenar boyunca noktalar (arayuz cizer)
            tablaKenari = LensCalibrator.TableToImage(geomCal, TableBorder(formLength, formWidth, 40)).Select(P),
            uyarilar,
            koseKaynagi = cornerMode ? "kalibrasyon-damasiz" : "kalibrasyon",
            kayit = logId,
            iz = captureTrace && TraceExists(logId),
            izMaske = captureTrace && TraceExists(logId, true),
            goruntu = image,
            // Goruntunun kapsadigi alan (mm, sol alt 0,0); 1 piksel = 1 mm
            goruntuAlani = new { uzunluk = DisplayLengthMm, genislik = DisplayWidthMm },
            parcalar = result.Slabs.Select(s => new
            {
                sekil = s.Shape.ToString(),
                alanCm2 = Math.Round(s.AreaMm2 / 100),
                donme = Math.Round(s.RotationDeg, 1),
                koseler = s.Vertices.Select(v => new[] { Math.Round(v.X, 1), Math.Round(v.Y, 1) }),
                kenarlar = s.EdgeLengths.Select(e => Math.Round(e)),
                acilar = s.AnglesDeg.Select(a => Math.Round(a, 1)),
                guvenilir = calibrationTrusted && s.IsReliable,
                yontem = s.Method,
                not = !calibrationTrusted ? "Kalibrasyon referans hatası sınırı aşıyor." : s.Note
            })
        });
    }
});

// Olcum kaydi: her olcumun fotografi ve sonucu D:\SMO\photos\olcumler altinda saklanir (git disi, yalniz bu bilgisayarda).
// Sapan olcumleri sonradan inceleyebilmek icin. <id>.jpg duzeltilmis ustten gorunum (1 piksel = 1 mm), ham kamera
// fotografi ham\<id>.jpg (yeniden olcmek icin). En fazla 300 olcum tutulur; eskileri silinir.
bool TraceExists(string id, bool mask = false) =>
    id != null && File.Exists(Path.Combine(dataDir, "olcumler", "iz", id + (mask ? "_maske.png" : ".txt")));

string SaveMeasurement(byte[] photo, byte[] displayJpg, double thicknessMm, double lengthMm, double widthMm, MeasureOutput r, List<string> warnings, string method, bool calibrationTrusted = true, string trace = null, byte[] traceMask = null)
{
    try
    {
        string dir = Path.Combine(dataDir, "olcumler");
        string rawDir = Path.Combine(dir, "ham");
        Directory.CreateDirectory(rawDir);
        string id = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        File.WriteAllBytes(Path.Combine(rawDir, id + ".jpg"), photo);
        if (displayJpg != null) File.WriteAllBytes(Path.Combine(dir, id + ".jpg"), displayJpg);
        var c = r.Calibration;
        var info = new
        {
            zaman = DateTime.Now,
            kalinlik = thicknessMm,
            tabla = c == null ? null : new { uzunluk = c.TableLengthMm, genislik = c.TableWidthMm },
            istenenTabla = new { uzunluk = lengthMm, genislik = widthMm },
            yontem = method,
            goruntu = c == null ? null : new { w = c.ImageWidth, h = c.ImageHeight },
            kayitGoruntusu = displayJpg == null ? null : new { uzunlukMm = DisplayLengthMm, genislikMm = DisplayWidthMm, pikselMm = 1.0, duzlemYuksekligiMm = thicknessMm },
            uyarilar = warnings,
            hata = r.Error,
            parcalar = r.Slabs.Select(s => new { s.Method, IsReliable = calibrationTrusted && s.IsReliable, kenarlar = s.EdgeLengths, koseler = s.Vertices.Select(P) })
        };
        File.WriteAllText(Path.Combine(dir, id + ".json"), System.Text.Json.JsonSerializer.Serialize(info, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        if (trace != null)
        {
            try
            {
                string traceDir = Path.Combine(dir, "iz");
                Directory.CreateDirectory(traceDir);
                File.WriteAllText(Path.Combine(traceDir, id + ".txt"), "id=" + id + "\n" + trace);
                if (traceMask != null) File.WriteAllBytes(Path.Combine(traceDir, id + "_maske.png"), traceMask);
            }
            catch (IOException ex) { Console.Error.WriteLine("Ölçüm izi kaydedilemedi: " + ex.Message); }
            catch (UnauthorizedAccessException ex) { Console.Error.WriteLine("Ölçüm izi kaydedilemedi: " + ex.Message); }
        }
        // Her olcumun JSON'u var: en eskiler kendi fotograflariyla (duzeltilmis, ham, eski "_2.jpg") birlikte silinir
        foreach (string old in Directory.GetFiles(dir, "*.json").OrderByDescending(f => f).Skip(300))
        {
            string name = Path.GetFileNameWithoutExtension(old);
            foreach (string f in new[] { old, Path.Combine(dir, name + ".jpg"), Path.Combine(dir, name + "_2.jpg"), Path.Combine(rawDir, name + ".jpg"),
                Path.Combine(dir, "iz", name + ".txt"), Path.Combine(dir, "iz", name + "_maske.png") })
                if (File.Exists(f)) File.Delete(f);
        }
        return id;
    }
    catch (IOException) { return null; }
}

static string Md5(byte[] data)
{
    using var md5 = System.Security.Cryptography.MD5.Create();
    return Convert.ToHexString(md5.ComputeHash(data)).ToLowerInvariant();
}

// Cekirdek kutuphanenin ASCII mesajlarini arayuz icin Turkce harflere cevirir
static string TurkceHata(string m) => m
    .Replace(" piksel fotografla yapildi. Ayni kameranin ayni akisindan cekin ya da kalibrasyonu yenileyin.", " piksel fotoğrafla yapıldı. Aynı kameranın aynı akışından (ana akış) çekin ya da kalibrasyonu yenileyin.")
    .Replace("Bu fotograf", "Bu fotoğraf");

try { app.Run(); }
catch (IOException error) when (error.InnerException is Microsoft.AspNetCore.Connections.AddressInUseException)
{
    Console.Error.WriteLine("Port kullanımda. Önceki Köprü Kesim sunucusunu durdurun veya başlatıcıya farklı bir port verin.");
    Environment.ExitCode = 3;
}
