using System;
using System.Collections.Generic;
using KopruKesim.Core;
using KopruKesim.Vision;
using OpenCvSharp;

// Kullanim: KopruKesim.Demo <fotograf.jpg> [bos_tabla.jpg]
// Fotograftaki mermerleri bulur, koselerini yazar ve "sonuc.jpg" olarak isaretli goruntuyu kaydeder.
static class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 1) { Console.WriteLine("Kullanim: KopruKesim.Demo <fotograf.jpg> [bos_tabla.jpg]"); return 1; }

        using (Mat frame = Cv2.ImRead(args[0]))
        {
            if (frame.Empty()) { Console.WriteLine("Goruntu okunamadi: " + args[0]); return 1; }

            // VARSAYIMSAL degerler: gercek tabla olculeri gelince guncellenecek.
            // Kose pikselleri ornek fotograftan (2560x1706) elle okundu.
            var cal = new TableCalibration
            {
                TableLengthMm = 3500,
                TableWidthMm = 2000,
                SlabThicknessMm = 20,
                ImageWidth = frame.Cols,
                ImageHeight = frame.Rows,
                CornerOrigin = new Point2(665, 129),   // fotografta sol ust
                CornerYEnd = new Point2(1557, 122),    // fotografta sag ust
                CornerFar = new Point2(1818, 1510),    // fotografta sag alt
                CornerXEnd = new Point2(342, 1530)     // fotografta sol alt
            };
            cal.Build();
            if (cal.Camera != null)
                Console.WriteLine("Kamera tahmini (mm): X={0:F0} Y={1:F0} yukseklik={2:F0}, tutarlilik={3:F2}",
                    cal.Camera.X, cal.Camera.Y, cal.Camera.Height, cal.Camera.Consistency);

            using (var detector = new SlabDetector(cal, new DetectorOptions()))
            {
                if (args.Length >= 2)
                {
                    using (Mat empty = Cv2.ImRead(args[1])) detector.SetReference(empty);
                }
                else
                {
                    // Referans yok: ornek fotografta bos oldugu bilinen bolgeden tabla rengini ol
                    detector.SetTableColor(frame, 1700, 1100, 2700, 1900);
                }

                List<SlabResult> slabs = detector.Detect(frame);
                using (Mat view = detector.Rectify(frame))
                {
                    for (int n = 0; n < slabs.Count; n++)
                    {
                        SlabResult r = slabs[n];
                        Console.WriteLine("\nParca {0}: {1}, {2} kose, alan {3:F0} cm2, donme {4:F1} derece",
                            n + 1, r.Shape, r.Vertices.Length, r.AreaMm2 / 100, r.RotationDeg);
                        var poly = new List<Point>();
                        for (int i = 0; i < r.Vertices.Length; i++)
                        {
                            Console.WriteLine("  K{0}: X={1:F1}  Y={2:F1}   (kenar {3:F0} mm, aci {4:F1})",
                                i + 1, r.Vertices[i].X, r.Vertices[i].Y, r.EdgeLengths[i], r.AnglesDeg[i]);
                            poly.Add(detector.ToRectifiedPixel(r.Vertices[i]));
                        }
                        Cv2.Polylines(view, new[] { poly }, true, new Scalar(0, 0, 255), 2);
                    }
                    Cv2.ImWrite("sonuc.jpg", view);
                }
            }
        }
        return 0;
    }
}
