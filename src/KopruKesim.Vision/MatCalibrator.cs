using System;
using System.Collections.Generic;
using System.Linq;
using KopruKesim.Core;
using OpenCvSharp;

namespace KopruKesim.Vision
{
    /// <summary>Tablanin seritle olculen dort kenari (paspaslarin dis koseleri arasi, mm) ve istege bagli kosegen.</summary>
    public sealed class TableSides
    {
        public double Bottom, Top, Left, Right;
        /// <summary>Sol alt -> sag ust kosegen; bilinmiyorsa NaN (sol alt kose dik kabul edilir).</summary>
        public double Diagonal = double.NaN;
    }

    /// <summary>
    /// 6 paspasli kalibrasyon: alt ve ust kenarin ortasina birer paspas daha. Paspasin uzun dis kenari tabla kenarina
    /// dayali; deger, o kenar boyunca sol tabla kosesinden paspasin sol dis kosesine seritle olculen uzaklik (mm).
    /// </summary>
    public sealed class MidMats
    {
        public double BottomX = double.NaN, TopX = double.NaN;
        public bool Any => !double.IsNaN(BottomX) || !double.IsNaN(TopX);
    }

    /// <summary>Kose paspasinin olculeri: uzun kenar tablanin uzun kenari (X) boyunca.</summary>
    public sealed class MatSpec
    {
        public double LongMm = 435, ShortMm = 150, ThicknessMm = 4.5;
    }

    /// <summary>
    /// Yalniz dort mavi paspasla kalibrasyon (dama gerekmez). Paspaslar tablanin koselerine tam oturur: dis kosesi tabla
    /// kosesi, kenarlari tablanin kenarlari boyunca. Tabla koseleri seritle olculen dort kenardan (ve varsa kosegenden)
    /// kurulur; tabla tam dikdortgen sayilmaz. Boylece her paspasin dort kosesinin tabladaki yeri bilinir (16 nokta,
    /// paspasin ust yuzeyi yuksekliginde).
    ///
    /// Lens (odak, k1 k2 k3) bu fotograftan cozulmez, kameranin kayitli lens bilgisinden alinir: noktalar yalniz tablanin
    /// koselerinde oldugu icin lens zayif belirlenir (eski paspas denemesinde olcek %18 kaymisti). Lens kameranin sabit
    /// ozelligidir. Fotograftan yalniz kameranin konumu cozulur: paspas kenarlari bukulmesi giderilerek duz cizgilere
    /// oturtulur, komsu kenarlarin kesisimi paspas koseleridir, 16 kose ile kamera konumu (solvePnP).
    /// </summary>
    public static class MatCalibrator
    {
        /// <summary>Lensin paspaslarla duzeltilmesi: "0" kapali, "k1", "k1k2", "fk1k2" (serbest birakilan parametreler).</summary>
        public static string RefineLens = "0";
        /// <summary>Kamera konumu yalniz paspaslarin dis koselerinden (tabla koseleri) cozulur; paspas olcusu yalniz kenarlari
        /// bulmaya yardim eder.</summary>
        public static bool FrameFromPrior = Environment.GetEnvironmentVariable("KK_PASPAS_CERCEVE") != "0";
        public static bool OuterCornersOnly =Environment.GetEnvironmentVariable("KK_PASPAS_DIS") != "0";
        /// <summary>Yalniz hata ayiklama: verilirse paspas koseleri bu kalibrasyonla da tablaya izdusurulup yazilir.</summary>
        public static LensCalibration DebugReference;
        public sealed class Result
        {
            public LensCalibration Calibration;
            public string Error;
            /// <summary>Her paspasin (sol ust, sag ust, sol alt, sag alt) koselerindeki en buyuk hata (mm).</summary>
            public double[] MatErrorMm;
            /// <summary>Bulunan paspas sayisi (hata mesaji icin).</summary>
            public int MatsFound;
            /// <summary>Kameranin konumu kayitli kalibrasyondan alindi (paspaslar yalniz cerceveyi belirledi).</summary>
            public bool UsedPriorPose;
            /// <summary>Paspaslarin dis koselerinin fotograftan bulunan yeri (tabla mm): sol ust, sag ust, sol alt, sag alt.</summary>
            public Point2[] PhotoCorners;
            /// <summary>Tabla duzeltmesinin tablada en buyuk kaydirmasi (mm).</summary>
            public double CorrectionMaxMm = double.NaN;
            /// <summary>6 paspasta: orta paspaslarin dis koselerinin duzeltmeden sonra kalan hatasi (mm; alt sol, alt sag, ust sol, ust sag).</summary>
            public double[] MidErrorMm;
            /// <summary>6 paspasta: yalniz kose duzeltmesiyle (kayitli kalan bukulme) orta dis koselerin hatasi (mm).</summary>
            public double[] MidRawErrorMm;
            /// <summary>6 paspasta bulunan kalan bukulme katsayisi.</summary>
            public double FittedRadial = double.NaN;
        }

