namespace KopruKesim.Core
{
    public enum SlabShape { Ucgen, Kare, Dikdortgen, Dortgen, Cokgen }

    /// <summary>Tabla uzerinde bulunan bir mermerin konum bilgisi. Tum degerler mm, orijin tablanin (0,0) kosesi.</summary>
    public sealed class SlabResult
    {
        /// <summary>Koseler; saat yonunun tersine sirali, orijine en yakin koseden baslar.</summary>
        public Point2[] Vertices;
        /// <summary>Kenar uzunluklari; EdgeLengths[i], Vertices[i] ile Vertices[i+1] arasidir.</summary>
        public double[] EdgeLengths;
        /// <summary>Her kosedeki ic aci (derece).</summary>
        public double[] AnglesDeg;
        public SlabShape Shape;
        public bool IsConvex;
        public double AreaMm2;
        public Point2 Centroid;
        public double MinX, MaxX, MinY, MaxY;
        /// <summary>En uzun kenarin X eksenine gore acisi (derece, -90..+90). Mermerin tabladaki donuklugu.</summary>
        public double RotationDeg;
    }

    public sealed class LocatorOptions
    {
        /// <summary>Kose bulmada kontur sadelestirme toleransi (mm). Kenar puruzu bundan kucukse kose sayilmaz.</summary>
        public double SimplifyToleranceMm = 8;
        /// <summary>Bundan kisa kenarlar (yuvarlak/kirik kose ucu, gurultu) atilir, komsu kenarlar uzatilip kesistirilir.</summary>
        public double MinEdgeMm = 40;
        /// <summary>Iki kenar arasindaki donus bundan kucukse tek kenar sayilir (derece).</summary>
        public double MinCornerAngleDeg = 6;
        /// <summary>Kare / dikdortgen siniflandirmasinda aci toleransi (derece).</summary>
        public double RightAngleToleranceDeg = 2;
        /// <summary>Kare siniflandirmasinda kenar orani toleransi.</summary>
        public double SquareSideTolerance = 0.02;
    }
}
