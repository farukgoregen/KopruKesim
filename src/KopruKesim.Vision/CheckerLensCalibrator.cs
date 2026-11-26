using System;
using System.Collections.Generic;
using System.Linq;
using KopruKesim.Core;
using OpenCvSharp;

namespace KopruKesim.Vision
{
    /// <summary>
    /// L + dama kalibrasyonu (2026-11-26, kullanici karari): dort kosedeki turkuaz L parcalarinin icinde birer A4 dama
    /// (9 x 6 ic kose, 25 mm kare).
    ///
    /// Lens yalniz damalardan: 4 x 54 ic kose, kare boyu bilinen; odak, balik gozu k1 k2 (LensDistortion; radyal k1 k2 k3
    /// tablanin icinde %0,39'a dek sapiyordu, balik gozu %0,24), optik merkez, kamera konumu ve her
    /// damanin tabla duzlemindeki yeri/acisi birlikte cozulur (PlanarBundle). Damalarin L'ye gore yeri varsayilmaz.
    /// Kamera yuksekligi kullanicinin olcumu (odak ile yukseklik dik bakista birbirini goturur; olcek kare boyundan).
    /// Tabla koseleri bu lensle L parcalarinin dis kenarlarindan (LCornerFinder).
    ///
    /// Gercek veriyle (2026-11-26, yeni kamera, 3840x2160): iki fotografta lens ayni (f 2001/2002, optik merkez
    /// 1941,1093 +-3 px, rms 0,2 px); disarida birakilan damanin karesi 3 damali lensle 24,89-25,31 mm; 1466 x 570 x 4 mm
    /// plaka ortada yatay ve dikey 1466-1471 x 570-572,5 mm, 1470 mm kenari duzeltmeden sonra 0,2 mm icinde duz.
    /// </summary>
    public static class CheckerLensCalibrator
    {
        public const int Cols = 9, Rows = 6;
        public const double SquareMm = 25;

        public sealed class Result
        {
            /// <summary>Tabla cercevesinde (sol alt L kosesi 0,0; X saga, Y yukari) duzeltmesiz kamera modeli.</summary>
            public LensCalibration Lens;
            public LCornerFinder.Result Corners;
            public string Error;
            /// <summary>Dama basina: kareyi bu lensle olcunce bulunan kare boyu (mm) ve izgaraya rijit oturtma sapmasi (mm, rms).</summary>
            public double[] BoardSquareMm, BoardShapeMm;
            /// <summary>Bir-disarida denetimi: lens diger uc damadan cozulunce bu damanin kare boyu (mm).</summary>
            public double[] HoldoutSquareMm;
        }

        public static Result Calibrate(Mat frame, Point2[] tape, double cameraHeightMm)
        {
            var res = new Result();
            var found = CheckerFinder.FindAll(frame, Cols, Rows, 4);
            if (found.Count != 4)
            {
                res.Error = $"Dört köşe damasının hepsi bulunamadı ({found.Count} bulundu). Her L parçasının içindeki 9 × 6 iç köşeli damanın fotoğrafta net ve tam göründüğünü kontrol edin.";
                return res;
            }
            // Hangi dama hangi kose: fotograftaki yeri (sira: sol ust, sag ust, sol alt, sag alt)
            double mx = frame.Cols / 2.0, my = frame.Rows / 2.0;
            var boards = new Point2[4][];
            foreach (var b in found)
            {
                int i = (b.Key.X >= mx ? 1 : 0) + (b.Key.Y >= my ? 2 : 0);
                if (boards[i] != null)
                {
                    res.Error = "Damalar fotoğrafın dört köşesine birer tane dağılmıyor. Her köşede bir L ve içinde bir dama olmalı.";
                    return res;
                }
                boards[i] = b.Value;
            }

            Solution all = Solve(frame, boards, new[] { 0, 1, 2, 3 }, cameraHeightMm, out string error);
            if (all == null) { res.Error = error; return res; }

            // Denetim (veriyle): her dama bu lensle olculur; ayrica lens diger uc damadan cozulup bu damanin karesi olculur
            res.BoardSquareMm = new double[4];
            res.BoardShapeMm = new double[4];
            res.HoldoutSquareMm = new double[4];
            for (int i = 0; i < 4; i++)
            {
                MeasureBoard(all, boards[i], out res.BoardSquareMm[i], out res.BoardShapeMm[i]);
                Solution three = Solve(frame, boards, Enumerable.Range(0, 4).Where(j => j != i).ToArray(), cameraHeightMm, out _);
                res.HoldoutSquareMm[i] = three == null ? double.NaN : MeasureBoard(three, boards[i], out _, out _);
            }

            // Gecici tabla cercevesi damalardan: orijin sol alt dama, X sag alt damaya dogru. L koseleri bu cercevede aranir.
            LensCalibration lens = all.Lens(tape);
            Point2[] centers = boards.Select(b => all.ToPlane(lens, b).Aggregate((a, c) => a + c) * (1.0 / b.Length)).ToArray();
            Reframe(lens, centers[2], centers[3]);
            centers = boards.Select(b => all.ToPlane(lens, b).Aggregate((a, c) => a + c) * (1.0 / b.Length)).ToArray();
            // A4 kagit L'nin icinde: L dis kosesi dama merkezinden ~ (148,5 + 5, 105 + 5) mm disarida; arama 300 mm
            var outward = new[] { new Point2(-1, 1), new Point2(1, 1), new Point2(-1, -1), new Point2(1, -1) };
            Point2[] expected = centers.Select((c, i) => c + new Point2(outward[i].X * 153.5, outward[i].Y * 110)).ToArray();
            LCornerFinder.Result first = LCornerFinder.Find(frame, lens, expected);
            if (first.Corners == null) { res.Error = first.Error; return res; }

            // Asil tabla cercevesi: bulunan L dis koseleri seritle olculen dis koselere rijit oturtulur (olcek ve sekil
            // degismez; lens ve konum damalardan geldigi icin kalan fark denetimdir, duzeltme degil)
            PlanarBundle.RigidFit(first.Corners, tape, out double phi, out Point2 shift);
            ReframeRigid(lens, phi, shift);
            LCornerFinder.Result corners = LCornerFinder.Find(frame, lens, tape);
            if (corners.Corners == null) { res.Error = corners.Error; return res; }

            lens.Points = 4 * Cols * Rows;
            lens.TagType = "L+dama";
            res.Lens = lens;
            res.Corners = corners;
            return res;
        }

