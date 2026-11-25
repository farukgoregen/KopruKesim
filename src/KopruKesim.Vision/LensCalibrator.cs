using System;
using System.Collections.Generic;
using System.Linq;
using KopruKesim.Core;
using OpenCvSharp;

namespace KopruKesim.Vision
{
    /// <summary>Kalibrasyon etiketinin olculeri.</summary>
    public sealed class CalibrationTag
    {
        public string Name;
        /// <summary>Ic kose sayisi (sutun x satir); sutunlar etiketin uzun kenari boyunca.</summary>
        public int Cols, Rows;
        public double SquareMm;
        /// <summary>Etiket merkezinin dayandigi koseden uzakligi: uzun kenar (X) ve kisa kenar (Y) yonunde.</summary>
        public double CenterX, CenterY;

        /// <summary>10x7 kare, 25 mm, A4 yatay sayfanin ortasinda (kalibrasyon-damasi.pdf ve "normal" etiket).</summary>
        public static readonly CalibrationTag Normal = new CalibrationTag { Name = "normal", Cols = 9, Rows = 6, SquareMm = 25, CenterX = 148.5, CenterY = 105 };
        public static readonly CalibrationTag Large = new CalibrationTag { Name = "buyuk", Cols = 6, Rows = 4, SquareMm = 38, CenterX = 148.5, CenterY = 105 };
        public static readonly CalibrationTag Small = new CalibrationTag { Name = "kucuk", Cols = 4, Rows = 3, SquareMm = 30, CenterX = 85, CenterY = 70 };
        public static CalibrationTag[] All { get { return new[] { Normal, Large, Small }; } }
    }

    /// <summary>
    /// Sabit kameranin bir kez yapilan kalibrasyonu (lens bukulmesi + kameranin tablaya gore konumu) ve olcumde
    /// fotografi duz ustten gorunume cevirme.
    ///
    /// Etiketler: dort koseye ve iki uzun kenarin tam ortasina birer etiket (6 etiket), hepsinin uzun kenari tablanin
    /// uzun kenari boyunca, kenarlara dayali. Fotografta alt uc etiket Y = 0 kenarinda, ust uc etiket Y = genislik
    /// kenarinda; orijin fotografta sol alttaki tabla kosesi, X soldan saga, Y asagidan yukari artar.
    ///
    /// Yontem: her etiketin butun ic koseleri bulunur; tabladaki yerleri etiketin yerinden ve kare boyundan bilinir.
    /// Etiketler goruntunun kenarlarina yayildigi icin (lens bukulmesinin en guclu oldugu yer) tek fotograftan
    /// OpenCV kamera kalibrasyonu (ic parametreler, k1 k2 k3 bukulme, tablaya gore konum) yapilabilir.
    /// </summary>
    public static class LensCalibrator
    {
        private static bool Debug { get { return Environment.GetEnvironmentVariable("KK_DEBUG") == "1"; } }

        /// <summary>
        /// 6 etiketin merkezleri (tabla mm), fotograftaki sirayla: ust sira soldan saga, sonra alt sira soldan saga.
        /// Orijin fotografta sol alttaki tabla kosesi (kullanicinin makine sifiri, 2026-11-20): X uzun kenar boyunca
        /// fotografta saga, Y kisa kenar boyunca yukari. Ust siradaki etiketler Y = genislik kenarinda.
        /// </summary>
        public static Point2[] TagCenters(double lengthMm, double widthMm, CalibrationTag tag)
        {
            return new[]
            {
                new Point2(tag.CenterX, widthMm - tag.CenterY), new Point2(lengthMm / 2, widthMm - tag.CenterY), new Point2(lengthMm - tag.CenterX, widthMm - tag.CenterY),
                new Point2(tag.CenterX, tag.CenterY), new Point2(lengthMm / 2, tag.CenterY), new Point2(lengthMm - tag.CenterX, tag.CenterY)
            };
        }

        /// <summary>Olcumde etiket alanlarini parca sanmamak icin etiket yerlesimi.</summary>
        public static MarkerLayout TagLayout(double lengthMm, double widthMm, CalibrationTag tag, Point2[] measuredCenters = null)
        {
            var l = new MarkerLayout
            {
                TableLengthMm = lengthMm, TableWidthMm = widthMm, Kind = "dama", PatternCols = tag.Cols, PatternRows = tag.Rows,
                SquareMm = tag.SquareMm, MarkerSizeMm = 2 * tag.CenterX
            };
            Point2[] c = measuredCenters ?? TagCenters(lengthMm, widthMm, tag);
            for (int i = 0; i < c.Length; i++) l.Markers.Add(new MarkerPoint(i, c[i].X, c[i].Y));
            return l;
        }

