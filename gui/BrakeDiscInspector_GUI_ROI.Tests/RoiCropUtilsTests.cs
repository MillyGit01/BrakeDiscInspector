using System;
using BrakeDiscInspector_GUI_ROI;
using OpenCvSharp;
using Xunit;

namespace BrakeDiscInspector_GUI_ROI.Tests;

public class RoiCropUtilsTests
{
    [Fact]
    public void TryGetRotatedCrop_RespectsClockwiseAngles()
    {
        using var source = new Mat(new Size(160, 160), MatType.CV_8UC1, Scalar.All(0));

        const double left = 60;
        const double top = 40;
        const double width = 40;
        const double height = 30;
        var roiRect = new Rect((int)left, (int)top, (int)width, (int)height);

        // Create an asymmetric pattern inside the ROI so the rotation direction matters.
        Cv2.Rectangle(source, roiRect, Scalar.All(30), -1);
        Cv2.Circle(source, new Point(roiRect.Left + 5, roiRect.Top + 8), 4, Scalar.All(200), -1);
        Cv2.Line(source, new Point(roiRect.Right - 2, roiRect.Top), new Point(roiRect.Right - 2, roiRect.Bottom - 1),
            Scalar.All(120), 2);

        const double angleDeg = 37.0;

        var roi = new RoiModel
        {
            Shape = RoiShape.Rectangle,
            Width = width,
            Height = height
        };
        roi.Left = left;
        roi.Top = top;

        Assert.True(RoiCropUtils.TryBuildRoiCropInfo(roi, out var info));
        Assert.Equal(left, info.Left);
        Assert.Equal(top, info.Top);
        Assert.Equal(width, info.Width);
        Assert.Equal(height, info.Height);

        Assert.True(RoiCropUtils.TryGetRotatedCrop(source, info, angleDeg, out var actualCrop, out var actualRect));
        using var actual = actualCrop;

        var expectedRect = BuildCropRect(info, source.Size());
        Assert.Equal(expectedRect.X, actualRect.X);
        Assert.Equal(expectedRect.Y, actualRect.Y);
        Assert.Equal(expectedRect.Width, actualRect.Width);
        Assert.Equal(expectedRect.Height, actualRect.Height);

        using var expected = BuildExpectedCrop(source, info, angleDeg, expectedRect);
        using var diff = new Mat();
        Cv2.Absdiff(expected, actual, diff);
        Assert.Equal(0, Cv2.CountNonZero(diff));
    }

    [Theory]
    [InlineData(20, 30, 40, 30, 27.5)]
    [InlineData(15.2, 18.8, 55.5, 32.1, -42)]
    public void TryGetRotatedCrop_RecentersAroundTransformedRoi(double left, double top, double width, double height, double angleDeg)
    {
        using var source = new Mat(new Size(200, 180), MatType.CV_8UC1, Scalar.All(0));

        var roi = new RoiModel
        {
            Shape = RoiShape.Rectangle,
            Width = width,
            Height = height
        };
        roi.Left = left;
        roi.Top = top;

        Assert.True(RoiCropUtils.TryBuildRoiCropInfo(roi, out var info));
        Assert.True(RoiCropUtils.TryGetRotatedCrop(source, info, angleDeg, out var crop, out var cropRect));
        using var _ = crop;

        var pivot = new Point2f((float)info.PivotX, (float)info.PivotY);
        using var rotation = Cv2.GetRotationMatrix2D(pivot, angleDeg, 1.0);

        static Point2f Apply(Mat matrix, Point2f p)
        {
            float x = (float)(matrix.Get<double>(0, 0) * p.X + matrix.Get<double>(0, 1) * p.Y + matrix.Get<double>(0, 2));
            float y = (float)(matrix.Get<double>(1, 0) * p.X + matrix.Get<double>(1, 1) * p.Y + matrix.Get<double>(1, 2));
            return new Point2f(x, y);
        }

        var roiCenter = new Point2f((float)(info.Left + info.Width * 0.5), (float)(info.Top + info.Height * 0.5));
        var transformedCenter = Apply(rotation, roiCenter);

        var cropCenter = new Point2d(cropRect.X + cropRect.Width / 2.0, cropRect.Y + cropRect.Height / 2.0);

        double deltaX = Math.Abs(cropCenter.X - transformedCenter.X);
        double deltaY = Math.Abs(cropCenter.Y - transformedCenter.Y);

        Assert.InRange(deltaX, 0, 0.51);
        Assert.InRange(deltaY, 0, 0.51);
    }

    private static Rect BuildCropRect(RoiCropInfo info, Size sourceSize)
    {
        int x = (int)Math.Floor(info.Left);
        int y = (int)Math.Floor(info.Top);
        int w = (int)Math.Ceiling(info.Left + Math.Max(info.Width, 1.0)) - x;
        int h = (int)Math.Ceiling(info.Top + Math.Max(info.Height, 1.0)) - y;

        w = Math.Max(w, 1);
        h = Math.Max(h, 1);

        x = Math.Clamp(x, 0, sourceSize.Width - 1);
        y = Math.Clamp(y, 0, sourceSize.Height - 1);
        w = Math.Clamp(w, 1, sourceSize.Width - x);
        h = Math.Clamp(h, 1, sourceSize.Height - y);

        return new Rect(x, y, w, h);
    }

    private static Mat BuildExpectedCrop(Mat source, RoiCropInfo info, double angleDeg, Rect expectedRect)
    {
        var pivot = new Point2f((float)info.PivotX, (float)info.PivotY);
        using var rotation = Cv2.GetRotationMatrix2D(pivot, angleDeg, 1.0);
        var border = source.Channels() == 4 ? new Scalar(0, 0, 0, 0) : Scalar.All(0);
        using var rotated = new Mat();
        Cv2.WarpAffine(source, rotated, rotation, source.Size(), InterpolationFlags.Linear, BorderTypes.Constant, border);
        return new Mat(rotated, expectedRect).Clone();
    }
}
