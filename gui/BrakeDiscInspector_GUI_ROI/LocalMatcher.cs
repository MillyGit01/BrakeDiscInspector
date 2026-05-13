using System;
using System.Globalization;
using System.Linq;
using OpenCvSharp;

namespace BrakeDiscInspector_GUI_ROI
{
    public static class LocalMatcher
    {
        public sealed class LocalMatchResult
        {
            public Point2d? Center { get; init; }
            public int Score { get; init; }
            public double AngleDeg { get; init; }
            public bool UsedFeatures { get; init; }
            public string ModeRequested { get; init; } = string.Empty;
            public string ModeUsed { get; init; } = string.Empty;
            public bool UsedFallback { get; init; }
            public string? Failure { get; init; }
            public double BestCorr { get; init; }
            public double SecondBestCorr { get; init; }
            public double CorrMargin { get; init; }
            public double BestScale { get; init; }
            public double BestAngleDeg { get; init; }
            public int ImageKeypoints { get; init; }
            public int PatternKeypoints { get; init; }
            public int GoodMatches { get; init; }
            public int Inliers { get; init; }
            public double AvgDistance { get; init; }
            public double GeometricScore { get; init; }
            public double FeatureScore { get; init; }
            public double TemplateScore { get; init; }
            public int SearchWidth { get; init; }
            public int SearchHeight { get; init; }
            public int PatternWidth { get; init; }
            public int PatternHeight { get; init; }
            public bool AcceptedByThreshold { get; init; }
        }

        private static void Log(Action<string>? log, string message) => log?.Invoke(message);

        private static Mat PackPoints(Point2f[] pts)
        {
            var mat = new Mat(pts.Length, 1, MatType.CV_32FC2);
            for (int i = 0; i < pts.Length; i++) mat.Set(i, 0, pts[i]);
            return mat;
        }

        private static Point2f[] UnpackPoints(Mat mat)
        {
            var pts = new Point2f[mat.Rows];
            for (int i = 0; i < mat.Rows; i++) pts[i] = mat.Get<Point2f>(i);
            return pts;
        }

        private static int ToScore(double value) => (int)Math.Round(100.0 * Math.Clamp(value, 0.0, 1.0));

        private static Mat ToGray(Mat src)
        {
            if (src is null || src.Empty()) return new Mat();
            if (src.Channels() == 1) return src.Clone();
            var dst = new Mat();
            if (src.Channels() == 3) Cv2.CvtColor(src, dst, ColorConversionCodes.BGR2GRAY);
            else if (src.Channels() == 4) Cv2.CvtColor(src, dst, ColorConversionCodes.BGRA2GRAY);
            else dst = src.Clone();
            return dst;
        }

        private static Mat ClaheBoost(Mat gray)
        {
            var dst = new Mat();
            using var clahe = Cv2.CreateCLAHE(clipLimit: 2.0, tileGridSize: new Size(8, 8));
            clahe.Apply(gray, dst);
            return dst;
        }

        private static Mat RotateAndScale(Mat srcGray, double angleDeg, double scale)
        {
            var center = new Point2f(srcGray.Cols / 2f, srcGray.Rows / 2f);
            using var matrix = Cv2.GetRotationMatrix2D(center, angleDeg, scale);
            var dst = new Mat(srcGray.Size(), srcGray.Type());
            Cv2.WarpAffine(srcGray, dst, matrix, srcGray.Size(), InterpolationFlags.Linear, BorderTypes.Reflect101);
            return dst;
        }