        public sealed class Result
        {
            public LensCalibration Calibration;
            public string Error;
            /// <summary>Bulunan etiket sayisi.</summary>
            public int TagsFound;
            /// <summary>Etiket basina modelin ortalama hatasi (mm), TagCenters sirasiyla: bulunan kose ile modelin o damanin
            /// bulunan yerine gore bekledigi kose arasindaki uzaklik.</summary>
            public double[] TagErrorMm;
            /// <summary>Damalarin bulunan yerinin beklenen yerden farki (mm, tabla ekseninde) ve acisi (derece).</summary>
            public Point2[] TagOffsetMm;
            public double[] TagRotationDeg;
        }

        /// <summary>Alti damanin gozlenen piksel noktalari ve bunlara karsilik gelen tabla noktalari (mm).</summary>
        public static string MatchBoardPoints(Mat frame, double lengthMm, double widthMm, CalibrationTag tag,
            out int found, out Point2[] imagePoints, out Point2[] tablePoints, Point2[] measuredCenters = null,
            BoardPlacement[] placements = null)
        {
            return MatchBoardPoints(CheckerFinder.FindAll(frame, tag.Cols, tag.Rows), lengthMm, widthMm, tag, out found, out imagePoints, out tablePoints, measuredCenters, placements);
        }

        private static string MatchBoardPoints(List<KeyValuePair<Point2, Point2[]>> boards, double lengthMm, double widthMm, CalibrationTag tag,
            out int found, out Point2[] imagePoints, out Point2[] tablePoints, Point2[] measuredCenters = null, BoardPlacement[] placements = null)
        {
            imagePoints = null;
            tablePoints = null;
            found = boards.Count;
            if (found != 6)
                return string.Format("Kalibrasyon icin 6 etiket gerekli, {0} bulundu. Dort koseye ve iki uzun kenarin ortasina birer etiket koyun; hepsi fotografta net gorunmeli.", found);

            // Hangi etiket hangi yer: fotografta ust uc / alt uc, her biri soldan saga.
            var byY = boards.OrderBy(b => b.Key.Y).ToList();
            var top = byY.Take(3).OrderBy(b => b.Key.X).ToList();
            var bottom = byY.Skip(3).OrderBy(b => b.Key.X).ToList();
            if (top.Max(b => b.Key.Y) >= bottom.Min(b => b.Key.Y))
                return "Etiketler iki sira (ust ve alt uzun kenar) olarak gorunmuyor. Kamera tablayi uzun kenari yatay gorecek sekilde bakmali.";

            var ordered = top.Concat(bottom).ToList();
            Point2[] centers = placements != null ? placements.Select(p => p.Center).ToArray() : measuredCenters ?? TagCenters(lengthMm, widthMm, tag);
            Homography h0 = Homography.FromPoints(ordered.Select(b => b.Key).ToList(), centers);
            Homography h0inv = h0.Inverse();
            var pixels = new List<Point2>();
            var millimeters = new List<Point2>();
            for (int t = 0; t < ordered.Count; t++)
            {
                Point2[] grid = BoardOffsets(ordered[t].Value, tag, h0inv, centers[t]);
                if (grid == null) return "Bir etiketin izgarasi cozulemedi (etiket " + (t + 1) + ").";
                pixels.AddRange(ordered[t].Value);
                // Bulunan yerlesim varsa dama kendi acisiyla doner (grid tabla eksenine hizali kabul edilir)
                millimeters.AddRange(placements != null ? grid.Select(p => placements[t].Apply(p)) : grid.Select(p => centers[t] + p));
            }
            imagePoints = pixels.ToArray();
            tablePoints = millimeters.ToArray();
            return null;
        }

