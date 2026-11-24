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
MeasureTrace.Install();   // olcum izi (olcumler\iz): cekirdegin hata ayiklama satirlari her olcumle saklanir
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
string dataDir = @"D:\SMO\photos";
// Olcum goruntusu (ekran ve olcumler klasoru): tablanin 3300 x 2000 mm'lik alani, 1 piksel = 1 mm
const double DisplayLengthMm = 3300, DisplayWidthMm = 2000;
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
// Fotografta cizilecek tabla siniri: paspasli kalibrasyonda bulunan dort koseden (tabla tam dik acili olmayabilir),
// degilse kalibrasyondaki olcunun dikdortgeni
static Point2[] TableOutline(LensCalibration c, int n)
{
    if (c.CornerSource != LensCalibration.CornerSourceMats || c.PaperCorners == null || c.PaperCorners.Length != 4)
        return TableBorder(c.TableLengthMm, c.TableWidthMm, n);
    Point2[] k = { c.PaperCorners[2], c.PaperCorners[0], c.PaperCorners[1], c.PaperCorners[3] };   // sol alt, sol ust, sag ust, sag alt
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
    // Tabla koseleri: "kagit" (kose kagitlarinin dis koseleri) ya da "mavi" (mavi paspaslarin dis koseleri)
    koseKaynagi = c.CornerSource,
    paspasKenarMm = double.IsNaN(c.EdgeRmsMm) ? (double?)null : Math.Round(c.EdgeRmsMm, 2),
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

app.MapGet("/api/kalibrasyon/kaydet", () =>
    SaveCalibrationArchive("kalibrasyon", lensPath, calibrationPhotoPath, cameraLensPath, emptyPath));

app.MapGet("/api/kalibrasyon/incele", () =>
{
    lock (gate)
    {
        LensCalibration cal = LoadLens();
        if (cal == null || !File.Exists(calibrationPhotoPath))
            return Fail("İncelenecek kayıtlı kalibrasyon fotoğrafı bulunamadı.");

        using Mat frame = Cv2.ImRead(calibrationPhotoPath);
        if (frame.Empty() || frame.Cols != cal.ImageWidth || frame.Rows != cal.ImageHeight)
            return Fail("Kayıtlı fotoğraf bu kalibrasyonun görüntü boyutuyla uyuşmuyor.");

        double length = cal.TableLengthMm, width = cal.TableWidthMm;
        double scale = 1200 / Math.Max(length, width);
        if (!double.IsFinite(scale) || length * scale < 1 || width * scale < 1)
            return Fail("Kalibrasyondaki tabla ölçüleriyle üstten görünüm oluşturulamıyor.");

        using Mat top = LensCalibrator.TopView(frame, cal, scale);
        CalibrationTag tag = LensTag(cal.TagType);
        Point2[] pixels, table;
        int[] slots;
        string pointError;
        bool matsOnly = cal.TagType == "paspas";   // yalniz paspasli kalibrasyon: eslestirilecek dama yok
        if (matsOnly) { pointError = "paspas"; pixels = null; table = null; slots = null; }
        else if (cal.CornerSource == LensCalibration.CornerSourceMats)
            pointError = LensCalibrator.MatchSavedBoards(frame, cal, tag, out pixels, out table, out slots);
        else
        {
            pointError = LensCalibrator.MatchBoardPoints(frame, length, width, tag, out _, out pixels, out table, cal.MeasuredTagCenters, cal.BoardPlacements);
            slots = pixels == null ? null : Enumerable.Range(0, pixels.Length).Select(j => j / (tag.Cols * tag.Rows)).ToArray();
        }
        object[] boards = Array.Empty<object>();
        double[] ValidPoint(Point2 p) => double.IsFinite(p.X) && double.IsFinite(p.Y) ? P(p) : null;
        if (pointError == null)
        {
            Point2[] projected = LensCalibrator.TableToImage(cal, table);
            Point2[] back = LensCalibrator.ImageToTable(cal, pixels);
            boards = slots.Distinct().OrderBy(i => i).Select(i =>
            {
                int[] indices = Enumerable.Range(0, slots.Length).Where(j => slots[j] == i).ToArray();
                double[] errors = indices.Select(j => Point2.Distance(back[j], table[j])).ToArray();
                return (object)new
                {
                    no = i + 1,
                    hataMm = errors.All(double.IsFinite) ? (double?)Math.Round(errors.Average(), 2) : null,
                    foto = indices.Select(j => new { gorulen = ValidPoint(pixels[j]), beklenen = ValidPoint(projected[j]) }),
                    tabla = indices.Select(j => new { gorulen = ValidPoint(back[j]), beklenen = ValidPoint(table[j]) })
                };
            }).ToArray();
        }

        string Jpeg(Mat image) => "data:image/jpeg;base64," + Convert.ToBase64String(image.ToBytes(".jpg", new ImageEncodingParam(ImwriteFlags.JpegQuality, 88)));
        return Results.Json(new
        {
            kalibrasyon = LensStatus(cal),
            foto = Jpeg(frame),
            ustten = Jpeg(top),
            cerceve = LensCalibrator.TableToImage(cal, TableOutline(cal, 40)).Select(ValidPoint),
            sifir = ValidPoint(LensCalibrator.TableToImage(cal, new[] { new Point2(0, 0) })[0]),
            // Kose kagitlarinin dis koseleri (tabla kosesi kabul edilen noktalar): fotografta ve tablada
            kagitKoseleri = cal.PaperCorners == null ? null : new
            {
                foto = LensCalibrator.TableToImage(cal, cal.PaperCorners).Select(ValidPoint),
                tabla = cal.PaperCorners.Select(ValidPoint)
            },
            damalar = boards,
            noktaUyarisi = pointError == null || matsOnly ? null : "Kayıtlı karede dama noktaları yeniden eşleştirilemedi. Görüntüler mevcut kalibrasyon modeliyle gösteriliyor."
        });
    }
});

app.MapPost("/api/kalibrasyon", async (HttpRequest req) =>
{
    IFormCollection form = await req.ReadFormAsync();
    byte[] bytes = await ReadFile(form);
    if (bytes == null) return Fail("Fotoğraf gelmedi.");
    double Num(string key) => double.TryParse(form[key].ToString().Replace(',', '.'), NumberStyles.Float, inv, out double v) && double.IsFinite(v) ? v : double.NaN;
    // Yalniz dort mavi paspasla kalibrasyon (dama yok): tablanin dort kenari ayri ayri girilir
    if (form.ContainsKey("alt")) return CalibrateWithMats(bytes, new TableSides { Bottom = Num("alt"), Top = Num("ust"), Left = Num("sol"), Right = Num("sag"), Diagonal = Num("kosegen") },
        new MatSpec { LongMm = Num("paspasUzun"), ShortMm = Num("paspasKisa"), ThicknessMm = Num("paspasKalinlik") });
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
    string type = form["etiket"].ToString();
    CalibrationTag[] tags = type == "otomatik" ? CalibrationTag.All : new[] { LensTag(type) };
    lock (gate)
    {
        using Mat frame = Cv2.ImDecode(bytes, ImreadModes.Color);
        if (frame.Empty()) return Fail("Dosya bir görüntü olarak okunamadı.");
        // Kameranin lensi: kayitli; yoksa son kalibrasyon fotografi koseleri de gorunen (6 damali) bir fotografsa ondan
        LensCalibration cameraLens = LoadCameraLens();
        if ((cameraLens == null || cameraLens.ImageWidth != frame.Cols || cameraLens.ImageHeight != frame.Rows) && File.Exists(calibrationPhotoPath))
        {
            using Mat old = Cv2.ImRead(calibrationPhotoPath);
            if (!old.Empty() && old.Cols == frame.Cols && old.Rows == frame.Rows)
                foreach (CalibrationTag tag in tags)
                {
                    LensCalibration c0 = LensCalibrator.Calibrate(old, length, width, tag).Calibration;
                    if (c0 != null && c0.CornerSource == LensCalibration.CornerSourcePaper && c0.MeetsReferenceTolerance)
                    {
                        Directory.CreateDirectory(lensDir);
                        c0.Save(cameraLensPath);
                        cameraLens = c0;
                        break;
                    }
                }
        }
        LensCalibrator.Result best = null;
        foreach (CalibrationTag tag in tags)
        {
            LensCalibrator.Result r = LensCalibrator.Calibrate(frame, length, width, tag, measuredCenters, cameraLens);
            if (r.Calibration != null && r.Calibration.MeetsReferenceTolerance) { best = r; break; }
            if (best == null || (r.Calibration != null
                ? best.Calibration == null || r.Calibration.MaxMm < best.Calibration.MaxMm
                : best.Calibration == null && r.TagsFound > best.TagsFound))
                best = r;
        }
        if (best.Calibration == null && best.Error.StartsWith(LensCalibrator.NeedsLensError))
            return Fail("Mavi paspaslar bulundu ama bu kameranın lens bilgisi yok. Köşe damaları paspasın altındayken balık gözü bükülmesi "
                + "fotoğraftan çözülemiyor. Bir kez paspasları kaldırıp altı dama tam görünürken kalibre edin (lens kaydedilir), "
                + "sonra paspasları geri koyup yeniden kalibre edin.");
        if (best.Calibration == null)
            return Fail(best.Error.Replace(" yerde bulundu; dort kosede de tam gorunmeli.", " yerde bulundu; dört köşede de tam görünmeli.")
                .Replace("Mavi paspaslar bulundu ama yalniz ", "Mavi paspaslar bulundu ama yalnız ").Replace(" dama gorunuyor; en az iki dama (uzun kenarlarin ortasindakiler) tam gorunmeli.", " dama görünüyor; en az iki dama (uzun kenarların ortasındakiler) tam görünmeli.")
                .Replace("Mavi paspaslarin koseleri hesaplanamadi (kenarlari duz gorunmuyor).", "Mavi paspasların köşeleri hesaplanamadı (kenarları düz görünmüyor).")
                .Replace("Mavi paspaslarin kenarlari yeterince gorunmuyor.", "Mavi paspasların kenarları yeterince görünmüyor.")
                .Replace("Kameranin konumu cozulemedi (damalar).", "Kameranın konumu çözülemedi (damalar).").Replace("Kalibrasyon icin 6 etiket gerekli", "Kalibrasyon için 6 etiket gerekli").Replace(" bulundu. Dort koseye ve iki uzun kenarin ortasina birer etiket koyun; hepsi fotografta net gorunmeli.", " bulundu. Dört köşeye ve iki uzun kenarın ortasına birer etiket koyun; hepsi fotoğrafta net görünmeli.")
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
        // Koseler de gorundu (6 dama): lens ve kamera konumu ileride mavi paspasli kalibrasyonda kullanilmak uzere saklanir
        if (best.Calibration.CornerSource == LensCalibration.CornerSourcePaper && measuredCenters == null)
            best.Calibration.Save(cameraLensPath);
        File.WriteAllBytes(calibrationPhotoPath, bytes);
        SaveCalibrationTopView(frame, best.Calibration);
        // Yeni kalibrasyon: kamera oynamis olabilir, eski bos tabla fotografi artik ayni goruntuyu vermez
        if (File.Exists(emptyPath)) File.Delete(emptyPath);
        LensCalibration saved = LoadLens();
        var uyarilar = new List<string>();
        bool mats = saved.CornerSource == LensCalibration.CornerSourceMats;
        if (mats && !best.PoseFromPrior)
            uyarilar.Add($"Kamera, lensin kaydedildiği kalibrasyondan beri oynamış görünüyor (orta damalar {(double.IsNaN(best.PriorBoardRmsPx) ? "eşleşmedi" : best.PriorBoardRmsPx.ToString("F1") + " piksel kayık")}). "
                + "Kameranın konumu yalnız iki orta damadan bulundu; uçlarda birkaç mm hata olabilir. En iyisi paspasları bir kez kaldırıp altı damayla kalibre edin, sonra paspaslarla yeniden kalibre edin.");
        if (mats && double.IsFinite(saved.EdgeRmsMm) && saved.EdgeRmsMm > 2.5)
            uyarilar.Add($"Mavi paspasların kenarları düz görünmüyor (sapma {saved.EdgeRmsMm:F1} mm). Paspaslar tablaya düz yatmalı, kenardan taşmamalı ve kıvrık olmamalı.");
        double cornerDev = saved.PaperCornerDeviationMm();
        if (mats && (Math.Abs(saved.TableLengthMm - length) > 10 || Math.Abs(saved.TableWidthMm - width) > 10))
            uyarilar.Add($"Paspas köşelerinden ölçülen tabla {saved.TableLengthMm:F0} × {saved.TableWidthMm:F0} mm, girilen {length:F0} × {width:F0} mm. "
                + "Ölçülen kullanıldı (tabla köşeleri paspaslardır). Şeritle ölçtüğünüz paspas köşeleri arası belirgin farklıysa damaların baskı boyutunu (dört kare 100 mm) kontrol edin.");
        if (mats && !double.IsNaN(cornerDev) && cornerDev > LensCalibration.MaxReferenceErrorMm)
        {
            Point2[] pc = saved.PaperCorners;
            uyarilar.Add($"Mavi paspasların dış köşeleri tam dikdörtgen değil (en büyük fark {cornerDev:F1} mm): "
                + $"alt {Point2.Distance(pc[2], pc[3]):F1}, üst {Point2.Distance(pc[0], pc[1]):F1}, sol {Point2.Distance(pc[2], pc[0]):F1}, sağ {Point2.Distance(pc[3], pc[1]):F1} mm, "
                + $"sol üst köşe X {pc[0].X:F1}, sağ üst köşe X {pc[1].X:F1} mm. "
                + "Sıfır noktası sol alt paspasın dış köşesi, X ekseni sağ alt paspasın dış köşesine doğru alındı; parça ölçüleri bundan etkilenmez. "
                + "Tablanın kendisi tam dik açılı değilse bu normaldir; değilse paspasları tabla köşelerine tam oturtun.");
        }
        else if (!double.IsNaN(cornerDev) && cornerDev > LensCalibration.MaxReferenceErrorMm)
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

// Paspasli kalibrasyon: dort mavi paspas tablanin koselerinde (dis kosesi tabla kosesi), tabla kenarlari seritle olculmus.
// Lens kameranin kayitli lens bilgisinden (son 6 damali kalibrasyon) alinir; fotograftan kameranin konumu cozulur.
IResult CalibrateWithMats(byte[] bytes, TableSides sides, MatSpec spec)
{
    foreach (var (v, name) in new[] { (sides.Bottom, "alt"), (sides.Top, "üst"), (sides.Left, "sol"), (sides.Right, "sağ") })
        if (double.IsNaN(v) || v < 500 || v > 10000) return Fail($"Tablanın {name} kenarını mm olarak girin (paspasların dış köşeleri arası).");
    if (!double.IsNaN(sides.Diagonal) && sides.Diagonal <= 0) sides.Diagonal = double.NaN;
    if (double.IsNaN(spec.LongMm) || spec.LongMm < 50 || double.IsNaN(spec.ShortMm) || spec.ShortMm < 30 || spec.ShortMm >= spec.LongMm)
        return Fail("Paspasın uzun ve kısa kenarını mm olarak girin.");
    if (double.IsNaN(spec.ThicknessMm) || spec.ThicknessMm < 0) spec.ThicknessMm = 0;
    lock (gate)
    {
        using Mat frame = Cv2.ImDecode(bytes, ImreadModes.Color);
        if (frame.Empty()) return Fail("Dosya bir görüntü olarak okunamadı.");
        Directory.CreateDirectory(lensDir);
        // Son deneme her durumda saklanir (basarisiz kalibrasyonu sonradan incelemek icin)
        File.WriteAllBytes(Path.Combine(lensDir, "son-deneme.jpg"), bytes);
        // Lens: kameranin kayitli lensi; konum tahmini (paspaslari fotografta aramak icin) son kalibrasyondan
        LensCalibration camLens = LoadCameraLens(), current = LoadLens();
        bool Fits(LensCalibration c) => c != null && c.ImageWidth == frame.Cols && c.ImageHeight == frame.Rows;
        LensCalibration source = Fits(camLens) ? camLens : Fits(current) ? current : null;
        if (source == null)
            return Fail($"Bu kamera için {frame.Cols}x{frame.Rows} çözünürlükte kayıtlı lens bilgisi yok. Lens bükülmesi paspaslardan çözülemez; bir kez altı damalı kalibrasyon gerekir.");
        var prior = new LensCalibration
        {
            ImageWidth = source.ImageWidth, ImageHeight = source.ImageHeight, Fx = source.Fx, Fy = source.Fy, Cx = source.Cx, Cy = source.Cy,
            Distortion = (double[])source.Distortion.Clone(),
            ResidualRadial = Fits(camLens) ? camLens.ResidualRadial : 0,   // lensin kalan bukulmesi: kameranin ozelligi
            Rvec = (double[])(Fits(current) ? current : source).Rvec.Clone(), Tvec = (double[])(Fits(current) ? current : source).Tvec.Clone(),
            // Kamera konumu bu kalibrasyondan alinir: dogrulugu da ondan
            MeanMm = (Fits(current) ? current : source).MeanMm, MaxMm = (Fits(current) ? current : source).MaxMm, Points = (Fits(current) ? current : source).Points
        };
        MatCalibrator.Result r = MatCalibrator.Calibrate(frame, sides, spec, prior);
        if (r.Calibration == null) return Fail(r.Error + " Fotoğraf incelenmek üzere kaydedildi (kalibrasyon\\son-deneme.jpg).");
        LensCalibration cal = r.Calibration;
        cal.Save(lensPath);
        File.WriteAllBytes(calibrationPhotoPath, bytes);
        SaveCalibrationTopView(frame, cal);
        if (File.Exists(emptyPath)) File.Delete(emptyPath);   // kamera oynamis olabilir: bos tabla yeniden kaydedilmeli
        LensCalibration saved = LoadLens();
        var uyarilar = new List<string>();
        string[] names = { "sol üst", "sağ üst", "sol alt", "sağ alt" };
        if (r.UsedPriorPose)
        {
            Point2[] pc = r.PhotoCorners;
            if (r.MatErrorMm.Max() > 10)
                uyarilar.Add($"Paspas köşelerinden ölçülen tabla girilen kenarlardan farklı (köşe başına {string.Join(", ", r.MatErrorMm.Select((e, i) => $"{names[i]} {e:F0}"))} mm): "
                    + $"fotoğrafa göre alt {Point2.Distance(pc[2], pc[3]):F0}, üst {Point2.Distance(pc[0], pc[1]):F0}, sol {Point2.Distance(pc[2], pc[0]):F0}, sağ {Point2.Distance(pc[3], pc[1]):F0} mm, "
                    + $"köşegenler {Point2.Distance(pc[2], pc[1]):F0} / {Point2.Distance(pc[3], pc[0]):F0} mm. Tabla köşeleri paspaslardan alındı; parça ölçüleri bundan etkilenmez. "
                    + "Fark büyükse paspasların köşeye tam oturduğunu kontrol edin ya da köşegeni ölçüp girin.");
        }
        else if (!saved.MeetsReferenceTolerance)
            uyarilar.Add($"Paspas köşeleri modele {saved.MaxMm:F1} mm'ye kadar uymuyor (sınır {LensCalibration.MaxReferenceErrorMm:F0} mm; paspas başına "
                + string.Join(", ", r.MatErrorMm.Select((e, i) => $"{names[i]} {e:F1}")) + " mm). Ölçüler önizleme sayılır. "
                + "Hatası büyük paspasın tabla köşesine tam oturduğunu, paspas ölçüsünü ve tabla kenar uzunluklarını kontrol edin.");
        if (double.IsFinite(saved.EdgeRmsMm) && saved.EdgeRmsMm > 2.5)
            uyarilar.Add($"Mavi paspasların kenarları düz görünmüyor (sapma {saved.EdgeRmsMm:F1} mm). Paspaslar tablaya düz yatmalı ve kıvrık olmamalı.");
        uyarilar.Add("Kalibrasyon kaydedildi. Şimdi boş tablayı (paspaslar yerinde, başka bir şey yokken) bir kez kaydedin.");
        return Results.Json(new { kalibrasyon = LensStatus(saved), paspasHatasiMm = r.MatErrorMm.Select(e => Math.Round(e, 2)), uyarilar });
    }
}

// Bos tabla fotografi: kalibrasyondan sonra, damalar kaldirilinca ayni kameradan. Olcumde yalniz bos tabladan
// farkli yerler parca sayilir (ahsaptaki lekeler, cita aralari, delikler karismaz).
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
        // Damalar hala tabladaysa uyar: bos tabla olarak kaydedilirse olcumde etiket alanlari kor kalir
        int tags = CheckerFinder.FindAll(frame, LensTag(c.TagType).Cols, LensTag(c.TagType).Rows).Count;
        if (tags > 0) return Fail($"Fotoğrafta {tags} kalibrasyon daması görünüyor. Boş tabla fotoğrafından önce damaları kaldırın.");
        File.WriteAllBytes(emptyPath, bytes);
        return Results.Json(LensStatus(c));
    }
});

app.MapPost("/api/kalibrasyon/sil", () =>
{
    lock (gate)
    {
        if (File.Exists(emptyPath)) File.Delete(emptyPath);
        if (File.Exists(lensPath)) File.Delete(lensPath);
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
app.MapPost("/api/olc", async (HttpRequest req) =>
{
    IFormCollection form = await req.ReadFormAsync();
    byte[] bytes = await ReadFile(form);
    if (bytes == null) return Fail("Fotoğraf gelmedi.");
    double thickness = double.TryParse(form["kalinlik"], NumberStyles.Float, inv, out double t) && t >= 0 ? t : 20;
    bool uniformTable = form["tekrenk"] == "1";
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
        MarkerLayout layout = cornerMode || lensCal.TagType == "paspas" ? null : LensCalibrator.TagLayout(formLength, formWidth, LensTag(lensCal.TagType), TagCentersForMasking(lensCal));
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
            RefineEdges = form["parcaIncelt"] != "0"   // parca kenarlarini fotografta cok noktayla oturt (karsilastirma icin kapatilabilir)
        };
        string method = cornerMode ? "damasiz-kalibrasyon" : "kalibrasyon";
        // Daha once kaydedilen duzeltilmis goruntu (3300 x 2000, 1 px = 1 mm, plakanin ust yuzeyi yuksekliginde) yeniden
        // yuklendiyse: zaten duz ustten gorunum, lens duzeltmesi yapilmaz. Bos tabla ayni sekilde duzeltilip referans olur.
        bool flat = !cornerMode && frame.Cols == (int)DisplayLengthMm && frame.Rows == (int)DisplayWidthMm
            && (lensCal.ImageWidth != frame.Cols || lensCal.ImageHeight != frame.Rows);
        LensCalibration geomCal = lensCal;
        MeasureOutput result;
        var sw = Stopwatch.StartNew();
        var trace = MeasureTrace.Begin();
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
        finally { trace.Dispose(); }
        using var resultHolder = result;
        // Iz basligi: olcumu birebir tekrarlamak icin gereken her girdi (kamera sifresi burada yok, hic gelmez)
        string usedEmpty = cornerMode ? cornerEmptyPath : emptyPath;
        var head = new System.Text.StringBuilder();
        head.AppendLine("sure_ms=" + sw.ElapsedMilliseconds);
        head.AppendLine("dosya_adi=" + (form.Files.GetFile("dosya")?.FileName ?? "-"));
        head.AppendLine("foto=" + frame.Cols + "x" + frame.Rows + " md5=" + Md5(bytes));
        head.AppendLine(string.Format(inv, "kalinlik={0} tekrenk={1} kalibrasyon={2} kenar={3} parcaIncelt={4} duz={5}", thickness, uniformTable ? 1 : 0, mode, margin, options.RefineEdges ? 1 : 0, flat ? 1 : 0));
        head.AppendLine(string.Format(inv, "secenek BorderMarginMm={0} MinAreaMm2={1} NoReferenceBrightnessDiff={2} CleanupMm={3} SimplifyToleranceMm={4} MinEdgeMm={5} RefineEdges={6}",
            options.BorderMarginMm, options.MinAreaMm2, options.NoReferenceBrightnessDiff, options.CleanupMm, options.Locator.SimplifyToleranceMm, options.Locator.MinEdgeMm, options.RefineEdges));
        head.AppendLine("bos=" + (File.Exists(usedEmpty) ? usedEmpty + " " + File.GetLastWriteTime(usedEmpty).ToString("yyyy-MM-dd HH:mm:ss") + " md5=" + Md5(File.ReadAllBytes(usedEmpty)) : "yok"));
        string usedLens = cornerMode ? cornerPath : lensPath;
        head.AppendLine("--- kalibrasyon " + usedLens);
        head.AppendLine(File.Exists(usedLens) ? File.ReadAllText(usedLens).TrimEnd() : "yok");
        head.AppendLine("--- adimlar");
        head.Append(trace.Text);
        head.AppendLine("--- sonuc");
        if (result.Error != null) head.AppendLine("hata=" + result.Error);
        foreach (var s in result.Slabs)
            head.AppendLine(string.Format(inv, "parca yontem={0} guvenilir={1} not={2} kenarlar={3} koseler={4}", s.Method, s.IsReliable, s.Note ?? "-",
                string.Join("/", s.EdgeLengths.Select(e => e.ToString("F1", inv))), string.Join(" ", s.Vertices.Select(v => v.X.ToString("F1", inv) + "," + v.Y.ToString("F1", inv)))));
        string traceText = head.ToString();
        if (result.Error != null)
        {
            SaveMeasurement(bytes, null, thickness, formLength, formWidth, result, new List<string>(), method, true, traceText);   // basarisiz olcum de incelensin
            return Results.Json(new { hata = TurkceHata(result.Error) }, statusCode: 400);
        }
        var uyarilar = new List<string>();
        bool calibrationTrusted = cornerMode || lensCal.MeetsReferenceTolerance;
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
        string logId = SaveMeasurement(bytes, displayJpg, thickness, formLength, formWidth, result, uyarilar, method, calibrationTrusted, traceText);
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
string SaveMeasurement(byte[] photo, byte[] displayJpg, double thicknessMm, double lengthMm, double widthMm, MeasureOutput r, List<string> warnings, string method, bool calibrationTrusted = true, string trace = null)
{
    try
    {
        string dir = Path.Combine(dataDir, "olcumler");
        string rawDir = Path.Combine(dir, "ham");
        Directory.CreateDirectory(rawDir);
        string id = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        File.WriteAllBytes(Path.Combine(rawDir, id + ".jpg"), photo);
        if (displayJpg != null) File.WriteAllBytes(Path.Combine(dir, id + ".jpg"), displayJpg);
        // Olcum izi ve parca maskesi (SlabDetector KK_DEBUG iken %TEMP%\kk_maske.png'ye yazar)
        string traceDir = Path.Combine(dir, "iz");
        Directory.CreateDirectory(traceDir);
        if (trace != null) File.WriteAllText(Path.Combine(traceDir, id + ".txt"), "id=" + id + "\n" + trace);
        string maskTmp = Path.Combine(Path.GetTempPath(), "kk_maske.png");
        if (trace != null && File.Exists(maskTmp)) { File.Copy(maskTmp, Path.Combine(traceDir, id + "_maske.png"), true); File.Delete(maskTmp); }
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
