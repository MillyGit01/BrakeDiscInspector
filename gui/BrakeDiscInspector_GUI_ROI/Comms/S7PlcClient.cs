using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BrakeDiscInspector_GUI_ROI.Util;
using S7.Net;

namespace BrakeDiscInspector_GUI_ROI.Comms
{
    public sealed class S7PlcClient : IPlcClient
    {
        private readonly Plc _plc;
        private readonly object _sync = new();
        private bool _disposed;

        public S7PlcClient(PlcConfig config)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            _plc = new Plc(CpuType.S71200, config.IpAddress, config.Rack, config.Slot);
        }

        public PlcConfig Config { get; }

        public bool IsConnected => !_disposed && _plc.IsConnected;

        public IReadOnlyList<PlcSignalDefinition> SignalDefinitions => PlcSignals.Definitions;

        public Task ConnectAsync(CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();

                lock (_sync)
                {
                    if (_disposed || _plc.IsConnected)
                    {
                        return;
                    }

                    try
                    {
                        _plc.Open();
                        GuiLog.Info($"[plc] Connected to {Config.IpAddress} rack={Config.Rack} slot={Config.Slot} pc_to_plc_db={Config.PcToPlcDbNumber} plc_to_pc_db={Config.PlcToPcDbNumber} diag_db={Config.DiagnosticDbNumber}");
                    }
                    catch (Exception ex)
                    {
                        GuiLog.Error($"[plc] Connect failed to {Config.IpAddress}", ex);
                        throw;
                    }
                }
            }, ct);
        }

        public Task DisconnectAsync()
        {
            return Task.Run(() =>
            {
                lock (_sync)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    try
                    {
                        _plc.Close();
                        GuiLog.Info("[plc] Disconnected");
                    }
                    catch (Exception ex)
                    {
                        GuiLog.Error("[plc] Disconnect failed", ex);
                    }
                }
            });
        }

        public Task<IDictionary<PlcSignalId, bool>> ReadAsync(CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                EnsureConnected();

                byte[] plcToPcBytes;
                byte[] pcToPlcBytes;
                lock (_sync)
                {
                    plcToPcBytes = _plc.ReadBytes(DataType.DataBlock, Config.PlcToPcDbNumber, 0, PlcSignals.PlcToPcReadLength) ?? Array.Empty<byte>();
                    pcToPlcBytes = _plc.ReadBytes(DataType.DataBlock, Config.PcToPlcDbNumber, 0, PlcSignals.PcToPlcBoolWriteLength) ?? Array.Empty<byte>();
                }

                return PlcSignals.Decode(
                    EnsureLength(plcToPcBytes, PlcSignals.PlcToPcReadLength),
                    EnsureLength(pcToPlcBytes, PlcSignals.PcToPlcBoolWriteLength));
            }, ct);
        }

        public Task WriteOutputsAsync(IDictionary<PlcSignalId, bool> outputs, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                if (outputs == null || outputs.Count == 0)
                {
                    return;
                }

                ct.ThrowIfCancellationRequested();
                EnsureConnected();

                byte[] buffer;
                lock (_sync)
                {
                    buffer = _plc.ReadBytes(DataType.DataBlock, Config.PcToPlcDbNumber, 0, PlcSignals.PcToPlcBoolWriteLength) ?? new byte[PlcSignals.PcToPlcBoolWriteLength];
                }

                if (buffer.Length < PlcSignals.PcToPlcBoolWriteLength)
                {
                    Array.Resize(ref buffer, PlcSignals.PcToPlcBoolWriteLength);
                }

                PlcSignals.EncodeOutputs(buffer, outputs);

                lock (_sync)
                {
                    _plc.WriteBytes(DataType.DataBlock, Config.PcToPlcDbNumber, 0, buffer);
                }
            }, ct);
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                try
                {
                    _plc.Close();
                    if (_plc is IDisposable disposablePlc)
                    {
                        disposablePlc.Dispose();
                    }
                }
                catch
                {
                    // Ignore dispose errors
                }

                _disposed = true;
            }
        }

        private static byte[] EnsureLength(byte[] buffer, int length)
        {
            if (buffer.Length >= length)
            {
                return buffer;
            }

            Array.Resize(ref buffer, length);
            return buffer;
        }

        private void EnsureConnected()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(S7PlcClient));
            }

            if (!_plc.IsConnected)
            {
                throw new InvalidOperationException("PLC is not connected");
            }
        }
    }
}