        /// <summary>Alti damali kalibrasyon: lens (odak, k1 k2 k3) ve kameranin tablaya gore konumu.</summary>
        public static Result Calibrate(Mat frame, double lengthMm, double widthMm, CalibrationTag tag, Point2[] measuredCenters = null)
        {
            var res = new Result();
            var boards = CheckerFinder.FindAll(frame, tag.Cols, tag.Rows);
            res.Error = MatchBoardPoints(boards, lengthMm, widthMm, tag, out res.TagsFound, out Point2[] pixels, out Point2[] millimeters, measuredCenters);
            if (res.Error != null) return res;
            var objectPoints = millimeters.Select(p => new Point3f((float)p.X, (float)p.Y, 0)).ToArray();
            var imagePoints = pixels.Select(p => new Point2f((float)p.X, (float)p.Y)).ToArray();

            // Tek gorunumden kalibrasyon: piksel kare, optik merkez goruntu ortasinda, tegetsel bukulme yok. Odak
            // icin baslangic: ~100 derece yatay gorus. k1 k2 k3 radyal bukulme.
            int w = frame.Cols, hgt = frame.Rows;
            double f0 = (w / 2.0) / Math.Tan(50 * Math.PI / 180);
            var cam = new double[3, 3] { { f0, 0, w / 2.0 }, { 0, f0, hgt / 2.0 }, { 0, 0, 1 } };
            var dist = new double[5];
            Vec3d[] rv, tv;
            var flags = CalibrationFlags.UseIntrinsicGuess | CalibrationFlags.FixAspectRatio | CalibrationFlags.FixPrincipalPoint | CalibrationFlags.ZeroTangentDist;
            double rms = Cv2.CalibrateCamera(new[] { objectPoints }, new[] { imagePoints }, new Size(w, hgt), cam, dist, out rv, out tv, flags,
                new TermCriteria(CriteriaTypes.Count | CriteriaTypes.Eps, 200, 1e-9));
            if (Debug) Console.WriteLine("lens: rms {0:F3} px, f {1:F1}, k {2}", rms, cam[0, 0], string.Join(" ", dist.Select(d => d.ToString("F4"))));

            var cal = new LensCalibration
            {
                TableLengthMm = lengthMm, TableWidthMm = widthMm, ImageWidth = w, ImageHeight = hgt,
                Fx = cam[0, 0], Fy = cam[1, 1], Cx = cam[0, 2], Cy = cam[1, 2], Distortion = dist,
                Rvec = new[] { rv[0].Item0, rv[0].Item1, rv[0].Item2 }, Tvec = new[] { tv[0].Item0, tv[0].Item1, tv[0].Item2 },
                RmsPx = rms, Points = objectPoints.Length, TagType = tag.Name,
                MeasuredTagCenters = measuredCenters == null ? null : (Point2[])measuredCenters.Clone()
            };

            // Ikinci adim: damalarin tabladaki yeri varsayilmaz. Lens, kamera konumu ve her damanin yeri/acisi birlikte
            // cozulur (PlanarBundle); boylece yanlis yere konmus bir kagit lens modelini egmez. Ilk adimin sonucu
            // baslangic degeridir.
            Point2[] expected = measuredCenters ?? TagCenters(lengthMm, widthMm, tag);
            int pointsPerTag = tag.Cols * tag.Rows;
            var bundle = new PlanarBundle
            {
                F = cam[0, 0], Cx = cam[0, 2], Cy = cam[1, 2], K1 = dist[0], K2 = dist[1], K3 = dist[4],
                Rvec = (double[])cal.Rvec.Clone(), Tvec = (double[])cal.Tvec.Clone()
            };
            for (int t = 0; t < 6; t++)
            {
                bundle.Boards.Add(new PlanarBundle.Board
                {
                    Local = Enumerable.Range(t * pointsPerTag, pointsPerTag).Select(i => millimeters[i] - expected[t]).ToArray(),
                    Pixels = pixels.Skip(t * pointsPerTag).Take(pointsPerTag).ToArray(),
                    X = expected[t].X, Y = expected[t].Y, Theta = 0,
                    Fixed = t == 3   // cozum cercevesi sol alt damaya bagli; tabla cercevesi asagida kurulur
                });
            }
            bool solved = false;
            try { solved = bundle.Solve() && bundle.RmsPx < rms && bundle.F > 0; }
            catch (Exception e) when (e is InvalidOperationException || e is ArithmeticException) { solved = false; }
            if (Debug) Console.WriteLine("lens (serbest yerlesim): rms {0:F3} px, f {1:F1}, k {2:F4} {3:F4} {4:F4}", bundle.RmsPx, bundle.F, bundle.K1, bundle.K2, bundle.K3);

            if (solved)
            {
                // Tabla cercevesi
                if (measuredCenters != null)
                {
                    // Elle olculmus merkezler: bulunan merkezler olculenlere rijit oturtulur
                    PlanarBundle.RigidFit(bundle.Boards.Select(b => new Point2(b.X, b.Y)).ToList(), measuredCenters, out double phi, out Point2 shift);
                    bundle.Reframe(phi, shift);
                }
                else
                {
                    // Kagit koseleri: kose kagitlarinin dis kosesi tabla kosesidir. Orijin sol alt (4), X ekseni sag alt (6)
                    // kagidin kosesine dogru. Orta kagitlar (2, 5) cerceveye katilmaz, yalniz lens icin kullanilir.
                    Point2[] corners = PaperCornersOf(bundle, expected, lengthMm, widthMm);
                    Point2 o = corners[2], d = corners[3] - corners[2];
                    double phi = -Math.Atan2(d.Y, d.X), c = Math.Cos(phi), sn = Math.Sin(phi);
                    bundle.Reframe(phi, new Point2(-(c * o.X - sn * o.Y), -(sn * o.X + c * o.Y)));
                    cal.PaperCorners = PaperCornersOf(bundle, expected, lengthMm, widthMm);
                }
                cal.Fx = cal.Fy = bundle.F;
                cal.Distortion = new[] { bundle.K1, bundle.K2, 0, 0, bundle.K3 };
                cal.Rvec = bundle.Rvec; cal.Tvec = bundle.Tvec; cal.RmsPx = bundle.RmsPx;
                cal.BoardPlacements = bundle.Boards.Select(b => new BoardPlacement(b.X, b.Y, b.Theta * 180 / Math.PI)).ToArray();
            }

            // Modelin hatasi: bulunan koseler tablaya geri izdusurulur, damanin (bulunan) yerine gore beklenen koseyle kiyaslanir
            Point2[] reference = cal.BoardPlacements == null
                ? millimeters
                : Enumerable.Range(0, millimeters.Length).Select(i => cal.BoardPlacements[i / pointsPerTag].Apply(millimeters[i] - expected[i / pointsPerTag])).ToArray();
            Point2[] back = ImageToTable(cal, pixels);
            var err = new double[reference.Length];
            for (int i = 0; i < err.Length; i++) err[i] = Point2.Distance(back[i], reference[i]);
            cal.MeanMm = err.Average();
            cal.MaxMm = err.Max();
            res.TagErrorMm = Enumerable.Range(0, 6).Select(t => err.Skip(t * pointsPerTag).Take(pointsPerTag).Average()).ToArray();
            if (cal.BoardPlacements != null)
            {
                res.TagOffsetMm = Enumerable.Range(0, 6).Select(t => cal.BoardPlacements[t].Center - expected[t]).ToArray();
                res.TagRotationDeg = cal.BoardPlacements.Select(p => p.RotationDeg).ToArray();
            }
            res.Calibration = cal;
            return res;
        }

