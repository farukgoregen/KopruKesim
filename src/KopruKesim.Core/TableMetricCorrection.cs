
using System;
using System.Linq;

namespace KopruKesim.Core
{
    /// <summary>Fixed-camera metric correction on the rectified table plane, in millimeters.</summary>
    public sealed class TableMetricCorrection
    {
        private readonly double[] _coefficients;
        private const double GridStep = 8, GridPadding = 100;
        private readonly double _length, _width, _offset;
        private readonly Lazy<InverseGrid> _inverse;

        public TableMetricCorrection(double[] coefficients, double length, double width)
            : this(coefficients, length, width, 0, null) { }

        private TableMetricCorrection(double[] coefficients, double length, double width, double offset, Lazy<InverseGrid> inverse)
        {
            if (coefficients == null || coefficients.Length != 14 ||
                coefficients.Any(c => double.IsNaN(c) || double.IsInfinity(c) || Math.Abs(c) > 20))
                throw new FormatException("Tabla alan düzeltmesi 14 geçerli katsayı içermeli.");
            if (!(length >= 500 && length <= 10000 && width >= 500 && width <= 10000))
                throw new FormatException("Tabla alan düzeltmesi için tabla boyutları geçersiz.");
            _coefficients = (double[])coefficients.Clone();
            _length = length; _width = width; _offset = offset;
            _inverse = inverse ?? new Lazy<InverseGrid>(BuildInverseGrid);
        }

        public double[] ToValues() => (double[])_coefficients.Clone();

        public TableMetricCorrection WithMargin(double margin) =>
            new TableMetricCorrection(_coefficients, _length, _width, _offset + margin, _inverse);

        private Point2 Displacement(Point2 point)
        {
            // Extend continuously beyond the calibrated table without amplifying displacement.
            double u = Math.Max(0, Math.Min(1, point.X / _length));
            double v = Math.Max(0, Math.Min(1, point.Y / _width));
            double pu = 2 * u - 1, pv = 2 * v - 1;
            double s = 4 * u * (1 - u), t = 4 * v * (1 - v);
            var c = _coefficients;
            return new Point2(
                s * (c[0] + c[2] * pv + c[8] * pu) + t * (c[4] + c[6] * pu + c[10] * pv) + s * t * c[12],
                s * (c[1] + c[3] * pv + c[9] * pu) + t * (c[5] + c[7] * pu + c[11] * pv) + s * t * c[13]);
        }

        public Point2 Apply(Point2 point) => point + Displacement(new Point2(point.X - _offset, point.Y - _offset));

        public Point2 Invert(Point2 target)
        {
            var p = new Point2(target.X - _offset, target.Y - _offset);
            InverseGrid grid = _inverse.Value;
            double gx = (p.X + GridPadding) / GridStep, gy = (p.Y + GridPadding) / GridStep;
            if (!(gx >= 0 && gy >= 0 && gx < grid.Width - 1 && gy < grid.Height - 1))
                return target + (InvertExact(p) - p);

            int ix = (int)gx, iy = (int)gy;
            int index = iy * grid.Width + ix;
            double x = gx - ix, y = gy - iy;
            Point2 a = grid.Shifts[index] * (1 - x) + grid.Shifts[index + 1] * x;
            Point2 b = grid.Shifts[index + grid.Width] * (1 - x) + grid.Shifts[index + grid.Width + 1] * x;
            return target + a * (1 - y) + b * y;
        }

        private InverseGrid BuildInverseGrid()
        {
            int width = (int)Math.Ceiling((_length + 2 * GridPadding) / GridStep) + 1;
            int height = (int)Math.Ceiling((_width + 2 * GridPadding) / GridStep) + 1;
            var shifts = new Point2[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    var p = new Point2(x * GridStep - GridPadding, y * GridStep - GridPadding);
                    shifts[y * width + x] = InvertExact(p) - p;
                }
            return new InverseGrid(width, height, shifts);
        }

        private Point2 InvertExact(Point2 target)
        {
            Point2 point = target;
            for (int i = 0; i < 16; i++)
            {
                Point2 next = target - Displacement(point);
                if (Math.Abs(next.X - point.X) + Math.Abs(next.Y - point.Y) < 1e-7)
                    return next;
                point = next;
            }
            return point;
        }
        private sealed class InverseGrid
        {
            public readonly int Width, Height;
            public readonly Point2[] Shifts;

            public InverseGrid(int width, int height, Point2[] shifts)
            {
                Width = width; Height = height; Shifts = shifts;
            }
        }
    }
}
