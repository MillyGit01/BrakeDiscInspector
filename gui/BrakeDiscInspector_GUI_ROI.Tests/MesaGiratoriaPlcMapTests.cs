using System.Collections.Generic;
using BrakeDiscInspector_GUI_ROI.Comms;

public sealed class MesaGiratoriaPlcMapTests
{
    [Fact]
    public void PlcConfig_UsesMesaGiratoriaDefaultDbNumbers()
    {
        var config = new PlcConfig("192.168.0.10", 0, 1);

        Assert.Equal(150, config.PcToPlcDbNumber);
        Assert.Equal(151, config.PlcToPcDbNumber);
        Assert.Equal(8, config.DiagnosticDbNumber);
    }

    [Fact]
    public void Decode_MapsPlcToPcMesaGiratoriaBits()
    {
        var plcToPc = new byte[PlcSignals.PlcToPcReadLength];
        plcToPc[0] = 0b0010_0001;
        plcToPc[1] = 0b1100_0111;

        var decoded = PlcSignals.Decode(plcToPc);

        Assert.True(decoded[PlcSignalId.PartPresent]);
        Assert.True(decoded[PlcSignalId.ResetError]);
        Assert.True(decoded[PlcSignalId.StartInspection]);
        Assert.True(decoded[PlcSignalId.InspectionCompleteCam1]);
        Assert.True(decoded[PlcSignalId.PlcReportedOkCam1]);
        Assert.True(decoded[PlcSignalId.AutoCam1Active]);
        Assert.True(decoded[PlcSignalId.AutoCam1Error]);
    }

    [Fact]
    public void EncodeOutputs_WritesOnlyPcToPlcMesaGiratoriaBoolBytes()
    {
        var pcToPlc = new byte[PlcSignals.PcToPlcBoolWriteLength];

        PlcSignals.EncodeOutputs(pcToPlc, new Dictionary<PlcSignalId, bool>
        {
            [PlcSignalId.SystemReady] = true,
            [PlcSignalId.Busy] = true,
            [PlcSignalId.InspectionComplete] = true,
            [PlcSignalId.ResultOk] = false,
            [PlcSignalId.AutoCam1Start] = true,
            [PlcSignalId.ResultNg] = true
        });

        Assert.Equal(0b0000_0101, pcToPlc[0]);
        Assert.Equal(0b0110_0010, pcToPlc[1]);
    }
}
