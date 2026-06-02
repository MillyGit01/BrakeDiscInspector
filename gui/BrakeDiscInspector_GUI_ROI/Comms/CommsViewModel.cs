using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BrakeDiscInspector_GUI_ROI.Util;
using BrakeDiscInspector_GUI_ROI.Workflow;
using Forms = System.Windows.Forms;

namespace BrakeDiscInspector_GUI_ROI.Comms
{
    public sealed class PlcIoPointViewModel : INotifyPropertyChanged
    {
        private bool _isOn;

        public PlcIoPointViewModel(PlcSignalDefinition definition)
        {
            Definition = definition ?? throw new ArgumentNullException(nameof(definition));
            Id = definition.Id;
            DisplayName = definition.DisplayName;
            IsInput = definition.Direction == PlcSignalDirection.Input;
        }

        public PlcSignalDefinition Definition { get; }

        public PlcSignalId Id { get; }

        public string DisplayName { get; }

        public bool IsInput { get; }

        public bool IsOn
        {
            get => _isOn;
            set
            {
                if (_isOn != value)
                {
                    _isOn = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public sealed class CommsSettingsSnapshot
    {
        public string PlcMode { get; set; } = "Simulation";

        public string PlcIpAddress { get; set; } = "192.168.0.1";

        public short Rack { get; set; }

        public short Slot { get; set; } = 1;

        public int PcToPlcDbNumber { get; set; } = PlcSignals.DefaultPcToPlcDbNumber;

        public int PlcToPcDbNumber { get; set; } = PlcSignals.DefaultPlcToPcDbNumber;

        public int DiagnosticDbNumber { get; set; } = PlcSignals.DefaultDiagnosticDbNumber;

        public int PollIntervalMs { get; set; } = 100;

        public bool AutoConnectOnStartup { get; set; }

        public bool AutoRunInspection { get; set; }

        public bool RequirePartPresent { get; set; } = true;

        public string CameraProvider { get; set; } = CameraProviders.Disabled;

        public string CameraSource { get; set; } = string.Empty;

        public string CameraOutputDirectory { get; set; } = string.Empty;
    }

    public sealed class CommsViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly Func<PlcConfig, string, IPlcClient> _clientFactory;
        private readonly Func<CameraConfig, ICameraClient> _cameraFactory;
        private readonly Func<CommsSettingsSnapshot, CancellationToken, Task>? _saveSettingsAsync;
        private readonly Func<string, CancellationToken, Task<bool?>>? _inspectImageAsync;
        private IPlcClient _client;
        private ICameraClient _camera;
        private string _clientMode;
        private readonly SynchronizationContext? _uiContext;
        private CancellationTokenSource? _pollingCts;
        private Task? _pollingTask;
        private int _pollIntervalMs;
        private bool _requirePartPresent;
        private readonly object _cycleSync = new();
        private readonly Dictionary<PlcSignalId, bool> _lastSignals = new();
        private CancellationTokenSource? _cycleCts;
        private string _plcIpAddress;
        private short _rack;
        private short _slot;
        private int _dbNumber;
        private int _plcToPcDbNumber;
        private int _diagnosticDbNumber;
        private string _plcMode;
        private string _connectionStatus = "Disconnected";
        private string _cameraProvider;
        private string _cameraSource;
        private string _cameraOutputDirectory;
        private string _cameraStatus = "Camera disconnected";
        private string _lastAcquiredImagePath = string.Empty;
        private string _cycleStatus = "Idle";
        private string _settingsStatus = "Comms settings not saved";
        private bool _autoConnectOnStartup;
        private bool _autoRunInspection;
        private bool _cycleInProgress;

        public CommsViewModel(
            PlcConfig config,
            string plcMode,
            Func<PlcConfig, string, IPlcClient> clientFactory,
            CameraConfig cameraConfig,
            Func<CameraConfig, ICameraClient> cameraFactory,
            Func<CommsSettingsSnapshot, CancellationToken, Task>? saveSettingsAsync,
            Func<string, CancellationToken, Task<bool?>>? inspectImageAsync,
            int pollIntervalMs,
            bool requirePartPresent,
            bool autoConnectOnStartup,
            bool autoRunInspection)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
            _cameraFactory = cameraFactory ?? throw new ArgumentNullException(nameof(cameraFactory));
            _inspectImageAsync = inspectImageAsync;
            _clientMode = NormalizePlcMode(plcMode);
            _client = _clientFactory(config, _clientMode);
            _camera = _cameraFactory(cameraConfig ?? new CameraConfig(BrakeDiscInspector_GUI_ROI.Comms.CameraProviders.Disabled, string.Empty, string.Empty));
            _saveSettingsAsync = saveSettingsAsync;
            _uiContext = SynchronizationContext.Current;
            _pollIntervalMs = Math.Max(50, pollIntervalMs);
            _requirePartPresent = requirePartPresent;
            _plcIpAddress = config.IpAddress;
            _rack = config.Rack;
            _slot = config.Slot;
            _dbNumber = config.DbNumber;
            _plcToPcDbNumber = config.PlcToPcDbNumber;
            _diagnosticDbNumber = config.DiagnosticDbNumber;
            _plcMode = _clientMode;
            _cameraProvider = cameraConfig?.Provider ?? BrakeDiscInspector_GUI_ROI.Comms.CameraProviders.Disabled;
            _cameraSource = cameraConfig?.Source ?? string.Empty;
            _cameraOutputDirectory = cameraConfig?.OutputDirectory ?? string.Empty;
            _autoConnectOnStartup = autoConnectOnStartup;
            _autoRunInspection = autoRunInspection;

            Inputs = new ObservableCollection<PlcIoPointViewModel>();
            Outputs = new ObservableCollection<PlcIoPointViewModel>();

            foreach (var definition in _client.SignalDefinitions)
            {
                var vm = new PlcIoPointViewModel(definition);
                if (definition.Direction == PlcSignalDirection.Input)
                {
                    Inputs.Add(vm);
                }
                else
                {
                    Outputs.Add(vm);
                }
            }

            ConnectCommand = new AsyncCommand(_ => ConnectAsync());
            DisconnectCommand = new AsyncCommand(_ => DisconnectAsync());
            SaveSettingsCommand = new AsyncCommand(_ => SaveSettingsCommandAsync());
            ToggleOutputCommand = new AsyncCommand(param => ToggleOutputAsync(param));
            ToggleInputCommand = new AsyncCommand(param => ToggleInputAsync(param));
            StartAutoCam1Command = new AsyncCommand(_ => PulseAutoCam1StartAsync());
            ConnectCameraCommand = new AsyncCommand(_ => ConnectCameraAsync());
            DisconnectCameraCommand = new AsyncCommand(_ => DisconnectCameraAsync());
            AcquireImageCommand = new AsyncCommand(_ => AcquireImageCycleAsync("manual"));
            BrowseCameraSourceCommand = new AsyncCommand(_ => BrowseCameraSourceAsync(), _ => IsFolderCameraProvider);
        }

        public ObservableCollection<PlcIoPointViewModel> Inputs { get; }

        public ObservableCollection<PlcIoPointViewModel> Outputs { get; }

        public IReadOnlyList<string> PlcModes { get; } = new[] { "Simulation", "S7" };

        public IReadOnlyList<string> CameraProviders { get; } = BrakeDiscInspector_GUI_ROI.Comms.CameraProviders.All;

        public string PlcMode
        {
            get => _plcMode;
            set
            {
                var normalized = NormalizePlcMode(value);
                if (_plcMode != normalized)
                {
                    _plcMode = normalized;
                    OnPropertyChanged();
                }
            }
        }

        public string PlcIpAddress
        {
            get => _plcIpAddress;
            set
            {
                if (_plcIpAddress != value)
                {
                    _plcIpAddress = value;
                    OnPropertyChanged();
                }
            }
        }

        public short Rack
        {
            get => _rack;
            set
            {
                if (_rack != value)
                {
                    _rack = value;
                    OnPropertyChanged();
                }
            }
        }

        public short Slot
        {
            get => _slot;
            set
            {
                if (_slot != value)
                {
                    _slot = value;
                    OnPropertyChanged();
                }
            }
        }

        public int DbNumber
        {
            get => _dbNumber;
            set
            {
                var normalized = Math.Max(1, value);
                if (_dbNumber != normalized)
                {
                    _dbNumber = normalized;
                    OnPropertyChanged();
                }
            }
        }

        public int PlcToPcDbNumber
        {
            get => _plcToPcDbNumber;
            set
            {
                var normalized = Math.Max(1, value);
                if (_plcToPcDbNumber != normalized)
                {
                    _plcToPcDbNumber = normalized;
                    OnPropertyChanged();
                }
            }
        }

        public int DiagnosticDbNumber
        {
            get => _diagnosticDbNumber;
            set
            {
                var normalized = Math.Max(1, value);
                if (_diagnosticDbNumber != normalized)
                {
                    _diagnosticDbNumber = normalized;
                    OnPropertyChanged();
                }
            }
        }

        public int PollIntervalMs
        {
            get => _pollIntervalMs;
            set
            {
                var normalized = Math.Max(50, value);
                if (_pollIntervalMs != normalized)
                {
                    _pollIntervalMs = normalized;
                    OnPropertyChanged();
                }
            }
        }

        public bool AutoConnectOnStartup
        {
            get => _autoConnectOnStartup;
            set
            {
                if (_autoConnectOnStartup != value)
                {
                    _autoConnectOnStartup = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool RequirePartPresent
        {
            get => _requirePartPresent;
            set
            {
                if (_requirePartPresent != value)
                {
                    _requirePartPresent = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ConnectionStatus
        {
            get => _connectionStatus;
            private set
            {
                if (_connectionStatus != value)
                {
                    _connectionStatus = value;
                    OnPropertyChanged();
                }
            }
        }

        public string CameraProvider
        {
            get => _cameraProvider;
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value) ? BrakeDiscInspector_GUI_ROI.Comms.CameraProviders.Disabled : value.Trim();
                if (_cameraProvider != normalized)
                {
                    _cameraProvider = normalized;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsFolderCameraProvider));
                    OnPropertyChanged(nameof(CameraSourceDisplay));
                    BrowseCameraSourceCommand?.RaiseCanExecuteChanged();
                }
            }
        }

        public bool IsFolderCameraProvider
            => string.Equals(CameraProvider, BrakeDiscInspector_GUI_ROI.Comms.CameraProviders.Folder, StringComparison.OrdinalIgnoreCase);

        public string CameraSource
        {
            get => _cameraSource;
            set
            {
                if (_cameraSource != value)
                {
                    _cameraSource = value ?? string.Empty;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CameraSourceDisplay));
                }
            }
        }

        public string CameraSourceDisplay
            => IsFolderCameraProvider
                ? (string.IsNullOrWhiteSpace(CameraSource) ? "No source folder selected" : CameraSource)
                : "Source is only used by the Folder provider";

        public string CameraOutputDirectory
        {
            get => _cameraOutputDirectory;
            set
            {
                if (_cameraOutputDirectory != value)
                {
                    _cameraOutputDirectory = value ?? string.Empty;
                    OnPropertyChanged();
                }
            }
        }

        public bool AutoRunInspection
        {
            get => _autoRunInspection;
            set
            {
                if (_autoRunInspection != value)
                {
                    _autoRunInspection = value;
                    OnPropertyChanged();
                }
            }
        }

        public string CameraStatus
        {
            get => _cameraStatus;
            private set
            {
                if (_cameraStatus != value)
                {
                    _cameraStatus = value;
                    OnPropertyChanged();
                }
            }
        }

        public string LastAcquiredImagePath
        {
            get => _lastAcquiredImagePath;
            private set
            {
                if (_lastAcquiredImagePath != value)
                {
                    _lastAcquiredImagePath = value;
                    OnPropertyChanged();
                }
            }
        }

        public string CycleStatus
        {
            get => _cycleStatus;
            private set
            {
                if (_cycleStatus != value)
                {
                    _cycleStatus = value;
                    OnPropertyChanged();
                }
            }
        }

        public string SettingsStatus
        {
            get => _settingsStatus;
            private set
            {
                if (_settingsStatus != value)
                {
                    _settingsStatus = value;
                    OnPropertyChanged();
                }
            }
        }

        public AsyncCommand ConnectCommand { get; }

        public AsyncCommand DisconnectCommand { get; }

        public AsyncCommand SaveSettingsCommand { get; }

        public AsyncCommand ToggleOutputCommand { get; }

        public AsyncCommand ToggleInputCommand { get; }

        public AsyncCommand StartAutoCam1Command { get; }

        public AsyncCommand ConnectCameraCommand { get; }

        public AsyncCommand DisconnectCameraCommand { get; }

        public AsyncCommand AcquireImageCommand { get; }

        public AsyncCommand BrowseCameraSourceCommand { get; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public async Task ConnectAsync()
        {
            EnsureClientMatchesConfig();

            if (_client.IsConnected)
            {
                ConnectionStatus = "Connected";
                return;
            }

            ConnectionStatus = "Connecting...";

            try
            {
                await _client.ConnectAsync().ConfigureAwait(false);
                ConnectionStatus = "Connected";
                await WriteOutputsSafeAsync(new Dictionary<PlcSignalId, bool>
                {
                    [PlcSignalId.SystemReady] = true,
                    [PlcSignalId.Busy] = false,
                    [PlcSignalId.Error] = false
                }).ConfigureAwait(false);
                StartPolling();
            }
            catch (Exception ex)
            {
                ConnectionStatus = "Disconnected";
                GuiLog.Error("[plc] Connection failed", ex);
            }
        }

        public async Task DisconnectAsync()
        {
            StopPolling();

            try
            {
                CancelCycle("disconnect");
                await _client.DisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GuiLog.Error("[plc] Disconnect failed", ex);
            }

            ConnectionStatus = "Disconnected";
        }

        private async Task SaveSettingsCommandAsync()
        {
            if (_saveSettingsAsync == null)
            {
                SettingsStatus = "Save unavailable";
                return;
            }

            try
            {
                var snapshot = CreateSettingsSnapshot();
                await _saveSettingsAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
                SettingsStatus = $"Saved to {AppConfigLoader.UserConfigPath}";
                GuiLog.Info($"[comms] Settings saved to '{AppConfigLoader.UserConfigPath}'");
            }
            catch (Exception ex)
            {
                SettingsStatus = $"Save failed: {ex.Message}";
                GuiLog.Error("[comms] Save settings failed", ex);
            }
        }

        private Task ToggleOutputAsync(object? parameter)
        {
            if (parameter is not PlcSignalId signalId)
            {
                return Task.CompletedTask;
            }

            var vm = Outputs.FirstOrDefault(o => o.Id == signalId);
            if (vm == null)
            {
                return Task.CompletedTask;
            }

            var newValue = !vm.IsOn;
            vm.IsOn = newValue;

            var payload = new Dictionary<PlcSignalId, bool>
            {
                [signalId] = newValue
            };

            return WriteOutputsSafeAsync(payload);
        }

        private Task ToggleInputAsync(object? parameter)
        {
            // Input toggling is currently a no-op; kept for potential simulation hooks.
            if (parameter is not PlcSignalId signalId)
            {
                return Task.CompletedTask;
            }

            var vm = Inputs.FirstOrDefault(i => i.Id == signalId);
            if (vm != null)
            {
                var newValue = !vm.IsOn;
                vm.IsOn = newValue;
                if (_client is ISimulatedPlcClient sim)
                {
                    return sim.SetInputAsync(signalId, newValue);
                }
            }

            return Task.CompletedTask;
        }

        private async Task PulseAutoCam1StartAsync()
        {
            if (!_client.IsConnected)
            {
                CycleStatus = "AutoCam1 start ignored: PLC disconnected";
                return;
            }

            CycleStatus = "AutoCam1 360 start pulse";
            await WriteOutputsSafeAsync(new Dictionary<PlcSignalId, bool>
            {
                [PlcSignalId.AutoCam1Start] = true
            }).ConfigureAwait(false);

            await Task.Delay(150).ConfigureAwait(false);

            await WriteOutputsSafeAsync(new Dictionary<PlcSignalId, bool>
            {
                [PlcSignalId.AutoCam1Start] = false
            }).ConfigureAwait(false);
        }

        public async Task ConnectCameraAsync()
        {
            EnsureCameraMatchesConfig();

            if (_camera.IsConnected)
            {
                CameraStatus = "Camera connected";
                return;
            }

            CameraStatus = "Camera connecting...";
            try
            {
                await _camera.ConnectAsync().ConfigureAwait(false);
                CameraStatus = "Camera connected";
            }
            catch (Exception ex)
            {
                CameraStatus = $"Camera disconnected: {ex.Message}";
                GuiLog.Error("[camera] Connection failed", ex);
            }
        }

        public async Task DisconnectCameraAsync()
        {
            try
            {
                await _camera.DisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GuiLog.Error("[camera] Disconnect failed", ex);
            }

            CameraStatus = "Camera disconnected";
        }

        public async Task AcquireImageCycleAsync(string reason = "manual")
        {
            await RunInspectionCycleAsync(reason, CancellationToken.None).ConfigureAwait(false);
        }

        private Task BrowseCameraSourceAsync()
        {
            if (!IsFolderCameraProvider)
            {
                return Task.CompletedTask;
            }

            using var dialog = new Forms.FolderBrowserDialog
            {
                Description = "Select the folder used by the Folder camera provider.",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false
            };

            if (!string.IsNullOrWhiteSpace(CameraSource) && System.IO.Directory.Exists(CameraSource))
            {
                dialog.SelectedPath = CameraSource;
            }

            if (dialog.ShowDialog() == Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
            {
                CameraSource = dialog.SelectedPath;
            }

            return Task.CompletedTask;
        }

        public async Task NotifyInspectionStarted()
        {
            var payload = new Dictionary<PlcSignalId, bool>
            {
                [PlcSignalId.Busy] = true,
                [PlcSignalId.Error] = false,
                [PlcSignalId.InspectionComplete] = false,
                [PlcSignalId.ResultOk] = false,
                [PlcSignalId.ResultNg] = false
            };

            await WriteOutputsSafeAsync(payload).ConfigureAwait(false);
        }

        public async Task NotifyInspectionFinished(bool ok)
        {
            var payload = new Dictionary<PlcSignalId, bool>
            {
                [PlcSignalId.Busy] = false,
                [PlcSignalId.InspectionComplete] = true,
                [PlcSignalId.ResultOk] = ok,
                [PlcSignalId.ResultNg] = !ok
            };

            await WriteOutputsSafeAsync(payload).ConfigureAwait(false);
        }

        public async Task NotifyError(bool active)
        {
            var payload = new Dictionary<PlcSignalId, bool>
            {
                [PlcSignalId.Error] = active
            };

            await WriteOutputsSafeAsync(payload).ConfigureAwait(false);
        }

        private async Task WriteOutputsSafeAsync(IDictionary<PlcSignalId, bool> outputs)
        {
            try
            {
                await _client.WriteOutputsAsync(outputs).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GuiLog.Error("[plc] WriteOutputsAsync failed", ex);
            }
        }

        private void StartPolling()
        {
            StopPolling();

            _pollingCts = new CancellationTokenSource();
            var token = _pollingCts.Token;

            _pollingTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var values = await _client.ReadAsync(token).ConfigureAwait(false);
                        UpdateSignals(values);
                        await HandleInputTransitionsAsync(values, token).ConfigureAwait(false);
                        await Task.Delay(PollIntervalMs, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        GuiLog.Error("[plc] Polling failed", ex);
                        await Task.Delay(500, token).ConfigureAwait(false);
                    }
                }
            }, token);
        }

        private void StopPolling()
        {
            try
            {
                _pollingCts?.Cancel();
            }
            catch
            {
                // Ignore cancellation errors
            }

            _pollingTask = null;
        }

        private void UpdateSignals(IDictionary<PlcSignalId, bool> values)
        {
            void Apply()
            {
                foreach (var input in Inputs)
                {
                    if (values.TryGetValue(input.Id, out var on))
                    {
                        input.IsOn = on;
                    }
                }

                foreach (var output in Outputs)
                {
                    if (values.TryGetValue(output.Id, out var on))
                    {
                        output.IsOn = on;
                    }
                }
            }

            if (_uiContext != null)
            {
                _uiContext.Post(_ => Apply(), null);
            }
            else
            {
                Apply();
            }
        }

        private async Task HandleInputTransitionsAsync(IDictionary<PlcSignalId, bool> values, CancellationToken ct)
        {
            var startNow = values.TryGetValue(PlcSignalId.StartInspection, out var start) && start;
            var startWas = _lastSignals.TryGetValue(PlcSignalId.StartInspection, out var previousStart) && previousStart;
            var legacyStartNow = values.TryGetValue(PlcSignalId.LegacyCapture1, out var legacyStart) && legacyStart;
            var legacyStartWas = _lastSignals.TryGetValue(PlcSignalId.LegacyCapture1, out var previousLegacyStart) && previousLegacyStart;
            var stopNow = values.TryGetValue(PlcSignalId.StopInspection, out var stop) && stop;
            var stopWas = _lastSignals.TryGetValue(PlcSignalId.StopInspection, out var previousStop) && previousStop;
            var partPresent = values.TryGetValue(PlcSignalId.PartPresent, out var present) && present;
            var resetError = values.TryGetValue(PlcSignalId.ResetError, out var reset) && reset;

            foreach (var kvp in values)
            {
                _lastSignals[kvp.Key] = kvp.Value;
            }

            if (resetError)
            {
                await NotifyError(false).ConfigureAwait(false);
            }

            if (stopNow && !stopWas)
            {
                CancelCycle("plc-stop");
            }

            if ((!startNow || startWas) && (!legacyStartNow || legacyStartWas))
            {
                return;
            }

            if (RequirePartPresent && !partPresent)
            {
                CycleStatus = "Start ignored: part not present";
                GuiLog.Warn("[plc] StartInspection ignored because PartPresent is false");
                return;
            }

            StartCycleFromPolling("plc-start", ct);
            await Task.CompletedTask.ConfigureAwait(false);
        }

        private void StartCycleFromPolling(string reason, CancellationToken pollingToken)
        {
            lock (_cycleSync)
            {
                if (_cycleInProgress)
                {
                    CycleStatus = "Cycle already running";
                    GuiLog.Warn($"[comms] cycle ignored reason={reason}: already running");
                    return;
                }

                _cycleCts?.Dispose();
                _cycleCts = CancellationTokenSource.CreateLinkedTokenSource(pollingToken);
                var cycleToken = _cycleCts.Token;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await RunInspectionCycleAsync(reason, cycleToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        GuiLog.Error("[comms] background cycle failed", ex);
                    }
                }, CancellationToken.None);
            }
        }

