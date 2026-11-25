using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace KopruKesim.Core
{
    /// <summary>
    /// Sabit kamera icin bir kez yapilan kalibrasyon: lens (ic parametreler ve bukulme katsayilari) ve kameranin
    /// tablaya gore konumu. Her olcumde fotograf bu modelle dogrudan duz ustten gorunume cevrilir (genis acili
    /// lensin bukuklugu boylece gider).
    ///
    /// Koordinat sistemi: orijin fotografta sol altta gorunen tabla kosesi (kullanicinin makine sifiri, 2026-11-20),
    /// X tablanin uzun kenari boyunca (fotografta saga), Y kisa kenar boyunca (fotografta yukari). mm.
    /// Kamera modeli OpenCV ile aynidir: x_kamera = R * x_tabla + t; piksel = K * bukulme(x_kamera / z).
    /// </summary>
    public sealed class LensCalibration
    {
        public double TableLengthMm, TableWidthMm;
        public int ImageWidth, ImageHeight;
        /// <summary>Ic parametreler (piksel).</summary>
        public double Fx, Fy, Cx, Cy;
        /// <summary>Bukulme katsayilari, OpenCV sirasiyla: k1, k2, p1, p2, k3 (ve varsa k4, k5, k6).</summary>
        public double[] Distortion = new double[5];
        /// <summary>Donme (Rodrigues vektoru) ve oteleme (mm): tabla -> kamera.</summary>
        public double[] Rvec = new double[3], Tvec = new double[3];
        /// <summary>Etiket koselerinin yeniden izdusum hatasi (piksel) ve tabla uzerindeki hatasi (mm).</summary>
        public double RmsPx, MeanMm, MaxMm;
        public const double MaxReferenceErrorMm = 3;
        public bool MeetsReferenceTolerance =>
            Points > 0 && !double.IsNaN(MaxMm) && MaxMm >= 0 && MaxMm <= MaxReferenceErrorMm
            && !double.IsNaN(MeanMm) && MeanMm >= 0 && MeanMm <= MaxMm;
        /// <summary>Elle ölçülen dama deseni merkezleri (mm), üst üç ve alt üç, soldan sağa.</summary>
        public Point2[] MeasuredTagCenters;
        /// <summary>
        /// Kalibrasyonda bulunan her damanin gercek yeri ve acisi (tabla mm), ust uc ve alt uc soldan saga. Eski
        /// kalibrasyon dosyalarinda yoktur (null).
        /// </summary>
        public BoardPlacement[] BoardPlacements;
        /// <summary>
        /// Kose kagitlarinin dis koseleri (tabla mm): sol ust (1), sag ust (3), sol alt (4), sag alt (6). Tabla cercevesi
        /// bunlardan kurulur: orijin sol alt kagidin kosesi, X ekseni sag alt kagidin kosesine dogru. Elle merkez
        /// girildiyse ya da eski dosyada null.
        /// </summary>
        public Point2[] PaperCorners;
        /// <summary>Tabla koselerinin kaynagi: "kagit" (kose kagitlarinin dis koseleri) ya da "mavi" (mavi paspaslarin dis koseleri).</summary>
        public string CornerSource = CornerSourcePaper;
        public const string CornerSourcePaper = "kagit", CornerSourceMats = "mavi", CornerSourceL = "L";
        /// <summary>
        /// Modelin tabladaki kalan hatasini seritle olculen tablaya gore duzelten alan (paspasli kalibrasyon); yoksa null.
        /// LensCalibrator.ImageToTable / TableToImage bunu kendisi uygular: disaridan bakinca tabla mm'si gercek tabladir.
        /// </summary>
        public TableCorrection Correction;
        /// <summary>
        /// Lensin bukulme modelinden kalan radyal hata (1/mm^2, tabla duzleminde, optik eksenin tablayi kestigi nokta
        /// etrafinda): kameranin sabit ozelligi, kamera-lens.txt'de saklanir ve her paspasli kalibrasyonda tabla
        /// duzeltmesine (TableCorrection.E) girer. 2026-11-24: siyah plakanin bes konumdaki olcumlerinden bir kez
        /// hesaplandi (-1,64e-9); yoksa 0 (duzeltme yalniz koseleri oturtur).
        /// </summary>
        public double ResidualRadial;
        /// <summary>
        /// L kose kalibrasyonunda L parcasinin dis duvari (mm): tablanin koseleri L'nin dis kenarlarinin bu kadar icinde,
        /// seritle olculen L dis koseleri de bu kadar iceri alinarak gercek tabla koseleri bulunur. 0: eski kayit (L'nin dis
        /// kosesi tabla kosesi sayiliyordu).
        /// </summary>
        public double LWallMm;

        /// <summary>Ayni kalibrasyonun duzeltmesiz kopyasi (modelin ham ciktisi gerektiginde).</summary>
        public LensCalibration WithoutCorrection()
        {
            var c = (LensCalibration)MemberwiseClone();
            c.Correction = null;
            return c;
        }

        /// <summary>
        /// Tablasi her yandan marginMm genisletilmis kopya: yeni tabla mm'si = eski + marginMm (eski 0,0 yeni
        /// marginMm,marginMm olur), boyu ve eni 2 * marginMm buyur. Olcumde parca tablanin kenarinin biraz disinda da
        /// aranabilsin diye (cita uclari duz degil: ortadaki citalar kose citalarindan 15-20 mm disari tasabiliyor).
        /// </summary>
        public LensCalibration WithTableMargin(double marginMm)
        {
            var c = (LensCalibration)MemberwiseClone();
            if (marginMm == 0) return c;
            var shift = new Point2(marginMm, marginMm);
            c.TableLengthMm = TableLengthMm + 2 * marginMm;
            c.TableWidthMm = TableWidthMm + 2 * marginMm;
            if (PaperCorners != null) c.PaperCorners = PaperCorners.Select(p => p + shift).ToArray();
            Point2[] model = Correction != null ? Correction.Measured
                : new[] { new Point2(0, 0), new Point2(TableLengthMm, 0), new Point2(0, TableWidthMm), new Point2(TableLengthMm, TableWidthMm) };
            Point2[] truth = Correction != null ? Correction.True : model;
            c.Correction = new TableCorrection(model, truth.Select(p => p + shift).ToArray(),
                Correction != null ? Correction.Center : new Point2(TableLengthMm / 2, TableWidthMm / 2), Correction != null ? Correction.E : 0,
                Correction?.EdgeCurve);   // kenar egrisi (u, v) parametresine bagli: kaydirmada ayni kalir
            return c;
        }

        /// <summary>Kagit koselerinin girilen tabla dikdortgeninin koselerinden en buyuk uzakligi (mm); bilinmiyorsa NaN.</summary>
        public double PaperCornerDeviationMm()
        {
            if (PaperCorners == null || PaperCorners.Length != 4) return double.NaN;
            Point2[] nominal = { new Point2(0, TableWidthMm), new Point2(TableLengthMm, TableWidthMm), new Point2(0, 0), new Point2(TableLengthMm, 0) };
            return Enumerable.Range(0, 4).Max(i => Point2.Distance(PaperCorners[i], nominal[i]));
        }
        public int Points;
        /// <summary>Damasiz kalibrasyonda tabla kenar noktalarinin modelden sapmasi (mm, rms); damali kalibrasyonda NaN.</summary>
        public double EdgeRmsMm = double.NaN;
        public string TagType = "";
        public DateTime Created = DateTime.Now;

        /// <summary>Donme matrisi (satir sirali 3x3), Rodrigues formuluyle.</summary>
        public double[] Rotation() => Rotations.FromRvec(Rvec);

        /// <summary>
        /// Kameranin tabla koordinatindaki yeri: C = -R^T t. Tabla yuzeyinden yukseklik |Z| (kamera tablanin hangi
        /// tarafinda olursa olsun pozitif). Kalinlik duzeltmesi icin.
        /// </summary>
        public CameraPose CameraPosition()
        {
            double[] r = Rotation();
            double x = -(r[0] * Tvec[0] + r[3] * Tvec[1] + r[6] * Tvec[2]);
            double y = -(r[1] * Tvec[0] + r[4] * Tvec[1] + r[7] * Tvec[2]);
            double z = -(r[2] * Tvec[0] + r[5] * Tvec[1] + r[8] * Tvec[2]);
            return new CameraPose(x, y, Math.Abs(z)) { FocalPx = (Fx + Fy) / 2 };
        }

        public void Save(string path)
        {
            var c = CultureInfo.InvariantCulture;
            Func<double[], string> arr = a => string.Join(",", a.Select(v => v.ToString("R", c)));
            var lines = new List<string>
            {
                "# Kopru kesim kamera kalibrasyonu (lens + tabla). Kamera ya da odak degisirse yeniden yapilmali.",
                "Tarih=" + Created.ToString("yyyy-MM-dd HH:mm:ss", c),
                "TableLengthMm=" + TableLengthMm.ToString("R", c),
                "TableWidthMm=" + TableWidthMm.ToString("R", c),
                "Goruntu=" + ImageWidth.ToString(c) + "x" + ImageHeight.ToString(c),
                "K=" + arr(new[] { Fx, Fy, Cx, Cy }),
                "Bukulme=" + arr(Distortion),
                "Rvec=" + arr(Rvec),
                "Tvec=" + arr(Tvec),
                "Hata=" + arr(new[] { RmsPx, MeanMm, MaxMm }),
                "Nokta=" + Points.ToString(c),
                "Etiket=" + TagType
            };
            if (!double.IsNaN(EdgeRmsMm))
                lines.Add("KenarMm=" + EdgeRmsMm.ToString("R", c));
            if (MeasuredTagCenters != null)
                lines.Add("DamaMerkezleri=" + arr(MeasuredTagCenters.SelectMany(p => new[] { p.X, p.Y }).ToArray()));
            if (BoardPlacements != null)
                lines.Add("DamaYerlesimi=" + arr(BoardPlacements.SelectMany(p => new[] { p.X, p.Y, p.RotationDeg }).ToArray()));
            if (PaperCorners != null)
                lines.Add("KagitKoseleri=" + arr(PaperCorners.SelectMany(p => new[] { p.X, p.Y }).ToArray()));
            lines.Add("KoseKaynagi=" + CornerSource);
            if (ResidualRadial != 0)
                lines.Add("KalanBukulme=" + ResidualRadial.ToString("R", c));
            if (LWallMm != 0)
                lines.Add("LDuvarMm=" + LWallMm.ToString("R", c));
            if (Correction != null)
                lines.Add("TablaDuzeltme=" + arr(Correction.ToValues()));
            if (Correction?.EdgeCurve != null)
                lines.Add("TablaKenarEgrisi=" + arr(Correction.EdgeCurve));
            System.IO.File.WriteAllLines(path, lines);
        }

        public static LensCalibration Load(string path)
        {
            var c = CultureInfo.InvariantCulture;
            var cal = new LensCalibration();
            Func<string, double[]> arr = s => s.Split(',').Select(v => double.Parse(v, c)).ToArray();
            double[] edgeCurve = null;
            foreach (string raw in System.IO.File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq < 0) throw new FormatException("Kalibrasyon satiri anlasilamadi: " + line);
                string key = line.Substring(0, eq), val = line.Substring(eq + 1);
                switch (key)
                {
                    case "Tarih": cal.Created = DateTime.ParseExact(val, "yyyy-MM-dd HH:mm:ss", c); break;
                    case "TableLengthMm": cal.TableLengthMm = double.Parse(val, c); break;
                    case "TableWidthMm": cal.TableWidthMm = double.Parse(val, c); break;
                    case "Goruntu":
                        string[] wh = val.Split('x');
                        cal.ImageWidth = int.Parse(wh[0], c); cal.ImageHeight = int.Parse(wh[1], c);
                        break;
                    case "K":
                        double[] k = arr(val);
                        if (k.Length != 4) throw new FormatException("K 4 deger olmali.");
                        cal.Fx = k[0]; cal.Fy = k[1]; cal.Cx = k[2]; cal.Cy = k[3];
                        break;
                    case "Bukulme": cal.Distortion = arr(val); break;
                    case "Rvec": cal.Rvec = arr(val); break;
                    case "Tvec": cal.Tvec = arr(val); break;
                    case "Hata":
                        double[] e = arr(val);
                        if (e.Length != 3) throw new FormatException("Hata 3 deger olmali.");
                        cal.RmsPx = e[0]; cal.MeanMm = e[1]; cal.MaxMm = e[2];
                        break;
                    case "DamaMerkezleri":
                        double[] coordinates = arr(val);
                        if (coordinates.Length != 12 || coordinates.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                            throw new FormatException("DamaMerkezleri 12 sonlu deger olmali.");
                        cal.MeasuredTagCenters = Enumerable.Range(0, 6)
                            .Select(i => new Point2(coordinates[2 * i], coordinates[2 * i + 1])).ToArray();
                        break;
                    case "DamaYerlesimi":
                        double[] pl = arr(val);
                        // Gorunmeyen (mavi paspasin altinda kalan) damalar NaN
                        if (pl.Length != 18 || pl.Any(v => double.IsInfinity(v)) || pl.All(double.IsNaN))
                            throw new FormatException("DamaYerlesimi 18 deger olmali.");
                        cal.BoardPlacements = Enumerable.Range(0, 6).Select(i => new BoardPlacement(pl[3 * i], pl[3 * i + 1], pl[3 * i + 2])).ToArray();
                        break;
                    case "KagitKoseleri":
                        double[] pc = arr(val);
                        if (pc.Length != 8 || pc.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                            throw new FormatException("KagitKoseleri 8 sonlu deger olmali.");
                        cal.PaperCorners = Enumerable.Range(0, 4).Select(i => new Point2(pc[2 * i], pc[2 * i + 1])).ToArray();
                        break;
                    case "KenarMm": cal.EdgeRmsMm = double.Parse(val, c); break;
                    case "KoseKaynagi": cal.CornerSource = val == CornerSourceMats ? CornerSourceMats : val == CornerSourceL ? CornerSourceL : CornerSourcePaper; break;
                    case "KalanBukulme": cal.ResidualRadial = double.Parse(val, c); break;
                    case "LDuvarMm": cal.LWallMm = double.Parse(val, c); break;
                    case "TablaDuzeltme": cal.Correction = TableCorrection.FromValues(arr(val)); break;
                    case "TablaKenarEgrisi": edgeCurve = arr(val); break;
                    case "Nokta": cal.Points = int.Parse(val, c); break;
                    case "Etiket": cal.TagType = val; break;
                    default: break;   // ileride eklenecek alanlar eski surumu bozmasin
                }
            }
            if (cal.ImageWidth <= 0 || cal.Fx <= 0 || cal.Rvec.Length != 3 || cal.Tvec.Length != 3)
                throw new FormatException("Kalibrasyon dosyasi eksik: " + path);
            // Kenar egrisi tabla duzeltmesine baglidir (satir sirasi onemli olmasin diye sonda kurulur)
            if (edgeCurve != null && cal.Correction != null)
            {
                TableCorrection k = cal.Correction;
                cal.Correction = new TableCorrection(k.Measured, k.True, k.Center, k.E, edgeCurve);
            }
            return cal;
        }
    }
}
