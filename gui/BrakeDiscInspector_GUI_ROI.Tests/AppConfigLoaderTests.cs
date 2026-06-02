using System.Text.Json;
using BrakeDiscInspector_GUI_ROI;

public sealed class AppConfigLoaderTests
{
    [Fact]
    public void Save_WritesCommsSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bdi-appsettings-{Guid.NewGuid():N}.json");
        try
        {
            var config = new AppConfig();
            config.Comms.Plc.Mode = "S7";
            config.Comms.Plc.IpAddress = "192.168.0.1";
            config.Comms.Plc.DbNumber = 150;
            config.Comms.Plc.PlcToPcDbNumber = 151;
            config.Comms.Plc.DiagnosticDbNumber = 8;
            config.Comms.Plc.PollIntervalMs = 100;
            config.Comms.AutoConnectOnStartup = true;
            config.Comms.RequirePartPresent = false;
            config.Comms.Camera.Provider = "Folder";
            config.Comms.Camera.Source = @"C:\images";

            AppConfigLoader.Save(config, path);

            using var stream = File.OpenRead(path);
            var saved = JsonSerializer.Deserialize<AppConfig>(stream);

            Assert.NotNull(saved);
            Assert.Equal("S7", saved!.Comms.Plc.Mode);
            Assert.Equal("192.168.0.1", saved.Comms.Plc.IpAddress);
            Assert.Equal(150, saved.Comms.Plc.DbNumber);
            Assert.Equal(151, saved.Comms.Plc.PlcToPcDbNumber);
            Assert.Equal(8, saved.Comms.Plc.DiagnosticDbNumber);
            Assert.Equal(100, saved.Comms.Plc.PollIntervalMs);
            Assert.True(saved.Comms.AutoConnectOnStartup);
            Assert.False(saved.Comms.RequirePartPresent);
            Assert.Equal("Folder", saved.Comms.Camera.Provider);
            Assert.Equal(@"C:\images", saved.Comms.Camera.Source);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