        private static (Point2d? center, int score, string? failure, double bestCorr, double secondBestCorr, double corrMargin, double bestScale, double bestAngleDeg, Point bestLoc, int candidatesEvaluated) MatchTemplateRot(
            Mat imageGray, Mat patternGray, int rotRangeDeg, double scaleMin, double scaleMax, Action<string>? log, string roleTag)
        {
            if (imageGray.Empty() || patternGray.Empty())
                return (null, 0, "imgs vacías", 0, 0, 0, 1.0, 0.0, new Point(), 0);

            double best = -1.0;
            Point2d? bestPoint = null;
            double bestAngle = 0.0;
            Point bestLoc = new();
            double bestScale = 1.0;
            var candidates = new System.Collections.Generic.List<(double corr, Point loc, double scale, double angle, int w, int h)>();

            var minScale = Math.Min(scaleMin, scaleMax);
            var maxScale = Math.Max(scaleMin, scaleMax);
            int steps = 5;
            var scales = Enumerable.Range(0, steps + 1)
                                   .Select(i => minScale + i * (maxScale - minScale) / Math.Max(steps, 1))
                                   .Distinct();

            foreach (var scale in scales)
            {
                for (int angle = -rotRangeDeg; angle <= rotRangeDeg; angle += 2)
                {
                    using var rotated = RotateAndScale(patternGray, angle, scale);
                    if (rotated.Width > imageGray.Width || rotated.Height > imageGray.Height)
                    {
                        Log(log, $"[TM] skip: rotPat({rotated.Width}x{rotated.Height}) > img({imageGray.Width}x{imageGray.Height}) @ang={angle},scale={scale:F3}");
                        continue;
                    }

                    using var response = new Mat();
                    Cv2.MatchTemplate(imageGray, rotated, response, TemplateMatchModes.CCoeffNormed);
                    Cv2.MinMaxLoc(response, out _, out double maxVal, out _, out Point maxLoc);
                    candidates.Add((maxVal, maxLoc, scale, angle, rotated.Width, rotated.Height));

                    if (maxVal > best)
                    {
                        best = maxVal;
                        bestPoint = new Point2d(maxLoc.X + rotated.Width / 2.0, maxLoc.Y + rotated.Height / 2.0);
                        bestAngle = angle;
                        bestScale = scale;
                        bestLoc = maxLoc;
                    }
                }
            }

            string? failure = bestPoint == null ? "sin correlación" : $"maxCorr={Math.Max(best, 0):F4}";
            double safeBest = Math.Max(best, 0);
            double safeSecond = 0;
            var sorted = candidates.OrderByDescending(c => c.corr).ToArray();
            if (sorted.Length > 1)
            {
                var top = sorted[0];
                double minSeparationPx = Math.Max(8, Math.Min(patternGray.Width, patternGray.Height) * 0.25);
                for (int i = 1; i < sorted.Length; i++)
                {
                    var cand = sorted[i];
                    var dx = cand.loc.X - top.loc.X;
                    var dy = cand.loc.Y - top.loc.Y;
                    if (Math.Sqrt(dx * dx + dy * dy) >= minSeparationPx)
                    {
                        safeSecond = Math.Max(cand.corr, 0);
                        break;
                    }
                }
            }
            double margin = safeBest - safeSecond;
            Log(log, $"[PATTERN][TM] role={roleTag} mode=tm_rot search={imageGray.Width}x{imageGray.Height} pattern={patternGray.Width}x{patternGray.Height} candidates={candidates.Count} best={safeBest:F4} second={safeSecond:F4} margin={margin:F4} score={ToScore(best)} angle={bestAngle:F3} scale={bestScale:F3} loc=({bestLoc.X},{bestLoc.Y}) center=({bestPoint?.X:F3},{bestPoint?.Y:F3})");
            return (bestPoint, ToScore(best), failure, safeBest, safeSecond, margin, bestScale, bestAngle, bestLoc, candidates.Count);
        }

