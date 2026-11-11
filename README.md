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
dotnet run --project src/KopruKesim.Demo -- fotograf.jpg [bos_tabla.jpg]
```

## Varsayımlar (güncellenecek)
- Tabla 3500 × 2000 mm, mermer kalınlığı 20 mm (`TableCalibration`).
- Tabla köşe pikselleri örnek fotoğraftan elle okundu (`Demo/Program.cs`).

## Kullanım özeti
```csharp
var cal = new TableCalibration { /* ölçüler, görüntü boyutu, 4 köşe pikseli */ };
cal.Build();
var detector = new SlabDetector(cal, new DetectorOptions());
detector.SetReference(bosTablaGoruntusu);      // önerilen
List<SlabResult> parcalar = detector.Detect(kameraGoruntusu);
// parcalar[0].Vertices -> köşeler (mm), saat yönünün tersine, orijine en yakın köşeden başlar
```