        /// <summary>Tabla koseleri (mm): sol ust, sag ust, sol alt, sag alt. Orijin sol alt, X sag alta dogru.</summary>
        public static Point2[] TableCorners(TableSides s, out string error)
        {
            error = null;
            var bl = new Point2(0, 0);
            var br = new Point2(s.Bottom, 0);
            Point2 tl, tr;
            if (!double.IsNaN(s.Diagonal) && s.Diagonal > 0)
            {
                // Sag ust: sol alttan kosegen, sag alttan sag kenar uzakliginda (yukarida)
                if (!Circles(bl, s.Diagonal, br, s.Right, p => p.Y > 0, out tr)) { error = "Köşegen, alt ve sağ kenarla bir üçgen oluşturmuyor."; return null; }
                // Sol ust: sol alttan sol kenar, sag ustten ust kenar uzakliginda (kosegenin solunda)
                if (!Circles(bl, s.Left, tr, s.Top, p => Cross(tr - bl, p - bl) > 0, out tl)) { error = "Köşegen, sol ve üst kenarla bir üçgen oluşturmuyor."; return null; }
            }
            else
            {
                tl = new Point2(0, s.Left);
                if (!Circles(tl, s.Top, br, s.Right, p => Cross(br - tl, p - tl) > 0, out tr)) { error = "Üst ve sağ kenar uzunlukları tabla köşesini kapatmıyor."; return null; }
            }
            return new[] { tl, tr, bl, br };
        }

        /// <summary>Paspasin dort kosesi (tabla mm). corner: 0 sol ust, 1 sag ust, 2 sol alt, 3 sag alt.</summary>
        public static Point2[] MatCorners(Point2[] table, int corner, MatSpec spec)
        {
            Point2 c = table[corner];
            Point2 h = table[corner ^ 1];          // ayni uzun kenardaki komsu (0<->1, 2<->3)
            Point2 v = table[(corner + 2) % 4];    // ayni kisa kenardaki komsu (0<->2, 1<->3)
            Point2 u = (h - c) * (1 / (h - c).Length);
            var n = new Point2(-u.Y, u.X);
            if (Point2.Dot(n, v - c) < 0) n = n * -1;   // tablanin icine
            return new[] { c, c + u * spec.LongMm, c + u * spec.LongMm + n * spec.ShortMm, c + n * spec.ShortMm };
        }

        /// <summary>Orta paspasin dort kosesi (tabla mm): 0 sol dis, 1 sag dis (tabla kenarinda), 2 sag ic, 3 sol ic.</summary>
        public static Point2[] MidMatCorners(Point2[] table, bool top, double x, MatSpec spec)
        {
            Point2 a = top ? table[0] : table[2], b = top ? table[1] : table[3], other = top ? table[2] : table[0];
            Point2 u = (b - a) * (1 / (b - a).Length);
            var n = new Point2(-u.Y, u.X);
            if (Point2.Dot(n, other - a) < 0) n = n * -1;   // tablanin icine
            Point2 c = a + u * x;
            return new[] { c, c + u * spec.LongMm, c + u * spec.LongMm + n * spec.ShortMm, c + n * spec.ShortMm };
        }

