using System;
using System.Windows;
using BrakeDiscInspector_GUI_ROI;
using Xunit;

namespace BrakeDiscInspector_GUI_ROI.Tests;

public class InspectionAlignmentHelperTests
{
    [Theory]
    [InlineData(RoiShape.Rectangle)]
    [InlineData(RoiShape.Circle)]
    [InlineData(RoiShape.Annulus)]
    public void MoveInspectionTo_DoesNotDriftAcrossRepeatedApplications(RoiShape shape)
    {
        var baselineMaster1 = new RoiModel
        {
            Shape = RoiShape.Rectangle,
            X = 120,
            Y = 110,
            Width = 60,
            Height = 40
        };

        var baselineMaster2 = new RoiModel
        {
            Shape = RoiShape.Rectangle,
            X = 250,
            Y = 180,
            Width = 55,
            Height = 35
        };

        var baselineInspection = CreateBaselineInspection(shape);
        var target = baselineInspection.Clone();

        var master1 = new Point(150, 140);
        var master2 = new Point(320, 210);

        InspectionAlignmentHelper.MoveInspectionTo(
            target,
            baselineInspection.Clone(),
            baselineMaster1,
            baselineMaster2,
            master1,
            master2);

        Assert.False(AreApproximatelyEqual(baselineInspection, target, shape));

        var firstResult = target.Clone();

        InspectionAlignmentHelper.MoveInspectionTo(
            target,
            baselineInspection.Clone(),
            baselineMaster1,
            baselineMaster2,
            master1,
            master2);

        AssertClose(firstResult, target, shape);
    }

    [Fact]
    public void MoveInspectionTo_AppliesRotation_WithoutScaling()
    {
        var baselineInspection = new RoiModel
        {
            Shape = RoiShape.Rectangle,
            X = 5,
            Y = 5,
            Width = 4,
            Height = 6,
            AngleDeg = 10
        };

        var target = baselineInspection.Clone();
        var anchors = CreateAnchorContext(scaleLock: false, disableRot: false);

        InspectionAlignmentHelper.MoveInspectionTo(target, baselineInspection, MasterAnchorChoice.Master1, anchors);

        Assert.Equal(-4, target.X, 6);
        Assert.Equal(7, target.Y, 6);
        Assert.Equal(4, target.Width, 6);
        Assert.Equal(6, target.Height, 6);
        Assert.Equal(100, target.AngleDeg, 6);
    }

    [Fact]
    public void MoveInspectionTo_DisableRot_TranslatesWithoutRotation()
    {
        var baselineInspection = new RoiModel
        {
            Shape = RoiShape.Rectangle,
            X = 5,
            Y = 5,
            Width = 4,
            Height = 6,
            AngleDeg = 10
        };

        var target = baselineInspection.Clone();
        var anchors = CreateAnchorContext(scaleLock: false, disableRot: true);

        InspectionAlignmentHelper.MoveInspectionTo(target, baselineInspection, MasterAnchorChoice.Master1, anchors);

        Assert.Equal(-4, target.X, 6);
        Assert.Equal(7, target.Y, 6);
        Assert.Equal(4, target.Width, 6);
        Assert.Equal(6, target.Height, 6);
        Assert.Equal(10, target.AngleDeg, 6);
    }

    [Fact]
    public void MoveInspectionTo_ScaleLock_KeepsSizeAndOffsetScale()
    {
        var baselineInspection = new RoiModel
        {
            Shape = RoiShape.Rectangle,
            X = 5,
            Y = 5,
            Width = 4,
            Height = 6,
            AngleDeg = 10
        };

        var target = baselineInspection.Clone();
        var anchors = CreateAnchorContext(scaleLock: true, disableRot: false);

        InspectionAlignmentHelper.MoveInspectionTo(target, baselineInspection, MasterAnchorChoice.Master1, anchors);

        Assert.Equal(-4, target.X, 6);
        Assert.Equal(7, target.Y, 6);
        Assert.Equal(4, target.Width, 6);
        Assert.Equal(6, target.Height, 6);
        Assert.Equal(100, target.AngleDeg, 6);
    }

    [Fact]
    public void MoveInspectionTo_DisableRotAndScaleLock_TranslatesOnly()
    {
        var baselineInspection = new RoiModel
        {
            Shape = RoiShape.Rectangle,
            X = 5,
            Y = 5,
            Width = 4,
            Height = 6,
            AngleDeg = 10
        };

        var target = baselineInspection.Clone();
        var anchors = CreateAnchorContext(scaleLock: true, disableRot: true);

        InspectionAlignmentHelper.MoveInspectionTo(target, baselineInspection, MasterAnchorChoice.Master1, anchors);

        Assert.Equal(-4, target.X, 6);
        Assert.Equal(7, target.Y, 6);
        Assert.Equal(4, target.Width, 6);
        Assert.Equal(6, target.Height, 6);
        Assert.Equal(10, target.AngleDeg, 6);
    }

