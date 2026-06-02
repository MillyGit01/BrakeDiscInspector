using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BrakeDiscInspector_GUI_ROI.Comms;

namespace BrakeDiscInspector_GUI_ROI
{
    public partial class MainWindow : Window
    {
        private CommsViewModel? _commsVm;

        private void InitComms()
        {
            try
            {
                if (_appConfig.Comms?.Enabled == false)
                {
                    Util.GuiLog.Info("[comms] Disabled by configuration");
                    return;
                }

                var plcSettings = _appConfig.Comms?.Plc ?? new AppConfig.PlcSettings();
                var cameraSettings = _appConfig.Comms?.Camera ?? new AppConfig.CameraSettings();
                var config = new PlcConfig(
                    plcSettings.IpAddress,
                    plcSettings.Rack,
                    plcSettings.Slot,
                    plcSettings.DbNumber,
                    plcSettings.PlcToPcDbNumber,
                    plcSettings.DiagnosticDbNumber);
                var cameraConfig = new CameraConfig(
                    cameraSettings.Provider,
                    cameraSettings.Source,
                    cameraSettings.OutputDirectory,
                    cameraSettings.TimeoutMs);

                _commsVm = new CommsViewModel(
                    config,
                    plcSettings.Mode,
                    CreatePlcClient,
                    cameraConfig,
                    CameraClientFactory.Create,
                    SaveCommsSettingsAsync,
                    RunCommsInspectionAsync,
                    plcSettings.PollIntervalMs,
                    _appConfig.Comms?.RequirePartPresent ?? true,
                    _appConfig.Comms?.AutoConnectOnStartup ?? false,
                    _appConfig.Comms?.AutoRunInspectionOnTrigger ?? false);

                CommsRoot.DataContext = _commsVm;

                if (_appConfig.Comms?.AutoConnectOnStartup == true)
                {
                    _ = _commsVm.ConnectAsync();
                    _ = _commsVm.ConnectCameraAsync();
                }
            }
            catch (Exception ex)
            {
                Util.GuiLog.Error("[comms] InitComms failed", ex);
            }
        }

        private Task SaveCommsSettingsAsync(CommsSettingsSnapshot settings, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var comms = _appConfig.Comms ??= new AppConfig.CommsConfig();
            var plc = comms.Plc ??= new AppConfig.PlcSettings();
            var camera = comms.Camera ??= new AppConfig.CameraSettings();

            comms.AutoConnectOnStartup = settings.AutoConnectOnStartup;
            comms.AutoRunInspectionOnTrigger = settings.AutoRunInspection;
            comms.RequirePartPresent = settings.RequirePartPresent;

            plc.Mode = settings.PlcMode;
            plc.IpAddress = settings.PlcIpAddress;
            plc.Rack = settings.Rack;
            plc.Slot = settings.Slot;
            plc.DbNumber = settings.PcToPlcDbNumber;
            plc.PlcToPcDbNumber = settings.PlcToPcDbNumber;
            plc.DiagnosticDbNumber = settings.DiagnosticDbNumber;
            plc.PollIntervalMs = settings.PollIntervalMs;

            camera.Provider = settings.CameraProvider;
            camera.Source = settings.CameraSource;
            camera.OutputDirectory = settings.CameraOutputDirectory;

            AppConfigLoader.SaveUserConfig(_appConfig);
            return Task.CompletedTask;
        }

        private static IPlcClient CreatePlcClient(PlcConfig config, string mode)
        {
            return string.Equals(mode, "S7", StringComparison.OrdinalIgnoreCase)
                ? new S7PlcClient(config)
                : new SimulatedPlcClient(config);
        }

        private async Task<bool?> RunCommsInspectionAsync(string imagePath, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !System.IO.File.Exists(imagePath))
            {
                Util.GuiLog.Warn($"[comms] inspection skipped: image not found '{imagePath}'");
                return null;
            }

            await Dispatcher.InvokeAsync(() => LoadImage(imagePath, runAutoAnalyze: false));
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

            if (HasAllMastersAndInspectionsDefined())
            {
                await AnalyzeMastersAsync(showFailureDialog: false).ConfigureAwait(false);
            }

            var vm = ViewModel;
            if (vm == null)
            {
                return null;
            }

            return await vm.EvaluateEnabledRoisForAutomationAsync(ct).ConfigureAwait(false);
        }
    }
}
