using System.Globalization;
using KopruKesim.Core;
using KopruKesim.Vision;
using OpenCvSharp;

// Yerel deneme arayuzu. Calistirma: dotnet run --project src/KopruKesim.Web
// Olcum kayitlari ve kayitli tablalar "veri" klasorunde tutulur.
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
// Yazdirilacak etiket PDF'leri depo kokundeki "ornek/dama" klasorunden gelir
string repoRoot = app.Environment.ContentRootPath;
while (repoRoot != null && !File.Exists(Path.Combine(repoRoot, "KopruKesim.sln"))) repoRoot = Path.GetDirectoryName(repoRoot);

IResult Fail(string message) => Results.Json(new { hata = message }, statusCode: 400);

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
        if (xy.Length != 2) throw new FormatException("Kose 'x,y' biciminde olmali: " + parts[i]);
        c[i] = new Point2(double.Parse(xy[0], inv), double.Parse(xy[1], inv));
    }
    return c;
}

async Task<byte[]> ReadFile(IFormCollection form)
{
    IFormFile f = form.Files.GetFile("dosya");
    if (f == null || f.Length == 0) return null;
    using var ms = new MemoryStream();
    await f.CopyToAsync(ms);
    return ms.ToArray();
}

// ---------- Tabla profilleri ----------
// Her tabla icin bir kez: referans fotograf + onayli dort kose + tabla olcusu (veri/tablalar/<id>/).
// Olcumde profil secilirse koseler bu fotografa tabla yuzeyi deseni eslestirilerek tasinir.
string profilesDir = Path.Combine(dataDir, "tablalar");
Directory.CreateDirectory(profilesDir);
bool ValidProfileId(string id) => !string.IsNullOrEmpty(id) && id.All(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-');
string ProfileFolder(string id) => Path.Combine(profilesDir, id);

object ProfileList() => Directory.GetDirectories(profilesDir)
    .Select(d => new { id = Path.GetFileName(d), file = Path.Combine(d, "profil.txt") })
    .Where(x => File.Exists(x.file))
    .Select(x =>
    {
        try { TableProfile p = TableProfile.Load(x.file); return new { id = x.id, ad = p.Name, uzunluk = p.TableLengthMm, genislik = p.TableWidthMm }; }
        catch (Exception) { return null; }
    })
    .Where(x => x != null)
    .OrderBy(x => x.ad)
    .ToList();

app.MapGet("/api/tablalar", () => { lock (gate) return Results.Json(ProfileList()); });

app.MapGet("/api/tablalar/{id}/foto", (string id) =>
{
    if (!ValidProfileId(id)) return Results.NotFound();
    string path = Path.Combine(ProfileFolder(id), "referans.jpg");
    return File.Exists(path) ? Results.File(path, "image/jpeg") : Results.NotFound();
});

// Kaydet: fotograf + koseler (+ ad, olcu). Koseler kenarlara piksel alti oturtulur.
app.MapPost("/api/tablalar", async (HttpRequest req) =>
{
    IFormCollection form = await req.ReadFormAsync();
    byte[] bytes = await ReadFile(form);
    if (bytes == null) return Fail("Fotoğraf gelmedi.");
    string name = form["ad"].ToString().Trim();
    if (name.Length == 0) return Fail("Tablaya bir ad verin.");
    double length = double.TryParse(form["uzunluk"], NumberStyles.Float, inv, out double l) ? l : 0;
    double width = double.TryParse(form["genislik"], NumberStyles.Float, inv, out double w) ? w : 0;
    if (length <= 0 || width <= 0) return Fail("Tablanın uzunluğunu ve genişliğini girin.");
    Point2[] corners;
    try { corners = ParseCorners(form["koseler"]); } catch (FormatException) { corners = null; }
    if (corners == null) return Fail("Dört köşe gelmedi.");
    lock (gate)
    {
        using Mat frame = Cv2.ImDecode(bytes, ImreadModes.Color);
        if (frame.Empty()) return Fail("Dosya bir görüntü olarak okunamadı.");
        if (form["incelt"] == "1") corners = TableMeasure.SnapCorners(frame, corners, Math.Max(3.0, Math.Max(frame.Cols, frame.Rows) / 1000.0));
        try
        {
            var test = new TableCalibration { TableLengthMm = length, TableWidthMm = width, ImageWidth = frame.Cols, ImageHeight = frame.Rows,
                CornerOrigin = corners[0], CornerYEnd = corners[1], CornerFar = corners[2], CornerXEnd = corners[3] };
            test.Build();
        }
        catch (Exception) { return Fail("Bu köşelerle geçerli bir tabla oluşmuyor. Köşeleri sırasıyla yerleştirin."); }
        string slug = new string(name.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) && ch < 128 ? ch : '_').ToArray()).Trim('_');
        if (slug.Length > 30) slug = slug.Substring(0, 30);
        string id = (slug.Length > 0 ? slug + "_" : "") + DateTime.Now.ToString("yyyyMMddHHmmss");
        string folder = ProfileFolder(id);
        Directory.CreateDirectory(folder);
        Cv2.ImWrite(Path.Combine(folder, "referans.jpg"), frame, new ImageEncodingParam(ImwriteFlags.JpegQuality, 95));
        new TableProfile { Name = name, TableLengthMm = length, TableWidthMm = width, ImageWidth = frame.Cols, ImageHeight = frame.Rows, Corners = corners }
            .Save(Path.Combine(folder, "profil.txt"));
        return Results.Json(new { id, koseler = corners.Select(P), tablalar = ProfileList() });
    }
});

