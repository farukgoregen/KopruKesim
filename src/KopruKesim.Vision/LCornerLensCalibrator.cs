using System;
using System.Collections.Generic;
using System.Linq;
using KopruKesim.Core;
using OpenCvSharp;

namespace KopruKesim.Vision
{
    public static class LCornerLensCalibrator
    {
        public sealed class Result
        {
            public LensCalibration Lens;
            public LCornerFinder.Result Corners;
            public string Error;
        }

        public const double ArmLengthMm = 180;

        public static Result Calibrate(Mat frame, Point2[] tape, double heightMm, Point2[] centerReference = null)
        {
            Point2[] rough = FindRoughCorners(frame);
            if (rough == null)
                return new Result { Error = "Lens hesabı için fotoğrafta dört turkuaz L parçası da görünmeli." };

            double length = Point2.Distance(tape[2], tape[3]), width = Point2.Distance(tape[2], tape[0]);
            double focal = heightMm * Point2.Distance(rough[2], rough[3]) / length;
            double[] start = { -Math.PI, 0, 0, length / 2, width / 2, focal, 0, 0, 0 };
            double[] lo = { -4.5, -1.2, -1.2, -length, -width, frame.Cols * 0.15, -1.2, -0.8, -0.65 };
            double[] hi = { -1.8, 1.2, 1.2, 2 * length, 2 * width, frame.Cols * 2.5, 0.45, 1, 0.65 };

            double[] CornerErrors(double[] p)
            {
                LensCalibration lens = Model(frame, p, heightMm, length, width);
                double[] r = lens.Rotation();
                return tape.SelectMany((q, i) =>
                {
                    Point2 prediction = PlanarBundle.Project(lens.Fx, lens.Cx, lens.Cy, p[6], p[7], p[8], r, lens.Tvec, q.X, q.Y);
                    return new[] { prediction.X - rough[i].X, prediction.Y - rough[i].Y };
                }).ToArray();
            }

            // Fit the rough corners only to initialize the edge search. Lens coefficients come from the arm samples.
            double[] linearLo = (double[])lo.Clone(), linearHi = (double[])hi.Clone();
            for (int i = 6; i < 9; i++) linearLo[i] = linearHi[i] = 0;
            double[] seed = CornerCalibrator.Lm(CornerErrors, start, linearLo, linearHi, 0);
            LensCalibration initial = Model(frame, seed, heightMm, length, width);
            LCornerFinder.Result observed = LCornerFinder.Find(frame, initial, tape, 430, measureTips: true);
            if (observed.Corners == null) return new Result { Error = observed.Error };

            var arms = new List<Arm>();
            for (int i = 0; i < 4; i++)
            {
                int across = i ^ 1, vertical = i ^ 2;
                LCornerFinder.CornerFit corner = observed.Fits[i];
                Point2[] tipX = ConsistentTipPoints(corner.TipX, p => p.X);
                Point2[] tipY = ConsistentTipPoints(corner.TipY, p => p.Y);
                if (tipX.Length < 4 || tipY.Length < 4)
                    return new Result { Error = $"Lens hesabı için {LCornerFinder.CornerNames[i]} L parçasının iki kol ucu da net görünmeli." };
                arms.Add(new Arm(tape[i], tape[across], LensCalibrator.TableToImage(initial, corner.PointsX.ToArray()), false));
                arms.Add(new Arm(tape[i], tape[vertical], LensCalibrator.TableToImage(initial, corner.PointsY.ToArray()), false));
                arms.Add(new Arm(tape[i], tape[across], LensCalibrator.TableToImage(initial, tipX), true));
                arms.Add(new Arm(tape[i], tape[vertical], LensCalibrator.TableToImage(initial, tipY), true));
            }

            double[][] ErrorGroups(double[] p, bool holdout)
            {
                LensCalibration lens = Model(frame, p, heightMm, length, width);
                double[] r = lens.Rotation();
                return arms.Select(arm => arm.Pixels.Where((pixel, i) => (i % 4 == 1) == holdout).Select(pixel =>
                {
                    Point2 q = PlanarBundle.BackProject(lens.Fx, lens.Cx, lens.Cy, p[6], p[7], p[8], r, lens.Tvec, pixel);
                    return Point2.Dot(arm.Normal, q - arm.Start);
                }).ToArray()).ToArray();
            }

            double[] TrainingErrors(double[] p)
            {
                var edges = ErrorGroups(p, false).SelectMany(errors => errors);
                double[] center = ReferenceErrors(Model(frame, p, heightMm, length, width), centerReference);
                // Give the manually marked lengths weight comparable to a group of sampled edge points.
                return edges.Concat(Enumerable.Repeat(center, 16).SelectMany(errors => errors)).ToArray();
            }

            var candidates = new List<(LensCalibration Lens, double P90)>();
            for (int order = 1; order <= 3; order++)
            {
                double[] lower = (double[])lo.Clone(), upper = (double[])hi.Clone();
                for (int i = 6 + order; i < 9; i++) lower[i] = upper[i] = 0;
                double[] fit = CornerCalibrator.Lm(TrainingErrors, seed, lower, upper, 1.5);
                seed = fit;
                LensCalibration candidate = Model(frame, fit, heightMm, length, width);
                if (!LensCalibrator.IsTableProjectionInvertible(candidate)) continue;

                double[] check = ErrorGroups(fit, true).SelectMany(errors => errors).Select(Math.Abs).OrderBy(e => e).ToArray();
                double[] centerErrors = ReferenceErrors(candidate, centerReference).Select(Math.Abs).ToArray();
                if (check.Length < 16 || check.Concat(centerErrors).Any(e => double.IsNaN(e) || double.IsInfinity(e))) continue;
                double centerError = centerErrors.DefaultIfEmpty(0).Max();
                double error = check[(int)(0.90 * (check.Length - 1))];
                if (Environment.GetEnvironmentVariable("KK_DEBUG") == "1")
                    Console.WriteLine(FormattableString.Invariant($"L lens order {order}: f {fit[5]:F1}, k {fit[6]:F4} {fit[7]:F4} {fit[8]:F4}, p90 {error:F3} mm, max {check.Max():F3} mm"));
                candidate.Points = arms.Sum(a => a.Pixels.Length);
                candidate.MeanMm = check.Average();
                candidate.MaxMm = Math.Max(check.Max(), centerError);
                candidate.EdgeRmsMm = Math.Sqrt(check.Average(e => e * e));
                candidate.RmsPx = candidate.EdgeRmsMm * candidate.Fx / heightMm;
                // The second frame constrains the interior scale using distances within each L.
                candidates.Add((candidate, Math.Max(error, centerError)));
            }

            if (candidates.Count == 0)
                return new Result { Error = "L kollarından geçerli bir lens dönüşümü bulunamadı. L parçalarının yerleşimini ve şerit ölçülerini kontrol edin." };
            double lowestP90 = candidates.Min(c => c.P90);
            // Compare models that are indistinguishable at the corners, even if one misses a held-out point.
            var plausible = candidates.Where(c => c.P90 <= lowestP90 + 0.2).ToArray();
            LensCalibration best = plausible.Where(c => c.Lens.MeetsReferenceTolerance).Select(c => c.Lens).LastOrDefault();
            if (best == null)
                return new Result { Error = $"L referans noktalarındaki en iyi modelin en büyük sapması {plausible.Min(c => c.Lens.MaxMm):F2} mm (sınır {LensCalibration.MaxReferenceErrorMm:F0} mm). L parçalarının yerleşimini ve şerit ölçülerini kontrol edin." };

            var comparable = centerReference == null ? plausible : plausible.Where(c => c.Lens.MeetsReferenceTolerance).ToArray();
            double spread = comparable.Max(c => InteriorScaleDifference(best, c.Lens));
            if (!(spread <= LensCalibration.MaxReferenceErrorMm))
                return new Result { Error = $"L kollarına benzer doğrulukla oturan lens modelleri tabla içinde {spread:F1} mm farklı ölçüyor. "
                    + (centerReference == null ? "İkinci karede bir L'yi ortaya, diğerini orta ile tabla kenarı arasına taşıyıp ikisini işaretleyin."
                                              : "İç L'lerin işaretlerini ve iki parçanın tablaya tam oturduğunu kontrol edin.") };

            LCornerFinder.Result refined = LCornerFinder.Find(frame, best, tape);
            if (refined.Corners == null) return new Result { Error = refined.Error };
            return new Result { Lens = best, Corners = refined };
        }