    [Fact]
    public void MoveInspectionTo_UsesMaster2AnchorOffset()
    {
        var baselineInspection = new RoiModel
        {
            Shape = RoiShape.Rectangle,
            X = 130,
            Y = 20,
            Width = 20,
            Height = 10,
            AngleDeg = 0
        };

        var target = baselineInspection.Clone();

        var baselineM1 = new Point2d(0, 0);
        var baselineM2 = new Point2d(100, 0);
        var detectedM1 = new Point2d(10, -5);
        var detectedM2 = new Point2d(70, 60);

        var scale = Distance(detectedM1, detectedM2) / Distance(baselineM1, baselineM2);
        var angleDelta = Math.Atan2(detectedM2.Y - detectedM1.Y, detectedM2.X - detectedM1.X)
                         - Math.Atan2(baselineM2.Y - baselineM1.Y, baselineM2.X - baselineM1.X);

        var anchors = new AnchorTransformContext(
            baselineM1,
            baselineM2,
            detectedM1,
            detectedM2,
            0,
            0,
            0,
            0,
            scale,
            angleDelta,
            scaleLock: false,
            disableRot: false);

        InspectionAlignmentHelper.MoveInspectionTo(target, baselineInspection, MasterAnchorChoice.Master2, anchors);

        var (baselineCx, baselineCy) = baselineInspection.GetCenter();
        var baselineOffset = new Point2d(baselineCx - baselineM2.X, baselineCy - baselineM2.Y);

        var cosA = Math.Cos(angleDelta);
        var sinA = Math.Sin(angleDelta);
        var expectedOffset = new Point2d(
            (baselineOffset.X * cosA - baselineOffset.Y * sinA),
            (baselineOffset.X * sinA + baselineOffset.Y * cosA));

        var expectedCenter = new Point2d(detectedM2.X + expectedOffset.X, detectedM2.Y + expectedOffset.Y);
        Assert.Equal(expectedCenter.X, target.X, 6);
        Assert.Equal(expectedCenter.Y, target.Y, 6);
    }

    private static AnchorTransformContext CreateAnchorContext(bool scaleLock, bool disableRot)
    {
        var baselineM1 = new Point2d(0, 0);
        var baselineM2 = new Point2d(10, 0);
        var detectedM1 = new Point2d(1, 2);
        var detectedM2 = new Point2d(1, 22);

        return new AnchorTransformContext(
            baselineM1,
            baselineM2,
            detectedM1,
            detectedM2,
            0,
            0,
            0,
            0,
            2.0,
            Math.PI / 2.0,
            scaleLock,
            disableRot);
    }

    private static RoiModel CreateBaselineInspection(RoiShape shape)
    {
        return shape switch
        {
            RoiShape.Rectangle => new RoiModel
            {
                Shape = RoiShape.Rectangle,
                X = 200,
                Y = 160,
                Width = 90,
                Height = 60,
                AngleDeg = 28
            },
            RoiShape.Circle => new RoiModel
            {
                Shape = RoiShape.Circle,
                CX = 210,
                CY = 150,
                R = 45,
                AngleDeg = -5
            },
            RoiShape.Annulus => new RoiModel
            {
                Shape = RoiShape.Annulus,
                CX = 205,
                CY = 155,
                R = 60,
                RInner = 25,
                AngleDeg = 12
            },
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
        };
    }

    private static bool AreApproximatelyEqual(RoiModel expected, RoiModel actual, RoiShape shape)
    {
        const double tolerance = 1e-6;

        return shape switch
        {
            RoiShape.Rectangle =>
                AreClose(expected.X, actual.X, tolerance) &&
                AreClose(expected.Y, actual.Y, tolerance) &&
                AreClose(expected.Width, actual.Width, tolerance) &&
                AreClose(expected.Height, actual.Height, tolerance) &&
                AreClose(expected.AngleDeg, actual.AngleDeg, tolerance),
            RoiShape.Circle =>
                AreClose(expected.CX, actual.CX, tolerance) &&
                AreClose(expected.CY, actual.CY, tolerance) &&
                AreClose(expected.R, actual.R, tolerance),
            RoiShape.Annulus =>
                AreClose(expected.CX, actual.CX, tolerance) &&
                AreClose(expected.CY, actual.CY, tolerance) &&
                AreClose(expected.R, actual.R, tolerance) &&
                AreClose(expected.RInner, actual.RInner, tolerance),
            _ => false
        };
    }

    private static void AssertClose(RoiModel expected, RoiModel actual, RoiShape shape)
    {
        const int precision = 6;

        Assert.Equal(expected.Shape, actual.Shape);

        switch (shape)
        {
            case RoiShape.Rectangle:
                Assert.Equal(expected.X, actual.X, precision);
                Assert.Equal(expected.Y, actual.Y, precision);
                Assert.Equal(expected.Width, actual.Width, precision);
                Assert.Equal(expected.Height, actual.Height, precision);
                Assert.Equal(expected.AngleDeg, actual.AngleDeg, precision);
                break;
            case RoiShape.Circle:
                Assert.Equal(expected.CX, actual.CX, precision);
                Assert.Equal(expected.CY, actual.CY, precision);
                Assert.Equal(expected.R, actual.R, precision);
                Assert.Equal(expected.AngleDeg, actual.AngleDeg, precision);
                break;
            case RoiShape.Annulus:
                Assert.Equal(expected.CX, actual.CX, precision);
                Assert.Equal(expected.CY, actual.CY, precision);
                Assert.Equal(expected.R, actual.R, precision);
                Assert.Equal(expected.RInner, actual.RInner, precision);
                Assert.Equal(expected.AngleDeg, actual.AngleDeg, precision);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }
    }

    private static double Distance(Point2d a, Point2d b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static bool AreClose(double expected, double actual, double tolerance)
    {
        return Math.Abs(expected - actual) <= tolerance;
    }
}
