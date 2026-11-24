using System;
using System.Collections.Generic;
using System.Globalization;

namespace KopruKesim.Core
{
    /// <summary>Makineye sabitlenmis bir isaretin numarasi ve merkezinin tabla koordinati (mm).</summary>
    public struct MarkerPoint
    {
        public int Id;
        public double X;
        public double Y;

        public MarkerPoint(int id, double x, double y) { Id = id; X = x; Y = y; }
    }

    /// <summary>
    /// Isaret yerlesimi: hangi numarali isaret tablanin hangi noktasinda. Isaretler kesim alaninin
    /// disina (sasi, tabla kenari) sabitlenir; yerleri makine koordinatinda bir kez olculur.
    /// Kamera oynasa bile her fotograftaki isaretlerden kalibrasyon yeniden hesaplanir.
    /// </summary>
    public sealed class MarkerLayout
    {
        public double TableLengthMm = 3300;
        public double TableWidthMm = 2000;
        /// <summary>
        /// Isaretlerin tabla ust yuzeyine gore yuksekligi (mm). Isaretler tabladan alcaktaysa eksi
        /// (ornek: tahtanin altindaki sasi yuzeyi -60). Kamera eğik baktigi icin hesaba katilir.
        /// </summary>
        public double MarkerHeightMm = 0;
        /// <summary>Basili isaretin kenar uzunlugu (mm); yalnizca bilgi ve basma icin.</summary>
        public double MarkerSizeMm = 100;
        public List<MarkerPoint> Markers = new List<MarkerPoint>();
        /// <summary>Etiket turu: "aruco" (numarali kare kod) ya da "dama" (koselerde ayni dama tahtasi deseni).</summary>
        public string Kind = "aruco";
        /// <summary>Dama tahtasinin ic kose sayisi (sutun x satir); 10x7 kareli desen icin 9x6.</summary>
        public int PatternCols = 9, PatternRows = 6;
        /// <summary>Dama karesinin tasarim kenar uzunlugu (mm); 0 = bilinmiyor (yalniz merkezler kullanilir).</summary>
        public double SquareMm = 0;
        /// <summary>
        /// Etiket kesim cizgisinden kesilmis (true) ya da A4 sayfanin tamami (false). Kesik etikette baski olcegi
        /// merkezin koseye uzakligini da kucultur; tam sayfada desen sayfanin ortasinda kalir.
        /// </summary>
        public bool CutTag = false;
        /// <summary>
        /// Deseni 90 derece cevrik konan etiketler (ic kose sutunlari tablanin Y yonunde). Kisa kenarin ortasindaki
        /// etiketler boyle konur; koseler ve ortalar boylece birbirinden ayrilir (iki fotografli olcum).
        /// </summary>
        public HashSet<int> Rotated = new HashSet<int>();
        /// <summary>True ise her etiketin yonu zorunludur (Rotated'a gore); false ise iki yon de kabul edilir.</summary>
        public bool StrictOrientation = false;

        /// <summary>
        /// Etiketin dayandigi tabla noktasi: kosedeki etiket icin kose, kenarin ortasindaki etiket icin kenar
        /// uzerindeki nokta. Bir eksende tabla kenarina ortadan yakinsa o kenar, degilse etiketin kendi koordinati.
        /// </summary>
        public Point2 AnchorOf(double x, double y)
        {
            double ax = Math.Min(x, TableLengthMm - x) < Math.Abs(x - TableLengthMm / 2) ? (x < TableLengthMm / 2 ? 0 : TableLengthMm) : x;
            double ay = Math.Min(y, TableWidthMm - y) < Math.Abs(y - TableWidthMm / 2) ? (y < TableWidthMm / 2 ? 0 : TableWidthMm) : y;
            return new Point2(ax, ay);
        }
    }
}