        private static (Point2d? center, int score, string? failure, int imgKps, int patKps, int goodCount, int inliers, double avgDist, double rotDegApprox) MatchFeatures(
            Mat imageGrayIn, Mat patternGrayIn, Action<string>? log, string roleTag)
        {
            // Trabajar sobre clones: NUNCA liberar ni reasignar Mats del caller
            using var imageGray = imageGrayIn.Clone();
            using var patternGray = patternGrayIn.Clone();

            if (imageGray.Empty() || patternGray.Empty())
            {
                Log(log, "[FEATURE] entradas vacías");
                return (null, 0, "imgs vacías", 0, 0, 0, 0, 256, 0.0);
            }

            using var orb = ORB.Create(
                2000,   // nFeatures
                1.2f,   // scaleFactor
                8,      // nLevels
                15,     // edgeThreshold
                0,      // firstLevel
                2,      // WTA_K
                ORBScoreType.Harris,
                31,     // patchSize
                10      // fastThreshold
            );

            var imgKp = orb.Detect(imageGray);
            var patKp = orb.Detect(patternGray);

            bool imgSmall = imageGray.Width * imageGray.Height <= 64 * 64;
            bool patSmall = patternGray.Width * patternGray.Height <= 64 * 64;

            if (imgKp.Length < 12 || imgSmall)
            {
                using var boosted = ClaheBoost(imageGray);
                boosted.CopyTo(imageGray); // ✅ sin Dispose del caller
                imgKp = orb.Detect(imageGray);
                Log(log, $"[FEATURE] CLAHE imagen -> kps={imgKp.Length}");
            }
            if (patKp.Length < 12 || patSmall)
            {
                using var boosted = ClaheBoost(patternGray);
                boosted.CopyTo(patternGray); // ✅ sin Dispose del caller
                patKp = orb.Detect(patternGray);
                Log(log, $"[FEATURE] CLAHE patrón -> kps={patKp.Length}");
            }

            Log(log, $"[FEATURE] kps(img,pat)=({imgKp.Length},{patKp.Length})");

            if (imgKp.Length < 8 || patKp.Length < 8)
            {
                Log(log, $"[FEATURE] abort: insufficient keypoints img={imgKp.Length} pat={patKp.Length}");
                return (null, 0, "insufficient-keypoints", imgKp.Length, patKp.Length, 0, 0, 0.0, 0.0);
            }

            using var imgDesc = new Mat();
            using var patDesc = new Mat();
            orb.Compute(imageGray, ref imgKp, imgDesc);
            orb.Compute(patternGray, ref patKp, patDesc);

            if (imgDesc.Empty() || patDesc.Empty() || imgKp.Length < 8 || patKp.Length < 8)
            {
                Log(log, "[FEATURE] sin descriptores suficientes");
                return (null, 0, "sin descriptores suficientes", imgKp.Length, patKp.Length, 0, 0, 256, 0.0);
            }

            using var matcher = new BFMatcher(NormTypes.Hamming, crossCheck: false);
            var knnMatches = matcher.KnnMatch(patDesc, imgDesc, k: 2);

            double[] ratios = new[] { 0.75, 0.80, 0.85, 0.90, 0.95 };
            DMatch[] good = Array.Empty<DMatch>();
            double usedRatio = ratios[0];
            foreach (var r in ratios)
            {
                var cand = knnMatches.Where(m => m.Length == 2 && m[0].Distance < r * m[1].Distance).Select(m => m[0]).ToArray();
                if (cand.Length >= 8) { good = cand; usedRatio = r; break; }
                good = cand; usedRatio = r;
            }

            Log(log, $"[FEATURE] good-matches={good.Length}");
            if (good.Length < 8)
            {
                Log(log, $"[FEATURE] abort: too-few-good-matches={good.Length}");
                return (null, 0, "too-few-good-matches", imgKp.Length, patKp.Length, good.Length, 0, 0.0, 0.0);
            }

            Log(log, $"[FEATURE] kps(pattern,img)=({patKp.Length},{imgKp.Length}) good={good.Length} ratio={usedRatio:F2}");

            var srcPts = good.Select(m => patKp[m.QueryIdx].Pt).ToArray();
            var dstPts = good.Select(m => imgKp[m.TrainIdx].Pt).ToArray();

            using var srcMat = PackPoints(srcPts);
            using var dstMat = PackPoints(dstPts);
            using var mask = new Mat();
            using var H = Cv2.FindHomography(srcMat, dstMat, HomographyMethods.Ransac, 3.0, mask);
            if (H.Empty())
            {
                Log(log, "[FEATURE] abort: homography-empty");
                return (null, 0, "homography-empty", imgKp.Length, patKp.Length, good.Length, 0, 0.0, 0.0);
            }

            int inliers = Cv2.CountNonZero(mask);
            double avgDist = good.Average(m => m.Distance);
            int scoreInliers = ToScore((double)inliers / Math.Max(good.Length, 1));
            int scoreDistance = ToScore(1.0 - Math.Clamp(avgDist / 256.0, 0.0, 1.0));
            int score = (int)Math.Round(0.7 * scoreInliers + 0.3 * scoreDistance);

            double rotDegApprox = 0.0;
            try
            {
                double a = H.Get<double>(0, 0);
                double b = H.Get<double>(0, 1);
                rotDegApprox = Math.Atan2(b, a) * 180.0 / Math.PI;
            }
            catch
            {
                // ignore
            }

            var rect = new[]
            {
                new Point2f(0, 0),
                new Point2f(patternGray.Cols, 0),
                new Point2f(patternGray.Cols, patternGray.Rows),
                new Point2f(0, patternGray.Rows)
            };

            using var rectMat = PackPoints(rect);
            using var rectOut = new Mat();
            Cv2.PerspectiveTransform(rectMat, rectOut, H);
            var transformed = UnpackPoints(rectOut);
            double cx = transformed.Average(p => p.X);
            double cy = transformed.Average(p => p.Y);

            Log(log,
                $"[FEATURE] inliers={inliers}/{good.Length} " +
                $"avgDist={avgDist:F1} score={score} rot≈{rotDegApprox:F1}deg");
            Log(log, $"[PATTERN][FEATURE] role={roleTag} kpsImg={imgKp.Length} kpsPat={patKp.Length} good={good.Length} inliers={inliers} avgDist={avgDist:F1} score={score}");
            return (new Point2d(cx, cy), score, null, imgKp.Length, patKp.Length, good.Length, inliers, avgDist, rotDegApprox);
        }