        // ---- Cozum ----

        private sealed class Solution
        {
            public double F, Cx, Cy, K1, K2, K3, RmsPx;
            public bool Fisheye;
            public int W, H;
            public double[] Rvec, Tvec;

            public LensCalibration Lens(Point2[] tape) => new LensCalibration
            {
                TableLengthMm = tape == null ? 0 : Point2.Distance(tape[2], tape[3]),
                TableWidthMm = tape == null ? 0 : Point2.Distance(tape[2], tape[0]),
                ImageWidth = W, ImageHeight = H,
                Fx = F, Fy = F, Cx = Cx, Cy = Cy,
                Distortion = new[] { K1, K2, 0, 0, K3 }, Fisheye = Fisheye,
                Rvec = (double[])Rvec.Clone(), Tvec = (double[])Tvec.Clone(), RmsPx = RmsPx
            };

            public Point2[] ToPlane(LensCalibration lens, Point2[] pixels) => LensCalibrator.ImageToTable(lens, pixels);
        }

        /// <summary>Damanin ic koseleri kendi ekseninde (merkeze gore, mm). Tabla cercevesi Y yukari: fotografta y asagi
        /// oldugundan izgaranin el yonu fotograftan secilir (ayna goruntu olmasin).</summary>
        private static Point2[] Local(Point2[] px)
        {
            Point2 alongCols = px[1] - px[0], alongRows = px[Cols] - px[0];
            double cross = alongCols.X * alongRows.Y - alongCols.Y * alongRows.X;
            double sign = cross > 0 ? -1 : 1;
            var pts = new Point2[px.Length];
            for (int n = 0; n < px.Length; n++)
                pts[n] = new Point2((n % Cols - (Cols - 1) / 2.0) * SquareMm, sign * (n / Cols - (Rows - 1) / 2.0) * SquareMm);
            return pts;
        }