        /// <summary>
        /// Inceleme icin: kayitli kalibrasyonla fotograftaki damalarin noktalari ve kalibrasyona gore tabladaki yerleri.
        /// Damalar kayitli yerlerine (BoardPlacements) fotografta en yakin olanla eslenir; bulunamayan yer atlanir.
        /// slots: her noktanin ait oldugu etiket yeri (0-5).
        /// </summary>
        public static string MatchSavedBoards(Mat frame, LensCalibration cal, CalibrationTag tag, out Point2[] imagePoints, out Point2[] tablePoints, out int[] slots)
        {
            imagePoints = null; tablePoints = null; slots = null;
            if (cal.BoardPlacements == null) return "Kalibrasyonda dama yerlesimi yok.";
            var boards = CheckerFinder.FindAll(frame, tag.Cols, tag.Rows);
            var px = new List<Point2>(); var tb = new List<Point2>(); var sl = new List<int>();
            for (int t = 0; t < cal.BoardPlacements.Length; t++)
            {
                BoardPlacement pl = cal.BoardPlacements[t];
                if (double.IsNaN(pl.X)) continue;
                Point2 c = TableToImage(cal, new[] { pl.Center })[0];
                // Damanin yaklasik piksel boyu: 1 kare (mm) kac piksel
                double squarePx = Point2.Distance(c, TableToImage(cal, new[] { pl.Center + new Point2(tag.SquareMm, 0) })[0]);
                var near = boards.Where(b => Point2.Distance(b.Key, c) < 3 * Math.Max(5, squarePx)).OrderBy(b => Point2.Distance(b.Key, c)).ToList();
                if (near.Count == 0) continue;
                // Yerel donusum: damanin cevresinde tabla -> goruntu (kalibrasyonla), izgara yonunu bulmak icin
                Point2[] around = { pl.Center, pl.Center + new Point2(50, 0), pl.Center + new Point2(0, 50), pl.Center + new Point2(50, 50) };
                Homography local = Homography.FromPoints(around.ToList(), TableToImage(cal, around).ToList());
                Point2[] grid = BoardOffsets(near[0].Value, tag, local, pl.Center);
                if (grid == null) continue;
                // grid tabla ekseninde; damanin kendi eksenine (kalibrasyondaki aci) geri cevrilip yerlesimle tasinir
                double a = -pl.RotationDeg * Math.PI / 180, ca = Math.Cos(a), sa = Math.Sin(a);
                for (int j = 0; j < grid.Length; j++)
                {
                    var g = new Point2(ca * grid[j].X - sa * grid[j].Y, sa * grid[j].X + ca * grid[j].Y);
                    px.Add(near[0].Value[j]); tb.Add(pl.Apply(g)); sl.Add(t);
                }
            }
            if (px.Count == 0) return "Kayitli karede dama bulunamadi.";
            imagePoints = px.ToArray(); tablePoints = tb.ToArray(); slots = sl.ToArray();
            return null;
        }

