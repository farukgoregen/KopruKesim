# Geliştirme notları

Tarih sırasıyla yapılanlar, ölçüm sonuçları ve denenip bırakılan yöntemler. Güncel özet `CLAUDE.md` içinde.

## Durum
- Core testleri geçiyor.
- Vision ve Demo gerçek OpenCvSharp 4.10 ile derlendi ve örnek fotoğrafta (`köprü kesim.jpeg`) doğrulandı (2026-11-11). Eski 3500×2000 varsayımıyla ve kalınlık düzeltmesiz sonuç, testteki konturla birebir aynıydı: (1512,45) (2804,43) (2816,642) (1518,654).
- Gerçek tabla 3300×2000 mm girildi (2026-11-11); 2000 mm sağdaki üç ayrı tahtayı da kapsıyor, Demo'daki sağ köşe pikselleri buna göre taşındı (sağ üst 1776,121; sağ alt 2194,1505). Açık renkli plaka, 20 mm kalınlıkla yaklaşık: (1465,43) (2648,39) (2657,511) (1468,521), yani yaklaşık 1186×475 mm. Plakanın gerçek ölçüsü bilinmiyor; doğrulama için ölçülmeli.
- Bu ayarla plaka 4 yerine 6 köşeli çıkıyor: sağ alt köşede, tahta aralığının maskeye karışmasından gelen 3 mm'lik bir çentik var. Plaka tabla kenarına ~36 mm yakın olduğu için Demo'da kenar şeridi 40 yerine 30 mm.
- Kalınlık düzeltmesi kameradan uzak kenarları (sol ve alt) içeri çeker: 10 ile 30 mm arasında sol kenar yaklaşık 30 mm, alt kenar 6 mm oynuyor.
- Mermer kalınlığı parçaya göre değişir; serbest sayı (0 ve üstü, üst sınır yok): Demo'da sayısal argüman, kodda `TableCalibration.SlabThicknessMm` (her `Detect` öncesi değiştirilebilir).
- Kamera tutarlılık değeri 1,32 (ideal 1; eski köşelerle 2,00 idi): köşe pikselleri hâlâ elle okunmuş, lens bozulması da düzeltilmiyor.
- Referanssız çalışmada siyah plaka yalnızca kısmen bulunuyor, tabladaki aletler de parça sayılıyor (toplam 5 parça).
- `köprü kesim.jpeg` için köşe pikselleri elle okundu ve `ornek/kopru_kesim_kalibrasyon.txt` içinde duruyor (ölçüm: `olc "köprü kesim.jpeg" 20 --kal ornek/kopru_kesim_kalibrasyon.txt --kenar 30`).