        public static (Point2d? center, int score) MatchInSearchROI(
            Mat fullImageBgr,
            RoiModel patternRoi,
            RoiModel searchRoi,
            string feature,
            int threshold,
            int rotRange,
            double scaleMin,
            double scaleMax,
            Mat? patternOverride = null,
            Action<string>? log = null,
            string role = "")
        {
            var detailed = MatchInSearchROIWithDetails(fullImageBgr, patternRoi, searchRoi, feature, threshold, rotRange, scaleMin, scaleMax, patternOverride, log, role);
            return (detailed.Center, detailed.Score);
        }

        public static LocalMatchResult MatchInSearchROIWithDetails(
            Mat fullImageBgr,
            RoiModel patternRoi,
            RoiModel searchRoi,
            string feature,
            int threshold,
            int rotRange,
            double scaleMin,
            double scaleMax,
            Mat? patternOverride = null,
            Action<string>? log = null,
            string role = "")
        {
            if (fullImageBgr == null) throw new ArgumentNullException(nameof(fullImageBgr));

            using var fullGray = ToGray(fullImageBgr);
            return MatchInSearchROIWithDetailsGray(
                fullGray,
                patternRoi,
                searchRoi,
                feature,
                threshold,
                rotRange,
                scaleMin,
                scaleMax,
                patternOverride,
                log,
                role);
        }

