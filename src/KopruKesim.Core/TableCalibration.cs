using System;

namespace KopruKesim.Core
{
    /// <summary>
    /// Tabla ve kamera kalibrasyonu. Koordinat sistemi: orijin tablanin secilen kosesi,
    /// X tablanin uzun kenari boyunca, Y kisa kenari boyunca (yatay ekranda X saga, Y yukari).
    /// </summary>
    public sealed class TableCalibration
    {
        /// <summary>Tablanin X yonundeki (uzun kenar) olcusu, mm.</summary>
        public double TableLengthMm = 3500;
        /// <summary>Tablanin Y yonundeki (kisa kenar) olcusu, mm.</summary>
        public double TableWidthMm = 2000;
        /// <summary>Mermer kalinligi, mm.</summary>
        public double SlabThicknessMm = 20;

        /// <summary>Kamera goruntusunun boyutu (piksel).</summary>
        public int ImageWidth;
        public int ImageHeight;

        /// <summary>Tabla koselerinin kamera goruntusundeki piksel konumlari.</summary>
        public Point2 CornerOrigin;   // (0, 0)
        public Point2 CornerYEnd;     // (0, Width)
        public Point2 CornerFar;      // (Length, Width)
        public Point2 CornerXEnd;     // (Length, 0)

        /// <summary>Kamera konumu elle olculduyse buraya yazilir; null ise homografiden tahmin edilir.</summary>
        public CameraPose ManualCamera;

        public Homography ImageToTable { get; private set; }
        public CameraPose Camera { get; private set; }

        /// <summary>Koseler veya olculer degistikten sonra cagrilir.</summary>
        public void Build()
        {
            if (ImageWidth <= 0 || ImageHeight <= 0) throw new InvalidOperationException("Goruntu boyutu girilmemis.");
            ImageToTable = Homography.FromPoints(
                new[] { CornerOrigin, CornerYEnd, CornerFar, CornerXEnd },
                new[]
                {
                    new Point2(0, 0), new Point2(0, TableWidthMm),
                    new Point2(TableLengthMm, TableWidthMm), new Point2(TableLengthMm, 0)
                });
            Camera = ManualCamera ?? CameraPose.Estimate(ImageToTable, ImageWidth, ImageHeight);
        }

        /// <summary>
        /// Kamera goruntusunu yatay tabla gorunumune ceviren donusum. Cikti goruntusunde
        /// orijin sol altta, X saga, Y yukari; olcek pxPerMm piksel/mm.
        /// </summary>
        public Homography ImageToRectified(double pxPerMm)
        {
            if (ImageToTable == null) Build();
            var tableToRect = new Homography(new[] { pxPerMm, 0, 0, 0, -pxPerMm, TableWidthMm * pxPerMm, 0, 0, 1 });
            return tableToRect.Multiply(ImageToTable);
        }
    }
}