        private static Solution Solve(Mat frame, Point2[][] boards, int[] use, double heightMm, out string error)
        {
            error = null;
            int w = frame.Cols, h = frame.Rows;
            Point2[][] local = use.Select(i => Local(boards[i])).ToArray();

            // Baslangic: OpenCV, her dama ayri gorunum (yerlesim bilinmiyor); odak ~110 derece yatay gorus
            var obj = local.Select(l => l.Select(p => new Point3f((float)p.X, (float)p.Y, 0)).ToArray()).ToArray();
            var img = use.Select(i => boards[i].Select(p => new Point2f((float)p.X, (float)p.Y)).ToArray()).ToArray();
            double f0 = (w / 2.0) / Math.Tan(55 * Math.PI / 180);
            var cam = new double[3, 3] { { f0, 0, w / 2.0 }, { 0, f0, h / 2.0 }, { 0, 0, 1 } };
            var dist = new double[5];
            Vec3d[] rv, tv;
            Cv2.CalibrateCamera(obj, img, new Size(w, h), cam, dist, out rv, out tv,
                CalibrationFlags.UseIntrinsicGuess | CalibrationFlags.FixAspectRatio | CalibrationFlags.ZeroTangentDist,
                new TermCriteria(CriteriaTypes.Count | CriteriaTypes.Eps, 200, 1e-9));

            // Ortak duzlem: ilk damanin pozu kamera konumu; digerleri o damanin duzleminde (x, y, aci)
            double[] r0 = Rotations.FromRvec(new[] { rv[0].Item0, rv[0].Item1, rv[0].Item2 });
            double[] t0 = { tv[0].Item0, tv[0].Item1, tv[0].Item2 };
            var bundle = new PlanarBundle
            {
                F = cam[0, 0], Cx = cam[0, 2], Cy = cam[1, 2], K1 = dist[0], K2 = dist[1], K3 = dist[4],
                Rvec = new[] { rv[0].Item0, rv[0].Item1, rv[0].Item2 }, Tvec = (double[])t0.Clone(),
                FreePrincipalPoint = true
            };
            for (int b = 0; b < use.Length; b++)
            {
                double[] ri = Rotations.FromRvec(new[] { rv[b].Item0, rv[b].Item1, rv[b].Item2 });
                double[] ti = { tv[b].Item0, tv[b].Item1, tv[b].Item2 };
                // Damanin noktalari -> kamera -> ilk damanin duzlemi: p0 = R0^T (Ri p + ti - t0)
                Point2[] inPlane = local[b].Select(p =>
                {
                    double xc = ri[0] * p.X + ri[1] * p.Y + ti[0] - t0[0];
                    double yc = ri[3] * p.X + ri[4] * p.Y + ti[1] - t0[1];
                    double zc = ri[6] * p.X + ri[7] * p.Y + ti[2] - t0[2];
                    return new Point2(r0[0] * xc + r0[3] * yc + r0[6] * zc, r0[1] * xc + r0[4] * yc + r0[7] * zc);
                }).ToArray();
                PlanarBundle.RigidFit(local[b], inPlane, out double th, out Point2 shift);
                bundle.Boards.Add(new PlanarBundle.Board
                {
                    Local = local[b], Pixels = boards[use[b]], X = shift.X, Y = shift.Y, Theta = th, Fixed = b == 0
                });
            }

            try
            {
                if (!bundle.Solve()) { error = "Damalardan lens çözülemedi."; return null; }
                // Olculen kamera yuksekligi: konum ve odak yukseklige olceklenir, sonra yukseklik sabit cozulur
                double[] r = Rotations.FromRvec(bundle.Rvec);
                double z = -(r[2] * bundle.Tvec[0] + r[5] * bundle.Tvec[1] + r[8] * bundle.Tvec[2]);
                if (!(Math.Abs(z) > 1) || Math.Abs(r[8]) < 0.1) { error = "Damalardan kamera konumu kurulamadı."; return null; }
                double scale = heightMm / Math.Abs(z);
                bundle.CameraZMm = Math.Sign(z) * heightMm;
                double dz = bundle.CameraZMm - z;
                for (int i = 0; i < 3; i++) bundle.Tvec[i] -= r[3 * i + 2] * dz;
                bundle.F *= scale;
                if (!bundle.Solve() || !(bundle.F > 0)) { error = "Ölçülen kamera yüksekliğiyle lens çözülemedi."; return null; }

                // Balik gozu formulu (LensDistortion): radyal cozumun bukulme egrisinden baslatilip yeniden cozulur
                if (!ToFisheye(bundle, w, h) || !bundle.Solve() || !(bundle.F > 0))
                { error = "Balık gözü lens modeli damalardan çözülemedi."; return null; }
            }
            catch (Exception e) when (e is InvalidOperationException || e is ArithmeticException)
            {
                error = "Damalardan lens çözülemedi.";
                return null;
            }
            if (!(bundle.RmsPx < 1.5))
            {
                error = $"Damalar tek bir lens modeline oturmuyor (sapma {bundle.RmsPx:F2} piksel). Damaların düz ve tablaya yapışık olduğunu kontrol edin.";
                return null;
            }
            return new Solution
            {
                F = bundle.F, Cx = bundle.Cx, Cy = bundle.Cy, K1 = bundle.K1, K2 = bundle.K2, K3 = bundle.K3, RmsPx = bundle.RmsPx, Fisheye = bundle.Fisheye,
                Rvec = bundle.Rvec, Tvec = bundle.Tvec, W = w, H = h
            };
        }