        public static Result Calibrate(Mat frame, TableSides sides, MatSpec spec, LensCalibration lens, MidMats mids = null)
        {
            var res = new Result();
            lens = lens?.WithoutCorrection();   // eski duzeltme modelin ham ciktisini bozmasin
            LensCalibration prior = lens;
            int w = frame.Cols, hgt = frame.Rows;
            if (lens == null || lens.ImageWidth != w || lens.ImageHeight != hgt)
            {
                res.Error = string.Format("Bu kamera icin {0}x{1} cozunurlukte kayitli lens bilgisi yok.", w, hgt);
                return res;
            }
            Point2[] table = TableCorners(sides, out string geoError);
            if (table == null) { res.Error = geoError; return res; }

            List<BlueMatFinder.BlueMat> found = BlueMatFinder.Find(frame, BlueMatFinder.WhiteReference(frame, Enumerable.Empty<Point2[]>()));
            res.MatsFound = found.Count;
            BlueMatFinder.BlueMat[] mats = PickByPrior(found, table, spec, lens, w) ?? BlueMatFinder.PickCorners(found);
            if (mats == null) { res.Error = string.Format("Mavi paspas {0} yerde bulundu; dört köşede de tam görünmeli.", found.Count); return res; }
            if (Debug)
                using (Mat vis = frame.Clone())
                {
                    foreach (var m in mats)
                    {
                        Cv2.Polylines(vis, new[] { m.Quad.Select(p => new Point((int)p.X, (int)p.Y)).ToArray() }, true, Scalar.Red, 2);
                        foreach (var sd in m.Sides) foreach (var p in sd) Cv2.Circle(vis, new Point((int)p.X, (int)p.Y), 3, Scalar.Lime, -1);
                    }
                    vis.SaveImage(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kk_paspas.jpg"));
                }
            if (Debug) foreach (var m in mats) Console.WriteLine("  secilen paspas merkez {0:F0},{1:F0} kenar noktalari {2}", m.Center.X, m.Center.Y, string.Join("/", m.Sides.Select(sd => sd.Length)));
            // Kaba tabla -> goruntu donusumu (bukulme yok sayilir): yalniz goruntudeki paspas koselerini modeldekilerle eslemek icin
            var middle = new Point2(mats.Average(m => m.Center.X), mats.Average(m => m.Center.Y));
            Point2[] outer = mats.Select(m => BlueMatFinder.OuterImageCorner(m, middle)).ToArray();
            Homography rough = Homography.FromPoints(table.ToList(), outer.ToList());
            bool usePrior = lens.Rvec != null && lens.Tvec != null && !lens.Tvec.All(v => v == 0);

            Func<Point2, Point2> undist = p => { Point2 q = LensCalibrator.Undistort(lens, p); return new Point2(lens.Cx + lens.Fx * q.X, lens.Cy + lens.Fy * q.Y); };
            var obj = new List<Point3f>(); var img = new List<Point2f>();
            var modelCorners = new Point2[4][];
            var imageCorners = new Point2[4][];   // bukulmesi giderilmis piksel
            var sideLines = new List<Tuple<Point2[], Point2, Point2>>();   // goruntu kenar noktalari + modeldeki kenar uclari
            double h = spec.ThicknessMm;
            double[] rvec = null, tvec = null;
            // Iki tur: once tahmini konumla (kayitli kalibrasyon ya da kaba donusum), sonra ilk turun konumuyla kenar
            // noktalari tahmini kenarlarin yakinindan yeniden toplanir (paspasa yapisan golge/leke disarida kalir)
            string refine = Environment.GetEnvironmentVariable("KK_PASPAS_LENS") ?? RefineLens;
            int passes = refine == "0" ? 2 : 5;
            for (int pass = 0; pass < passes; pass++)
            {
            if (pass >= 2 && refine != "0")
            {
                // Lensi de duzelt: paspas koseleri (16 nokta, tablanin dort kosesinde, yerleri bilinen) ile tek gorunumden
                // kalibrasyon; baslangic kayitli lens. Tabla geometrisi tam bilindigi icin olcek kaymaz (eski paspas
                // denemesinde tabla olcusu de serbestti). Koseler paspas yuzeyi duzleminde (z = 0 kabul, kalinlik sonra).
                var objP = obj.Select(p => new Point3f(p.X, p.Y, 0)).ToArray();
                var imgP = imageCorners.SelectMany(c => c).Select(q => Distort(lens, q)).Select(q => new Point2f((float)q.X, (float)q.Y)).ToArray();
                var cam = new double[3, 3] { { lens.Fx, 0, lens.Cx }, { 0, lens.Fy, lens.Cy }, { 0, 0, 1 } };
                double[] d0 = lens.Distortion;
                var dist = new double[] { d0.Length > 0 ? d0[0] : 0, d0.Length > 1 ? d0[1] : 0, 0, 0, d0.Length > 4 ? d0[4] : 0 };
                var flags = CalibrationFlags.UseIntrinsicGuess | CalibrationFlags.FixAspectRatio | CalibrationFlags.ZeroTangentDist | CalibrationFlags.FixK3;
                if (!refine.Contains("c")) flags |= CalibrationFlags.FixPrincipalPoint;
                if (!refine.Contains("k2")) flags |= CalibrationFlags.FixK2;
                if (!refine.Contains("f")) flags |= CalibrationFlags.FixFocalLength;
                double rms = Cv2.CalibrateCamera(new[] { objP }, new[] { imgP }, new Size(w, hgt), cam, dist, out Vec3d[] rv, out Vec3d[] tv, flags,
                    new TermCriteria(CriteriaTypes.Count | CriteriaTypes.Eps, 200, 1e-10));
                if (Debug) Console.WriteLine("  lens duzeltme ({0}): rms {1:F2} px, f {2:F1}, c {3:F0},{4:F0}, k {5}", refine, rms, cam[0, 0], cam[0, 2], cam[1, 2], string.Join(" ", dist.Select(v => v.ToString("F4"))));
                lens = new LensCalibration
                {
                    ImageWidth = w, ImageHeight = hgt, Fx = cam[0, 0], Fy = cam[1, 1], Cx = cam[0, 2], Cy = cam[1, 2], Distortion = dist,
                    // Yeni lensle uyumlu konum (paspas yuzeyi duzlemine gore; kalinlik farki tahmin icin onemsiz)
                    Rvec = new[] { rv[0].Item0, rv[0].Item1, rv[0].Item2 }, Tvec = new[] { tv[0].Item0, tv[0].Item1, tv[0].Item2 }
                };
                rvec = lens.Rvec; tvec = lens.Tvec;
            }
            obj.Clear(); img.Clear(); sideLines.Clear();
            LensCalibration guess = pass == 0 ? (usePrior ? lens : null) : WithPose(lens, rvec, tvec);
            double tol = pass == 0 ? 80 : 25;   // ilk tahmin paspaslarin eski yerine gore: 40-60 px kayik olabilir
            imageCorners = new Point2[4][];
            for (int m = 0; m < 4; m++)
            {
                Point2[] model = MatCorners(table, m, spec);
                BlueMatFinder.BlueMat work = mats[m];
                int[] match;
                if (guess != null)
                {
                    // Model koselerinin fotograftaki tahmini; kenar noktalari bu tahmini kenarlara gore toplanir
                    Point2[] proj = LensCalibrator.TableToImage(guess, model, h);
                    work = BlueMatFinder.Resample(mats[m], proj, tol);
                    match = new[] { 0, 1, 2, 3 };
                }
                else
                {
                    // Kaba donusum: goruntudeki koseler (Quad, sirali) model koselerine dongusel sirayla eslenir: 4 kaydirma
                    // x 2 yon icinden toplam uzakligi en kucuk olan (birebir esleme garanti)
                    Point2[] proj = model.Select(rough.Transform).ToArray();
                    match = null; double bestCost = double.MaxValue;
                    for (int sh = 0; sh < 4; sh++)
                        for (int dirSign = -1; dirSign <= 1; dirSign += 2)
                        {
                            int[] mt = Enumerable.Range(0, 4).Select(i => ((dirSign * (i - sh)) % 4 + 4) % 4).ToArray();
                            double cost = Enumerable.Range(0, 4).Sum(i => Point2.Distance(proj[mt[i]], mats[m].Quad[i]));
                            if (cost < bestCost) { bestCost = cost; match = mt; }
                        }
                }
                if (Debug) Console.WriteLine("  tur {0} paspas {1}: kenar noktalari {2}", pass, m, string.Join("/", work.Sides.Select(sd => sd.Length)));
                if (work.Sides.Any(sd => sd.Length < 5)) { res.Error = "Mavi paspasların kenarları yeterince görünmüyor."; return res; }
                // Kenar dogrulari (bukulmesi giderilmis pikselde)
                var normals = new Point2[4]; var offsets = new double[4];
                for (int s = 0; s < 4; s++)
                {
                    Point2[] und = work.Sides[s].Select(undist).ToArray();
                    LensCalibrator.RobustLine(und, out normals[s], out offsets[s], out _);
                    if (!OuterCornersOnly || match[s] == 0 || match[(s + 1) % 4] == 0)   // dis kenarlar tabla kenari boyunca
                        sideLines.Add(Tuple.Create(work.Sides[s], model[match[s]], model[match[(s + 1) % 4]]));
                }
                modelCorners[m] = new Point2[4]; imageCorners[m] = new Point2[4];
                for (int i = 0; i < 4; i++)
                {
                    int a = (i + 3) % 4;   // Quad[i]: kenar (i-1) ile kenar i'nin kesisimi
                    double det = normals[a].X * normals[i].Y - normals[a].Y * normals[i].X;
                    if (Math.Abs(det) < 0.2) { res.Error = "Mavi paspasların köşeleri hesaplanamadı (kenarları düz görünmüyor)."; return res; }
                    Point2 x = PlanarBundle.Intersect(normals[a], offsets[a], normals[i], offsets[i]);
                    imageCorners[m][i] = x; modelCorners[m][i] = model[match[i]];
                    // Yalniz dis kose (tabla kosesi) cozume girer: paspas yalniz koseyi belli eden bir isaret; ic koseleri
                    // fotografin en ucunda lens modelinin zayif oldugu yerde kalir ve %3'e varan olcek hatasi tasiyordu
                    if (OuterCornersOnly && pass == passes - 1 && match[i] != 0) continue;   // ara turlarda 16 kose (daha saglam tahmin)
                    obj.Add(new Point3f((float)model[match[i]].X, (float)model[match[i]].Y, (float)h));
                    img.Add(new Point2f((float)x.X, (float)x.Y));
                }
            }

            for (int attempt = 0; attempt < 2; attempt++)
            {
                using (var camM = LensCalibrator.CameraMatrix(lens))
                using (var distM = new Mat(1, 5, MatType.CV_64FC1, Scalar.All(0)))
                using (var rvM = new Mat())
                using (var tvM = new Mat())
                using (var objM = InputArray.Create(obj))
                using (var imgM = InputArray.Create(img))
                {
                    Cv2.SolvePnP(objM, imgM, camM, distM, rvM, tvM);
                    if (rvM.Empty() || tvM.Empty()) { res.Error = "Kameranın konumu paspaslardan çözülemedi."; return res; }
                    rvec = new[] { rvM.At<double>(0), rvM.At<double>(1), rvM.At<double>(2) };
                    tvec = new[] { tvM.At<double>(0), tvM.At<double>(1), tvM.At<double>(2) };
                }
                double[] r = Rotations.FromRvec(rvec);
                double cz = -(r[2] * tvec[0] + r[5] * tvec[1] + r[8] * tvec[2]);
                if (cz > 0 || attempt == 1) break;
                // Kamera tabla duzleminin ote tarafinda cikti: paspasin ust yuzeyi kameraya dogru olmali
                for (int i = 0; i < obj.Count; i++) obj[i] = new Point3f(obj[i].X, obj[i].Y, -obj[i].Z);
            }
            }

            // Kamera oynamadiysa: kameranin konumu kayitli kalibrasyondan oldugu gibi alinir, paspaslar yalniz tabla
            // cercevesini belirler (sifir sol alt paspasin dis kosesi, X ekseni sag alt paspasin dis kosesine dogru). Olcek ve
            // bukulmeye dokunulmaz: fotografin en ucundaki koselerde lens modeli %1-3 sapabiliyor (canli kare 2026-11-23:
            // sol kenar 1991 yerine ~1970), dort koseye zorla oturtulan konum tablanin ortasini bozuyordu.
            res.UsedPriorPose = false;
            if (usePrior && FrameFromPrior)
            {
                var outerPx = new Point2[4];
                for (int m = 0; m < 4; m++)
                    for (int i = 0; i < 4; i++)
                        if (Point2.Distance(modelCorners[m][i], table[m]) < 1e-6) outerPx[m] = Distort(lens, imageCorners[m][i]);
                Point2[] old = LensCalibrator.ImageToTable(prior, outerPx, h);
                Point2 o = old[2], dx = old[3] - old[2];
                double phi = -Math.Atan2(dx.Y, dx.X), c = Math.Cos(phi), sn = Math.Sin(phi);
                var shift = new Point2(-(c * o.X - sn * o.Y), -(sn * o.X + c * o.Y));
                double[] R = Rotations.FromRvec(prior.Rvec);
                double[] Rn = Rotations.Multiply(R, new double[] { c, sn, 0, -sn, c, 0, 0, 0, 1 });
                rvec = Rotations.ToRvec(Rn);
                tvec = new[]
                {
                    prior.Tvec[0] - (Rn[0] * shift.X + Rn[1] * shift.Y),
                    prior.Tvec[1] - (Rn[3] * shift.X + Rn[4] * shift.Y),
                    prior.Tvec[2] - (Rn[6] * shift.X + Rn[7] * shift.Y)
                };
                res.UsedPriorPose = true;
            }

            Point2 tl = table[0], tr = table[1], bl = table[2], br = table[3];
            var cal = new LensCalibration
            {
                TableLengthMm = Math.Round(Math.Max(br.X, tr.X), 1),
                TableWidthMm = Math.Round(Math.Max(tl.Y, tr.Y), 1),
                ImageWidth = w, ImageHeight = hgt,
                Fx = lens.Fx, Fy = lens.Fy, Cx = lens.Cx, Cy = lens.Cy, Distortion = (double[])lens.Distortion.Clone(),
                Rvec = rvec, Tvec = tvec, TagType = "paspas", CornerSource = LensCalibration.CornerSourceMats,
                PaperCorners = (Point2[])table.Clone()
            };

            // Hata: goruntudeki paspas koseleri paspas yuzeyi duzlemine geri izdusurulur, modeldeki yerle kiyaslanir
            res.MatErrorMm = new double[4];
            var all = new List<double>();
            double sq = 0; int cnt = 0;
            for (int m = 0; m < 4; m++)
            {
                Point2[] px = imageCorners[m].Select(q => Distort(lens, q)).ToArray();
                Point2[] back = LensCalibrator.ImageToTable(cal, px, h);
                double[] e = back.Select((q, i) => Point2.Distance(q, modelCorners[m][i])).ToArray();
                if (Debug && DebugReference != null)
                {
                    Point2[] rb = LensCalibrator.ImageToTable(DebugReference, px, h);
                    Console.WriteLine("  paspas {0}: kayitli kalibrasyonla kenarlar {1} mm, koseler {2}", m,
                        string.Join("/", Enumerable.Range(0, 4).Select(i => Point2.Distance(rb[i], rb[(i + 1) % 4]).ToString("F0"))),
                        string.Join(" ", rb.Select(q => string.Format("({0:F0},{1:F0})", q.X, q.Y))));
                }
                if (Debug)
                    Console.WriteLine("  paspas {0}: fotograftan kenarlar {1} mm, koseler {2}", m,
                        string.Join("/", Enumerable.Range(0, 4).Select(i => Point2.Distance(back[i], back[(i + 1) % 4]).ToString("F0"))),
                        string.Join(" ", back.Select((q, i) => string.Format("({0:F0},{1:F0})->({2:F0},{3:F0})", q.X, q.Y, modelCorners[m][i].X, modelCorners[m][i].Y))));
                if (OuterCornersOnly) e = e.Where((v, i) => Point2.Distance(modelCorners[m][i], table[m]) < 1e-6).ToArray();
                res.MatErrorMm[m] = e.Max();
                all.AddRange(e);
            }
            foreach (var sl in sideLines)
            {
                Point2[] back = LensCalibrator.ImageToTable(cal, sl.Item1, h);
                Point2 a = sl.Item2, d = sl.Item3 - sl.Item2; d = d * (1 / d.Length);
                foreach (Point2 q in back)
                {
                    double dist = Math.Abs(Cross(d, q - a));
                    if (dist < 10) { sq += dist * dist; cnt++; }
                }
            }
            cal.RmsPx = 0;
            cal.Points = all.Count;
            cal.MeanMm = all.Average();
            cal.MaxMm = all.Max();
            cal.EdgeRmsMm = cnt > 0 ? Math.Sqrt(sq / cnt) : double.NaN;
            if (res.UsedPriorPose)
            {
                // Tabla koseleri paspaslarin fotograftaki dis koseleridir (girilen kenarlar yalniz karsilastirma icin:
                // MatErrorMm = fotograftaki kose ile serit olcusunden kurulan kose arasi). Modelin dogrulugu kayitli
                // kalibrasyonunkidir (konum ve lens ondan).
                var outerPx = new Point2[4];
                for (int m = 0; m < 4; m++)
                    for (int i = 0; i < 4; i++)
                        if (Point2.Distance(modelCorners[m][i], table[m]) < 1e-6) outerPx[m] = Distort(lens, imageCorners[m][i]);
                Point2[] photo = LensCalibrator.ImageToTable(cal, outerPx, h);
                res.PhotoCorners = photo;
                cal.PaperCorners = photo;
                cal.TableLengthMm = Math.Round(Math.Max(photo[1].X, photo[3].X), 1);
                cal.TableWidthMm = Math.Round(Math.Max(photo[0].Y, photo[1].Y), 1);
                if (!double.IsNaN(prior.MaxMm) && prior.Points > 0) { cal.MeanMm = prior.MeanMm; cal.MaxMm = prior.MaxMm; cal.Points = prior.Points; }
            }

            // Tabla duzeltmesi: lensin kalan bukulmesi (kameranin sabit ozelligi, kamera-lens.txt KalanBukulme) giderilir ve
            // paspaslarin fotograftaki dis koseleri seritle olculen tablaya (girilen kenarlar + kosegen) tam oturtulur.
            // Bundan sonra ImageToTable gercek tabla mm'si verir. Kalan bukulme yoksa yalniz koseler oturtulur.
            {
                var outerPx = new Point2[4];
                for (int m = 0; m < 4; m++)
                    for (int i = 0; i < 4; i++)
                        if (Point2.Distance(modelCorners[m][i], table[m]) < 1e-6) outerPx[m] = Distort(lens, imageCorners[m][i]);
                Point2[] photo = LensCalibrator.ImageToTable(cal, outerPx, h);
                Point2 center = LensCalibrator.ImageToTable(cal, new[] { new Point2(cal.Cx, cal.Cy) })[0];
                cal.ResidualRadial = prior.ResidualRadial;
                if (mids != null && mids.Any)
                {
                    // 6 paspas: orta paspaslarin tabla kenarindaki dis koseleri de eklenir; kalan bukulme (E) bunlardan
                    // bulunur (kenarin ortasindaki sapma E'yi belirler, dort kose belirlemez)
                    var meas = photo.ToList(); var tru = table.ToList();
                    var used = new HashSet<BlueMatFinder.BlueMat>(mats);
                    foreach (bool top in new[] { false, true })
                    {
                        double x = top ? mids.TopX : mids.BottomX;
                        if (double.IsNaN(x)) continue;
                        string name = top ? "Üst" : "Alt";
                        Point2[] model = MidMatCorners(table, top, x, spec);
                        Point2[] proj = LensCalibrator.TableToImage(cal, model, h);
                        Point2 pc = new Point2(proj.Average(q => q.X), proj.Average(q => q.Y));
                        BlueMatFinder.BlueMat cand = found.Where(f => !used.Contains(f)).OrderBy(f => Point2.Distance(f.Center, pc)).FirstOrDefault();
                        if (cand == null || Point2.Distance(cand.Center, pc) > w * 0.08)
                        {
                            res.Error = string.Format("{0} kenarın ortasındaki paspas bulunamadı (fotoğrafta beklenen yer {1:F0},{2:F0} px). Girilen uzaklığı ve paspasın kenara dayalı olduğunu kontrol edin.", name, pc.X, pc.Y);
                            return res;
                        }
                        used.Add(cand);
                        Point2[] img2 = null;
                        foreach (double tolPx in new[] { 80.0, 25.0 })
                        {
                            if (img2 != null)
                            {
                                // Ikinci tur: ilk turda bulunan koselerden tahmin (kenar noktalari gercek kenarin yakinindan)
                                proj = img2;
                            }
                            BlueMatFinder.BlueMat work = BlueMatFinder.Resample(cand, proj, tolPx);
                            if (work.Sides.Any(sd => sd.Length < 5)) { res.Error = name + " kenarın ortasındaki paspasın kenarları yeterince görünmüyor."; return res; }
                            var nr = new Point2[4]; var of = new double[4];
                            for (int sd = 0; sd < 4; sd++) LensCalibrator.RobustLine(work.Sides[sd].Select(undist).ToArray(), out nr[sd], out of[sd], out _);
                            var next = new Point2[4];
                            for (int i = 0; i < 4; i++)
                            {
                                int a = (i + 3) % 4;
                                if (Math.Abs(nr[a].X * nr[i].Y - nr[a].Y * nr[i].X) < 0.2) { res.Error = name + " kenarın ortasındaki paspasın köşeleri hesaplanamadı."; return res; }
                                next[i] = Distort(lens, PlanarBundle.Intersect(nr[a], of[a], nr[i], of[i]));
                            }
                            img2 = next;
                        }
                        Point2[] back = LensCalibrator.ImageToTable(cal, img2, h);
                        if (Debug)
                            Console.WriteLine("  orta paspas {0}: fotograftan kenarlar {1} mm, dis koseler ({2:F1},{3:F1})->({4:F1},{5:F1}) ({6:F1},{7:F1})->({8:F1},{9:F1})", name,
                                string.Join("/", Enumerable.Range(0, 4).Select(i => Point2.Distance(back[i], back[(i + 1) % 4]).ToString("F0"))),
                                back[0].X, back[0].Y, model[0].X, model[0].Y, back[1].X, back[1].Y, model[1].X, model[1].Y);
                        for (int i = 0; i < 2; i++) { meas.Add(back[i]); tru.Add(model[i]); }
                    }
                    // Karsilastirma: yalniz kose duzeltmesi + kayitli kalan bukulmeyle orta noktalar nereye gidiyordu
                    var cornersOnly = new TableCorrection(photo, table, center, prior.ResidualRadial);
                    res.MidRawErrorMm = Enumerable.Range(4, meas.Count - 4).Select(i => Point2.Distance(cornersOnly.Apply(meas[i]), tru[i])).ToArray();
                    cal.Correction = TableCorrection.Fit(meas.ToArray(), tru.ToArray(), center, out double rms);
                    cal.ResidualRadial = cal.Correction.E;
                    res.FittedRadial = cal.Correction.E;
                    res.MidErrorMm = Enumerable.Range(4, meas.Count - 4).Select(i => Point2.Distance(cal.Correction.Apply(meas[i]), tru[i])).ToArray();
                    if (Debug) Console.WriteLine("  6 paspas: kalan bukulme {0:E2} (kayitli {1:E2}), orta koseler once {2} mm, sonra {3} mm, rms {4:F2}", cal.Correction.E, prior.ResidualRadial,
                        string.Join("/", res.MidRawErrorMm.Select(v => v.ToString("F1"))), string.Join("/", res.MidErrorMm.Select(v => v.ToString("F1"))), rms);
                }
                else cal.Correction = new TableCorrection(photo, table, center, cal.ResidualRadial);
                res.PhotoCorners = photo;
                res.CorrectionMaxMm = cal.Correction.MaxShiftMm();
                cal.PaperCorners = (Point2[])table.Clone();
                cal.TableLengthMm = Math.Round(Math.Max(table[1].X, table[3].X), 1);
                cal.TableWidthMm = Math.Round(Math.Max(table[0].Y, table[1].Y), 1);
                if (Debug) Console.WriteLine("tabla duzeltmesi: kalan bukulme {0:E2}, en buyuk kayma {1:F1} mm", cal.ResidualRadial, res.CorrectionMaxMm);
            }
            if (Debug)
                Console.WriteLine("paspas kalibrasyonu: kose hatasi ort {0:F2} en buyuk {1:F2} mm (paspas basina {2}), kenar rms {3:F2} mm, kamera {4}",
                    cal.MeanMm, cal.MaxMm, string.Join("/", res.MatErrorMm.Select(v => v.ToString("F1"))), cal.EdgeRmsMm, cal.CameraPosition());
            res.Calibration = cal;
            return res;
        }

        /// <summary>
        /// Kamera sabit: kayitli kalibrasyonun konumuyla paspaslarin fotografta nerede olmasi gerektigi tahmin edilir, her
        /// koseye tahmine en yakin aday secilir (fotografin kenarindaki mavimsi beton, golge, kablo karismasin). Kayitli
        /// konum yoksa ya da bir kosede yakin aday yoksa null (o zaman en genis yayilan dortlu secilir).
        /// </summary>
        static BlueMatFinder.BlueMat[] PickByPrior(List<BlueMatFinder.BlueMat> found, Point2[] table, MatSpec spec, LensCalibration prior, int imageWidth)
        {
            if (prior.Rvec == null || prior.Tvec == null || prior.Tvec.All(v => v == 0) || found.Count < 4) return null;
            var res = new BlueMatFinder.BlueMat[4];
            for (int m = 0; m < 4; m++)
            {
                Point2[] mc = MatCorners(table, m, spec);
                Point2 center = new Point2(mc.Average(p => p.X), mc.Average(p => p.Y));
                Point2 px = LensCalibrator.TableToImage(prior, new[] { center })[0];
                BlueMatFinder.BlueMat best = found.Where(f => !res.Contains(f)).OrderBy(f => Point2.Distance(f.Center, px)).FirstOrDefault();
                if (best == null || Point2.Distance(best.Center, px) > imageWidth * 0.08) return null;
                res[m] = best;
            }
            return res;
        }

        static LensCalibration WithPose(LensCalibration lens, double[] rvec, double[] tvec) => new LensCalibration
        {
            ImageWidth = lens.ImageWidth, ImageHeight = lens.ImageHeight, Fx = lens.Fx, Fy = lens.Fy, Cx = lens.Cx, Cy = lens.Cy,
            Distortion = (double[])lens.Distortion.Clone(), Rvec = (double[])rvec.Clone(), Tvec = (double[])tvec.Clone()
        };

        /// <summary>Bukulmesi giderilmis piksel -> fotograftaki (bukulmus) piksel.</summary>
        static Point2 Distort(LensCalibration cal, Point2 undistortedPx)
        {
            double x = (undistortedPx.X - cal.Cx) / cal.Fx, y = (undistortedPx.Y - cal.Cy) / cal.Fy;
            double[] D = cal.Distortion;
            double k1 = D.Length > 0 ? D[0] : 0, k2 = D.Length > 1 ? D[1] : 0, p1 = D.Length > 2 ? D[2] : 0, p2 = D.Length > 3 ? D[3] : 0, k3 = D.Length > 4 ? D[4] : 0;
            double r2 = x * x + y * y, rad = 1 + r2 * (k1 + r2 * (k2 + r2 * k3));
            double xd = x * rad + 2 * p1 * x * y + p2 * (r2 + 2 * x * x), yd = y * rad + p1 * (r2 + 2 * y * y) + 2 * p2 * x * y;
            return new Point2(cal.Cx + cal.Fx * xd, cal.Cy + cal.Fy * yd);
        }

        static double Cross(Point2 a, Point2 b) => a.X * b.Y - a.Y * b.X;

        /// <summary>Iki cemberin kesisimi; uygun olan (pick) secilir.</summary>
        static bool Circles(Point2 c0, double r0, Point2 c1, double r1, Func<Point2, bool> pick, out Point2 result)
        {
            result = new Point2(double.NaN, double.NaN);
            double d = Point2.Distance(c0, c1);
            if (d < 1e-9 || d > r0 + r1 || d < Math.Abs(r0 - r1)) return false;
            double a = (r0 * r0 - r1 * r1 + d * d) / (2 * d), hh = Math.Sqrt(Math.Max(0, r0 * r0 - a * a));
            Point2 e = (c1 - c0) * (1 / d), mid = c0 + e * a, perp = new Point2(-e.Y, e.X);
            Point2 p1 = mid + perp * hh, p2 = mid - perp * hh;
            if (pick(p1)) { result = p1; return true; }
            if (pick(p2)) { result = p2; return true; }
            return false;
        }

        static bool Debug => Environment.GetEnvironmentVariable("KK_DEBUG") == "1";
    }
}
