using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BrakeDiscInspector_GUI_ROI.Util;

namespace BrakeDiscInspector_GUI_ROI.Comms
{
    public sealed class SimulatedPlcClient : IPlcClient, ISimulatedPlcClient
    {
        private readonly object _sync = new();
        private readonly Dictionary<PlcSignalId, bool> _signals = new();
        private bool _disposed;
        private bool _isConnected;

        public SimulatedPlcClient(PlcConfig config)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            foreach (var definition in PlcSignals.Definitions)
            {
                _signals[definition.Id] = false;
            }
        }

        public PlcConfig Config { get; }

        public bool IsConnected
        {
            get
            {
                lock (_sync)
                {
                    return !_disposed && _isConnected;
                }
            }
        }

        public IReadOnlyList<PlcSignalDefinition> SignalDefinitions => PlcSignals.Definitions;

        public Task ConnectAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_sync)
            {
                ThrowIfDisposed();
                _isConnected = true;
                _signals[PlcSignalId.SystemReady] = true;
            }

            GuiLog.Info($"[plc-sim] Connected pc_to_plc_db={Config.PcToPlcDbNumber} plc_to_pc_db={Config.PlcToPcDbNumber} diag_db={Config.DiagnosticDbNumber}");
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return Task.CompletedTask;
                }

                _isConnected = false;
            }

            GuiLog.Info("[plc-sim] Disconnected");
            return Task.CompletedTask;
        }

        public Task<IDictionary<PlcSignalId, bool>> ReadAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_sync)
            {
                EnsureConnected();
                return Task.FromResult((IDictionary<PlcSignalId, bool>)_signals.ToDictionary(kvp => kvp.Key, kvp => kvp.Value));
            }
        }

        public Task WriteOutputsAsync(IDictionary<PlcSignalId, bool> outputs, CancellationToken ct = default)
        {
            if (outputs == null || outputs.Count == 0)
            {
                return Task.CompletedTask;
            }

            ct.ThrowIfCancellationRequested();
            lock (_sync)
            {
                EnsureConnected();
                foreach (var kvp in outputs)
                {
                    var definition = PlcSignals.Definitions.FirstOrDefault(d => d.Id == kvp.Key);
                    if (definition?.Direction == PlcSignalDirection.Output)
                    {
                        _signals[kvp.Key] = kvp.Value;
                    }
                }
            }

            return Task.CompletedTask;
        }

        public Task SetInputAsync(PlcSignalId signalId, bool value, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_sync)
            {
                EnsureConnected();
                var definition = PlcSignals.Definitions.FirstOrDefault(d => d.Id == signalId);
                if (definition?.Direction == PlcSignalDirection.Input)
                {
                    _signals[signalId] = value;
                }
            }

            return Task.CompletedTask;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _disposed = true;
                _isConnected = false;
            }
        }

        private void EnsureConnected()
        {
            ThrowIfDisposed();
            if (!_isConnected)
            {
                throw new InvalidOperationException("PLC simulator is not connected");
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SimulatedPlcClient));
            }
        }
    }
}
