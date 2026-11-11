using System.Globalization;
using KopruKesim.Core;
using KopruKesim.Vision;
using OpenCvSharp;

// Yerel deneme arayuzu. Calistirma: dotnet run --project src/KopruKesim.Web
// Kalibrasyon ve bos tabla referansi "veri" klasorunde tutulur.
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

var inv = CultureInfo.InvariantCulture;
var gate = new object();
string dataDir = Path.Combine(app.Environment.ContentRootPath, "veri");
Directory.CreateDirectory(dataDir);
string calPath = Path.Combine(dataDir, "kalibrasyon.txt");
string refPath = Path.Combine(dataDir, "kalibrasyon_referans.png");

// Ornek gorseller depo kokundeki "ornek" klasorunden gelir
string repoRoot = app.Environment.ContentRootPath;
while (repoRoot != null && !File.Exists(Path.Combine(repoRoot, "KopruKesim.sln"))) repoRoot = Path.GetDirectoryName(repoRoot);
var samples = new Dictionary<string, string> { { "bos", "bos_tabla.webp" }, { "mermer", "mermer.webp" } };

IResult Fail(string message) => Results.Json(new { hata = message }, statusCode: 400);

object State()
{
    if (!File.Exists(calPath)) return new { kurulu = false };
    TableCalibration cal = TableCalibration.Load(calPath);
    return new
    {
        kurulu = true,
        tabla = new { uzunluk = cal.TableLengthMm, genislik = cal.TableWidthMm },
        goruntu = new { w = cal.ImageWidth, h = cal.ImageHeight },
        koseler = new[] { P(cal.CornerOrigin), P(cal.CornerYEnd), P(cal.CornerFar), P(cal.CornerXEnd) },
        tutarlilik = cal.Camera == null ? (double?)null : Math.Round(cal.Camera.Consistency, 2),
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
        Point2[] corners = TableFinder.FindCorners(frame);
        bool found = corners != null;
        if (!found)
        {
            // Bulunamadi: kullanici surukleyip duzeltsin diye iceride bir dortgen oner
            double mx = frame.Cols * 0.2, my = frame.Rows * 0.15;
            corners = new[] { new Point2(mx, my), new Point2(frame.Cols - mx, my), new Point2(frame.Cols - mx, frame.Rows - my), new Point2(mx, frame.Rows - my) };
        }
        SaveCalibration(corners, length, width, frame.Cols, frame.Rows);
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
    double thickness = double.TryParse(form["kalinlik"], NumberStyles.Float, inv, out double t) ? t : 20;
    bool useReference = form["referans"] == "1";
    double margin = double.TryParse(form["kenar"], NumberStyles.Float, inv, out double m) ? m : 40;
    lock (gate)
    {
        if (!File.Exists(calPath)) return Fail("Önce kurulum yapın: boş tablanın fotoğrafını yükleyin.");
        TableCalibration cal = TableCalibration.Load(calPath);
        cal.SlabThicknessMm = thickness;
        using Mat frame = Cv2.ImDecode(bytes, ImreadModes.Color);
        if (frame.Empty()) return Fail("Dosya bir görüntü olarak okunamadı.");
        if (frame.Cols != cal.ImageWidth || frame.Rows != cal.ImageHeight)
            return Fail($"Bu fotoğraf {frame.Cols}×{frame.Rows} piksel, kurulum ise {cal.ImageWidth}×{cal.ImageHeight}. Aynı kameradan çekilmiş bir fotoğraf yükleyin ya da kurulumu yenileyin.");

        // Kenar puruzu toleransi genis: kameraya bakan kenarda yan yuzey ve golge kenari dalgalandirir
        var options = new DetectorOptions
        {
            BorderMarginMm = margin,
            Locator = new LocatorOptions { SimplifyToleranceMm = 30, MinEdgeMm = 150 }
        };
        using var detector = new SlabDetector(cal, options);
        bool usedReference = false;
        if (useReference && File.Exists(refPath))
        {
            using Mat empty = Cv2.ImRead(refPath);
            if (!empty.Empty() && empty.Cols == frame.Cols && empty.Rows == frame.Rows) { detector.SetReference(empty); usedReference = true; }
        }
        if (!usedReference) detector.SetTableColorAuto(frame);

        List<SlabResult> slabs = detector.Detect(frame);
        using Mat view = detector.Rectify(frame);
        string image = "data:image/jpeg;base64," + Convert.ToBase64String(view.ToBytes(".jpg"));
        return Results.Json(new
        {
            tabla = new { uzunluk = cal.TableLengthMm, genislik = cal.TableWidthMm },
            kalinlik = thickness,
            referansKullanildi = usedReference,
            goruntu = image,
            parcalar = slabs.Select(s => new
            {
                sekil = s.Shape.ToString(),
                alanCm2 = Math.Round(s.AreaMm2 / 100),
                donme = Math.Round(s.RotationDeg, 1),
                koseler = s.Vertices.Select(v => new[] { Math.Round(v.X, 1), Math.Round(v.Y, 1) }),
                kenarlar = s.EdgeLengths.Select(e => Math.Round(e)),
                acilar = s.AnglesDeg.Select(a => Math.Round(a, 1))
            })
        });
    }
});

app.Run();