## Kurulum ve ölçüm akışı (2026-11-11)
- 0 noktası: fotoğrafta sol üstte görünen tabla köşesi (yatay görünümde sol alt). Kurulumda bir kez belirlenir; operatör seçmez. Makinenin kendi sıfırıyla aynı köşe olmalı (henüz teyit edilmedi).
- `kur` boş tabla ister. Dolu tabloda otomatik bulma yanlış köşe verir ama hata vermez; önizleme görseline bakıp onaylamak şart.
- Doğrulama (`ornek/sanal_parcalar.png`, boş tabla görseline boyanmış ölçüsü bilinen iki dikdörtgen, kalınlık 0): köşeler 4 mm, kenar uzunlukları 2–5 mm içinde; koyu parça referans sayesinde tam bulundu. Gerçek kamera ve gerçek kalınlıkla sınanmadı.
- `--tikla` (pencerede köşe tıklama) yazıldı ama hiç çalıştırılmadı.
- Kalınlık operatörden alınır (0 ve üstü serbest sayı; web'de 0/10/20/30 hızlı düğmeleri); tek kameradan güvenilir hesaplanamıyor.

## Web arayüzü ve mermerli örnek (2026-11-11)
- `ornek/mermer.webp` (beyaz mermer, 20 mm): (1209,360) (2637,362) (2636,1388) (1213,1376), yaklaşık 1426×1022 mm. Gerçek ölçüsü bilinmiyor.
- Bu görsel ile `ornek/bos_tabla.webp` aynı sahne DEĞİL (ayrı üretilmiş, tahta dokusu farklı); referansla karşılaştırma her yerde gürültü veriyor. Bu yüzden arayüzde referans varsayılan olarak kapalı, tabla rengi ortancadan (medyan) tahmin ediliyor (`SetTableColorAuto`). Gerçek sabit kamerada referans açık denenmeli.
- Kameraya bakan kenarda yan yüzey ve gölge kenarı 30 mm kadar dalgalandırıyor; arayüz ve örnek ölçüm `SimplifyToleranceMm=30`, `MinEdgeMm=150` kullanıyor (Demo'da `--puruz 30 --minkenar 150`). O kenarın konumu ±15 mm belirsiz.
- `DetectorOptions.CombinedDiffThreshold` (referans varken parlaklık+renk toplam farkı) eklendi; sanal parçalarda sonucu değiştirmedi.

## Köşe bulma: testere kolu ve soluk tahtalar (2026-11-12)
- Sorun (`ornek/bos_tabla_kol.jpg`, kullanıcının fotoğrafı): sağdaki üç tahta çok soluk (HSV doygunluk 2–17), renk maskesi onları tabla saymıyor; testere kolu da sağ kenarın ortasını kapatıyor. Eski yöntem sağ kenarı ana tablanın içinde buluyordu.
- Çözüm (`TableFinder.FindCorners(frame, uzunluk, genislik)`): kaba dörtgen tutarsızsa her kenar boyunca ortadan dışarı yürünür, parlaklık geçişinin kesildiği yer kenarın gerçek ucudur. Bozuk kenarın köşeleri komşu kenarların bittiği yerden alınır; hangi kenarın bozuk olduğunu bilinen tabla ölçüsüyle tutarlılık seçer (fiziksel olarak anlamsız kamera konumları elenir). Sonra her yeni köşe, kenarın o uca yakın görünen parçasına doğru oturtularak inceltilir.
- Sonuç: sağ üst köşe tahtanın ucuna oturuyor, sağ alt köşe ~5 px (~12 mm) dışarıda. `ornek/bos_tabla.webp` sonucu değişmedi.
- Doğru köşelerle bu fotoğrafta tutarlılık 1,41 çıkıyor: fotoğraf büyük olasılıkla kırpılmış/yeniden boyutlandırılmış, optik merkez ortada değil. Bu durumda tutarlılık ve kalınlık düzeltmesi daha az güvenilir; gerçek kamerada kırpılmamış tam kare kullanılmalı.
- Hata ayıklama: `KK_DEBUG=1` ortam değişkeni adayları konsola yazar.
- Denenip bırakılan: bozuk kenarı kaçış noktası + tutarlılık=1 ile tahmin etmek. Diğer kenarlardaki birkaç piksellik açı hatası tahmini ~30 px kaydırıyor; tahta aralıkları ~60 px olduğu için güvenilmez.

## Kamera kayması uyarısı ve şekil kontrolü (2026-11-12)
- `ornek/dolu_tabla_kol.webp` ile `ornek/bos_tabla_kol.jpg` aynı sahne DEĞİL (boş tabla görüntüsü sonradan hazırlanmış): tabla görüntüde ~25–30 px farklı yerde, tahta dokusu birebir örtüşmüyor. Boş tabladan alınan kalibrasyon dolu fotoğrafta 60–80 mm kaydırıyordu.
- Otomatik hizalama denendi ve bırakıldı: ORB nokta eşleştirme testere koluna kilitlendi (tahta delikleri/aralıkları birbirinin aynısı, eşleşmiyor); ECC yüzey oturtma yakınsamadı; kenarları yeniden arama üst kenarı makine kirişine, sağ kenarı kola kaydırdı. Yanlış hizalama sessizce yanlış sonuç verdiği için hiç yapılmıyor.
- Bunun yerine `TableMeasure.Run` kurulumdaki tabla kenarlarının ölçüm fotoğrafında hâlâ kenar üstünde olup olmadığını ölçer (`TableFinder.EdgeSupport`, referansa oranla). %60'ın altındaysa `CameraMoved` = uyarı; bu durumda referansla karşılaştırma da kapatılır. Uyumsuz çiftte %38, uyumlu üç çiftte %100.
- `SlabDetector` her sonucu sınar (`SlabResult.IsReliable`, `Note`): en çok 8 köşe, kenarlar kesişmiyor, siluet doluluğu ≥ 0,75, kontur noktalarının ≥ %85'i çokgen kenarına (15 mm + kalınlık) yakın. Aletler, kol, yarım görünen siyah plaka "mermer değil" olarak ayrılır; arayüz bunları kesik çizgiyle gösterir, köşe vermez.
- Web ve Demo ölçümü aynı akışı (`TableMeasure`) kullanır.

## Konum işaretleri (ArUco) – kamera kaymasına çözüm (2026-11-12)
- Makineye kesim alanı dışında 6–8 ArUco işareti (sözlük 4x4_50) sabitlenir; her birinin merkezinin tabla koordinatı (mm) `isaretler.txt` dosyasına yazılır (`MarkerLayout`). Kalibrasyon HER fotoğrafta işaretlerden yeniden hesaplanır (`MarkerSolver`, en küçük kareler homografi); kamera oynasa da ölçüm kaymaz.
- En az 4 işaret gerekir. Diğerleriyle 8 mm'den fazla uyuşmayan işaret atılır (yerinden oynamış olabilir), kullanıcıya bildirilir. Yeterli güvenilir işaret yoksa ölçüm VERİLMEZ.
- İşaretler tabla yüzeyinden farklı yükseklikteyse `MarkerHeightMm` (alttaysa eksi) ile kamera konumu tahmin edilerek düzeltilir. Bu düzeltme YAZILDI AMA SINANMADI (sanal denemede yükseklik 0).
- Boş tabla referansı da kendi işaretleriyle üstten görünüme çevrilir; kamera oynasa da karşılaştırma doğru kalır.
- Sanal deneme (`ornek/isaretli/`, `sanal_parcalar.png` üzerine yapıştırılmış 8 işaret; `dolu_kaymis.jpg` = 35 px kayma, 2° dönme, %4 yakınlaşma, hafif perspektif, bir işaret kadraj dışında): köşeler gerçek değerlerden en çok ~3 mm sapıyor, kaymamış görüntüyle aynı. Aynı kaymış görüntüde işaretsiz yöntem 100+ mm kayıyor (uyarı veriyor).
- Basılacak işaretler: `dotnet run --project src/KopruKesim.Demo -- isaret-uret <klasor> [adet] [piksel]`. Ölçüm: `olc foto.jpg 20 --isaret isaretler.txt`. Web: Kurulum bölümünde "İşaret dosyası yükle" (`veri/isaretler.txt`).
- Bilinen kusur: SlabLocator bazen bir kenarın ortasına 0–1 mm'lik sahte kenar ekliyor (6 köşe görünür, gerçek köşeler doğru). Düzeltilmedi.
- Sanal işaret yapıştırırken dikkat: tabla koordinat sistemi görüntüye göre aynalı; işaret sayfası aynalanırsa ArUco okumaz (gerçek basılı işarette sorun yok).

## Hassasiyet çalışması, gerçek masa denemesi (2026-11-12)
- Deneme düzeni: tabla yerine 1500×800 mm masa (`ornek/masa/`), üstünde gri plastik kablo kanalı kapağı. Fotoğraflar telefonla elde çekildi; boş/dolu aynı konumdan değil, kalibrasyon dolu fotoğraftan yapıldı. Masa açık renkli, otomatik köşe bulma (tahta rengine göre) burada çalışmaz.
- `TableFinder.RefineCorners`: kabaca verilen köşeleri, her kenarı ±N px içindeki en güçlü geçişe doğru oturtarak inceltir (yuvarlatılmış köşe uçları dışarıda). Demo: `kur foto --koseler ... --incelt 25`; web: sürüklenen köşeler bırakılınca otomatik. Masada tutarlılık 1,00, kamera yüksekliği ~991 mm çıktı. Yontuk sol alt köşede pencere 20/25 px arasında köşe 10 px oynuyor.
- Üstten görünüm çözünürlüğü artık otomatik (`DetectorOptions.PxPerMm = 0`): kamera görüntüsündeki en ince ölçek korunur (en fazla 16 MP). Eskisi sabit 2 mm/piksel idi.
- Köşe bulma toleransları parça boyuna göre küçülür (`ScaledLocator`): büyük mermerde aynı, küçük parçada kısa kenarlar atılmaz.
- Referanssız, tek renkli tabla için `NoReferenceBrightnessDiff` (Demo `--parlaklik 35`, web "Tabla tek renkli"); web bu seçenekte temizlik 8 mm, en küçük parça 60×60 mm.
- Hata düzeltildi: otomatik köşe bulma bozuk (doğrusal) dörtgen verince web kurulumu çöküyordu; artık `IsValidQuad` eler, kurulum elle yerleştirme önerisine geçer.
- Kapak ölçümü: kalınlık 0 ile ~135×60 mm, 10 mm ile ~122×60 mm, açılar 86–94°. Kapak düz plaka değil (kavisli, açık uçlu profil); gerçek ölçüsü bilinmiyor.
- Demo yeni seçenekler: `--minalan mm2`, `--olcek px/mm`, `--parlaklik N`.

## Renkten bağımsız tabla bulma (2026-11-12)
- `TableFinder.FindByRegion`: görüntü ortasındaki yüzeyin rengine benzeyen, ortayı içeren bölge büyütülür (Lab, parlaklık yarı ağırlık), dörtgene sadeleştirilir, `RefineCorners` ile kenarlara oturtulur. Bölge görüntü kenarına taşarsa ya da doluluğu < 0,9 ise reddedilir.
- `FindCorners` artık adayları (ahşap yöntemi + bölge büyütme, iki tolerans) toplar ve kenar desteği (`EdgeSupport`) en yüksek olanı seçer. Sonuç: iki masa fotoğrafı (tutarlılık 1,00 / 0,96) ve iki ahşap tabla fotoğrafı (eski sonuçlar aynı) doğru.
- Varsayım: tabla fotoğrafın ortasında ve çevresinden renkçe ayrılıyor.

## Kamera kaymışsa tablayı ölçüm fotoğrafında yeniden bulma (2026-11-12)
- `TableMeasure.Run` (işaret yokken): kenar görünürlüğü %60'ın altındaysa tabla ölçüm fotoğrafında `TableFinder.FindCorners` ile yeniden bulunur; bulunan dörtgenin kenar desteği ≥ 0,6 ise ölçüm onunla yapılır (`MeasureOutput.Relocated`, web `tablaYenidenBulundu`). Boş tabla referansı kurulum kalibrasyonuyla üstten görünüme çevrildiği için karşılaştırma da çalışır. Bulunamazsa eski uyarı kalır.
- `FindByRegion`: oy çokluğuyla ortadaki bölge seçilir (tam ortada parça olabilir); ek aday olarak Canny kenarları bariyer yapılır (tabla duvara/beyaz dolaba değiyorsa taşmasın). Altı fotoğrafta da tabla bulundu (`ornek/masa/bos2.jpg`, `kutulu.jpg` dahil).
- Sınırlar: dağınık tabloda (aletler, kol) yeniden bulma başarısız olabiliyor (`dolu_tabla_kol.webp`), o zaman uyarı verilir. Kırpılmış fotoğraflarda tutarlılık 1'den uzak çıkıyor (`bos2.jpg` 3,08) ve kalınlık düzeltmesi daha az güvenilir.
- Beyaz kutu beyaz masada büyük ölçüde ayrılamadı (yalnız yazılar/kenarlar); renk/parlaklığa dayalı ayırmanın sınırı. Kenar tabanlı parça bulma henüz yok.

## Kenar tabanlı parça bulma – yapay zekâsız (2026-11-12)
- `EdgeRectFinder` (Vision): üstten görünümde LSD ile uzun düz çizgiler → her çizginin uçları yürünerek bulunur → paralel karşı kenar, kenar boyunca ortanca geçiş gücü profilinin tepesiyle aranır (zayıf kenar uzunluk boyunca toplanınca belirginleşir) → kısa kenarlar uçların ±25 mm'sinde dik profil tepesiyle → dörtgen. Sınamalar: açılar 70–110°, her kenarın ≥ %60'ı gerçekten kenar (eşik kenarın kendi gücüne göreli), kenar köşenin ötesinde devam etmemeli (tahta aralıkları elenir), iç/dış renk farkı ≥ 6 (damar dörtgenleri ve gölge elenir). Adaylar büyükten küçüğe seçilir (kutu etiketi gibi iç çizgiler dış çerçeveye yenilir).
- `DetectorOptions.EdgeMode`: Off / Fallback (varsayılan; aynı yerde güvenilir renk sonucu yoksa) / Prefer. Sonuç `SlabResult.Method` = "renk" | "kenar"; web "kenarlarından bulundu" gösterir. Demo: `--kenar-yontemi Off|Fallback|Prefer`.
- Ölçülen (gerçek 290×127×45 mm Klemsan kutusu, `ornek/masa/kutulu.jpg`, kalınlık 45): renk yöntemi bulamadı; kenar yöntemi 292–294 × 125–126 mm, açılar 89,4–90,9°. Boş masa ×2 ve boş ahşap tabla: sahte parça yok. Sanal parçalar ve işaretli kaymış görüntü: 805×502 / 1204×602 (önceki gibi).
- Denenip bırakılan: maskesiz kenar haritası + doldurma (damarlara karıştı); sabit kenar eşiği (doku eşiği aşıyor, her şeyi kenar sayıyordu).
- Sınırlar: yalnız dörtgen parçalar; tek gerçek ölçülü nesneyle sınandı; kırık/eğri kenarlı ham plakada renk yöntemi (ya da ileride SAM) gerekir.