        public static LocalMatchResult MatchInSearchROIWithDetailsGray(
            Mat fullImageGray,
            RoiModel patternRoi,
            RoiModel searchRoi,
            string feature,
            int threshold,
            int rotRange,
            double scaleMin,
            double scaleMax,
            Mat? patternOverride = null,
            Action<string>? log = null,
            string role = "")
        {
            if (fullImageGray == null) throw new ArgumentNullException(nameof(fullImageGray));
            if (searchRoi == null) throw new ArgumentNullException(nameof(searchRoi));

            Mat? fullGrayBase = null;
            bool disposeFullGray = false;

            try
            {
                if (fullImageGray.Channels() == 1)
                {
                    fullGrayBase = fullImageGray;
                }
                else
                {
                    fullGrayBase = ToGray(fullImageGray);
                    disposeFullGray = true;
                }

                if (fullGrayBase.Empty())
                {
                    Log(log, "[INPUT] imagen gris vacía");
                    return new LocalMatchResult { Center = null, Score = 0, AngleDeg = 0, UsedFeatures = false };
                }

                var searchRect = RectFromRoi(fullGrayBase, searchRoi);
                var patternRect = patternOverride == null
                    ? RectFromRoi(fullGrayBase, patternRoi)
                    : new Rect(0, 0, patternOverride.Width, patternOverride.Height);

                if (log != null && System.Diagnostics.Debugger.IsAttached)
                {
                    bool contained = patternOverride == null &&
                                     patternRect.X >= searchRect.X &&
                                     patternRect.Y >= searchRect.Y &&
                                     patternRect.Right <= searchRect.Right &&
                                     patternRect.Bottom <= searchRect.Bottom;

                    Log(log, $"[DBG] searchRect={searchRect.X},{searchRect.Y},{searchRect.Width},{searchRect.Height} patternRect={patternRect.X},{patternRect.Y},{patternRect.Width},{patternRect.Height} contained={contained}");

                    if (contained)
                    {
                        var expectedLocalRect = new Rect(patternRect.X - searchRect.X, patternRect.Y - searchRect.Y, patternRect.Width, patternRect.Height);
                        using var searchGrayBase = new Mat(fullGrayBase, searchRect);
                        using var expectedPatch = new Mat(searchGrayBase, expectedLocalRect);
                        using var patFromFull = new Mat(fullGrayBase, patternRect);

                        using var diff = new Mat();
                        Cv2.Absdiff(patFromFull, expectedPatch, diff);
                        Scalar sum = Cv2.Sum(diff);
                        double mad = (sum.Val0 + sum.Val1 + sum.Val2 + sum.Val3) / (patternRect.Width * patternRect.Height);
                        Log(log, $"[DBG] GT offset=({expectedLocalRect.X},{expectedLocalRect.Y}) MAD={mad:F4}");

                        using var resp = new Mat();
                        Cv2.MatchTemplate(searchGrayBase, patFromFull, resp, TemplateMatchModes.CCoeffNormed);
                        Cv2.MinMaxLoc(resp, out _, out double maxVal, out _, out Point maxLoc);
                        Log(log, $"[DBG] TM@0deg scale=1: max={maxVal:F4} loc=({maxLoc.X},{maxLoc.Y}) vs expected=({expectedLocalRect.X},{expectedLocalRect.Y})");
                    }
                }

                if (searchRect.Width < 5 || searchRect.Height < 5)
                {
                    Log(log, "[INPUT] search ROI demasiado pequeño");
                    return new LocalMatchResult { Center = null, Score = 0, AngleDeg = 0, UsedFeatures = false };
                }

                using var searchGray = new Mat(fullGrayBase, searchRect);

                Mat? patternRegion = null;
                Mat? patternGray = null;
                bool disposePatternGray = false;

                try
                {
                    if (patternOverride != null)
                    {
                        if (patternOverride.Empty() || patternOverride.Width < 3 || patternOverride.Height < 3)
                        {
                            Log(log, "[INPUT] patrón override vacío/pequeño");
                            return new LocalMatchResult { Center = null, Score = 0, AngleDeg = 0, UsedFeatures = false };
                        }

                        if (patternOverride.Channels() == 1)
                        {
                            patternGray = patternOverride;
                        }
                        else
                        {
                            patternGray = ToGray(patternOverride);
                            disposePatternGray = true;
                        }
                    }
                    else
                    {
                        var pr = patternRect;
                        if (pr.Width < 3 || pr.Height < 3)
                        {
                            Log(log, "[INPUT] patrón demasiado pequeño]");
                            return new LocalMatchResult { Center = null, Score = 0, AngleDeg = 0, UsedFeatures = false };
                        }
                        patternRegion = new Mat(fullGrayBase, pr);
                        patternGray = patternRegion;
                    }

                    if (patternGray == null || patternGray.Empty())
                    {
                        Log(log, "[INPUT] patrón vacío/pequeño");
                        return new LocalMatchResult { Center = null, Score = 0, AngleDeg = 0, UsedFeatures = false };
                    }

                    Log(log, $"[INPUT] feature={feature} thr={threshold} search={searchRect.Width}x{searchRect.Height} pattern={patternGray.Width}x{patternGray.Height}");
                    string roleTag = string.IsNullOrWhiteSpace(role) ? (patternRoi?.Role == RoiRole.Master2Pattern ? "M2" : "M1") : role;

                    var mode = (feature ?? "").Trim().ToLowerInvariant();

                    Log(log,
                        $"[MATCH] mode={mode} thr={threshold} " +
                        $"searchRect=({searchRect.X},{searchRect.Y},{searchRect.Width},{searchRect.Height}) " +
                        $"patternRect=({patternRect.X},{patternRect.Y},{patternRect.Width},{patternRect.Height})");

                    if (mode == "edges")
                    {
                        using var searchEdges = new Mat();
                        using var patternEdges = new Mat();

                        Cv2.Canny(searchGray, searchEdges, 50, 150);
                        Cv2.Canny(patternGray, patternEdges, 50, 150);

                        int nzSearch = Cv2.CountNonZero(searchEdges);
                        int nzPattern = Cv2.CountNonZero(patternEdges);

                        Log(log, $"[EDGES] nz(search,pattern)=({nzSearch},{nzPattern}) thr={threshold}");
                        Log(log, "[EDGES] using Canny + MatchTemplateRot");

                        var tm = MatchTemplateRot(searchEdges, patternEdges, rotRange, scaleMin, scaleMax, log, roleTag);

                        if (tm.center is null || tm.score < threshold)
                        {
                            Log(log, $"[EDGES] no-hit score={tm.score} (<{threshold}) corr={tm.bestCorr:F3} cause={tm.failure}");
                            return new LocalMatchResult { Center = null, Score = tm.score, AngleDeg = tm.bestAngleDeg, UsedFeatures = false, ModeRequested = mode, ModeUsed = "edges", Failure = tm.failure, BestCorr = tm.bestCorr, SecondBestCorr = tm.secondBestCorr, CorrMargin = tm.corrMargin, BestScale = tm.bestScale, BestAngleDeg = tm.bestAngleDeg, AcceptedByThreshold = false, SearchWidth = searchRect.Width, SearchHeight = searchRect.Height, PatternWidth = patternGray.Width, PatternHeight = patternGray.Height, TemplateScore = tm.score };
                        }

                        var globalEdges = new Point2d(
                            searchRect.X + tm.center.Value.X,
                            searchRect.Y + tm.center.Value.Y);

                        Log(log,
                            FormattableString.Invariant(
                                $"[EDGES] HIT center=({globalEdges.X:F1},{globalEdges.Y:F1}) score={tm.score} corr={tm.bestCorr:F3}"));

                        return new LocalMatchResult { Center = globalEdges, Score = tm.score, AngleDeg = tm.bestAngleDeg, UsedFeatures = false, ModeRequested = mode, ModeUsed = "edges", BestCorr = tm.bestCorr, SecondBestCorr = tm.secondBestCorr, CorrMargin = tm.corrMargin, BestScale = tm.bestScale, BestAngleDeg = tm.bestAngleDeg, AcceptedByThreshold = true, SearchWidth = searchRect.Width, SearchHeight = searchRect.Height, PatternWidth = patternGray.Width, PatternHeight = patternGray.Height, TemplateScore = tm.score };
                    }

                    if (mode == "tm_rot")
                    {
                        var tm = MatchTemplateRot(searchGray, patternGray, rotRange, scaleMin, scaleMax, log, roleTag);

                        if (tm.center is null || tm.score < threshold)
                        {
                            Log(log, $"[TM] no-hit score={tm.score} (<{threshold}) corr={tm.bestCorr:F3} cause={tm.failure}");
                            return new LocalMatchResult { Center = null, Score = tm.score, AngleDeg = tm.bestAngleDeg, UsedFeatures = false, ModeRequested = mode, ModeUsed = "tm_rot", Failure = tm.failure, BestCorr = tm.bestCorr, SecondBestCorr = tm.secondBestCorr, CorrMargin = tm.corrMargin, BestScale = tm.bestScale, BestAngleDeg = tm.bestAngleDeg, AcceptedByThreshold = false, SearchWidth = searchRect.Width, SearchHeight = searchRect.Height, PatternWidth = patternGray.Width, PatternHeight = patternGray.Height, TemplateScore = tm.score };
                        }

                        var globalTM = new Point2d(searchRect.X + tm.center.Value.X, searchRect.Y + tm.center.Value.Y);
                        Log(log,
                            FormattableString.Invariant(
                                $"[RESULT] HIT (TM) center=({globalTM.X:F1},{globalTM.Y:F1}) score={tm.score} corr={tm.bestCorr:F3}"));
                        return new LocalMatchResult { Center = globalTM, Score = tm.score, AngleDeg = tm.bestAngleDeg, UsedFeatures = false, ModeRequested = mode, ModeUsed = "tm_rot", BestCorr = tm.bestCorr, SecondBestCorr = tm.secondBestCorr, CorrMargin = tm.corrMargin, BestScale = tm.bestScale, BestAngleDeg = tm.bestAngleDeg, AcceptedByThreshold = true, SearchWidth = searchRect.Width, SearchHeight = searchRect.Height, PatternWidth = patternGray.Width, PatternHeight = patternGray.Height, TemplateScore = tm.score };
                    }

                    // 1) FEATURES
                    var feat = MatchFeatures(searchGray, patternGray, log, roleTag);

                    // 2) AUTO: fallback a TM si fallan features
                    if (mode == "auto" && (feat.center is null || feat.score < threshold))
                    {
                        var tm = MatchTemplateRot(searchGray, patternGray, rotRange, scaleMin, scaleMax, log, roleTag);
                        Log(log, $"[PATTERN][AUTO] role={roleTag} requested=auto used=tm_fallback fallback=True causeFeat={feat.failure ?? "<none>"} featScore={feat.score} tmScore={tm.score} threshold={threshold}");

                        if (tm.center is null || tm.score < threshold)
                        {
                            Log(log, $"[RESULT] no-hit scoreFeat={feat.score} scoreTM={tm.score} (<{threshold}) causeFeat={feat.failure} causeTM={tm.failure}");
                            var maxScore = Math.Max(feat.score, tm.score);
                            var angleDeg = tm.score >= feat.score ? tm.bestAngleDeg : feat.rotDegApprox;
                            return new LocalMatchResult { Center = null, Score = maxScore, AngleDeg = angleDeg, UsedFeatures = tm.score < feat.score, ModeRequested = mode, ModeUsed = "auto_fail", UsedFallback = true, Failure = $"feat={feat.failure};tm={tm.failure}", BestCorr = tm.bestCorr, SecondBestCorr = tm.secondBestCorr, CorrMargin = tm.corrMargin, BestScale = tm.bestScale, BestAngleDeg = tm.bestAngleDeg, ImageKeypoints = feat.imgKps, PatternKeypoints = feat.patKps, GoodMatches = feat.goodCount, Inliers = feat.inliers, AvgDistance = feat.avgDist, FeatureScore = feat.score, TemplateScore = tm.score, AcceptedByThreshold = false, SearchWidth = searchRect.Width, SearchHeight = searchRect.Height, PatternWidth = patternGray.Width, PatternHeight = patternGray.Height };
                        }

                        var globalTM = new Point2d(searchRect.X + tm.center.Value.X, searchRect.Y + tm.center.Value.Y);
                        Log(log,
                            FormattableString.Invariant(
                                $"[RESULT] HIT (TM) center=({globalTM.X:F1},{globalTM.Y:F1}) score={tm.score} corr={tm.bestCorr:F3}"));
                        return new LocalMatchResult { Center = globalTM, Score = tm.score, AngleDeg = tm.bestAngleDeg, UsedFeatures = false, ModeRequested = mode, ModeUsed = "tm_fallback", UsedFallback = true, Failure = feat.failure, BestCorr = tm.bestCorr, SecondBestCorr = tm.secondBestCorr, CorrMargin = tm.corrMargin, BestScale = tm.bestScale, BestAngleDeg = tm.bestAngleDeg, ImageKeypoints = feat.imgKps, PatternKeypoints = feat.patKps, GoodMatches = feat.goodCount, Inliers = feat.inliers, AvgDistance = feat.avgDist, FeatureScore = feat.score, TemplateScore = tm.score, AcceptedByThreshold = true, SearchWidth = searchRect.Width, SearchHeight = searchRect.Height, PatternWidth = patternGray.Width, PatternHeight = patternGray.Height };
                    }

                    if (feat.center is null || feat.score < threshold)
                    {
                        var reason = feat.failure ?? (feat.center is null ? "sin coincidencias" : $"score={feat.score}");
                        Log(log, $"[RESULT] no-hit score={feat.score} (<{threshold}) cause={reason}");
                        return new LocalMatchResult { Center = null, Score = feat.score, AngleDeg = feat.rotDegApprox, UsedFeatures = true, ModeRequested = mode, ModeUsed = "features", Failure = reason, ImageKeypoints = feat.imgKps, PatternKeypoints = feat.patKps, GoodMatches = feat.goodCount, Inliers = feat.inliers, AvgDistance = feat.avgDist, FeatureScore = feat.score, AcceptedByThreshold = false, SearchWidth = searchRect.Width, SearchHeight = searchRect.Height, PatternWidth = patternGray.Width, PatternHeight = patternGray.Height };
                    }

                    var global = new Point2d(searchRect.X + feat.center.Value.X, searchRect.Y + feat.center.Value.Y);
                    Log(log,
                        FormattableString.Invariant(
                            $"[RESULT] HIT (FEATURES) center=({global.X:F1},{global.Y:F1}) score={feat.score} inliers={feat.inliers}/{Math.Max(feat.goodCount, 1)}"));
                    Log(log, $"[PATTERN][AUTO] role={roleTag} requested={mode} used=features fallback=False featScore={feat.score} threshold={threshold} good={feat.goodCount} inliers={feat.inliers}");
                    return new LocalMatchResult { Center = global, Score = feat.score, AngleDeg = feat.rotDegApprox, UsedFeatures = true, ModeRequested = mode, ModeUsed = "features", ImageKeypoints = feat.imgKps, PatternKeypoints = feat.patKps, GoodMatches = feat.goodCount, Inliers = feat.inliers, AvgDistance = feat.avgDist, FeatureScore = feat.score, AcceptedByThreshold = true, SearchWidth = searchRect.Width, SearchHeight = searchRect.Height, PatternWidth = patternGray.Width, PatternHeight = patternGray.Height };
                }
                finally
                {
                    if (disposePatternGray)
                    {
                        patternGray?.Dispose();
                    }
                    patternRegion?.Dispose();
                }
            }
            finally
            {
                if (disposeFullGray)
                {
                    fullGrayBase?.Dispose();
                }
            }
        }

