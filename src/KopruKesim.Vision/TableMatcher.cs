using System;
using System.Collections.Generic;
using System.Linq;
using KopruKesim.Core;
using OpenCvSharp;
using OpenCvSharp.Features2D;

namespace KopruKesim.Vision
{
    /// <summary>Referans fotograftan tasinan tabla koseleri ve eslesmenin guvenilirligi.</summary>
    public sealed class TableMatch
    {
        /// <summary>Bu fotograftaki koseler (profil sirasinda). Eslesme guvenilir degilse null.</summary>
        public Point2[] Corners;
        /// <summary>Oran testinden gecen ozellik eslesmesi sayisi.</summary>
        public int Matches;
        /// <summary>Homografiye uyan eslesme sayisi.</summary>
        public int Inliers;
        /// <summary>Uyan eslesmelerin homografiye ortalama uzakligi (piksel, bu fotografin cozunurlugunde).</summary>
        public double RmsPx = double.NaN;
        /// <summary>Eslesme kurulamadiysa nedeni (Turkce).</summary>
        public string Error;
    }

    /// <summary>
    /// Tabla duz bir yuzeydir: ayni yuzeyin iki fotografi bir homografiyle birbirine tam oturur.
    /// Yuzeyin deseni (ahsap damari, delikler, ekler, lekeler) referans fotografla eslestirilir
    /// (SIFT ozellikleri, oran testi, RANSAC homografi) ve referansta onaylanmis koseler bu
    /// fotografa tasinir. Kamera her fotografta farkli yerde olabilir; tablanin bir kismi
    /// parcalarla ortulu ya da bir kosesi kapali olabilir. Yapay zeka kullanilmaz.
    /// </summary>
    public static class TableMatcher
    {
        /// <summary>Guvenilir eslesme icin homografiye uyan en az ozellik sayisi.</summary>
        public const int MinInliers = 40;
        /// <summary>Uyanlarin oran testinden gecenlere orani en az bu olmali.</summary>
        public const double MinInlierRatio = 0.25;
        /// <summary>Hiz icin goruntuler uzun kenari bu boyuta kucultulerek eslestirilir.</summary>
        private const int WorkMaxPx = 2000;
        private const int MaxFeatures = 6000;

