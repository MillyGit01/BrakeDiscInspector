using BrakeDiscInspector_GUI_ROI.Comms;

public sealed class CommsViewModelTests
{
    [Fact]
    public async Task TriggerCameraViaPlcCommand_PulsesTrigger1Output()
    {
        var plcConfig = new PlcConfig("192.168.0.1", 0, 1);
        var cameraConfig = new CameraConfig(CameraProviders.Disabled, string.Empty, string.Empty);
        var plcClient = new RecordingPlcClient(plcConfig, isConnected: true);

        using var vm = new CommsViewModel(
            plcConfig,
            "S7",
            (_, _) => plcClient,
            cameraConfig,
            _ => new NoopCameraClient(cameraConfig),
            saveSettingsAsync: null,
            inspectImageAsync: null,
            pollIntervalMs: 100,
            requirePartPresent: true,
            autoConnectOnStartup: false,
            autoRunInspection: false);

        vm.TriggerCameraViaPlcCommand.Execute(null);

        await WaitForAsync(() => plcClient.WriteHistory.Count >= 2);

        Assert.True(plcClient.WriteHistory[0][PlcSignalId.Trigger1]);
        Assert.False(plcClient.WriteHistory[1][PlcSignalId.Trigger1]);
        Assert.Equal("PLC camera trigger pulse", vm.CycleStatus);
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(25, cts.Token);
        }
    }

    private sealed class RecordingPlcClient : IPlcClient
    {
        private readonly bool _isConnected;

        public RecordingPlcClient(PlcConfig config, bool isConnected)
        {
            Config = config;
            _isConnected = isConnected;
        }

        public PlcConfig Config { get; }

        public bool IsConnected => _isConnected;

        public IReadOnlyList<PlcSignalDefinition> SignalDefinitions => PlcSignals.Definitions;

        public List<Dictionary<PlcSignalId, bool>> WriteHistory { get; } = new();

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DisconnectAsync() => Task.CompletedTask;

        public Task<IDictionary<PlcSignalId, bool>> ReadAsync(CancellationToken ct = default)
            => Task.FromResult((IDictionary<PlcSignalId, bool>)new Dictionary<PlcSignalId, bool>());

        public Task WriteOutputsAsync(IDictionary<PlcSignalId, bool> outputs, CancellationToken ct = default)
        {
            WriteHistory.Add(new Dictionary<PlcSignalId, bool>(outputs));
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    private sealed class NoopCameraClient : ICameraClient
    {
        public NoopCameraClient(CameraConfig config)
        {
            Config = config;
        }

        public CameraConfig Config { get; }

        public bool IsConnected => false;

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DisconnectAsync() => Task.CompletedTask;

        public Task<CameraFrame> AcquireAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("Camera acquisition is not used by this test.");

        public void Dispose()
        {
        }
    }
}
