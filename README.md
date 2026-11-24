# Köprü Kesim – Mermer Konum Bulma

Tablanın üstündeki sabit kameranın görüntüsünden tabla üzerindeki mermerin köşe koordinatlarını (mm) bulur.

## Koordinat sistemi
- Orijin (0,0), kamera görüntüsünde sol altta görünen fiziksel tabla köşesidir.
- X tablanın uzun kenarı boyunca sağa, Y kısa kenarı boyunca yukarı artar.

## Projeler
| Proje | İçerik | Bağımlılık |
|---|---|---|
| `src/KopruKesim.Core` | Homografi, kamera konumu, lens kalibrasyonu dosyası, kalınlık düzeltmesi, çokgen hesapları | Yok (netstandard2.0) |
| `src/KopruKesim.Vision` | Lens + tabla kalibrasyonu (damalı / damasız), üstten görünüm, mermeri tabladan ayırma, kontur çıkarma | OpenCvSharp4 |
| `src/KopruKesim.Web` | Yerel ölçüm arayüzü ve IP kamera (Dahua RTSP) | OpenCvSharp4.runtime.win |
| `tests/KopruKesim.Tests` | Testler | Yok |

## Çalıştırma
```
dotnet run --project tests/KopruKesim.Tests
```

## Sunucuyu çalıştırma
Windows'ta `.NET 8 SDK` gerekir. `KopruKesim.cmd` dosyasına çift tıklayın ve **1** ile yerel modu veya **2** ile telefon/tablet için ağ modunu seçin. Sunucu hazır olduğunda `http://localhost:5080` açılır. Ağ modunda telefonun `https://<bilgisayar-ip>:5443` adresi sunucu penceresinde görünür. Durdurmak için `Ctrl+C` kullanın.

Port çakışıyorsa terminalden örneğin `KopruKesim.cmd yerel 5082` veya `KopruKesim.cmd ag 5082 5444` çalıştırın.

Kamera sabittir; kalibrasyon bir kez yapılır, sonra her fotoğraf bu kalibrasyonla düzeltilip ölçülür. İki yöntem vardır:
- **Kalibrasyonlu**: tablanın dört köşesine ve iki uzun kenarının ortasına 10 × 7 kare (25 mm) A4 dama etiketi konur (`ornek/dama/dama_kose_etiketleri_A4.pdf`, %100 / "Gerçek boyut" ile basılmalı), bir fotoğrafla kalibre edilir, sonra damalar kaldırılır. Köşelere mavi paspas konduysa tabla köşesi paspasların dış köşesinden alınır; köşe damaları paspasın altında kalabilir, iki orta dama görünmeli. Lens bu durumda en son altı damalı kalibrasyondan alınır (bir kez, paspassız yapılır).
- **Damasız kalibrasyon**: dama gerekmez. Fotoğrafta dört köşe ve dört kenarın ortası (tablada şeritle işaretlenir) elle işaretlenir.

Her iki yöntemde kalibrasyondan sonra boş tabla bir kez kaydedilir; ölçümde yalnız boş tabladan farklı yerler parça sayılır.

Ağ modunda HTTPS kullanılır. Kimlik doğrulaması bulunmadığı için bu modu güvenilir yerel ağda kullanın.

Kalibrasyonlar ve ölçüm kayıtları `src/KopruKesim.Web/veri/` altında tutulur (git dışı). Ayrıntılı ve güncel durum: `CLAUDE.md`, geçmiş: `docs/gelistirme-notlari.md`.

## Ölçüler
- Tabla ölçüsü kalibrasyonda girilir (atölye tablası 3300 × 2000 mm; şeritle teyit edilmeli).
- Mermer kalınlığı parçaya göre değişir (0 ve üstü, serbest sayı); arayüzde girilir (varsayılan 20 mm). Kâğıt gibi ince nesnede 0 girilmeli.
