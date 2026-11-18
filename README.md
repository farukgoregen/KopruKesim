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
Sonra tarayıcıda http://localhost:5080 adresini açın. Kurulum gerekmez: tabla ölçüsünü ve mermer kalınlığını girip fotoğraf yüklenir. Köşe bulma iki yoldan seçilir:
- **Dama etiketli** (varsayılan, en kesin): tablanın dört köşesine yazdırılmış dama etiketi konur (PDF'ler `ornek/dama/`, arayüzden de indirilebilir). Etiketler %100 / "Gerçek boyut" ile basılmalı.
- **Damasız**: köşeler tabla kenarlarından bulunur; sarı noktalar sürüklenerek düzeltilebilir. Doğru bulunan bir fotoğraf "kayıtlı tabla" (profil) olarak saklanabilir; sonraki fotoğraflarda köşeler yüzey deseni eşleştirilerek taşınır. Taşınan köşeler tabla kenarlarına oturmazsa profil kullanılmaz ve köşeler otomatik bulunur.

Telefon kamerası için ağ modu: `baslat_ag.bat` (https, yerel ağ). Ağ modunda kimlik doğrulama yoktur; yalnız güvenilir yerel ağda kullanın.

Ölçümler, kalibrasyon ve kayıtlı tablalar `src/KopruKesim.Web/veri/` altında tutulur (git dışı). Ayrıntılı ve güncel durum: `CLAUDE.md`, geçmiş: `docs/gelistirme-notlari.md`.

## Ölçüler
- Tabla ölçüsü arayüzde girilir (deneme masası 1500 × 800 mm; atölye tablası 3300 × 2000 mm).
- Mermer kalınlığı parçaya göre değişir (0 ve üstü, serbest sayı); Demo'da sayısal argüman olarak verilir, verilmezse 20 mm (`TableCalibration.SlabThicknessMm`).

## Varsayımlar (Demo / kurulumlu eski akış)
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
