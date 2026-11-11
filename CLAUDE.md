# Köprü Kesim – Mermer Konum Bulma

Köprü kesim makinasının tablasına konan mermerin köşe koordinatlarını (mm) kamera görüntüsünden bulan C# projesi. Kullanıcıyla Türkçe konuş; kod tanımlayıcıları İngilizce, yorumlar Türkçe (ASCII).

## Koordinat sistemi
- Orijin (0,0): kamera fotoğrafında sol üstte görünen tabla köşesi; yatay ekranda sol alt.
- X: tablanın uzun kenarı (ekranda sağa), Y: kısa kenar (ekranda yukarı). Birim mm.

## Yapı
- `src/KopruKesim.Core` – bağımlılıksız (netstandard2.0, C# 7.3): Homography, CameraPose, TableCalibration, SlabLocator, PolygonMath.
- `src/KopruKesim.Vision` – OpenCvSharp4: SlabDetector (düzeltme, ayırma, kontur), TableFinder (boş tabloda köşeleri otomatik bulur).
- `src/KopruKesim.Demo` – konsol, iki komut:
  - `dotnet run --project src/KopruKesim.Demo -- kur bos_tabla.jpg [--kal dosya] [--koseler ...] [--tikla] [--referanssiz]` – kurulum, bir kez: köşeleri bulur, kalibrasyonu, önizlemeyi ve boş tabla referansını kaydeder.
  - `dotnet run --project src/KopruKesim.Demo -- olc foto.jpg [kalinlik_mm] [--kal dosya] [--kenar mm]` – ölçüm; `sonuc.jpg` yazar.
- `src/KopruKesim.Web` – yerel deneme arayüzü (ASP.NET Core, tek sayfa `wwwroot/index.html`): `dotnet run --project src/KopruKesim.Web --urls http://localhost:5080` ya da `baslat.bat`. Kalibrasyon `src/KopruKesim.Web/veri/` içinde tutulur (git dışı).
- `ornek/` – örnek görseller ve kalibrasyonları (`bos_tabla.webp` gerçek kamera değil, örnek olarak hazırlandı).
- `tests/KopruKesim.Tests` – NuGet'siz test koşucusu: `dotnet run --project tests/KopruKesim.Tests`

## Durum
- Core testleri geçiyor.
- Vision ve Demo gerçek OpenCvSharp 4.10 ile derlendi ve örnek fotoğrafta (`köprü kesim.jpeg`) doğrulandı (2026-11-11). Eski 3500×2000 varsayımıyla ve kalınlık düzeltmesiz sonuç, testteki konturla birebir aynıydı: (1512,45) (2804,43) (2816,642) (1518,654).
- Gerçek tabla 3300×2000 mm girildi (2026-11-11); 2000 mm sağdaki üç ayrı tahtayı da kapsıyor, Demo'daki sağ köşe pikselleri buna göre taşındı (sağ üst 1776,121; sağ alt 2194,1505). Açık renkli plaka, 20 mm kalınlıkla yaklaşık: (1465,43) (2648,39) (2657,511) (1468,521), yani yaklaşık 1186×475 mm. Plakanın gerçek ölçüsü bilinmiyor; doğrulama için ölçülmeli.
- Bu ayarla plaka 4 yerine 6 köşeli çıkıyor: sağ alt köşede, tahta aralığının maskeye karışmasından gelen 3 mm'lik bir çentik var. Plaka tabla kenarına ~36 mm yakın olduğu için Demo'da kenar şeridi 40 yerine 30 mm.
- Kalınlık düzeltmesi kameradan uzak kenarları (sol ve alt) içeri çeker: 10 ile 30 mm arasında sol kenar yaklaşık 30 mm, alt kenar 6 mm oynuyor.
- Mermer kalınlığı parçaya göre değişir (10–30 mm): Demo'da sayısal argüman, kodda `TableCalibration.SlabThicknessMm` (her `Detect` öncesi değiştirilebilir).
- Kamera tutarlılık değeri 1,32 (ideal 1; eski köşelerle 2,00 idi): köşe pikselleri hâlâ elle okunmuş, lens bozulması da düzeltilmiyor.
- Referanssız çalışmada siyah plaka yalnızca kısmen bulunuyor, tabladaki aletler de parça sayılıyor (toplam 5 parça).
- `köprü kesim.jpeg` için köşe pikselleri elle okundu ve `ornek/kopru_kesim_kalibrasyon.txt` içinde duruyor (ölçüm: `olc "köprü kesim.jpeg" 20 --kal ornek/kopru_kesim_kalibrasyon.txt --kenar 30`).

## Kurulum ve ölçüm akışı (2026-11-11)
- 0 noktası: fotoğrafta sol üstte görünen tabla köşesi (yatay görünümde sol alt). Kurulumda bir kez belirlenir; operatör seçmez. Makinenin kendi sıfırıyla aynı köşe olmalı (henüz teyit edilmedi).
- `kur` boş tabla ister. Dolu tabloda otomatik bulma yanlış köşe verir ama hata vermez; önizleme görseline bakıp onaylamak şart.
- Doğrulama (`ornek/sanal_parcalar.png`, boş tabla görseline boyanmış ölçüsü bilinen iki dikdörtgen, kalınlık 0): köşeler 4 mm, kenar uzunlukları 2–5 mm içinde; koyu parça referans sayesinde tam bulundu. Gerçek kamera ve gerçek kalınlıkla sınanmadı.
- `--tikla` (pencerede köşe tıklama) yazıldı ama hiç çalıştırılmadı.
- Kalınlık operatörden alınır (10–30 mm); tek kameradan güvenilir hesaplanamıyor.

## Web arayüzü ve mermerli örnek (2026-11-11)
- `ornek/mermer.webp` (beyaz mermer, 20 mm): (1209,360) (2637,362) (2636,1388) (1213,1376), yaklaşık 1426×1022 mm. Gerçek ölçüsü bilinmiyor.
- Bu görsel ile `ornek/bos_tabla.webp` aynı sahne DEĞİL (ayrı üretilmiş, tahta dokusu farklı); referansla karşılaştırma her yerde gürültü veriyor. Bu yüzden arayüzde referans varsayılan olarak kapalı, tabla rengi ortancadan (medyan) tahmin ediliyor (`SetTableColorAuto`). Gerçek sabit kamerada referans açık denenmeli.
- Kameraya bakan kenarda yan yüzey ve gölge kenarı 30 mm kadar dalgalandırıyor; arayüz ve örnek ölçüm `SimplifyToleranceMm=30`, `MinEdgeMm=150` kullanıyor (Demo'da `--puruz 30 --minkenar 150`). O kenarın konumu ±15 mm belirsiz.
- `DetectorOptions.CombinedDiffThreshold` (referans varken parlaklık+renk toplam farkı) eklendi; sanal parçalarda sonucu değiştirmedi.

## Sıradaki işler
1. ~~Vision + Demo'yu derle, örnek fotoğrafta doğrula.~~ (yapıldı)
2. ~~Gerçek tabla ölçülerini ve kalınlığı gir.~~ (yapıldı: 3300×2000, kalınlık parametre)
3. ~~Boş tabla referans görüntüsüyle ayırmayı dene.~~ (yapıldı, sanal parçalarla)
4. ~~Kalibrasyonu dosyaya kaydet/yükle, köşeleri otomatik bul.~~ (yapıldı; `--tikla` denenmedi)
   - Gerçek sabit kameradan boş + dolu fotoğraf çiftiyle ve ölçüsü bilinen parçayla doğrula.
   - Makinenin sıfır köşesini ve eksen yönlerini teyit et.
5. Lens bozulması düzeltmesi.
6. PLC ekranı için arayüz (WinForms/WPF) ve hedef .NET sürümü netleşecek.