        /// <summary>Kose kagitlarinin (1, 3, 4, 6) dis koseleri, cozumun su anki cercevesinde.</summary>
        private static Point2[] PaperCornersOf(PlanarBundle bundle, Point2[] expected, double lengthMm, double widthMm)
        {
            int[] tags = { 0, 2, 3, 5 };
            Point2[] tableCorners = { new Point2(0, widthMm), new Point2(lengthMm, widthMm), new Point2(0, 0), new Point2(lengthMm, 0) };
            return Enumerable.Range(0, 4).Select(i =>
            {
                PlanarBundle.Board b = bundle.Boards[tags[i]];
                Point2 off = tableCorners[i] - expected[tags[i]];   // desen merkezinden kagidin dis kosesine (kagit ekseninde)
                double c = Math.Cos(b.Theta), s = Math.Sin(b.Theta);
                return new Point2(b.X + c * off.X - s * off.Y, b.Y + s * off.X + c * off.Y);
            }).ToArray();
        }

        /// <summary>
        /// Etiketin ic koselerinin etiket merkezine gore tabladaki yeri (mm). Dedektor koseleri satir satir verir ama
        /// izgaranin genisligi (sutun ya da satir sayisi) ve yonu bilinmez: iki yorum denenir, goruntude duzgun izgara
        /// olusturan secilir; yonu kaba homografinin o noktadaki X/Y dogrultusundan bulunur.
        /// </summary>
        private static Point2[] BoardOffsets(Point2[] pts, CalibrationTag tag, Homography tableToImage, Point2 center)
        {
            int n = pts.Length;
            Homography best = null; int bw = 0, bh = 0; double bestErr = double.MaxValue;
            foreach (int[] wh in new[] { new[] { tag.Cols, tag.Rows }, new[] { tag.Rows, tag.Cols } })
            {
                var grid = Enumerable.Range(0, n).Select(k => new Point2(k % wh[0], k / wh[0])).ToList();
                Homography g;
                try { g = Homography.FromPoints(grid, pts); } catch (ArgumentException) { continue; }
                double e = Enumerable.Range(0, n).Max(k => Point2.Distance(g.Transform(grid[k]), pts[k]));
                if (e < bestErr) { bestErr = e; best = g; bw = wh[0]; bh = wh[1]; }
            }
            if (best == null) return null;
            var gc = new Point2((bw - 1) / 2.0, (bh - 1) / 2.0);
            Point2 c0 = best.Transform(gc);
            Point2 di = best.Transform(gc + new Point2(1, 0)) - c0, dj = best.Transform(gc + new Point2(0, 1)) - c0;
            Point2 t0 = tableToImage.Transform(center);
            Point2 ex = tableToImage.Transform(center + new Point2(10, 0)) - t0, ey = tableToImage.Transform(center + new Point2(0, 10)) - t0;
            Func<Point2, Point2, double> cos = (a, b) => Point2.Dot(a, b) / Math.Max(1e-9, a.Length * b.Length);
            bool iAlongX = Math.Abs(cos(di, ex)) >= Math.Abs(cos(di, ey));
            double si = Math.Sign(cos(di, iAlongX ? ex : ey)), sj = Math.Sign(cos(dj, iAlongX ? ey : ex));
            if (si == 0 || sj == 0) return null;
            var res = new Point2[n];
            for (int k = 0; k < n; k++)
            {
                double a = (k % bw - gc.X) * tag.SquareMm * si, b = (k / bw - gc.Y) * tag.SquareMm * sj;
                res[k] = iAlongX ? new Point2(a, b) : new Point2(b, a);
            }
            return res;
        }

