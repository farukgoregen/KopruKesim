# Köprü Kesim – Mermer Konum Bulma

Kamera görüntüsünden tabla üzerindeki mermerin köşe koordinatlarını (mm) bulur.

## Koordinat sistemi
- Orijin (0,0): fotoğrafta sol üstte görünen tabla köşesi; yatay ekranda sol alt.
- X: tablanın uzun kenarı boyunca (ekranda sağa), Y: kısa kenar boyunca (ekranda yukarı).

## Projeler
| Proje | İçerik | Bağımlılık |
|---|---|---|
| `src/KopruKesim.Core` | Homografi, kamera konumu tahmini, köşe bulma, kalınlık düzeltmesi, çokgen hesapları | Yok (netstandard2.0) |
| `src/KopruKesim.Vision` | Görüntüyü düzeltme, mermeri tabladan ayırma, kontur çıkarma | OpenCvSharp4 |
| `src/KopruKesim.Demo` | Fotoğraf üzerinde deneme (konsol) | OpenCvSharp4.runtime.win |
| `tests/KopruKesim.Tests` | Core testleri (NuGet gerektirmez) | Yok |

## Çalıştırma
```
dotnet run --project tests/KopruKesim.Tests
# Kurulum (bir kez, BOS tabla ile): koseleri bulur, kalibrasyon.txt + onizleme + referans kaydeder
dotnet run --project src/KopruKesim.Demo -- kur bos_tabla.jpg
# Olcum: kalinlik mm olarak verilir (verilmezse 20)
dotnet run --project src/KopruKesim.Demo -- olc fotograf.jpg 20
```

## Arayüz (yerel)
`baslat.bat` dosyasına çift tıklayın ya da:
```
dotnet run --project src/KopruKesim.Web --urls http://localhost:5080
```
Sonra tarayıcıda http://localhost:5080 adresini açın. Önce boş tabla fotoğrafıyla kurulum, sonra kalınlık seçip mermer fotoğrafıyla ölçüm yapılır.

## Ölçüler
- Tabla 3300 × 2000 mm (gerçek ölçü).
- Mermer kalınlığı parçaya göre değişir (10–30 mm); Demo'da sayısal argüman olarak verilir, verilmezse 20 mm (`TableCalibration.SlabThicknessMm`).

## Varsayımlar (güncellenecek)
- Kurulumdan sonra `kalibrasyon_onizleme.jpg` gözle kontrol edilmeli; kamera yerinden oynarsa kurulum tekrarlanır.
- Otomatik köşe bulma yalnızca boş tabloda güvenilir; tutmazsa `--tikla` ya da `--koseler x,y x,y x,y x,y` (sol üst = 0 noktası, sağ üst, sağ alt, sol alt).

## Kullanım özeti
```csharp
var cal = new TableCalibration { /* ölçüler, görüntü boyutu, 4 köşe pikseli */ };
cal.Build();
var detector = new SlabDetector(cal, new DetectorOptions());
detector.SetReference(bosTablaGoruntusu);      // önerilen
List<SlabResult> parcalar = detector.Detect(kameraGoruntusu);
// parcalar[0].Vertices -> köşeler (mm), saat yönünün tersine, orijine en yakın köşeden başlar
```