        public static (Point2d? center, double score) MatchInSearchROI(Mat image, Rect searchRect, Mat template, double threshold = 0.65)
        {
            if (image == null || image.Empty() || template == null || template.Empty())
            {
                return (null, 0);
            }

            int x = Math.Clamp(searchRect.X, 0, Math.Max(image.Width - 1, 0));
            int y = Math.Clamp(searchRect.Y, 0, Math.Max(image.Height - 1, 0));
            int w = Math.Clamp(searchRect.Width, 1, Math.Max(image.Width - x, 1));
            int h = Math.Clamp(searchRect.Height, 1, Math.Max(image.Height - y, 1));

            var safeRect = new Rect(x, y, w, h);

            using var roi = new Mat(image, safeRect);
            using var result = new Mat();
            Cv2.MatchTemplate(roi, template, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out Point maxLoc);

            if (maxVal < threshold)
            {
                return (null, maxVal);
            }

            var center = new Point2d(
                safeRect.X + maxLoc.X + template.Width / 2.0,
                safeRect.Y + maxLoc.Y + template.Height / 2.0);

            return (center, maxVal);
        }

        private static Rect RectFromRoi(Mat img, RoiModel roi)
        {
            double left, top, right, bottom;
            if (roi.Shape == RoiShape.Rectangle)
            {
                left = roi.Left; top = roi.Top; right = left + roi.Width; bottom = top + roi.Height;
            }
            else
            {
                left = roi.CX - roi.R; top = roi.CY - roi.R; right = roi.CX + roi.R; bottom = roi.CY + roi.R;
            }

            int x = (int)Math.Floor(left);
            int y = (int)Math.Floor(top);
            int w = (int)Math.Ceiling(right - x);
            int h = (int)Math.Ceiling(bottom - y);

            int maxWidth  = Math.Max(img.Width  - 1, 0);
            int maxHeight = Math.Max(img.Height - 1, 0);

            x = Math.Clamp(x, 0, maxWidth);
            y = Math.Clamp(y, 0, maxHeight);
            w = Math.Clamp(w, 1, Math.Max(img.Width  - x, 1));
            h = Math.Clamp(h, 1, Math.Max(img.Height - y, 1));

            return new Rect(x, y, w, h);
        }
    }
}
