using System;
using System.Threading;
using System.Threading.Tasks;

namespace BrakeDiscInspector_GUI_ROI.Comms
{
    public sealed class UnavailableCameraClient : ICameraClient
    {
        private readonly string _reason;

        public UnavailableCameraClient(CameraConfig config, string reason)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            _reason = string.IsNullOrWhiteSpace(reason) ? "Camera provider is not configured." : reason;
        }

        public CameraConfig Config { get; }

        public bool IsConnected => false;

        public Task ConnectAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            throw new NotSupportedException(_reason);
        }

        public Task DisconnectAsync()
            => Task.CompletedTask;

        public Task<CameraFrame> AcquireAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            throw new NotSupportedException(_reason);
        }

        public void Dispose()
        {
        }
    }
}
