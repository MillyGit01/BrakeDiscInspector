using System.Collections.Generic;
using System.Linq;
using BrakeDiscInspector_GUI_ROI;
using BrakeDiscInspector_GUI_ROI.Workflow;
using Xunit;

namespace BrakeDiscInspector_GUI_ROI.Tests;

public class RoiPlacementEngineTests
{
    [Fact]
    public void DisableRot_TranslatesByAnchorWithoutResizing()
    {
        var baselineMasters = new List<RoiModel>
        {
            new RoiModel { Role = RoiRole.Master1Pattern, Shape = RoiShape.Rectangle, X = 0, Y = 0, Width = 10, Height = 10 },
            new RoiModel { Role = RoiRole.Master2Pattern, Shape = RoiShape.Rectangle, X = 10, Y = 0, Width = 10, Height = 10 }
        };

        var inspections = new List<RoiModel>
        {
            new RoiModel { Id = "r1", Shape = RoiShape.Rectangle, X = 4, Y = 1, Width = 6, Height = 8, AngleDeg = 12 },
            new RoiModel { Id = "r2", Shape = RoiShape.Circle, CX = 6, CY = 2, R = 5, Width = 10, Height = 10, AngleDeg = -5 },
            new RoiModel { Id = "r3", Shape = RoiShape.Annulus, CX = 8, CY = 3, R = 6, RInner = 2, Width = 12, Height = 12, AngleDeg = 20 }
        };

        var anchorMap = new Dictionary<string, MasterAnchorChoice>
        {
            ["r1"] = MasterAnchorChoice.Master1,
            ["r2"] = MasterAnchorChoice.Master2,
            ["r3"] = MasterAnchorChoice.Mid
        };

        var input = new RoiPlacementInput(
            new ImgPoint(0, 0),
            new ImgPoint(10, 0),
            new ImgPoint(5, 5),
            new ImgPoint(15, 5),
            DisableRot: true,
            ScaleLock: true,
            ScaleMode.None,
            true,
            anchorMap);

        var output = RoiPlacementEngine.Place(input, baselineMasters, inspections);

        Assert.Equal(3, output.InspectionsPlaced.Count);

        var r1 = output.InspectionsPlaced[0];
        Assert.Equal(4 + 5, r1.X, 6);
        Assert.Equal(1 + 5, r1.Y, 6);
        Assert.Equal(6, r1.Width, 6);
        Assert.Equal(8, r1.Height, 6);
        Assert.Equal(12, r1.AngleDeg, 6);

        var r2 = output.InspectionsPlaced[1];
        Assert.Equal(6 + 5, r2.CX, 6);
        Assert.Equal(2 + 5, r2.CY, 6);
        Assert.Equal(5, r2.R, 6);
        Assert.Equal(-5, r2.AngleDeg, 6);

        var r3 = output.InspectionsPlaced[2];
        Assert.Equal(8 + 5, r3.CX, 6);
        Assert.Equal(3 + 5, r3.CY, 6);
        Assert.Equal(6, r3.R, 6);
        Assert.Equal(2, r3.RInner, 6);
        Assert.Equal(20, r3.AngleDeg, 6);
    }

    [Fact]
    public void RotationEnabled_RotatesOffsetAndAngle()
    {
        var baselineMasters = new List<RoiModel>
        {
            new RoiModel { Role = RoiRole.Master1Pattern, Shape = RoiShape.Rectangle, X = 0, Y = 0, Width = 10, Height = 10 },
            new RoiModel { Role = RoiRole.Master2Pattern, Shape = RoiShape.Rectangle, X = 10, Y = 0, Width = 10, Height = 10 }
        };

        var inspection = new RoiModel
        {
            Id = "r1",
            Shape = RoiShape.Rectangle,
            X = 5,
            Y = 5,
            Width = 4,
            Height = 6,
            AngleDeg = 10
        };

        var input = new RoiPlacementInput(
            new ImgPoint(0, 0),
            new ImgPoint(10, 0),
            new ImgPoint(0, 0),
            new ImgPoint(0, 10),
            DisableRot: false,
            ScaleLock: true,
            ScaleMode.None,
            true,
            new Dictionary<string, MasterAnchorChoice> { ["r1"] = MasterAnchorChoice.Master1 });

        var output = RoiPlacementEngine.Place(input, baselineMasters, new List<RoiModel> { inspection });
        var placed = output.InspectionsPlaced.Single();

        Assert.Equal(-5, placed.X, 6);
        Assert.Equal(5, placed.Y, 6);
        Assert.Equal(4, placed.Width, 6);
        Assert.Equal(6, placed.Height, 6);
        Assert.Equal(100, placed.AngleDeg, 6);
    }