        public static TableMatch Locate(Mat reference, Point2[] referenceCorners, Mat frame)
        {
            var result = new TableMatch();
            if (reference == null || reference.Empty() || frame == null || frame.Empty() || referenceCorners == null || referenceCorners.Length != 4)
            {
                result.Error = "Tabla profili eksik.";
                return result;
            }
            double sr = Math.Min(1.0, WorkMaxPx / (double)Math.Max(reference.Cols, reference.Rows));
            double sf = Math.Min(1.0, WorkMaxPx / (double)Math.Max(frame.Cols, frame.Rows));

            using (Mat gr = Gray(reference, sr))
            using (Mat gf = Gray(frame, sf))
            using (Mat mask = new Mat(gr.Size(), MatType.CV_8UC1, Scalar.All(0)))
            using (var sift = SIFT.Create(MaxFeatures))
            using (Mat dr = new Mat())
            using (Mat df = new Mat())
            {
                // Yalniz referanstaki tabla yuzeyinden ozellik: zemin ve cevre her fotografta degisir
                var poly = referenceCorners.Select(p => new Point((int)Math.Round(p.X * sr), (int)Math.Round(p.Y * sr))).ToArray();
                Cv2.FillConvexPoly(mask, poly, Scalar.All(255));
                KeyPoint[] kr, kf;
                sift.DetectAndCompute(gr, mask, out kr, dr);
                sift.DetectAndCompute(gf, null, out kf, df);
                if (kr.Length < MinInliers || kf.Length < MinInliers)
                {
                    result.Error = "Tabla yüzeyinde yeterli desen bulunamadı.";
                    return result;
                }
                var src = new List<Point2d>();
                var dst = new List<Point2d>();
                using (var matcher = new BFMatcher(NormTypes.L2))
                {
                    foreach (DMatch[] m in matcher.KnnMatch(dr, df, 2))
                    {
                        if (m.Length < 2 || m[0].Distance >= 0.75f * m[1].Distance) continue;
                        Point2f a = kr[m[0].QueryIdx].Pt, b = kf[m[0].TrainIdx].Pt;
                        src.Add(new Point2d(a.X / sr, a.Y / sr));
                        dst.Add(new Point2d(b.X / sf, b.Y / sf));
                    }
                }
                result.Matches = src.Count;
                if (src.Count < MinInliers)
                {
                    result.Error = "Bu fotoğraf kayıtlı tablayla eşleşmedi (ortak desen az). Farklı bir tabla ya da çok farklı bir açı olabilir.";
                    return result;
                }
                using (Mat inl = new Mat())
                using (Mat h = Cv2.FindHomography(src, dst, HomographyMethods.Ransac, 3.0 / sf, inl))
                {
                    if (h == null || h.Empty())
                    {
                        result.Error = "Bu fotoğraf kayıtlı tablayla eşleşmedi.";
                        return result;
                    }
                    var inlierFlags = new byte[inl.Rows];
                    for (int i = 0; i < inl.Rows; i++) inlierFlags[i] = inl.At<byte>(i, 0);
                    double sum = 0;
                    int n = 0;
                    for (int i = 0; i < src.Count; i++)
                    {
                        if (inlierFlags[i] == 0) continue;
                        Point2d p = Apply(h, src[i]);
                        double dx = p.X - dst[i].X, dy = p.Y - dst[i].Y;
                        sum += dx * dx + dy * dy;
                        n++;
                    }
                    result.Inliers = n;
                    result.RmsPx = n > 0 ? Math.Sqrt(sum / n) : double.NaN;
                    if (n < MinInliers || n < MinInlierRatio * src.Count)
                    {
                        result.Error = string.Format("Bu fotoğraf kayıtlı tablayla güvenilir eşleşmedi ({0} uyumlu nokta).", n);
                        return result;
                    }
                    var corners = referenceCorners.Select(p => { Point2d q = Apply(h, new Point2d(p.X, p.Y)); return new Point2(q.X, q.Y); }).ToArray();
                    if (!Plausible(corners, frame))
                    {
                        result.Error = "Eşleşme tutarsız bir tabla verdi.";
                        return result;
                    }
                    result.Corners = corners;
                }
            }
            return result;
        }

        private static Mat Gray(Mat img, double scale)
        {
            var g = new Mat();
            if (img.Channels() == 1) img.CopyTo(g); else Cv2.CvtColor(img, g, ColorConversionCodes.BGR2GRAY);
            if (scale < 1.0) Cv2.Resize(g, g, new Size(), scale, scale, InterpolationFlags.Area);
            return g;
        }

        private static Point2d Apply(Mat h, Point2d p)
        {
            double w = h.At<double>(2, 0) * p.X + h.At<double>(2, 1) * p.Y + h.At<double>(2, 2);
            return new Point2d(
                (h.At<double>(0, 0) * p.X + h.At<double>(0, 1) * p.Y + h.At<double>(0, 2)) / w,
                (h.At<double>(1, 0) * p.X + h.At<double>(1, 1) * p.Y + h.At<double>(1, 2)) / w);
        }

        /// <summary>Tasinan dortgen dis bukey, yeterince buyuk ve goruntuye yakin olmali (yanlis homografiyi eler).</summary>
        private static bool Plausible(Point2[] c, Mat frame)
        {
            double cross = 0;
            int sign = 0;
            for (int i = 0; i < 4; i++)
            {
                Point2 a = c[i], b = c[(i + 1) % 4], d = c[(i + 2) % 4];
                double z = (b.X - a.X) * (d.Y - b.Y) - (b.Y - a.Y) * (d.X - b.X);
                int s = Math.Sign(z);
                if (s == 0 || (sign != 0 && s != sign)) return false;
                sign = s;
                cross += a.X * b.Y - b.X * a.Y;
            }
            double area = Math.Abs(cross) / 2;
            if (area < 0.02 * frame.Cols * frame.Rows) return false;
            double mx = frame.Cols * 0.5, my = frame.Rows * 0.5;
            return c.All(p => p.X > -mx && p.X < frame.Cols + mx && p.Y > -my && p.Y < frame.Rows + my);
        }
    }
}
