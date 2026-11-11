using System;

namespace KopruKesim.Core
{
    /// <summary>
    /// Kameranin tabla koordinatlarindaki konumu (mm). Mermerin ust yuzeyi tabladan yuksekte
    /// oldugu icin egik kamerada kaymis gorunur; bu kaymayi duzeltmek icin kameranin yeri gerekir.
    /// </summary>
    public sealed class CameraPose
    {
        public double X;
        public double Y;
        /// <summary>Kameranin tabla yuzeyinden yuksekligi (mm).</summary>
        public double Height;
        /// <summary>Tahmin edilen odak uzakligi (piksel).</summary>
        public double FocalPx;
        /// <summary>
        /// Iki bagimsiz odak tahmininin orani (ideal = 1). 1'den cok uzaksa tabla olculeri veya
        /// isaretlenen koseler hatalidir; kalibrasyon ekraninda uyari olarak gosterilebilir.
        /// </summary>
        public double Consistency;

        public CameraPose(double x, double y, double height)
        {
            X = x; Y = y; Height = height; Consistency = 1;
        }

        /// <summary>
        /// Kamera konumunu homografiden tahmin eder; elle olcum gerektirmez.
        /// Varsayim: kare piksel, optik merkez goruntunun ortasinda, lens bozulmasi kucuk.
        /// Kamera tablaya tam dik bakiyorsa tahmin yapilamaz ve null doner; o durumda
        /// konum elle girilmelidir.
        /// </summary>
        public static CameraPose Estimate(Homography imageToTable, double imageWidth, double imageHeight)
        {
            Homography t2i = imageToTable.Inverse();
            double cx = imageWidth / 2, cy = imageHeight / 2;
            // Optik merkezi orijine tasi; sutunlar h1, h2, h3
            double[] h1 = Column(t2i, 0, cx, cy), h2 = Column(t2i, 1, cx, cy), h3 = Column(t2i, 2, cx, cy);

            // Donme matrisinin ilk iki sutunu dik ve esit boyda olmali; buradan f^2 icin iki tahmin cikar.
            double numA = -(h1[0] * h2[0] + h1[1] * h2[1]), denA = h1[2] * h2[2];
            double numB = h2[0] * h2[0] + h2[1] * h2[1] - h1[0] * h1[0] - h1[1] * h1[1], denB = h1[2] * h1[2] - h2[2] * h2[2];
            double scale = h1[2] * h1[2] + h2[2] * h2[2];
            if (scale < 1e-30) return null;
            bool okA = Math.Abs(denA) > 1e-9 * scale && numA / denA > 0;
            bool okB = Math.Abs(denB) > 1e-9 * scale && numB / denB > 0;
            if (!okA && !okB) return null;

            double f2;
            if (okA && okB) f2 = Math.Abs(denA) >= Math.Abs(denB) ? numA / denA : numB / denB;
            else f2 = okA ? numA / denA : numB / denB;
            double f = Math.Sqrt(f2);

            double[] r1 = { h1[0] / f, h1[1] / f, h1[2] };
            double[] r2 = { h2[0] / f, h2[1] / f, h2[2] };
            double[] t = { h3[0] / f, h3[1] / f, h3[2] };
            double n1 = Norm(r1), n2 = Norm(r2);
            double lambda = 2.0 / (n1 + n2);
            for (int i = 0; i < 3; i++) { r1[i] /= n1; r2[i] /= n2; t[i] *= lambda; }
            double[] r3 =
            {
                r1[1] * r2[2] - r1[2] * r2[1],
                r1[2] * r2[0] - r1[0] * r2[2],
                r1[0] * r2[1] - r1[1] * r2[0]
            };

            // Kamera merkezi C = -R^T t
            var pose = new CameraPose(-Dot(r1, t), -Dot(r2, t), Math.Abs(Dot(r3, t)));
            pose.FocalPx = f;
            pose.Consistency = okA && okB ? Math.Sqrt((numA / denA) / (numB / denB)) : 1;
            return pose;
        }

        private static double[] Column(Homography h, int col, double cx, double cy)
        {
            double z = h[2, col];
            return new[] { h[0, col] - cx * z, h[1, col] - cy * z, z };
        }

        private static double Norm(double[] v) { return Math.Sqrt(Dot(v, v)); }
        private static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
    }
}
