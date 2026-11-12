using System;
using System.Collections.Generic;
using KopruKesim.Core;
using OpenCvSharp;
using OpenCvSharp.Aruco;

namespace KopruKesim.Vision
{
    /// <summary>
    /// ArUco isaretlerini bulur ve basilacak isaret goruntulerini uretir. Sozluk: 4x4 (50 isaret);
    /// hucreleri buyuk oldugu icin uzaktan ve dusuk cozunurlukte de okunur.
    /// </summary>
    public static class MarkerFinder
    {
        public const PredefinedDictionaryName DictionaryName = PredefinedDictionaryName.Dict4X4_50;

        /// <summary>Fotograftaki isaretlerin merkezleri (numara -> piksel).</summary>
        public static Dictionary<int, Point2> Find(Mat frame)
        {
            var found = new Dictionary<int, Point2>();
            using (var gray = new Mat())
            using (Dictionary dict = CvAruco.GetPredefinedDictionary(DictionaryName))
            {
                Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
                var p = new DetectorParameters { CornerRefinementMethod = CornerRefineMethod.Subpix };
                Point2f[][] corners;
                int[] ids;
                Point2f[][] rejected;
                CvAruco.DetectMarkers(gray, dict, out corners, out ids, p, out rejected);
                for (int i = 0; i < ids.Length; i++)
                {
                    // Merkez: karsi koseleri birlestiren iki kosegenin kesisimi (perspektifte dogru merkez)
                    Point2f[] c = corners[i];
                    Point2? center = DiagonalIntersection(c[0], c[2], c[1], c[3]);
                    if (center == null) continue;
                    if (found.ContainsKey(ids[i])) { found.Remove(ids[i]); continue; }   // ayni numara iki kez: guvenme
                    found[ids[i]] = center.Value;
                }
            }
            return found;
        }

        /// <summary>Basilacak isaret goruntusu (beyaz cerceveli, numara yazili).</summary>
        public static Mat Render(int id, int sizePx)
        {
            using (Dictionary dict = CvAruco.GetPredefinedDictionary(DictionaryName))
            using (var marker = new Mat())
            {
                dict.GenerateImageMarker(id, sizePx, marker, 1);
                int pad = sizePx / 6;
                var page = new Mat(sizePx + 2 * pad, sizePx + 2 * pad, MatType.CV_8UC1, Scalar.All(255));
                using (var roi = new Mat(page, new Rect(pad, pad, sizePx, sizePx))) marker.CopyTo(roi);
                Cv2.PutText(page, "#" + id, new Point(pad, sizePx + pad + pad * 3 / 4), HersheyFonts.HersheySimplex, pad / 40.0, Scalar.All(0), Math.Max(1, pad / 25));
                return page;
            }
        }

        private static Point2? DiagonalIntersection(Point2f a1, Point2f a2, Point2f b1, Point2f b2)
        {
            double dx1 = a2.X - a1.X, dy1 = a2.Y - a1.Y, dx2 = b2.X - b1.X, dy2 = b2.Y - b1.Y;
            double d = dx1 * dy2 - dy1 * dx2;
            if (Math.Abs(d) < 1e-9) return null;
            double t = ((b1.X - a1.X) * dy2 - (b1.Y - a1.Y) * dx2) / d;
            return new Point2(a1.X + t * dx1, a1.Y + t * dy1);
        }
    }
}