        /// <summary>Goruntu noktalarinin tabla duzlemindeki yeri (mm): bukulme giderilir, isin tabla duzlemiyle kesisimi.</summary>
        public static Point2[] ImageToTable(LensCalibration cal, Point2[] image) => ImageToTable(cal, image, 0);

        /// <summary>Goruntu noktalarinin tabla yuzeyinden zMm yukaridaki duzlemdeki yeri (X, Y mm).</summary>
        public static Point2[] ImageToTable(LensCalibration cal, Point2[] image, double zMm)
        {
            double zp = zMm * CameraSideSign(cal);
            double[] r = cal.Rotation();
            // Kamera merkezi C = -R^T t (tabla koordinatinda)
            double cx = -(r[0] * cal.Tvec[0] + r[3] * cal.Tvec[1] + r[6] * cal.Tvec[2]);
            double cy = -(r[1] * cal.Tvec[0] + r[4] * cal.Tvec[1] + r[7] * cal.Tvec[2]);
            double cz = -(r[2] * cal.Tvec[0] + r[5] * cal.Tvec[1] + r[8] * cal.Tvec[2]);
            var res = new Point2[image.Length];
            for (int i = 0; i < image.Length; i++)
            {
                Point2 q = Undistort(cal, image[i]);
                // Kamera isini (x, y, 1) tabla koordinatina: d = R^T (x, y, 1); tabla duzlemi (Z = 0) ile kesisim
                double dx = r[0] * q.X + r[3] * q.Y + r[6], dy = r[1] * q.X + r[4] * q.Y + r[7], dz = r[2] * q.X + r[5] * q.Y + r[8];
                double s = Math.Abs(dz) < 1e-12 ? double.NaN : (zp - cz) / dz;
                res[i] = new Point2(cx + s * dx, cy + s * dy);
                // Seritle olculen tablaya gore kalan hata duzeltmesi (yukseklikten bagimsiz kabul: kalinlikla degisimi < 1 mm)
                if (cal.Correction != null) res[i] = cal.Correction.Apply(res[i]);
            }
            return res;
        }

        /// <summary>
        /// Piksel -> bukulmesi giderilmis normalize koordinat (x/z, y/z). OpenCV UndistortPoints varsayilan 5 adimda
        /// durur; genis acili lensin kose bolgesinde yakinsamiyor (sanal denemede 4-14 mm hata). Burada Gauss-Newton
        /// ile bukulme modeli tersine cozulur (radyal k1 k2 k3 ve tegetsel p1 p2).
        /// </summary>
        public static Point2 Undistort(LensCalibration cal, Point2 pixel)
        {
            double xd = (pixel.X - cal.Cx) / cal.Fx, yd = (pixel.Y - cal.Cy) / cal.Fy;
            double[] D = cal.Distortion;
            double k1 = D.Length > 0 ? D[0] : 0, k2 = D.Length > 1 ? D[1] : 0, p1 = D.Length > 2 ? D[2] : 0, p2 = D.Length > 3 ? D[3] : 0, k3 = D.Length > 4 ? D[4] : 0;
            double x = xd, y = yd;
            for (int it = 0; it < 50; it++)
            {
                double r2 = x * x + y * y, rad = 1 + r2 * (k1 + r2 * (k2 + r2 * k3));
                double fx = x * rad + 2 * p1 * x * y + p2 * (r2 + 2 * x * x) - xd;
                double fy = y * rad + p1 * (r2 + 2 * y * y) + 2 * p2 * x * y - yd;
                if (Math.Abs(fx) + Math.Abs(fy) < 1e-14) break;
                double dr = k1 + 2 * k2 * r2 + 3 * k3 * r2 * r2;   // d(rad)/d(r2)
                double jxx = rad + 2 * x * x * dr + 2 * p1 * y + 6 * p2 * x, jxy = 2 * x * y * dr + 2 * p1 * x + 2 * p2 * y;
                double jyx = 2 * x * y * dr + 2 * p1 * x + 2 * p2 * y, jyy = rad + 2 * y * y * dr + 6 * p1 * y + 2 * p2 * x;
                double det = jxx * jyy - jxy * jyx;
                if (Math.Abs(det) < 1e-12) break;
                x -= (jyy * fx - jxy * fy) / det;
                y -= (-jyx * fx + jxx * fy) / det;
            }
            return new Point2(x, y);
        }

