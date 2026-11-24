# Ölçüm hassasiyetini artırma — araştırma (2026-11-18)

Kural: yapay zekâ (öğrenen model) kullanılmayacak; yalnızca klasik görüntü işleme, kalibrasyon ve donanım.

## Bugünkü durumun teşhisi

Denemeler (veri/olcumler, 2026-11-18):

| Deneme | Sonuç (A4 = 297×210) | Asıl hata kaynağı |
|---|---|---|
| Deneme masası 1500×800, tepeden telefon | 298–299 × 209–212 | Ön kenar kalınlığı, lens |
| Kontrplak masa 2060×1030, alçak açı | 293–301 × 218 (1. çekim 262) | Yanlış kenar (ahşap çizgisi), ön kenar kalınlığı, çok yatık açı |
| Gerçek tabla 3300×2000 | Tutarlılık 2,02 | Tahta eki dış kenar sanıldı; sağ kenar makine kafasıyla kapalı |

Ortak nokta: **ölçeği ve perspektifi tablanın ahşap kenarlarından çıkarmak** güvenilmez. Kenarlar kapanıyor, birden fazla parçadan oluşuyor, kalınlıkları görünüyor ve makinenin sıfırıyla aynı yerde olmayabiliyor. Elle sürüklenen köşe de birkaç piksellik hata taşır.

## KISIT (kullanıcı, 2026-11-18)
Tablaya plaka sabitlemek ve oymak **yasak**. Kalibrasyon tablanın köşelerinin tespitine dayanmalı. Aşağıdaki 1. madde bu yüzden geçersiz; yerine:

- **Lens kalibrasyonu tabla dışında yapılır.** Kamera monte edilmeden önce, düz bir levhaya basılmış ChArUco desenini 15–30 pozda çekerek yapılır; tablaya hiçbir şey konmaz. Bu, ölçümü %1–3 kaydıran bozulmayı kaldırır.
- **Köşe bulma v2:**
  1. Fotoğraf önce bozulmadan arındırılır (undistort).
  2. Her kenar için çok sayıda alt piksel kenar noktası toplanır. Makine kafasının kapattığı kısımlar atlanır, çizgi RANSAC ile görünen parçalardan uydurulur.
  3. Birden fazla aday dörtgenden (tahta ekleri, dış kenar) **girilen tabla ölçüsüyle ve kamera modeliyle en tutarlı olan** seçilir (tutarlılık ≈ 1).
  4. Ön kenarda kalınlık görünüyorsa üst yüzey geçişi seçilir.
  5. Köşe, iki çizginin kesişimi olarak alınır.
- **Sabit kamerada köşeler bir kez, boş tablada ve çok karenin ortalamasıyla hassas bulunur.** Sonraki fotoğraflarda bu köşeler kullanılır, kenar desteğiyle kamera kayması denetlenir.
- **Makinenin lazeriyle doğrulama (tablaya bir şey koymadan):** Köprünün hizalama lazeri tabla köşelerine ve birkaç noktaya götürülür. Kamera lazer noktasını görür (parlak nokta alt pikselde kolay bulunur), CNC koordinatı yazılır. Bu hem köşe tespitinin doğruluğunu ölçer hem de istenirse görüntüden makine koordinatına doğrudan eşleme sağlar.

## Öneriler (kazanç / maliyet sırasıyla)

