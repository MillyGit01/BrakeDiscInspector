using System.Threading;
using System.Threading.Tasks;

namespace BrakeDiscInspector_GUI_ROI.Comms
{
    public interface ISimulatedPlcClient
    {
        Task SetInputAsync(PlcSignalId signalId, bool value, CancellationToken ct = default);
    }
}
