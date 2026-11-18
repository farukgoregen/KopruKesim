using System;
using System.Globalization;
using KopruKesim.Core;
using KopruKesim.Vision;
using OpenCvSharp;

// Gelistirici araci (konsol). Olcum akisi web arayuzundedir (src/KopruKesim.Web).
//   profil-dene <referans.jpg> <x,y;x,y;x,y;x,y> <fotograf.jpg> [cizim.jpg]
//       referans fotograftaki tabla koselerini fotografa yuzey deseni eslestirmesiyle tasir (TableMatcher).
static class Program
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static int Main(string[] args)
    {
        if (args.Length >= 4 && args[0] == "profil-dene") return TryProfile(args);
        Console.WriteLine("Kullanim:");
        Console.WriteLine("  KopruKesim.Demo profil-dene <referans.jpg> <x,y;x,y;x,y;x,y> <fotograf.jpg> [cizim.jpg]");
        Console.WriteLine("Koseler sirasi: sol ust (0 noktasi), sag ust, sag alt, sol alt.");
        return 1;
    }

    // ---------- profil-dene ----------
    static int TryProfile(string[] args)
    {
        string[] parts = args[2].Split(';');
        if (parts.Length != 4) { Console.WriteLine("Dort kose gerekli: x,y;x,y;x,y;x,y"); return 1; }
        var corners = new Point2[4];
        for (int i = 0; i < 4; i++)
        {
            string[] xy = parts[i].Split(',');
            corners[i] = new Point2(double.Parse(xy[0], Inv), double.Parse(xy[1], Inv));
        }
        using (Mat reference = Cv2.ImRead(args[1]))
        using (Mat frame = Cv2.ImRead(args[3]))
        {
            if (reference.Empty() || frame.Empty()) { Console.WriteLine("Goruntu okunamadi."); return 1; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            TableMatch m = TableMatcher.Locate(reference, corners, frame);
            sw.Stop();
            Console.WriteLine("Sure: {0} ms, eslesme {1}, uyumlu {2}, ortalama hata {3:F2} px", sw.ElapsedMilliseconds, m.Matches, m.Inliers, m.RmsPx);
            if (m.Corners == null) { Console.WriteLine("Eslesmedi: " + m.Error); return 2; }
            for (int i = 0; i < 4; i++) Console.WriteLine("  Kose {0}: {1:F1}, {2:F1}", i, m.Corners[i].X, m.Corners[i].Y);
            if (args.Length >= 5)
            {
                var pts = Array.ConvertAll(m.Corners, p => new Point((int)Math.Round(p.X), (int)Math.Round(p.Y)));
                Cv2.Polylines(frame, new[] { pts }, true, Scalar.LimeGreen, Math.Max(2, frame.Cols / 400));
                Cv2.ImWrite(args[4], frame);
            }
        }
        return 0;
    }
}
