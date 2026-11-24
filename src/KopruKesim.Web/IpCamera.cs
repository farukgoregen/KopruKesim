using OpenCvSharp;

/// <summary>
/// RTSP ile IP kameradan (Dahua) surekli kare okur; en son kare bellekte tutulur. Olcum icin
/// istek anindan sonra gelmis taze bir kare alinir (tampondaki eski kare olculmesin).
///
/// Sifre yalniz bellekte, baglanti adresinin icinde durur: dosyaya, kayda, durum yanitina ya da hata
/// iletisine yazilmaz. Sunucu yeniden baslayinca arayuzden yeniden girilir.
/// </summary>
sealed class IpCamera : IDisposable
{
    private readonly object _lock = new object();
    private Mat _latest;
    private DateTime _latestAt = DateTime.MinValue;
    // Her baglanti bir "kusak": ayar degisince ya da kesilince eski okuyucu kusagi gecersizlesince kendiliginden
    // kapanir. Eski baglantinin acilmasini ya da zaman asimini beklemek gerekmez (istek takilmaz).
    private int _generation;
    private string _display;      // sifresiz gosterim adi (adres/kanal)
    private string _user;
    private bool _configured;
    private string _status = "Bağlı değil";
    private bool _connected;
    private int _width, _height;
    private double _fps;

    static IpCamera()
    {
        // FFmpeg ayarlari ilk VideoCapture'dan once: RTSP TCP uzerinden (UDP'de kayip kareler bozuk goruntu verir),
        // 5 sn baglanti zaman asimi; gunluk kapali (FFmpeg hata iletisinde adresi, dolayisiyla sifreyi yazabilir).
        Environment.SetEnvironmentVariable("OPENCV_FFMPEG_CAPTURE_OPTIONS", "rtsp_transport;tcp|stimeout;5000000|timeout;5000000");
        Environment.SetEnvironmentVariable("OPENCV_FFMPEG_LOGLEVEL", "-8");
        Environment.SetEnvironmentVariable("OPENCV_LOG_LEVEL", "SILENT");
    }

