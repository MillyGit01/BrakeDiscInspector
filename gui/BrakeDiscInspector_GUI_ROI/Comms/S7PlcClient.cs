using System;
using System.Collections.Generic;
using System.Linq;
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
                        GuiLog.Info($"[plc] Connected to {Config.IpAddress} rack={Config.Rack} slot={Config.Slot}");
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

                byte[] buffer;
                lock (_sync)
                {
                    buffer = _plc.ReadBytes(DataType.DataBlock, PlcSignals.DbNumber, 0, 4) ?? Array.Empty<byte>();
                }

                var data = buffer.Length >= 4 ? buffer : buffer.Concat(new byte[4 - buffer.Length]).ToArray();
                var result = new Dictionary<PlcSignalId, bool>();

                foreach (var signal in PlcSignals.Definitions)
                {
                    var bit = GetBit(data, signal.ByteOffset, signal.BitOffset);
                    result[signal.Id] = bit;
                }

                return (IDictionary<PlcSignalId, bool>)result;
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
                    buffer = _plc.ReadBytes(DataType.DataBlock, PlcSignals.DbNumber, 0, 4) ?? new byte[4];
                }

                if (buffer.Length < 4)
                {
                    Array.Resize(ref buffer, 4);
                }

                foreach (var kvp in outputs)
                {
                    var definition = PlcSignals.Definitions.FirstOrDefault(d => d.Id == kvp.Key && d.Direction == PlcSignalDirection.Output);
                    if (definition == null)
                    {
                        continue;
                    }

                    SetBit(buffer, definition.ByteOffset, definition.BitOffset, kvp.Value);
                }

                lock (_sync)
                {
                    _plc.WriteBytes(DataType.DataBlock, PlcSignals.DbNumber, 0, buffer);
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

        private static bool GetBit(IReadOnlyList<byte> buffer, int byteIndex, int bitIndex)
        {
            if (byteIndex < 0 || bitIndex < 0 || bitIndex > 7 || byteIndex >= buffer.Count)
            {
                return false;
            }

            return (buffer[byteIndex] & (1 << bitIndex)) != 0;
        }

        private static void SetBit(IList<byte> buffer, int byteIndex, int bitIndex, bool value)
        {
            if (byteIndex < 0 || bitIndex < 0 || bitIndex > 7)
            {
                return;
            }

            if (byteIndex >= buffer.Count)
            {
                return;
            }

            if (value)
            {
                buffer[byteIndex] = (byte)(buffer[byteIndex] | (1 << bitIndex));
            }
            else
            {
                buffer[byteIndex] = (byte)(buffer[byteIndex] & ~(1 << bitIndex));
            }
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
