using System.Text.Json;
using BrakeDiscInspector_GUI_ROI;
using Xunit;

namespace BrakeDiscInspector_GUI_ROI.Tests;

public class MasterLayoutTests
{
    [Fact]
    public void AnnulusRoiSerialization_RoundTripsInnerRadius()
    {
        var layout = new MasterLayout
        {
            Inspection = new RoiModel
            {
                Shape = RoiShape.Annulus,
                CX = 150,
                CY = 120,
                R = 60,
                RInner = 25,
                AngleDeg = 12.5
            }
        };

        if (layout.Inspection is not null)
        {
            layout.Inspection.Width = layout.Inspection.R * 2.0;
            layout.Inspection.Height = layout.Inspection.Width;
        }

        string json = JsonSerializer.Serialize(layout);
        var roundTrip = JsonSerializer.Deserialize<MasterLayout>(json);

        Assert.NotNull(roundTrip);
        Assert.NotNull(roundTrip!.Inspection);
        Assert.Equal(RoiShape.Annulus, roundTrip.Inspection!.Shape);
        Assert.Equal(25, roundTrip.Inspection.RInner);
        Assert.Equal(60, roundTrip.Inspection.R);
    }

    [Fact]
    public void AnalyzeDisableRot_DefaultsToFalse_WhenMissing()
    {
        const string json = "{\"Analyze\":{\"ScaleLock\":true}}";

        var layout = JsonSerializer.Deserialize<MasterLayout>(json);

        Assert.NotNull(layout);
        Assert.False(layout!.Analyze.DisableRot);
    }
}