        public static double[] ReferenceErrors(LensCalibration lens, Point2[] reference)
        {
            if (reference == null) return Array.Empty<double>();
            Point2[] q = LensCalibrator.ImageToTable(lens, reference);
            var errors = new double[q.Length];
            for (int i = 0; i < q.Length; i += 3)
            {
                errors[i] = Point2.Distance(q[i], q[i + 1]) - ArmLengthMm;
                errors[i + 1] = Point2.Distance(q[i], q[i + 2]) - ArmLengthMm;
                errors[i + 2] = Point2.Distance(q[i + 1], q[i + 2]) - ArmLengthMm * Math.Sqrt(2);
            }
            return errors;
        }

        private static Point2[] ConsistentTipPoints(List<Point2> points, Func<Point2, double> coordinate)
        {
            if (points.Count == 0) return Array.Empty<Point2>();
            double median = points.Select(coordinate).OrderBy(v => v).ElementAt(points.Count / 2);
            return points.Where(p => Math.Abs(coordinate(p) - median) <= 5).ToArray();
        }

        private static double InteriorScaleDifference(LensCalibration model, LensCalibration other)
        {
            double[] fractions = { 0.2, 0.5, 0.8 };
            Point2[] points = fractions.SelectMany(y => fractions.Select(x =>
                new Point2(model.TableLengthMm * x, model.TableWidthMm * y))).ToArray();
            Point2[] reference = LensCalibrator.TableToImage(model, points);
            Point2[] mapped = LensCalibrator.ImageToTable(other, reference);
            double worst = 0;
            for (int i = 0; i < points.Length; i++)
                for (int j = i + 1; j < points.Length; j++)
                    worst = Math.Max(worst, Math.Abs(Point2.Distance(points[i], points[j]) - Point2.Distance(mapped[i], mapped[j])));
            return worst;
        }

