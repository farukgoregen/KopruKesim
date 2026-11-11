# Köprü Kesim – Mermer Konum Bulma

Köprü kesim makinasının tablasına konan mermerin köşe koordinatlarını (mm) kamera görüntüsünden bulan C# projesi. Kullanıcıyla Türkçe konuş; kod tanımlayıcıları İngilizce, yorumlar Türkçe (ASCII).

## Koordinat sistemi
- Orijin (0,0): kamera fotoğrafında sol üstte görünen tabla köşesi; yatay ekranda sol alt.
- X: tablanın uzun kenarı (ekranda sağa), Y: kısa kenar (ekranda yukarı). Birim mm.

## Yapı
- `src/KopruKesim.Core` – bağımlılıksız (netstandard2.0, C# 7.3): Homography, CameraPose, TableCalibration, SlabLocator, PolygonMath.
- `src/KopruKesim.Vision` – OpenCvSharp4: SlabDetector (düzeltme, ayırma, kontur).
- `src/KopruKesim.Demo` – konsol denemesi: `dotnet run --project src/KopruKesim.Demo -- foto.jpg [bos_tabla.jpg]`
- `tests/KopruKesim.Tests` – NuGet'siz test koşucusu: `dotnet run --project tests/KopruKesim.Tests`

## Durum
- Core testleri geçiyor.
- Vision ve Demo gerçek OpenCvSharp 4.10 ile derlendi ve örnek fotoğrafta (`köprü kesim.jpeg`) doğrulandı (2026-11-11). Açık renkli plaka için sonuç (3500×2000 varsayımıyla):
  - Kalınlık düzeltmesiz (kalınlık 0): (1512,45) (2804,43) (2816,642) (1518,654) — testteki konturla birebir aynı.
  - 20 mm kalınlık düzeltmesiyle (Demo'nun varsayılanı): (1536,53) (2804,51) (2816,642) (1541,654). Kameradan uzak kenarlar (sol ve alt) içeri çekilir; fark hata değil.
- Referanssız çalışmada siyah plaka yalnızca kısmen bulunuyor, tabladaki aletler de parça sayılıyor (toplam 5 parça).
- Tabla ölçüleri (3500×2000 mm), kalınlık (20 mm) ve Demo'daki köşe pikselleri VARSAYIMSAL.

## Sıradaki işler
1. ~~Vision + Demo'yu derle, örnek fotoğrafta doğrula.~~ (yapıldı)
2. Gerçek tabla ölçülerini ve kalınlığı gir.
3. Boş tabla referans görüntüsüyle ayırmayı dene (SetReference); koyu/parlak taşlar referanssız güvenilir değil.
4. Kalibrasyon ekranı: operatör 4 tabla köşesini işaretler; kalibrasyonu dosyaya kaydet/yükle.
5. Lens bozulması düzeltmesi.
6. PLC ekranı için arayüz (WinForms/WPF) ve hedef .NET sürümü netleşecek.
