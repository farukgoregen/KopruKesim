using System;

namespace KopruKesim.Core
{
    /// <summary>
    /// Lens bukulme formulleri (normalize kamera koordinati x/z, y/z uzerinde).
    ///
    /// Radyal (OpenCV standart): r_d = r (1 + k1 r^2 + k2 r^4 + k3 r^6), tegetsel p1 p2.
    /// Balik gozu (OpenCV fisheye, 2 katsayi): theta = atan(r), r_d = theta (1 + k1 theta^2 + k2 theta^4).
    ///
    /// 2026-11-26, yeni kamera (3840x2160, ~13 derece egik, 2032 mm): yalniz 4 kose damasindan cozulup tablanin
    /// icindeki 9 gercek damayla sinandi. Radyal k1 k2 k3: en kotu %0,39, ortalama %0,20; balik gozu k1 k2: en kotu
    /// %0,24, ortalama %0,15 (kose damalarina oturma ikisinde de 0,243 px). Radyal modelin katsayilari koselere uyup
    /// tablanin ortasinda dalgalaniyordu; balik gozu aciya gore bukuldugu icin 2 katsayiyla duzgun.
    /// </summary>
    public static class LensDistortion
    {
        /// <summary>Bukulmesiz (x, y) -> bukulmus (xd, yd). d: radyalde k1 k2 p1 p2 k3, balik gozunde k1 k2 (gerisi 0).</summary>
        public static void Distort(bool fisheye, double[] d, double x, double y, out double xd, out double yd)
        {
            double k1 = d.Length > 0 ? d[0] : 0, k2 = d.Length > 1 ? d[1] : 0;
            if (fisheye)
            {
                double r = Math.Sqrt(x * x + y * y);
                double s = r < 1e-12 ? 1 : ThetaD(Math.Atan(r), k1, k2) / r;
                xd = x * s; yd = y * s;
                return;
            }
            double p1 = d.Length > 2 ? d[2] : 0, p2 = d.Length > 3 ? d[3] : 0, k3 = d.Length > 4 ? d[4] : 0;
            double r2 = x * x + y * y, rad = 1 + r2 * (k1 + r2 * (k2 + r2 * k3));
            xd = x * rad + 2 * p1 * x * y + p2 * (r2 + 2 * x * x);
            yd = y * rad + p1 * (r2 + 2 * y * y) + 2 * p2 * x * y;
        }

        /// <summary>Balik gozu: theta_d = theta (1 + k1 theta^2 + k2 theta^4).</summary>
        public static double ThetaD(double theta, double k1, double k2)
        {
            double t2 = theta * theta;
            return theta * (1 + t2 * (k1 + t2 * k2));
        }

        /// <summary>d(theta_d)/d(theta); pozitif kaldikca formul tersinir (tek degerli).</summary>
        public static double ThetaDSlope(double theta, double k1, double k2)
        {
            double t2 = theta * theta;
            return 1 + t2 * (3 * k1 + 5 * k2 * t2);
        }

        /// <summary>
        /// Bukulmus (xd, yd) -> bukulmesiz (x, y). Balik gozunde theta_d(theta) = r_d Newton ile cozulur (tek degisken);
        /// radyalde Gauss-Newton (OpenCV UndistortPoints genis acili lensin kosesinde yakinsamiyordu, 2026-11-20).
        /// </summary>
        public static void Undistort(bool fisheye, double[] d, double xd, double yd, out double x, out double y)
        {
            double k1 = d.Length > 0 ? d[0] : 0, k2 = d.Length > 1 ? d[1] : 0;
            if (fisheye)
            {
                double rd = Math.Sqrt(xd * xd + yd * yd);
                if (rd < 1e-12) { x = xd; y = yd; return; }
                double th = rd;
                for (int it = 0; it < 50; it++)
                {
                    double g = ThetaD(th, k1, k2) - rd, dg = ThetaDSlope(th, k1, k2);
                    if (Math.Abs(dg) < 1e-12) break;
                    double step = g / dg;
                    th -= step;
                    if (Math.Abs(step) < 1e-15) break;
                }
                // theta >= 90 derece kameranin arkasi: gecersiz
                double s = th >= Math.PI / 2 ? double.NaN : Math.Tan(th) / rd;
                x = xd * s; y = yd * s;
                return;
            }
            double p1 = d.Length > 2 ? d[2] : 0, p2 = d.Length > 3 ? d[3] : 0, k3 = d.Length > 4 ? d[4] : 0;
            x = xd; y = yd;
            for (int it = 0; it < 50; it++)
            {
                double r2 = x * x + y * y, rad = 1 + r2 * (k1 + r2 * (k2 + r2 * k3));
                double fx = x * rad + 2 * p1 * x * y + p2 * (r2 + 2 * x * x) - xd;
                double fy = y * rad + p1 * (r2 + 2 * y * y) + 2 * p2 * x * y - yd;
                if (Math.Abs(fx) + Math.Abs(fy) < 1e-14) break;
                double dr = k1 + 2 * k2 * r2 + 3 * k3 * r2 * r2;   // d(rad)/d(r2)
                double jxx = rad + 2 * x * x * dr + 2 * p1 * y + 6 * p2 * x, jxy = 2 * x * y * dr + 2 * p1 * x + 2 * p2 * y;
                double jyx = jxy, jyy = rad + 2 * y * y * dr + 6 * p1 * y + 2 * p2 * x;
                double det = jxx * jyy - jxy * jyx;
                if (Math.Abs(det) < 1e-12) break;
                x -= (jyy * fx - jxy * fy) / det;
                y -= (-jyx * fx + jxx * fy) / det;
            }
        }
    }
}