app.MapPost("/api/tablalar/sil", async (HttpRequest req) =>
{
    IFormCollection form = await req.ReadFormAsync();
    string id = form["id"].ToString();
    if (!ValidProfileId(id)) return Fail("Geçersiz tabla.");
    lock (gate)
    {
        string folder = ProfileFolder(id);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        return Results.Json(ProfileList());
    }
});

// Olcum: tabladaki parcalarin koseleri (mm) ve ustten gorunum
app.MapPost("/api/olc", async (HttpRequest req) =>
{
    IFormCollection form = await req.ReadFormAsync();
    byte[] bytes = await ReadFile(form);
    if (bytes == null) return Fail("Fotoğraf gelmedi.");
    double thickness = double.TryParse(form["kalinlik"], NumberStyles.Float, inv, out double t) && t >= 0 ? t : 20;
    bool uniformTable = form["tekrenk"] == "1";
    // Kose dama etiketleri: "kucuk", "buyuk" ya da "normal"; yerlesim tabla olcusunden uretilir.
    // Bos ise damasiz: tabla koseleri fotograftan (otomatik, elle ya da kayitli tabladan) bulunur.
    string checker = form["dama"].ToString();
    double formLength = double.TryParse(form["uzunluk"], NumberStyles.Float, inv, out double fl) && fl > 0 ? fl : 0;
    double formWidth = double.TryParse(form["genislik"], NumberStyles.Float, inv, out double fw) && fw > 0 ? fw : 0;
    double margin = double.TryParse(form["kenar"], NumberStyles.Float, inv, out double m) ? m : 40;
    lock (gate)
    {
        if (checker != "" && checker != "buyuk" && checker != "normal" && checker != "kucuk") return Fail("Bilinmeyen etiket türü.");
        bool checkerless = checker == "";
        string profileId = form["profil"].ToString();
        bool hasProfile = checkerless && ValidProfileId(profileId) && File.Exists(Path.Combine(ProfileFolder(profileId), "profil.txt"));
        if (!hasProfile && (formLength <= 0 || formWidth <= 0)) return Fail("Tablanın uzunluğunu ve genişliğini girin.");
        MarkerLayout layout = checkerless ? null : CheckerLayout(formLength, formWidth, checker);
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
        // Damasiz yolda koseler uc kaynaktan gelebilir: elle verilen (suruklenen) koseler, kayitli tabla
        // profili (desen eslestirmesi) ya da fotograftan otomatik bulma.
        Point2[] manualCorners = null;
        try { manualCorners = ParseCorners(form["koseler"]); } catch (FormatException) { manualCorners = null; }
        TableMatch profileMatch = null;
        string profileNote = null;
        MeasureOutput attempt = null;
        // Iki kose modu: kullanici yalniz sol ust ve sag alt koseyi verir ("kosegen"); "koseler" tahmindir.
        // Diger iki kose, verilen koselerden cikan tabla kenarlarinin kesisiminden bulunur.
        string diag = form["kosegen"].ToString();
        if (checkerless && manualCorners != null && !string.IsNullOrWhiteSpace(diag))
        {
            string[] dp = diag.Split(';');
            try
            {
                if (dp.Length != 2) throw new FormatException("Iki kose gerekli.");
                Point2 P2(string t) { string[] xy = t.Split(','); if (xy.Length != 2) throw new FormatException(t); return new Point2(double.Parse(xy[0], inv), double.Parse(xy[1], inv)); }
                manualCorners = DiagonalCorners.Complete(frame, P2(dp[0]), P2(dp[1]), manualCorners);
                attempt = TableMeasure.RunWithCorners(frame, manualCorners, formLength, formWidth, options, thickness, false);
            }
            catch (Exception e) when (e is FormatException || e is ArgumentException)
            {
                return Fail("İki köşeden tabla kenarları bulunamadı: " + e.Message);
            }
        }
        else if (checkerless && manualCorners != null)
        {
            // Elle yakinlastirilarak konan kose yalniz birkac piksel duzeltilir; genis pencere dusuk kontrastli
            // kenarda koseyi ice dogru yanlis cizgiye cekebiliyor (2026-11-18 ofis masasi).
            attempt = TableMeasure.RunWithCorners(frame, manualCorners, formLength, formWidth, options, thickness, true, Math.Max(3.0, Math.Max(frame.Cols, frame.Rows) / 1000.0));
        }
        else if (hasProfile)
        {
            TableProfile profile = null;
            try { profile = TableProfile.Load(Path.Combine(ProfileFolder(profileId), "profil.txt")); }
            catch (Exception e) when (e is FormatException || e is IOException || e is OverflowException)
            {
                // Bozuk profil dosyasi olcumu durdurmasin: koseler fotograftan otomatik bulunur
                profileNote = "Kayıtlı tabla dosyası okunamadı; köşeler fotoğraftan otomatik bulundu. Tablayı yeniden kaydedin.";
            }
            if (profile != null)
            {
                using Mat profileImage = Cv2.ImRead(Path.Combine(ProfileFolder(profileId), "referans.jpg"));
                formLength = profile.TableLengthMm; formWidth = profile.TableWidthMm;
                attempt = TableMeasure.RunWithProfile(frame, profile, profileImage, options, thickness, out profileMatch);
                if (attempt.Error != null)
                {
                    profileNote = $"Kayıtlı tabla ({profile.Name}) bu fotoğrafla eşleşmedi: {attempt.Error} Köşeler fotoğraftan otomatik bulundu; sarı çerçeveyi kontrol edin.";
                    attempt.Dispose();
                    attempt = TableMeasure.RunSingle(frame, formLength, formWidth, options, thickness);
                }
            }
        }
        if (attempt == null)
            attempt = checkerless
                ? TableMeasure.RunSingle(frame, formLength, formWidth, options, thickness)
                : TableMeasure.RunWithTags(frame, layout, options, thickness);
        // Secilen etiket turunde dort etiket cikmazsa digerleri denenir: yanlis tur secmek olcumu bozmasin
        string usedChecker = checker;
        if (layout != null && layout.Kind == "dama" && checker != "" && attempt.Error != null && attempt.Error.StartsWith("Yeterli isaret"))
        {
            foreach (string other in new[] { "kucuk", "buyuk", "normal" })
            {
                if (other == checker) continue;
                MarkerLayout l2 = CheckerLayout(formLength, formWidth, other);
                MeasureOutput r2 = TableMeasure.RunWithTags(frame, l2, options, thickness);
                if (r2.Error == null) { attempt.Dispose(); attempt = r2; layout = l2; usedChecker = other; break; }
                r2.Dispose();
            }
        }
        using MeasureOutput result = attempt;
        string checkerName(string t) => t == "kucuk" ? "Küçük" : t == "buyuk" ? "Büyük kareli" : "Normal";
        var isaretler = result.Markers == null ? null : new
        {
            kullanilan = result.Markers.Used,
            gorunmeyen = result.Markers.Missing,
            atilan = result.Markers.Rejected,
            ortalamaMm = Math.Round(result.Markers.RmsMm, 1),
            enKotuMm = Math.Round(result.Markers.MaxMm, 1),
            noktalar = result.Markers.GridPoints,
            baskiOlcegi = double.IsNaN(result.Markers.PrintScale) ? (double?)null : Math.Round(result.Markers.PrintScale, 3),
            xyOrani = double.IsNaN(result.Markers.AxisRatio) ? (double?)null : Math.Round(result.Markers.AxisRatio, 3)
        };
        var profil = profileMatch == null ? null : new
        {
            eslesti = result.ProfileMatched,
            eslesme = profileMatch.Matches,
            uyumlu = profileMatch.Inliers,
            hataPx = double.IsNaN(profileMatch.RmsPx) ? (double?)null : Math.Round(profileMatch.RmsPx, 2)
        };
        if (result.Error != null)
        {
            var failNotes = new List<string>();
            if (profileNote != null) failNotes.Add(profileNote);
            SaveMeasurement(bytes, thickness, formLength, formWidth, result, null, failNotes, checker);   // basarisiz olcum de incelensin
            return Results.Json(new { hata = TurkceHata(result.Error), isaretler, profil }, statusCode: 400);
        }
        List<SlabResult> slabs = result.Slabs;
        // Guven: tabla koseleri ne kadar guvenilir bulundu (kenar destegi + kamera tutarliligi).
        // Tutarlilik kirpilmamis fotografta ~1 olmali; uzaklasmasi koselerin ya da tabla olcusunun yanlis olabilecegini gosterir.
        double? consistency = result.Calibration?.Camera == null ? (double?)null : result.Calibration.Camera.Consistency;
        var uyarilar = new List<string>();
        if (profileNote != null) uyarilar.Add(profileNote);
        // Profilden tasinan koselerde de gosterilir: yanlis desen eslesmesini ortaya cikaran tek isaret bu
        if (manualCorners == null && !double.IsNaN(result.TableSupport) && result.TableSupport < 0.8)
            uyarilar.Add($"Tabla kenarları zayıf bulundu (%{result.TableSupport * 100:F0}).");
        if (result.Markers != null && !double.IsNaN(result.Markers.PrintScale) && Math.Abs(result.Markers.PrintScale - 1) > 0.02)
            uyarilar.Add($"Köşe etiketleri gerçek boyutta değil, yaklaşık %{result.Markers.PrintScale * 100:F0} ölçekle basılmış görünüyor (kareler {layout.SquareMm * result.Markers.PrintScale:F1} mm, olması gereken {layout.SquareMm:F0} mm). Ölçüm buna göre düzeltildi; en doğru sonuç için etiketleri yazdırırken ölçeği %100 / \"Gerçek boyut\" seçin (\"Sayfaya sığdır\" değil).");
        if (result.Markers != null && !double.IsNaN(result.Markers.AxisRatio) && Math.Abs(result.Markers.AxisRatio - 1) > 0.025)
            uyarilar.Add($"Etiket kareleri tablanın uzun ve kısa yönünde farklı boyda görünüyor (oran {result.Markers.AxisRatio:F2}). Girilen tabla uzunluğu ya da genişliği hatalı olabilir veya bir etiket köşeye tam dayalı değil.");
        // Esik genis: telefon fotograflarinda koseler dogruyken de 0,78-1,39 goruldu (2026-11-17, A4 0-3 mm);
        // yalniz belirgin bozuklukta (0,38 gibi: kenar yanlis cizgiye oturmus) uyar.
        if (result.Markers == null && consistency != null && (consistency < 0.6 || consistency > 1.6))
            uyarilar.Add($"Tabla köşeleri kamera geometrisiyle tam uyuşmuyor (tutarlılık {consistency:F2}). Köşeler ya da girilen tabla ölçüsü hatalı olabilir; Fotoğraf sekmesinde sarı çerçeveyi kontrol edin.");
        if (usedChecker != checker)
            uyarilar.Insert(0, $"Seçili etiket türü ({checkerName(checker)}) fotoğraftaki etiketlere uymadı; etiketler {checkerName(usedChecker)} olarak tanındı ve ölçüm buna göre yapıldı. Ayarlardan doğru türü seçebilirsiniz.");
        string logId = SaveMeasurement(bytes, thickness, formLength, formWidth, result, consistency, uyarilar, usedChecker);
        TableCalibration used = result.Calibration;
        string image = "data:image/jpeg;base64," + Convert.ToBase64String(result.Rectified.ToBytes(".jpg"));
        return Results.Json(new
        {
            tabla = new { uzunluk = used.TableLengthMm, genislik = used.TableWidthMm },
            kalinlik = thickness,
            tekFoto = checkerless,
            goruntuBoyutu = new { w = used.ImageWidth, h = used.ImageHeight },
            tablaKoseleri = new[] { P(used.CornerOrigin), P(used.CornerYEnd), P(used.CornerFar), P(used.CornerXEnd) },
            tutarlilik = used.Camera == null ? (double?)null : Math.Round(used.Camera.Consistency, 2),
            kenarDestegi = double.IsNaN(result.TableSupport) ? (double?)null : Math.Round(result.TableSupport, 2),
            uyarilar,
            profil,
            koseKaynagi = result.ProfileMatched ? "profil" : manualCorners != null ? "elle" : checkerless ? "otomatik" : "etiket",
            kayit = logId,
            isaretler,
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

// Tablanin dort kosesine A4 yatay dama etiketi: sayfa kenarlari tabla kenarina dayali, sayfanin uzun kenari
// tablanin uzun kenari (X) boyunca. Desen sayfanin ortasinda: merkez = kose + (148.5, 105) mm iceri.
static MarkerLayout CheckerLayout(double lengthMm, double widthMm, string type)
{
    // Etiket kenarlari tabla kenarina dayali, etiketin uzun kenari tablanin uzun kenari (X) boyunca.
    // buyuk/normal: A4 sayfanin tamami (297 x 210); kucuk: kesim cizgisinden kesilmis 170 x 140 mm etiket.
    // square: tasarim kare kenari (mm); tum ic koselerle kalibrasyon ve baski olcegi tahmini icin.
    int cols, rows; double hx, hy, size, square; bool cut = false;
    switch (type)
    {
        case "buyuk": cols = 6; rows = 4; hx = 148.5; hy = 105; size = 266; square = 38; break;
        case "kucuk": cols = 4; rows = 3; hx = 85; hy = 70; size = 170; square = 30; cut = true; break;
        default: cols = 9; rows = 6; hx = 148.5; hy = 105; size = 250; square = 25; break;
    }
    var l = new MarkerLayout { TableLengthMm = lengthMm, TableWidthMm = widthMm, MarkerHeightMm = 0, Kind = "dama", PatternCols = cols, PatternRows = rows, MarkerSizeMm = size, SquareMm = square, CutTag = cut };
    l.Markers.Add(new MarkerPoint(0, hx, hy));
    l.Markers.Add(new MarkerPoint(1, hx, widthMm - hy));
    l.Markers.Add(new MarkerPoint(2, lengthMm - hx, widthMm - hy));
    l.Markers.Add(new MarkerPoint(3, lengthMm - hx, hy));
    return l;
}

// Yazdirilacak kose etiketleri (PDF)
app.MapGet("/api/etiket/{tur}", (string tur) =>
{
    string file = tur == "buyuk" ? "dama_kose_etiketleri_buyuk_kare_A4.pdf" : tur == "normal" ? "dama_kose_etiketleri_A4.pdf" : tur == "kucuk" ? "dama_kose_etiketleri_kucuk_A4.pdf" : null;
    if (repoRoot == null || file == null) return Results.NotFound();
    string path = Path.Combine(repoRoot, "ornek", "dama", file);
    return File.Exists(path) ? Results.File(path, "application/pdf", file) : Results.NotFound();
});

// Olcum kaydi: her olcumun fotografi ve sonucu veri/olcumler altinda saklanir (git disi, yalniz bu bilgisayarda).
// Sapan olcumleri sonradan inceleyebilmek icin. En fazla 300 olcum tutulur; eskileri silinir.
string SaveMeasurement(byte[] photo, double thicknessMm, double lengthMm, double widthMm, MeasureOutput r, double? consistency, List<string> warnings, string tagType)
{
    try
    {
        string dir = Path.Combine(dataDir, "olcumler");
        Directory.CreateDirectory(dir);
        string id = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        File.WriteAllBytes(Path.Combine(dir, id + ".jpg"), photo);
        var c = r.Calibration;
        var info = new
        {
            zaman = DateTime.Now,
            kalinlik = thicknessMm,
            tabla = c == null ? null : new { uzunluk = c.TableLengthMm, genislik = c.TableWidthMm },
            istenenTabla = new { uzunluk = lengthMm, genislik = widthMm },
            etiket = tagType,
            goruntu = c == null ? null : new { w = c.ImageWidth, h = c.ImageHeight },
            tablaKoseleri = c == null ? null : new[] { P(c.CornerOrigin), P(c.CornerYEnd), P(c.CornerFar), P(c.CornerXEnd) },
            tutarlilik = consistency,
            kenarDestegi = double.IsNaN(r.TableSupport) ? (double?)null : r.TableSupport,
            uyarilar = warnings,
            hata = r.Error,
            parcalar = r.Slabs.Select(s => new { s.Method, s.IsReliable, kenarlar = s.EdgeLengths, koseler = s.Vertices.Select(P) })
        };
        File.WriteAllText(Path.Combine(dir, id + ".json"), System.Text.Json.JsonSerializer.Serialize(info, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        foreach (string old in Directory.GetFiles(dir, "*.jpg").OrderByDescending(f => f).Skip(300))
        {
            File.Delete(old);
            string js = Path.ChangeExtension(old, ".json");
            if (File.Exists(js)) File.Delete(js);
        }
        return id;
    }
    catch (IOException) { return null; }
}

// Cekirdek kutuphanenin ASCII mesajlarini arayuz icin Turkce harflere cevirir
static string TurkceHata(string m) => m
    .Replace("Yeterli isaret gorunmuyor: ", "Köşe etiketlerinden yalnızca ").Replace(" bulundu, en az 4 gerekli.", " tanesi bulundu; dört köşedeki etiketin dördü de fotoğrafta net ve yeterince büyük görünmeli.").Replace("bulundu", "bulundu")
    .Replace("Isaretler birbiriyle uyusmuyor", "İşaretler birbiriyle uyuşmuyor").Replace("en buyuk fark", "en büyük fark")
    .Replace("Bir isaret yerinden oynamis olabilir; daha fazla isaret gorunur olmali.", "Bir işaret yerinden oynamış olabilir; daha fazla işaret görünür olmalı.")
    .Replace("Isaretler gecerli bir kalibrasyon vermiyor (ayni dogru uzerinde olabilirler).", "İşaretler geçerli bir kalibrasyon vermiyor (aynı doğru üzerinde olabilirler).")
    .Replace("Bu fotograf", "Bu fotoğraf").Replace("Ayni kameradan cekilmis bir fotograf yukleyin ya da kurulumu yenileyin.", "Aynı kameradan çekilmiş bir fotoğraf yükleyin ya da kurulumu yenileyin.")
    .Replace("Kalibrasyon yok: once kurulum yapin.", "Kalibrasyon yok: önce kurulum yapın.")
    .Replace("Tabla bu fotografta bulunamadi. Tablanin tamami gorunecek sekilde yeniden cekin.", "Tabla bu fotoğrafta bulunamadı. Tablanın tamamı görünecek şekilde yeniden çekin.");

app.Run();