        private void CancelCycle(string reason)
        {
            try
            {
                _cycleCts?.Cancel();
                CycleStatus = $"Cycle cancel requested ({reason})";
            }
            catch
            {
                // Ignore cancellation errors.
            }
        }

        private async Task RunInspectionCycleAsync(string reason, CancellationToken ct)
        {
            lock (_cycleSync)
            {
                if (_cycleInProgress)
                {
                    CycleStatus = "Cycle already running";
                    GuiLog.Warn($"[comms] cycle ignored reason={reason}: already running");
                    return;
                }

                _cycleInProgress = true;
            }

            try
            {
                CycleStatus = $"Cycle starting ({reason})";
                await NotifyInspectionStarted().ConfigureAwait(false);

                if (!_camera.IsConnected)
                {
                    await ConnectCameraAsync().ConfigureAwait(false);
                }

                var frame = await _camera.AcquireAsync(ct).ConfigureAwait(false);
                LastAcquiredImagePath = frame.FilePath;
                CycleStatus = $"Image acquired: {System.IO.Path.GetFileName(frame.FilePath)}";

                bool? ok = null;
                if (AutoRunInspection && _inspectImageAsync != null)
                {
                    CycleStatus = "Inspection running";
                    ok = await _inspectImageAsync(frame.FilePath, ct).ConfigureAwait(false);
                }

                if (ok.HasValue)
                {
                    await NotifyInspectionFinished(ok.Value).ConfigureAwait(false);
                    CycleStatus = ok.Value ? "Cycle finished: OK" : "Cycle finished: NG";
                }
                else
                {
                    await WriteOutputsSafeAsync(new Dictionary<PlcSignalId, bool>
                    {
                        [PlcSignalId.Busy] = false
                    }).ConfigureAwait(false);
                    CycleStatus = AutoRunInspection ? "Cycle finished: result unknown" : "Image acquired";
                }
            }
            catch (OperationCanceledException)
            {
                CycleStatus = "Cycle cancelled";
                await WriteOutputsSafeAsync(new Dictionary<PlcSignalId, bool>
                {
                    [PlcSignalId.Busy] = false
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                CycleStatus = $"Cycle error: {ex.Message}";
                GuiLog.Error("[comms] Inspection cycle failed", ex);
                await WriteOutputsSafeAsync(new Dictionary<PlcSignalId, bool>
                {
                    [PlcSignalId.Busy] = false,
                    [PlcSignalId.Error] = true
                }).ConfigureAwait(false);
            }
            finally
            {
                lock (_cycleSync)
                {
                    _cycleInProgress = false;
                }
            }
        }

        private void EnsureClientMatchesConfig()
        {
            var desiredMode = NormalizePlcMode(PlcMode);
            var desired = new PlcConfig(PlcIpAddress, Rack, Slot, DbNumber, PlcToPcDbNumber, DiagnosticDbNumber);

            if (_client.Config.IpAddress == desired.IpAddress &&
                _client.Config.Rack == desired.Rack &&
                _client.Config.Slot == desired.Slot &&
                _client.Config.DbNumber == desired.DbNumber &&
                _client.Config.PlcToPcDbNumber == desired.PlcToPcDbNumber &&
                _client.Config.DiagnosticDbNumber == desired.DiagnosticDbNumber &&
                string.Equals(_clientMode, desiredMode, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            StopPolling();
            _client.Dispose();
            _clientMode = desiredMode;
            _client = _clientFactory(desired, desiredMode);
            _lastSignals.Clear();
        }

        private CommsSettingsSnapshot CreateSettingsSnapshot()
        {
            return new CommsSettingsSnapshot
            {
                PlcMode = PlcMode,
                PlcIpAddress = PlcIpAddress,
                Rack = Rack,
                Slot = Slot,
                PcToPlcDbNumber = DbNumber,
                PlcToPcDbNumber = PlcToPcDbNumber,
                DiagnosticDbNumber = DiagnosticDbNumber,
                PollIntervalMs = PollIntervalMs,
                AutoConnectOnStartup = AutoConnectOnStartup,
                AutoRunInspection = AutoRunInspection,
                RequirePartPresent = RequirePartPresent,
                CameraProvider = CameraProvider,
                CameraSource = CameraSource,
                CameraOutputDirectory = CameraOutputDirectory
            };
        }

        private void EnsureCameraMatchesConfig()
        {
            var desired = new CameraConfig(CameraProvider, CameraSource, CameraOutputDirectory);
            if (string.Equals(_camera.Config.Provider, desired.Provider, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_camera.Config.Source, desired.Source, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_camera.Config.OutputDirectory, desired.OutputDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _camera.Dispose();
            _camera = _cameraFactory(desired);
        }

        private static string NormalizePlcMode(string? mode)
            => string.Equals(mode, "S7", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "SiemensS7", StringComparison.OrdinalIgnoreCase)
                    ? "S7"
                    : "Simulation";

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void Dispose()
        {
            StopPolling();
            CancelCycle("dispose");
            _pollingCts?.Dispose();
            _cycleCts?.Dispose();
            _client.Dispose();
            _camera.Dispose();
        }
    }
}
