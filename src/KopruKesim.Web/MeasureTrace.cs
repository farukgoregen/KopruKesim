using System.Text;

// Olcum izi: olcum sirasinda cekirdegin hata ayiklama satirlari (KK_DEBUG) bu tampona yazilir ve olcum kaydiyla
// (olcumler\iz\<id>.txt) saklanir. Boylece her olcumun hangi adimda hangi kenari, noktayi, koseyi sectigi sonradan
// okunabilir ve ayni girdilerle birebir yeniden calistirilabilir. Yakalama AsyncLocal ile yalniz olcumu yapan akisa
// (ve onun paralel islerine) baglidir; sunucunun diger konsol satirlari karismaz.
sealed class MeasureTrace : TextWriter
{
    static readonly AsyncLocal<StringBuilder> Current = new AsyncLocal<StringBuilder>();
    static TextWriter _console = Console.Out;
    static bool _echo;

    public override Encoding Encoding => _console.Encoding;

    // Sunucu acilisinda bir kez. Olcum sirasinda KK_DEBUG acilir; satirlar konsola yalniz kullanici KK_DEBUG=1 verdiyse gider
    public static void Install()
    {
        _echo = Environment.GetEnvironmentVariable("KK_DEBUG") == "1";
        _console = Console.Out;
        Console.SetOut(new MeasureTrace());
    }

    public static Scope Begin() => new Scope();

    public sealed class Scope : IDisposable
    {
        readonly StringBuilder _sb = new StringBuilder();
        readonly StringBuilder _prev;
        // Olcumler sunucu kilidi (gate) altinda tek tek yapilir; ortam degiskeni yalniz olcum suresince acik kalir
        public Scope() { _prev = Current.Value; Current.Value = _sb; Environment.SetEnvironmentVariable("KK_DEBUG", "1"); }
        public string Text { get { lock (_sb) return _sb.ToString(); } }
        public void Dispose()
        {
            Current.Value = _prev;
            Environment.SetEnvironmentVariable("KK_DEBUG", _echo ? "1" : null);
        }
    }

    // Cekirdekteki hata ayiklama satirlari degilse (sunucu mesajlari) her zaman konsola gider
    public override void Write(char value)
    {
        StringBuilder sb = Current.Value;
        if (sb != null) { lock (sb) sb.Append(value); if (_echo) _console.Write(value); }
        else _console.Write(value);
    }

    public override void Write(string value)
    {
        StringBuilder sb = Current.Value;
        if (sb != null) { lock (sb) sb.Append(value); if (_echo) _console.Write(value); }
        else _console.Write(value);
    }

    public override void WriteLine(string value)
    {
        StringBuilder sb = Current.Value;
        if (sb != null) { lock (sb) sb.Append(value).Append('\n'); if (_echo) _console.WriteLine(value); }
        else _console.WriteLine(value);
    }

    public override void Flush() => _console.Flush();
}