    /// <summary>
    /// Dahua adresi: rtsp://kullanici:sifre@ip:port/cam/realmonitor?channel=K&amp;subtype=A
    /// (subtype 0 = ana akis, tam cozunurluk; 1 = alt akis). "adres" tam bir rtsp:// adresi ya da (deneme icin)
    /// bir video dosyasi da olabilir; o zaman kullanici/sifre eklenmez.
    /// </summary>
    public void Configure(string address, int port, string user, string password, int channel, int subtype)
    {
        address = (address ?? "").Trim();
        if (address.Length == 0) throw new ArgumentException("Kamera adresini girin.");
        string url, display;
        if (address.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) || File.Exists(address))
        {
            url = address;
            display = address.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) ? HideSecret(address) : Path.GetFileName(address);
        }
        else
        {
            if (address.Any(ch => char.IsWhiteSpace(ch) || ch == '/' || ch == '@')) throw new ArgumentException("Kamera adresi yalnız IP olmalı (örnek 192.168.1.108).");
            string cred = string.IsNullOrEmpty(user) ? "" : Uri.EscapeDataString(user) + (string.IsNullOrEmpty(password) ? "" : ":" + Uri.EscapeDataString(password)) + "@";
            url = $"rtsp://{cred}{address}:{port}/cam/realmonitor?channel={channel}&subtype={subtype}";
            display = $"{address}:{port}, kanal {channel}, {(subtype == 0 ? "ana akış" : "alt akış")}";
        }
        int gen;
        lock (_lock)
        {
            gen = ++_generation;
            _configured = true; _display = display; _user = user;
            _status = "Bağlanıyor…"; _connected = false; _width = _height = 0; _fps = 0;
            _latest?.Dispose(); _latest = null; _latestAt = DateTime.MinValue;
        }
        new Thread(() => ReadLoop(gen, url)) { IsBackground = true, Name = "IpCamera" }.Start();
    }

    public void Stop()
    {
        lock (_lock)
        {
            _generation++;
            _configured = false; _connected = false; _status = "Bağlı değil"; _display = null; _user = null;
            _width = _height = 0; _fps = 0;
            _latest?.Dispose(); _latest = null; _latestAt = DateTime.MinValue;
        }
    }

    private bool Alive(int gen) { return Volatile.Read(ref _generation) == gen; }

    /// <summary>Simdiden sonra gelmis bir karenin kopyasi; zaman asiminda null.</summary>
    public Mat FreshFrame(TimeSpan timeout)
    {
        DateTime asked = DateTime.UtcNow;
        DateTime until = asked + timeout;
        while (DateTime.UtcNow < until)
        {
            lock (_lock)
            {
                if (!_configured) return null;
                if (_latest != null && _latestAt > asked) return _latest.Clone();
            }
            Thread.Sleep(20);
        }
        return null;
    }

    /// <summary>Arayuze gidecek durum: sifre icermez.</summary>
    public object Status()
    {
        lock (_lock)
        {
            return new
            {
                ayarli = _configured,
                bagli = _connected,
                durum = _status,
                adres = _display,
                kullanici = _user,
                genislik = _width,
                yukseklik = _height,
                fps = Math.Round(_fps, 1),
                sonKareSn = _latestAt == DateTime.MinValue ? (double?)null : Math.Round((DateTime.UtcNow - _latestAt).TotalSeconds, 1)
            };
        }
    }

    private void ReadLoop(int gen, string url)
    {
        bool isFile = !url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase);
        while (Alive(gen))
        {
            // Acma ve okuma zaman asimi 5 sn (CAP_PROP_OPEN_TIMEOUT_MSEC=53, CAP_PROP_READ_TIMEOUT_MSEC=54). Olmazsa
            // ulasilamayan bir IP'de acma ~20 sn takilir ve ardindan girilen dogru adres de onu bekler.
            using (var cap = new VideoCapture(url, VideoCaptureAPIs.FFMPEG, new[] { 53, 5000, 54, 5000 }))
            {
                if (!Alive(gen)) break;
                if (!cap.IsOpened())
                {
                    SetStatus(gen, false, "Kameraya bağlanılamadı. IP adresini, kullanıcı adını, şifreyi ve kameranın bu ağda olduğunu kontrol edin. 3 sn sonra yeniden denenecek.");
                    Wait(gen, 3000);
                    continue;
                }
                lock (_lock)
                {
                    if (!Alive(gen)) break;
                    _width = cap.FrameWidth; _height = cap.FrameHeight; _fps = cap.Fps;
                    _connected = true; _status = "Bağlı";
                }
                int misses = 0;
                double fileDelay = isFile ? 1000.0 / (cap.Fps > 0 ? cap.Fps : 25) : 0;
                using (var frame = new Mat())
                {
                    while (Alive(gen))
                    {
                        if (!cap.Read(frame) || frame.Empty())
                        {
                            if (isFile) { cap.Set(VideoCaptureProperties.PosFrames, 0); continue; }   // deneme dosyasi basa sarar
                            if (++misses > 50) break;   // akis koptu: yeniden baglan
                            Thread.Sleep(20);
                            continue;
                        }
                        misses = 0;
                        lock (_lock)
                        {
                            if (!Alive(gen)) break;
                            _latest?.Dispose();
                            _latest = frame.Clone();
                            _latestAt = DateTime.UtcNow;
                            _width = frame.Cols; _height = frame.Rows;
                        }
                        if (fileDelay > 0) Thread.Sleep((int)fileDelay);
                    }
                }
            }
            if (Alive(gen)) { SetStatus(gen, false, "Kamera akışı koptu; yeniden bağlanılıyor…"); Wait(gen, 1000); }
        }
    }

    private void SetStatus(int gen, bool connected, string status)
    {
        lock (_lock) { if (Alive(gen)) { _connected = connected; _status = status; } }
    }

    private void Wait(int gen, int ms)
    {
        for (int i = 0; i < ms / 100 && Alive(gen); i++) Thread.Sleep(100);
    }

    /// <summary>rtsp://kullanici:sifre@ -> rtsp://kullanici:***@</summary>
    private static string HideSecret(string url)
    {
        int at = url.IndexOf('@');
        int start = url.IndexOf("://", StringComparison.Ordinal) + 3;
        if (at < 0 || start < 3 || at < start) return url;
        string cred = url.Substring(start, at - start);
        int colon = cred.IndexOf(':');
        return colon < 0 ? url : url.Substring(0, start) + cred.Substring(0, colon) + ":***" + url.Substring(at);
    }

    public void Dispose() { Stop(); }
}
