using System;
using System.Collections.Generic;

namespace KopruKesim.Core
{
    /// <summary>
    /// Duzlemden duzleme perspektif donusum (3x3). Kamera pikselini tabla mm'sine cevirir.
    /// Kameranin egik durmasi ve goruntunun donuk olmasi bu donusumle birlikte duzelir.
    /// </summary>
    public sealed class Homography
    {
        private readonly double[] _m; // satir oncelikli 3x3

        public Homography(double[] m)
        {
            if (m == null || m.Length != 9) throw new ArgumentException("3x3 matris icin 9 eleman gerekli.");
            _m = (double[])m.Clone();
        }

        public double this[int row, int col] { get { return _m[row * 3 + col]; } }

        public double[] ToArray() { return (double[])_m.Clone(); }

        public Point2 Transform(Point2 p)
        {
            double w = _m[6] * p.X + _m[7] * p.Y + _m[8];
            return new Point2(
                (_m[0] * p.X + _m[1] * p.Y + _m[2]) / w,
                (_m[3] * p.X + _m[4] * p.Y + _m[5]) / w);
        }

        public Homography Inverse()
        {
            double a = _m[0], b = _m[1], c = _m[2], d = _m[3], e = _m[4], f = _m[5], g = _m[6], h = _m[7], i = _m[8];
            double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
            if (Math.Abs(det) < 1e-300) throw new InvalidOperationException("Homografi tersi alinamiyor (tekil matris).");
            return new Homography(new[]
            {
                (e * i - f * h) / det, (c * h - b * i) / det, (b * f - c * e) / det,
                (f * g - d * i) / det, (a * i - c * g) / det, (c * d - a * f) / det,
                (d * h - e * g) / det, (b * g - a * h) / det, (a * e - b * d) / det
            });
        }

        /// <summary>this * other: once other, sonra this uygulanir.</summary>
        public Homography Multiply(Homography other)
        {
            var r = new double[9];
            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 3; col++)
                    r[row * 3 + col] = _m[row * 3] * other._m[col] + _m[row * 3 + 1] * other._m[3 + col] + _m[row * 3 + 2] * other._m[6 + col];
            return new Homography(r);
        }

        /// <summary>
        /// Eslesen noktalardan homografi hesaplar. En az 4 nokta gerekir; daha fazla nokta
        /// verilirse en kucuk kareler ile cozer (kalibrasyon hassasiyeti artar).
        /// </summary>
        public static Homography FromPoints(IList<Point2> src, IList<Point2> dst)
        {
            if (src == null || dst == null || src.Count != dst.Count || src.Count < 4)
                throw new ArgumentException("En az 4 nokta cifti gerekli.");

            // Sayisal kararlilik icin noktalar merkeze tasinip olceklenir (Hartley normalizasyonu).
            double[] ns = Normalizer(src), nd = Normalizer(dst);
            int n = src.Count;
            var ata = new double[8, 8];
            var atb = new double[8];
            var row = new double[8];
            for (int k = 0; k < n; k++)
            {
                double x = (src[k].X - ns[0]) * ns[2], y = (src[k].Y - ns[1]) * ns[2];
                double u = (dst[k].X - nd[0]) * nd[2], v = (dst[k].Y - nd[1]) * nd[2];
                // u = (h0 x + h1 y + h2) / (h6 x + h7 y + 1)
                row[0] = x; row[1] = y; row[2] = 1; row[3] = 0; row[4] = 0; row[5] = 0; row[6] = -u * x; row[7] = -u * y;
                Accumulate(ata, atb, row, u);
                row[0] = 0; row[1] = 0; row[2] = 0; row[3] = x; row[4] = y; row[5] = 1; row[6] = -v * x; row[7] = -v * y;
                Accumulate(ata, atb, row, v);
            }
            double[] h = SolveLinear(ata, atb);
            var hn = new Homography(new[] { h[0], h[1], h[2], h[3], h[4], h[5], h[6], h[7], 1.0 });

            // Normalizasyonu geri al: H = Td^-1 * Hn * Ts
            var ts = new Homography(new[] { ns[2], 0, -ns[0] * ns[2], 0, ns[2], -ns[1] * ns[2], 0, 0, 1 });
            var tdInv = new Homography(new[] { 1 / nd[2], 0, nd[0], 0, 1 / nd[2], nd[1], 0, 0, 1 });
            return tdInv.Multiply(hn).Multiply(ts);
        }

        private static double[] Normalizer(IList<Point2> pts)
        {
            double cx = 0, cy = 0;
            foreach (var p in pts) { cx += p.X; cy += p.Y; }
            cx /= pts.Count; cy /= pts.Count;
            double d = 0;
            foreach (var p in pts) d += Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
            d /= pts.Count;
            if (d < 1e-12) throw new ArgumentException("Kalibrasyon noktalari ust uste.");
            return new[] { cx, cy, Math.Sqrt(2) / d };
        }

        private static void Accumulate(double[,] ata, double[] atb, double[] row, double b)
        {
            for (int i = 0; i < 8; i++)
            {
                atb[i] += row[i] * b;
                for (int j = 0; j < 8; j++) ata[i, j] += row[i] * row[j];
            }
        }

        /// <summary>Kismi pivotlu Gauss eleme.</summary>
        private static double[] SolveLinear(double[,] a, double[] b)
        {
            int n = b.Length;
            for (int col = 0; col < n; col++)
            {
                int pivot = col;
                for (int r = col + 1; r < n; r++)
                    if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col])) pivot = r;
                if (Math.Abs(a[pivot, col]) < 1e-12)
                    throw new ArgumentException("Kalibrasyon noktalari gecersiz (uc nokta ayni dogru uzerinde olabilir).");
                if (pivot != col)
                {
                    for (int c = 0; c < n; c++) { double t = a[col, c]; a[col, c] = a[pivot, c]; a[pivot, c] = t; }
                    double tb = b[col]; b[col] = b[pivot]; b[pivot] = tb;
                }
                for (int r = col + 1; r < n; r++)
                {
                    double f = a[r, col] / a[col, col];
                    if (f == 0) continue;
                    for (int c = col; c < n; c++) a[r, c] -= f * a[col, c];
                    b[r] -= f * b[col];
                }
            }
            var x = new double[n];
            for (int r = n - 1; r >= 0; r--)
            {
                double s = b[r];
                for (int c = r + 1; c < n; c++) s -= a[r, c] * x[c];
                x[r] = s / a[r, r];
            }
            return x;
        }
    }
}
