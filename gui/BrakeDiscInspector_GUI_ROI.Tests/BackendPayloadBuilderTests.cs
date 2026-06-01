using System.Text.Json;
using BrakeDiscInspector_GUI_ROI;
using OpenCvSharp;
using Xunit;

namespace BrakeDiscInspector_GUI_ROI.Tests;

public class BackendPayloadBuilderTests
{
    [Fact]
    public void TryPrepareCanonicalRoi_RectangleShapeUsesCanonicalCropDimensions()
    {
        using var src = new Mat(new Size(220, 180), MatType.CV_8UC3, Scalar.Black);
        var roi = new RoiModel
        {
            Shape = RoiShape.Rectangle,
            Width = 48,
            Height = 32,
            AngleDeg = 0
        };
        roi.Left = 120;
        roi.Top = 80;

        Assert.True(BackendPayloadBuilder.TryPrepareCanonicalRoi(src, roi, out var payload, out _));
        Assert.NotNull(payload);

        using var doc = JsonDocument.Parse(payload!.ShapeJson ?? "{}");
        var root = doc.RootElement;

        Assert.Equal("rect", root.GetProperty("kind").GetString());
        Assert.Equal(0, root.GetProperty("x").GetDouble());
        Assert.Equal(0, root.GetProperty("y").GetDouble());
        Assert.Equal(payload.Width, root.GetProperty("w").GetDouble());
        Assert.Equal(payload.Height, root.GetProperty("h").GetDouble());
    }

    [Fact]
    public void TryPrepareCanonicalRoi_AnnulusShapeIncludesInnerRadius()
    {
        using var src = new Mat(new Size(260, 220), MatType.CV_8UC3, Scalar.Black);
        var roi = new RoiModel
        {
            Shape = RoiShape.Annulus,
            CX = 180,
            CY = 120,
            R = 48,
            RInner = 20,
            AngleDeg = 0
        };

        Assert.True(BackendPayloadBuilder.TryPrepareCanonicalRoi(src, roi, out var payload, out _));
        Assert.NotNull(payload);

        using var doc = JsonDocument.Parse(payload!.ShapeJson ?? "{}");
        var root = doc.RootElement;

        Assert.Equal("annulus", root.GetProperty("kind").GetString());
        Assert.Equal(roi.R, root.GetProperty("r").GetDouble());
        Assert.Equal(roi.RInner, root.GetProperty("r_inner").GetDouble());
    }

    [Fact]
    public void TryBuildRoiCropInfo_UsesLeftTopForRectangle()
    {
        var roi = new RoiModel
        {
            Shape = RoiShape.Rectangle,
            Width = 60,
            Height = 40
        };
        roi.Left = 50;
        roi.Top = 30;

        Assert.True(RoiCropUtils.TryBuildRoiCropInfo(roi, out var info));
        Assert.Equal(roi.Left, info.Left);
        Assert.Equal(roi.Top, info.Top);
        Assert.Equal(roi.Width, info.Width);
        Assert.Equal(roi.Height, info.Height);
    }
}