        /// <summary>Tabla noktalarinin (mm, Z = 0) goruntudeki yeri (piksel), bukulme dahil.</summary>
        public static Point2[] TableToImage(LensCalibration cal, Point2[] table) => TableToImage(cal, table, 0);

        /// <summary>
        /// Tabla yuzeyinden zMm yukaridaki noktalarin (tabla X, Y mm) goruntudeki yeri. Yukari = kameraya dogru
        /// (kalibrasyonda tabla Z ekseninin isareti kameranin hangi tarafta olduguna gore secilir).
        /// </summary>
        public static Point2[] TableToImage(LensCalibration cal, Point2[] table, double zMm)
        {
            double z = zMm * CameraSideSign(cal);
            using (var obj = new Mat(table.Length, 1, MatType.CV_64FC3))
            using (var img = new Mat())
            using (var k = CameraMatrix(cal))
            using (var d = DistMat(cal))
            using (var rv = new Mat(3, 1, MatType.CV_64FC1))
            using (var tv = new Mat(3, 1, MatType.CV_64FC1))
            {
                TableCorrection corr = cal.Correction;
                for (int i = 0; i < table.Length; i++)
                {
                    Point2 m = corr == null ? table[i] : corr.Invert(table[i]);
                    obj.Set(i, 0, new Vec3d(m.X, m.Y, z));
                }
                for (int i = 0; i < 3; i++) { rv.Set(i, 0, cal.Rvec[i]); tv.Set(i, 0, cal.Tvec[i]); }
                Cv2.ProjectPoints(obj, rv, tv, k, d, img);
                var res = new Point2[table.Length];
                for (int i = 0; i < table.Length; i++) { Vec2d p = img.Get<Vec2d>(i, 0); res[i] = new Point2(p.Item0, p.Item1); }
                return res;
            }
        }

        /// <summary>Tablanin ortasinda 1 mm'nin goruntude kac piksel oldugu (ustten gorunumun dogal olcegi).</summary>
        public static double NativeScale(LensCalibration cal)
        {
            double L = cal.TableLengthMm, W = cal.TableWidthMm;
            Point2[] p = TableToImage(cal, new[] { new Point2(L / 2, W / 2), new Point2(L / 2 + 50, W / 2), new Point2(L / 2, W / 2 + 50) });
            return (Point2.Distance(p[0], p[1]) + Point2.Distance(p[0], p[2])) / 100;
        }

        private static readonly object CacheLock = new object();
        private static LensCalibration _cacheCal;
        private static double _cacheK;
        private static Mat _mapX, _mapY;

