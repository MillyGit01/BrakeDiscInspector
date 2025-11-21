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

    public sealed class CommsViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly IPlcClient _client;
        private readonly SynchronizationContext? _uiContext;
        private CancellationTokenSource? _pollingCts;
        private Task? _pollingTask;
        private string _plcIpAddress;
        private string _connectionStatus = "Disconnected";

        public CommsViewModel(IPlcClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _uiContext = SynchronizationContext.Current;
            _plcIpAddress = client.Config.IpAddress;
            Rack = client.Config.Rack;
            Slot = client.Config.Slot;

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
            ToggleOutputCommand = new AsyncCommand(param => ToggleOutputAsync(param));
            ToggleInputCommand = new AsyncCommand(param => ToggleInputAsync(param));
        }

        public ObservableCollection<PlcIoPointViewModel> Inputs { get; }

        public ObservableCollection<PlcIoPointViewModel> Outputs { get; }

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

        public short Rack { get; }

        public short Slot { get; }

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

        public AsyncCommand ConnectCommand { get; }

        public AsyncCommand DisconnectCommand { get; }

        public AsyncCommand ToggleOutputCommand { get; }

        public AsyncCommand ToggleInputCommand { get; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public async Task ConnectAsync()
        {
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
                await _client.DisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GuiLog.Error("[plc] Disconnect failed", ex);
            }

            ConnectionStatus = "Disconnected";
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
                vm.IsOn = !vm.IsOn;
            }

            return Task.CompletedTask;
        }

        public async Task NotifyInspectionStarted()
        {
            var payload = new Dictionary<PlcSignalId, bool>
            {
                [PlcSignalId.Busy] = true,
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
                        await Task.Delay(100, token).ConfigureAwait(false);
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

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void Dispose()
        {
            StopPolling();
            _pollingCts?.Dispose();
            _client.Dispose();
        }
    }
}
