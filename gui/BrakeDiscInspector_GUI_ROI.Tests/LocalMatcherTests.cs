using OpenCvSharp;
using Xunit;

namespace BrakeDiscInspector_GUI_ROI.Tests
{
    public class LocalMatcherTests
    {
        [Fact]
        public void MatchInSearchROI_AllowsEqualSizedTemplate()
        {
            using var fullImage = new Mat(new Size(80, 80), MatType.CV_8UC3, Scalar.Black);

            // Draw a distinctive pattern inside the ROI so template matching has a clear maximum.
            Cv2.Rectangle(fullImage, new Rect(10, 10, 60, 60), new Scalar(40, 80, 160), -1);
            Cv2.Circle(fullImage, new Point(40, 40), 12, new Scalar(200, 30, 60), -1);

            var patternRoi = new RoiModel
            {
                Shape = RoiShape.Rectangle,
                X = 40,
                Y = 40,
                Width = 60,
                Height = 60
            };

            var searchRoi = patternRoi.Clone();

            var (center, score) = LocalMatcher.MatchInSearchROI(
                fullImage,
                patternRoi,
                searchRoi,
                feature: "tm_rot",
                thr: 0,
                rotRange: 0,
                scaleMin: 1.0,
                scaleMax: 1.0);

            Assert.NotNull(center);
            Assert.InRange(center.Value.X, 39.5, 40.5);
            Assert.InRange(center.Value.Y, 39.5, 40.5);
            Assert.InRange(score, 90, 100);
        }

        [Fact]
        public void MatchInSearchROI_UsesOverridePatternWhenProvided()
        {
            using var fullImage = new Mat(new Size(200, 200), MatType.CV_8UC3, Scalar.Black);

            // Pattern positioned away from the search ROI center
            var patternRect = new Rect(70, 90, 40, 30);
            Cv2.Rectangle(fullImage, patternRect, new Scalar(10, 220, 30), -1);
            Cv2.Circle(fullImage, new Point(patternRect.X + patternRect.Width / 2, patternRect.Y + patternRect.Height / 2), 10,
                new Scalar(180, 20, 200), -1);

            var patternRoi = new RoiModel
            {
                Shape = RoiShape.Rectangle,
                X = patternRect.X + patternRect.Width / 2.0,
                Y = patternRect.Y + patternRect.Height / 2.0,
                Width = patternRect.Width,
                Height = patternRect.Height
            };

            var searchRoi = new RoiModel
            {
                Shape = RoiShape.Rectangle,
                X = 110,
                Y = 110,
                Width = 120,
                Height = 120
            };

            using var patternView = new Mat(fullImage, patternRect);
            using var patternOverride = patternView.Clone();

            var (center, score) = LocalMatcher.MatchInSearchROI(
                fullImage,
                patternRoi,
                searchRoi,
                feature: "tm_rot",
                thr: 0,
                rotRange: 0,
                scaleMin: 1.0,
                scaleMax: 1.0,
                patternOverride: patternOverride);

            Assert.NotNull(center);
            Assert.InRange(center.Value.X, patternRoi.X - 0.5, patternRoi.X + 0.5);
            Assert.InRange(center.Value.Y, patternRoi.Y - 0.5, patternRoi.Y + 0.5);
            Assert.InRange(score, 70, 100);
        }

        [Fact]
        public void MatchInSearchROIWithDetails_TmRot_FillsModeAndTemplateMetrics()
        {
            using var fullImage = new Mat(new Size(120, 120), MatType.CV_8UC3, Scalar.Black);
            var patternRect = new Rect(35, 50, 30, 20);
            Cv2.Rectangle(fullImage, patternRect, new Scalar(190, 30, 200), -1);
            Cv2.Line(fullImage, new Point(patternRect.X, patternRect.Y), new Point(patternRect.Right - 1, patternRect.Bottom - 1), new Scalar(20, 240, 20), 2);

            var roi = new RoiModel { Shape = RoiShape.Rectangle, X = 50, Y = 60, Width = 30, Height = 20 };
            var search = new RoiModel { Shape = RoiShape.Rectangle, X = 50, Y = 60, Width = 80, Height = 80 };

            var result = LocalMatcher.MatchInSearchROIWithDetails(fullImage, roi, search, "tm_rot", 40, 0, 1.0, 1.0);
            Assert.Equal("tm_rot", result.ModeRequested);
            Assert.Equal("tm_rot", result.ModeUsed);
            Assert.False(result.UsedFallback);
            Assert.False(result.UsedFeatures);
            Assert.True(result.AcceptedByThreshold);
            Assert.Equal(result.Score, (int)result.TemplateScore);
            Assert.True(result.BestCorr > 0);
        }

        [Fact]
        public void MatchInSearchROIWithDetails_Auto_AlwaysSetsModeUsed()
        {
            using var fullImage = new Mat(new Size(150, 150), MatType.CV_8UC3, Scalar.Black);
            var patternRect = new Rect(60, 70, 28, 28);
            Cv2.Rectangle(fullImage, patternRect, new Scalar(220, 220, 220), -1);
            Cv2.Circle(fullImage, new Point(74, 84), 8, new Scalar(10, 10, 10), -1);

            var pattern = new RoiModel { Shape = RoiShape.Rectangle, X = 74, Y = 84, Width = 28, Height = 28 };
            var search = new RoiModel { Shape = RoiShape.Rectangle, X = 74, Y = 84, Width = 70, Height = 70 };

            var result = LocalMatcher.MatchInSearchROIWithDetails(fullImage, pattern, search, "auto", 70, 4, 0.95, 1.05);
            Assert.Equal("auto", result.ModeRequested);
            Assert.False(string.IsNullOrWhiteSpace(result.ModeUsed));
            Assert.Contains(result.ModeUsed, new[] { "features", "tm_fallback", "auto_fail" });
            if (result.ModeUsed == "tm_fallback")
            {
                Assert.True(result.UsedFallback);
                Assert.True(result.TemplateScore > 0);
            }
            if (result.ModeUsed == "features")
            {
                Assert.True(result.UsedFeatures);
                Assert.True(result.FeatureScore > 0);
            }
        }
    }
}