        /// <summary>
        /// Fotografi duz ustten gorunume cevirir: her cikti pikseli (tabla mm x k, orijin sol altta, Y yukari) icin
        /// fotograftaki yeri lens modeliyle hesaplanir. Haritalar ayni kalibrasyon ve olcek icin bellekte tutulur.
        /// </summary>
        public static Mat TopView(Mat frame, LensCalibration cal, double k)
        {
            int cw = (int)Math.Round(cal.TableLengthMm * k), ch = (int)Math.Round(cal.TableWidthMm * k);
            lock (CacheLock)
            {
                if (!ReferenceEquals(_cacheCal, cal) || Math.Abs(_cacheK - k) > 1e-12 || _mapX == null)
                {
                    _mapX?.Dispose(); _mapY?.Dispose();
                    var pts = new Point2[cw * ch];
                    for (int v = 0; v < ch; v++)
                        for (int u = 0; u < cw; u++)
                            pts[v * cw + u] = new Point2((u + 0.5) / k, cal.TableWidthMm - (v + 0.5) / k);
                    Point2[] img = TableToImage(cal, pts);
                    _mapX = new Mat(ch, cw, MatType.CV_32FC1);
                    _mapY = new Mat(ch, cw, MatType.CV_32FC1);
                    var mx = new float[cw * ch]; var my = new float[cw * ch];
                    for (int i = 0; i < mx.Length; i++) { mx[i] = (float)img[i].X; my[i] = (float)img[i].Y; }
                    _mapX.SetArray(mx); _mapY.SetArray(my);
                    _cacheCal = cal; _cacheK = k;
                }
                var top = new Mat();
                Cv2.Remap(frame, top, _mapX, _mapY, InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
                return top;
            }
        }

        /// <summary>Kameranin tabla Z ekseninin hangi yonunde oldugu (+1 ya da -1).</summary>
        public static double CameraSideSign(LensCalibration cal)
        {
            double[] r = cal.Rotation();
            double cz = -(r[2] * cal.Tvec[0] + r[5] * cal.Tvec[1] + r[8] * cal.Tvec[2]);
            return cz < 0 ? -1 : 1;
        }

        private static readonly object DisplayLock = new object();
        private static LensCalibration _dispCal;
        private static string _dispKey;
        private static Mat _dispX, _dispY;

        /// <summary>
        /// Gosterim ve kayit icin ustten gorunum: tabla X 0..lengthMm, Y 0..widthMm, k piksel/mm (k = 1 ise 1 piksel =
        /// 1 mm), orijin sol altta. Goruntu tabla yuzeyinden zMm yukaridaki duzlemde duzeltilir: zMm = parca kalinligi
        /// verilirse plakanin ust yuzeyinin kenarlari gercek X, Y yerinde gorunur ve olculen koselerle cakisir
        /// (tabla yuzeyindeki seyler ise kameradan uzaklastikca biraz kayar). Haritalar ayni ayarlar icin bellekte tutulur.
        /// </summary>
        public static Mat TopViewAt(Mat frame, LensCalibration cal, double lengthMm, double widthMm, double k, double zMm)
        {
            int cw = (int)Math.Round(lengthMm * k), ch = (int)Math.Round(widthMm * k);
            string key = string.Join("/", lengthMm.ToString("R"), widthMm.ToString("R"), k.ToString("R"), zMm.ToString("R"));
            lock (DisplayLock)
            {
                if (!ReferenceEquals(_dispCal, cal) || _dispKey != key || _dispX == null)
                {
                    _dispX?.Dispose(); _dispY?.Dispose();
                    var pts = new Point2[cw * ch];
                    for (int v = 0; v < ch; v++)
                        for (int u = 0; u < cw; u++)
                            pts[v * cw + u] = new Point2((u + 0.5) / k, widthMm - (v + 0.5) / k);
                    Point2[] img = TableToImage(cal, pts, zMm);
                    var mx = new float[cw * ch]; var my = new float[cw * ch];
                    for (int i = 0; i < mx.Length; i++) { mx[i] = (float)img[i].X; my[i] = (float)img[i].Y; }
                    _dispX = new Mat(ch, cw, MatType.CV_32FC1); _dispY = new Mat(ch, cw, MatType.CV_32FC1);
                    _dispX.SetArray(mx); _dispY.SetArray(my);
                    _dispCal = cal; _dispKey = key;
                }
                var top = new Mat();
                Cv2.Remap(frame, top, _dispX, _dispY, InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
                return top;
            }
        }

        internal static Mat CameraMatrix(LensCalibration cal)
        {
            var k = new Mat(3, 3, MatType.CV_64FC1, Scalar.All(0));
            k.Set(0, 0, cal.Fx); k.Set(1, 1, cal.Fy); k.Set(0, 2, cal.Cx); k.Set(1, 2, cal.Cy); k.Set(2, 2, 1.0);
            return k;
        }

        private static Mat DistMat(LensCalibration cal)
        {
            var d = new Mat(1, cal.Distortion.Length, MatType.CV_64FC1);
            for (int i = 0; i < cal.Distortion.Length; i++) d.Set(0, i, cal.Distortion[i]);
            return d;
        }
    }
}