        /// <summary>
        /// Radyal cozumu balik gozune cevirir: goruntu icindeki aci araliginda theta_d(theta) = r_d,radyal(tan theta)
        /// olacak k1 k2 en kucuk karelerle (dogrusal). Sonra bundle.Fisheye ile yeniden cozulur.
        /// </summary>
        private static bool ToFisheye(PlanarBundle b, int w, int h)
        {
            // Aci araligi damalarin gozlenen noktalarindan: radyal formul goruntu koselerine ulasamayabilir (2026-11-26,
            // yeni kamera: r_d en fazla 0,91, goruntu koseleri 1,09-1,11; koselerden cevirme anlamsiz baslangic veriyordu)
            double[] radial = { b.K1, b.K2, 0, 0, b.K3 };
            double thMax = 0;
            foreach (Point2 c in b.Boards.SelectMany(board => board.Pixels))
            {
                LensDistortion.Undistort(false, radial, (c.X - b.Cx) / b.F, (c.Y - b.Cy) / b.F, out double x, out double y);
                double r = Math.Sqrt(x * x + y * y);
                if (double.IsNaN(r)) return false;
                thMax = Math.Max(thMax, Math.Atan(r));
            }
            double s22 = 0, s24 = 0, s44 = 0, sy2 = 0, sy4 = 0;
            for (int i = 1; i <= 100; i++)
            {
                double th = thMax * i / 100, r = Math.Tan(th), r2 = r * r;
                double y = r * (1 + r2 * (b.K1 + r2 * (b.K2 + r2 * b.K3))) / th - 1, t2 = th * th, t4 = t2 * t2;
                s22 += t2 * t2; s24 += t2 * t4; s44 += t4 * t4; sy2 += y * t2; sy4 += y * t4;
            }
            double det = s22 * s44 - s24 * s24;
            if (Math.Abs(det) < 1e-300) return false;
            b.K1 = (sy2 * s44 - sy4 * s24) / det;
            b.K2 = (s22 * sy4 - s24 * sy2) / det;
            b.K3 = 0;
            b.Fisheye = true;
            return true;
        }

        /// <summary>Damanin koselerini lensle tabla duzlemine indirir; olcekli rijit oturtmayla kare boyu (mm) ve sekil sapmasi.</summary>
        private static double MeasureBoard(Solution s, Point2[] px, out double squareMm, out double shapeMm)
        {
            Point2[] q = LensCalibrator.ImageToTable(s.Lens(null), px);
            Point2[] g = Local(px);
            // q ~ k R g + t (g merkezli)
            double qx = q.Average(p => p.X), qy = q.Average(p => p.Y);
            double sxx = 0, sxy = 0, gg = 0;
            for (int i = 0; i < q.Length; i++)
            {
                double ax = g[i].X, ay = g[i].Y, bx = q[i].X - qx, by = q[i].Y - qy;
                sxx += ax * bx + ay * by; sxy += ax * by - ay * bx; gg += ax * ax + ay * ay;
            }
            double k = Math.Sqrt(sxx * sxx + sxy * sxy) / gg, phi = Math.Atan2(sxy, sxx), c = Math.Cos(phi), sn = Math.Sin(phi);
            double e2 = 0;
            for (int i = 0; i < q.Length; i++)
            {
                double ex = qx + k * (c * g[i].X - sn * g[i].Y) - q[i].X, ey = qy + k * (sn * g[i].X + c * g[i].Y) - q[i].Y;
                e2 += ex * ex + ey * ey;
            }
            squareMm = k * SquareMm;
            shapeMm = Math.Sqrt(e2 / q.Length);
            return squareMm;
        }

        /// <summary>Tabla cercevesini iki noktaya gore kurar: o orijin, X ekseni o -> x yonunde.</summary>
        private static void Reframe(LensCalibration lens, Point2 o, Point2 x)
        {
            Point2 d = x - o;
            double phi = -Math.Atan2(d.Y, d.X), c = Math.Cos(phi), s = Math.Sin(phi);
            ReframeRigid(lens, phi, new Point2(-(c * o.X - s * o.Y), -(s * o.X + c * o.Y)));
        }

        /// <summary>Yeni tabla noktasi = R(phi) * eski + shift. Lens degismez, yalniz kamera konumu.</summary>
        private static void ReframeRigid(LensCalibration lens, double phi, Point2 shift)
        {
            double c = Math.Cos(phi), s = Math.Sin(phi);
            double[] R = lens.Rotation();
            double[] Rn = Rotations.Multiply(R, new[] { c, s, 0, -s, c, 0, 0, 0, 1 });
            double[] t = lens.Tvec;
            lens.Tvec = new[]
            {
                t[0] - (Rn[0] * shift.X + Rn[1] * shift.Y),
                t[1] - (Rn[3] * shift.X + Rn[4] * shift.Y),
                t[2] - (Rn[6] * shift.X + Rn[7] * shift.Y)
            };
            lens.Rvec = Rotations.ToRvec(Rn);
        }
    }
}
