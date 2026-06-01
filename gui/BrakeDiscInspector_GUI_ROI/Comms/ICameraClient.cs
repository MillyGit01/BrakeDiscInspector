using System;
using System.Threading;
using System.Threading.Tasks;

namespace BrakeDiscInspector_GUI_ROI.Comms
{
    public interface ICameraClient : IDisposable
    {
        CameraConfig Config { get; }

        bool IsConnected { get; }

        Task ConnectAsync(CancellationToken ct = default);

        Task DisconnectAsync();

        Task<CameraFrame> AcquireAsync(CancellationToken ct = default);
    }
}
