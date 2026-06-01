using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BrakeDiscInspector_GUI_ROI.Comms
{
    public sealed class PlcConfig
    {
        public PlcConfig(string ipAddress, short rack, short slot, int dbNumber = 1)
        {
            IpAddress = ipAddress;
            Rack = rack;
            Slot = slot;
            DbNumber = dbNumber > 0 ? dbNumber : 1;
        }

        public string IpAddress { get; }

        public short Rack { get; }

        public short Slot { get; }

        public int DbNumber { get; }
    }

    public interface IPlcClient : IDisposable
    {
        PlcConfig Config { get; }

        bool IsConnected { get; }

        IReadOnlyList<PlcSignalDefinition> SignalDefinitions { get; }

        Task ConnectAsync(CancellationToken ct = default);

        Task DisconnectAsync();

        Task<IDictionary<PlcSignalId, bool>> ReadAsync(CancellationToken ct = default);

        Task WriteOutputsAsync(IDictionary<PlcSignalId, bool> outputs, CancellationToken ct = default);
    }
}