### 1. Tabla kenarı yerine makine koordinatı bilinen sabit referans plakaları (en büyük kazanç)
- Tablanın çevresine, kesim alanına girmeyen yerlere (şase üstü, köşe dışları) **6–9 adet** düz, rijit dama (ChArUco/satranç) plakası vidalanır. Dört değil daha fazla nokta homografiyi en küçük karelerle sağlamlaştırır; bir plaka kapanırsa diğerleri yeter.
- Her plakanın merkezinin **makine koordinatı bir kez ölçülür**: köprünün lazer imleci (bridge saw'larda standart; kesim hizalama lazeri) plakanın merkez işaretine getirilir, CNC ekranındaki X/Y yazılır. Böylece fotoğraftan doğrudan **makine milimetresi** çıkar; "makine sıfırı tablanın köşesiyle aynı mı" sorusu ortadan kalkar.
- Bizde hazır olan: `CheckerFinder` (dama bulma, alt piksel), `CheckerGridFit` (tüm iç köşelerle en küçük kareler), `MarkerLayout` (etiket yerleşim dosyası). Eklenecek: plaka başına makine koordinatı girişi ve "tabla ölçüsü" yerine bu koordinatlardan kalibrasyon.
- Sektör örneği: Breton Genya'da kamera köprüye tablaya **dik** monte, ayrıca kesim hattını gösteren lazer ve kalınlık sensörü var; slab fotoğrafı yazılımda (Sphera / QuiCut) makine koordinatına oturtuluyor.

### 2. Lens kalibrasyonu (bir kez)
- Düzeltilmemiş lens bozulması geniş açıda ölçümü alan boyunca **%1–3** kaydırabiliyor; 3300 mm'de bu 30–100 mm demek. Tabla kenarlarında gördüğümüz 2–4 px orta bombe bunun izi.
- Yöntem: büyük, düz bir ChArUco tahtası kameranın gördüğü alanın her yerinde (köşeler dahil) 15–30 pozda çekilir; OpenCV `calibrateCameraCharuco` / `calibrateCamera` ile iç parametreler ve bozulma katsayıları bulunur; her fotoğraf önce `undistort` edilir. OpenCvSharp4 4.10'da mevcut.
- Hedef: yeniden izdüşüm hatası ≤ 0,1 px (makine görüş lensinde tipik ~0,07 px, güvenlik kamerası lensinde ~0,2 px).
- Dikkat: kalibrasyon tahtası düz olmalı; 40 cm'de 0,1 mm eğrilik bozulma hesabında ~0,3 px hata yapar.

### 3. Sabit, yüksek, tablaya dik endüstriyel kamera
- Telefonla eğik çekim en büyük hata kaynağı; uzak tarafta 1 piksel derinlikte 2–3 mm'ye karşılık geliyordu.
- Kamera tablanın ortasının üstüne, mümkün olduğunca dik ve yüksek (örnek bir köprü testerede kamera yüksekliği 3800 mm) sabitlenir; kamera hareket ederse kalibrasyon geçersiz olur (bizde "kamera kaydı" uyarısı zaten var).
- Çözünürlük hesabı: 3300 mm tablayı 6000 px yatay görüntüyle (≈24 MP) çekmek ≈ 0,55 mm/px. Alt piksel kenar (0,1–0,2 px) ile kuramsal ≈ 0,1 mm; gerçekçi hedef ±0,5–1 mm. Ticari slab tarayıcılar 32 MP kamerayla 3988×2200 mm alan tarıyor (Park SlabVision).
- Sabit odak, sabit pozlama, düşük bozulmalı (makine görüşü) lens; otomatik odak ve görüntü iyileştirme kapalı.

### 4. Alt piksel kenar bulma + çizgi uydurma
- Canny–Devernay alt piksel kenar noktaları, gürültüsüz koşulda 0,05 px'ten iyi; düz bir kenar boyunca ~1000 nokta toplanıp çizgi uydurulunca hata daha da düşer (her nokta bağımsız gürültü taşıdığı için ortalanır).
- Uygulama: IPOL'deki Canny/Devernay algoritması C#'a taşınır (küçük kod); parça kenarının noktaları RANSAC ile ayıklanıp en küçük kareler çizgisi uydurulur; köşe = iki çizginin kesişimi. Bizdeki `RefineCorners`/`EdgeRectFinder` buna doğru geliştirilir.
- Önce 1–2 numara yapılmalı: lens bozulması düzeltilmeden çizgi uydurmak bombeli kenarı düz sanar.

### 5. Işık
- Eşit, yaygın ışık; gölge ve parlama kenarı kaydırır.
- Cilalı mermer/granit için **çapraz polarizasyon**: ışık kaynağına polarizör levha, objektife 90° döndürülmüş polarizör filtre. Parlak yansımayı keser, taşın dokusunu bırakır (metal olmayan yüzeylerde etkili).
- Tablanın rengi taşla zıt olmalı; tabla temiz tutulmalı (alet, takoz parça bulmayı bozuyor).

### 6. Kalınlık ve paralaks
- Taşın üst yüzü tabladan kalınlık kadar yüksek; kamera ne kadar yüksek ve dikse paralaks o kadar küçük. Kalibrasyonla kameranın konumu (yüksekliği) tam bilinince bizim kalınlık düzeltmesi kesinleşir.

### 7. Ölçüm tekrarı
- Canlı modda birden fazla karenin ortancası (bizde var) rastgele hatayı düşürür; sistematik hatayı (lens, kalibrasyon) düşürmez.

### 8. İleri seçenek: lazer çizgi (yapılandırılmış ışık)
- Kenarı ve kalınlığı lazer çizgisinin kırılmasından ölçer; taş ile tabla aynı renkteyken işe yarar. Ancak cilalı granitte parlak yansıma lazer çizgisini bozabiliyor. İlk aşamada gerekmez.

## Doğrulama yöntemi
- Ölçüsü bilinen (kumpas/şeritle) en az 3 rijit parça, tablanın 9 farklı bölgesine (köşeler, orta) konur; her konumda boyut ve konum hatası tabloya yazılır → hata haritası. Konum doğruluğu, makine lazeri parçanın köşesine getirilip CNC koordinatıyla karşılaştırılarak ölçülür.
- Kabul ölçütü (öneri): boyut ve konum hatası her noktada ≤ ±1 mm.

## Önerilen iş sırası
1. Sabit referans plakaları + makine koordinatından kalibrasyon (yazılım: plaka koordinatı girişi; donanım: plakalar).
2. Lens kalibrasyonu adımı (web arayüzünde "kamera kalibrasyonu" sihirbazı, sonuç `veri/` içinde).
3. Gerçek kamera seçimi ve montajı (sabit, dik, yüksek, ≥ 20 MP, makine görüşü lensi) + ışık/polarizör.
4. Alt piksel kenar + çizgi uydurma.
5. Hata haritasıyla doğrulama.

## Kaynaklar
- [Breton Sphera](https://breton.it/products/software/breton-sphera), [Breton Genya (Stoneworld)](https://www.stoneworld.com/articles/92248-machine-of-the-month-genya-by-breton)
- [DryLayout — slab fotoğraflama ve kalibrasyon](https://drylayout.com/en/blog/photographing-stone-slabs-smartphone-to-scanner)
- [Bondia — köprü testere bakımı (kamera ve lazer kalibrasyonu)](https://www.bondiatools.com/stone-bridge-cutting-saws/), [Shelber 4+1 köprü testere (kamera yüksekliği)](https://shelber.com/revolutionizing-stone-processing-the-ultimate-guide-to-the-41-axis-cnc-bridge-saw/)
- [OpenCV — ChArUco algılama](https://docs.opencv.org/4.13.0/df/d4a/tutorial_charuco_detection.html), [ChArUco ile ArUco karşılaştırması (arXiv 2606.28555)](https://arxiv.org/pdf/2606.28555), [charucal](https://github.com/HeadTriXz/charucal)
- [Steger — alt piksel kenar hassasiyeti](https://mv.in.tum.de/_media/members/steger/publications/1998/isprs-comm-iii-98-steger.pdf), [Canny/Devernay uygulaması](https://www.researchgate.net/publication/321349876_A_Sub-Pixel_Edge_Detector_an_Implementation_of_the_CannyDevernay_Algorithm), [Kalibrasyon harpı (bozulma ölçümü)](https://arxiv.org/pdf/1212.5656)
- [Lens bozulması düzeltmeli düzlem ölçümü (PMC3826339)](https://www.ncbi.nlm.nih.gov/pmc/articles/PMC3826339/), [Homografi ile perspektif düzeltme (MDPI)](https://www.mdpi.com/1424-8220/25/6/1891), [Piksel/alan dönüşümü ve bozulma kayması](https://industrialmonitordirect.com/blogs/knowledgebase/machine-vision-camera-calibration-converting-pixels), [Çözünürlük hesaplayıcı](https://rellingsystems.com/resources/vision/resolution-calculator)
- [Çapraz polarizasyon (VA Imaging)](https://va-imaging.com/en-us/blogs/machine-vision-knowledge-center/polarization-in-your-machine-vision-application), [Parlama için ışık çözümleri (Quality Magazine)](https://www.qualitymag.com/articles/99403-machine-vision-lighting-solutions-for-unwanted-glare)
- [Z-LASER — taş sektöründe lazer](https://z-laser.com/en/industry-solutions/lasers-for-the-stone-glass-and-ceramics-industry/), [Taş yüzey lazer tarayıcı (MDPI)](https://www.mdpi.com/2075-1702/14/10/1225)