    [Fact]
    public void Place_IsIdempotentForSameInputs()
    {
        var baselineMasters = new List<RoiModel>
        {
            new RoiModel { Role = RoiRole.Master1Pattern, Shape = RoiShape.Rectangle, X = 0, Y = 0, Width = 10, Height = 10 },
            new RoiModel { Role = RoiRole.Master2Pattern, Shape = RoiShape.Rectangle, X = 10, Y = 0, Width = 10, Height = 10 }
        };

        var inspection = new RoiModel
        {
            Id = "r1",
            Shape = RoiShape.Rectangle,
            X = 4,
            Y = 2,
            Width = 6,
            Height = 8,
            AngleDeg = 15
        };

        var input = new RoiPlacementInput(
            new ImgPoint(0, 0),
            new ImgPoint(10, 0),
            new ImgPoint(3, 4),
            new ImgPoint(13, 4),
            DisableRot: false,
            ScaleLock: true,
            ScaleMode.None,
            true,
            new Dictionary<string, MasterAnchorChoice> { ["r1"] = MasterAnchorChoice.Master1 });

        var first = RoiPlacementEngine.Place(input, baselineMasters, new List<RoiModel> { inspection });
        var second = RoiPlacementEngine.Place(input, baselineMasters, new List<RoiModel> { inspection });

        var firstRoi = first.InspectionsPlaced.Single();
        var secondRoi = second.InspectionsPlaced.Single();

        Assert.Equal(firstRoi.X, secondRoi.X, 6);
        Assert.Equal(firstRoi.Y, secondRoi.Y, 6);
        Assert.Equal(firstRoi.AngleDeg, secondRoi.AngleDeg, 6);
        Assert.Equal(firstRoi.Width, secondRoi.Width, 6);
        Assert.Equal(firstRoi.Height, secondRoi.Height, 6);
    }

    [Fact]
    public void Place_ReappliesCorrectlyAcrossImageAlternation()
    {
        var baselineMasters = new List<RoiModel>
        {
            new RoiModel { Role = RoiRole.Master1Pattern, Shape = RoiShape.Rectangle, X = 0, Y = 0, Width = 10, Height = 10 },
            new RoiModel { Role = RoiRole.Master2Pattern, Shape = RoiShape.Rectangle, X = 10, Y = 0, Width = 10, Height = 10 }
        };

        var inspection = new RoiModel
        {
            Id = "r1",
            Shape = RoiShape.Rectangle,
            X = 4,
            Y = 2,
            Width = 6,
            Height = 8,
            AngleDeg = 15
        };

        var anchorMap = new Dictionary<string, MasterAnchorChoice> { ["r1"] = MasterAnchorChoice.Master1 };

        var inputA = new RoiPlacementInput(
            new ImgPoint(0, 0),
            new ImgPoint(10, 0),
            new ImgPoint(2, 3),
            new ImgPoint(12, 3),
            DisableRot: true,
            ScaleLock: true,
            ScaleMode.None,
            true,
            anchorMap);

        var inputB = new RoiPlacementInput(
            new ImgPoint(0, 0),
            new ImgPoint(10, 0),
            new ImgPoint(-4, 1),
            new ImgPoint(6, 1),
            DisableRot: true,
            ScaleLock: true,
            ScaleMode.None,
            true,
            anchorMap);

        var outputA1 = RoiPlacementEngine.Place(inputA, baselineMasters, new List<RoiModel> { inspection });
        var outputB = RoiPlacementEngine.Place(inputB, baselineMasters, new List<RoiModel> { inspection });
        var outputA2 = RoiPlacementEngine.Place(inputA, baselineMasters, new List<RoiModel> { inspection });

        var a1 = outputA1.InspectionsPlaced.Single();
        var a2 = outputA2.InspectionsPlaced.Single();

        Assert.Equal(a1.X, a2.X, 6);
        Assert.Equal(a1.Y, a2.Y, 6);
        Assert.Equal(a1.AngleDeg, a2.AngleDeg, 6);
        Assert.Equal(a1.Width, a2.Width, 6);
        Assert.Equal(a1.Height, a2.Height, 6);
        Assert.NotEqual(a1.X, outputB.InspectionsPlaced.Single().X, 6);
    }
}
