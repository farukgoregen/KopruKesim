# Köprü Kesim – Mermer Konum Bulma

Kamera fotoğrafından tablaya konan mermerin köşe koordinatlarını (mm) bulan C# projesi. Kullanıcıyla Türkçe konuş; kod tanımlayıcıları İngilizce, yorumlar Türkçe (ASCII). Ayrıntılı geçmiş, ölçüm sonuçları ve denenip bırakılan yöntemler: `docs/gelistirme-notlari.md` (gerektiğinde oku).

## Koordinat sistemi
- Orijin: fotoğrafta sol üstte görünen tabla köşesi (yatay görünümde sol alt). X uzun kenar, Y kısa kenar, mm.
- Makinenin kendi sıfır köşesiyle aynı olduğu henüz teyit edilmedi.

## Yapı
- `src/KopruKesim.Core` (netstandard2.0, C# 7.3, bağımlılıksız): Homography, CameraPose, TableCalibration, SlabLocator (köşe + kalınlık düzeltmesi), PolygonMath, MarkerLayout/MarkerSolver.
- `src/KopruKesim.Vision` (OpenCvSharp4 4.10): TableFinder (tabla köşeleri), SlabDetector (renk yöntemi + şekil kontrolü), EdgeRectFinder (düz kenar yöntemi), MarkerFinder (ArUco), TableMeasure (ortak ölçüm akışı).
- `src/KopruKesim.Demo` (konsol): `kur bos.jpg [--uzunluk --genislik --koseler ... --incelt N]`, `olc foto.jpg [kalinlik] [--kal --isaret --referanssiz --parlaklik --kenar-yontemi ...]`, `isaret-uret klasor`.
- `src/KopruKesim.Web`: yerel arayüz, `baslat.bat` ya da `dotnet run --project src/KopruKesim.Web --urls http://localhost:5080`. Kalibrasyon `veri/` içinde (git dışı).
- `tests/KopruKesim.Tests`: `dotnet run --project tests/KopruKesim.Tests`.
- `ornek/`: deneme görselleri; `masa/kutulu.jpg` gerçek ölçülü (Klemsan kutusu 290×127×45 mm). `bos_tabla*.webp` ve `mermer.webp` hazırlanmış görseller, gerçek kamera değil.
- Hata ayıklama: `KK_DEBUG=1`.

## Ölçüm akışı (özet)
1. Tabla: renk (ahşap) + bölge büyütme (kenar bariyerli) adayları, kenar desteği en yüksek seçilir, `RefineCorners` ile piksel altı oturtma.
2. Kamera kontrolü: kurulum kenarları yerinde değilse tabla ölçüm fotoğrafında yeniden bulunur; olmazsa uyarı. İşaret dosyası varsa kalibrasyon her fotoğrafta işaretlerden.
3. Üstten görünüm (ölçek otomatik).
4. Parça: renk/parlaklık maskesi; güvenilir sonuç yoksa düz kenar yöntemi (`EdgeMode.Fallback`).
5. Köşeler kenarlara doğru oturtularak; kalınlık (operatör girer, 0+) ile uzak kenarlar içeri çekilir.
6. Şekil kontrolü: plakaya benzemeyen nesneler "mermer değil", köşe verilmez.

## Bilinen durum
- Gerçek ölçülü kutu: 293×126 mm (gerçek 290×127). Sanal parçalarda ≤5 mm. Boş tablalarda sahte parça yok.
- Kırpılmış fotoğrafta kamera hesabı (tutarlılık) bozulur, kalınlık düzeltmesi zayıflar.
- Kenar yöntemi yalnız dörtgen parçalar için. İşaret yüksekliği düzeltmesi ve `--tikla` sınanmadı.
- Kameraya bakan kenar ±15 mm belirsiz olabilir; SlabLocator bazen 0–1 mm'lik sahte köşe ekliyor.

## Sıradaki işler
1. Gerçek sabit kamerada boş + dolu fotoğraf çifti ve ölçüsü bilinen birkaç parçayla doğrulama.
2. Makine sıfırı ve eksen yönlerinin teyidi; konum işaretlerinin takılıp ölçülmesi.
3. Lens bozulması kalibrasyonu.
4. Canlı kamerayla sürekli ölçüm; sonucun PLC'ye iletilmesi.
5. PLC ekranı arayüzü (WinForms/WPF) ve hedef .NET sürümü.