        private sealed class Arm
        {
            public readonly Point2 Start, Normal;
            public readonly Point2[] Pixels;

            public Arm(Point2 start, Point2 end, Point2[] pixels, bool tip)
            {
                Point2 direction = (end - start) * (1 / Point2.Distance(start, end));
                Start = tip ? start + direction * ArmLengthMm : start;
                Normal = tip ? direction : new Point2(-direction.Y, direction.X);
                Pixels = pixels;
            }
        }

        private static LensCalibration Model(Mat frame, double[] p, double height, double length, double width)
        {
            var rv = new[] { p[0], p[1], p[2] };
            double[] r = Rotations.FromRvec(rv);
            return new LensCalibration
            {
                TableLengthMm = length, TableWidthMm = width,
                ImageWidth = frame.Cols, ImageHeight = frame.Rows,
                Fx = p[5], Fy = p[5], Cx = frame.Cols / 2.0, Cy = frame.Rows / 2.0,
                Distortion = new[] { p[6], p[7], 0, 0, p[8] }, Rvec = rv,
                Tvec = Enumerable.Range(0, 3).Select(i => -(r[3 * i] * p[3] + r[3 * i + 1] * p[4] + r[3 * i + 2] * height)).ToArray()
            };
        }

        private static Point2[] FindRoughCorners(Mat frame)
        {
            using (Mat score = LCornerFinder.Score(frame))
            using (var mask = new Mat())
            using (var labels = new Mat())
            using (var stats = new Mat())
            using (var centers = new Mat())
            using (var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5)))
            {
                Cv2.Threshold(score, mask, 0.5, 255, ThresholdTypes.Binary);
                mask.ConvertTo(mask, MatType.CV_8UC1);
                Cv2.MorphologyEx(mask, mask, MorphTypes.Open, kernel);
                int count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centers);
                labels.GetArray(out int[] pixels);

                var corners = new Point2[4];
                for (int i = 0; i < corners.Length; i++)
                {
                    bool right = (i & 1) != 0, bottom = i >= 2;
                    int component = Enumerable.Range(1, count - 1).Where(id =>
                        (centers.At<double>(id, 0) >= frame.Cols / 2.0) == right &&
                        (centers.At<double>(id, 1) >= frame.Rows / 2.0) == bottom &&
                        stats.At<int>(id, 2) >= 20 && stats.At<int>(id, 3) >= 20 &&
                        stats.At<int>(id, 4) >= 400)
                        .OrderByDescending(id => stats.At<int>(id, 4)).FirstOrDefault();
                    if (component == 0) return null;

                    int x0 = stats.At<int>(component, 0), y0 = stats.At<int>(component, 1);
                    int x1 = x0 + stats.At<int>(component, 2), y1 = y0 + stats.At<int>(component, 3);
                    int extreme = int.MinValue;
                    for (int y = y0; y < y1; y++)
                        for (int x = x0; x < x1; x++)
                        {
                            if (pixels[y * frame.Cols + x] != component) continue;
                            int outward = (right ? x : -x) + (bottom ? y : -y);
                            if (outward <= extreme) continue;
                            extreme = outward;
                            corners[i] = new Point2(x, y);
                        }
                }
                return corners;
            }
        }
    }
}
