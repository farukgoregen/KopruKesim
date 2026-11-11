using System;

namespace KopruKesim.Core
{
    /// <summary>2B nokta / vektor. Tabla koordinatlarinda birim mm, goruntude piksel.</summary>
    public struct Point2
    {
        public double X;
        public double Y;

        public Point2(double x, double y) { X = x; Y = y; }

        public static Point2 operator +(Point2 a, Point2 b) { return new Point2(a.X + b.X, a.Y + b.Y); }
        public static Point2 operator -(Point2 a, Point2 b) { return new Point2(a.X - b.X, a.Y - b.Y); }
        public static Point2 operator *(Point2 a, double k) { return new Point2(a.X * k, a.Y * k); }

        public double Length { get { return Math.Sqrt(X * X + Y * Y); } }

        public static double Dot(Point2 a, Point2 b) { return a.X * b.X + a.Y * b.Y; }
        public static double Cross(Point2 a, Point2 b) { return a.X * b.Y - a.Y * b.X; }
        public static double Distance(Point2 a, Point2 b) { return (a - b).Length; }

        public override string ToString()
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "({0:F1}, {1:F1})", X, Y);
        }
    }
}
