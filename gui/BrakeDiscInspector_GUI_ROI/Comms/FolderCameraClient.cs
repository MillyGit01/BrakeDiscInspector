using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BrakeDiscInspector_GUI_ROI.Util;

namespace BrakeDiscInspector_GUI_ROI.Comms
{
    public sealed class FolderCameraClient : ICameraClient
    {
        private static readonly string[] ImageExtensions = { ".bmp", ".png", ".jpg", ".jpeg", ".tif", ".tiff" };
        private readonly object _sync = new();
        private string[] _files = Array.Empty<string>();
        private int _nextIndex;
        private bool _disposed;
        private bool _isConnected;

        public FolderCameraClient(CameraConfig config)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public CameraConfig Config { get; }

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

        public Task ConnectAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_sync)
            {
                ThrowIfDisposed();
                if (string.IsNullOrWhiteSpace(Config.Source) || !Directory.Exists(Config.Source))
                {
                    throw new DirectoryNotFoundException($"Camera folder source not found: '{Config.Source}'");
                }

                _files = Directory.EnumerateFiles(Config.Source)
                    .Where(path => ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (_files.Length == 0)
                {
                    throw new InvalidOperationException($"Camera folder source contains no images: '{Config.Source}'");
                }

                _nextIndex = 0;
                _isConnected = true;
            }

            GuiLog.Info($"[camera-folder] Connected source='{Config.Source}' images={_files.Length}");
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

            GuiLog.Info("[camera-folder] Disconnected");
            return Task.CompletedTask;
        }

        public async Task<CameraFrame> AcquireAsync(CancellationToken ct = default)
        {
            string src;
            lock (_sync)
            {
                EnsureConnected();
                src = _files[_nextIndex];
                _nextIndex = (_nextIndex + 1) % _files.Length;
            }

            ct.ThrowIfCancellationRequested();
            var acquiredAt = DateTimeOffset.Now;
            var dst = ResolveOutputPath(src, acquiredAt);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            await Task.Run(() => File.Copy(src, dst, overwrite: true), ct).ConfigureAwait(false);
            GuiLog.Info($"[camera-folder] Acquired src='{src}' dst='{dst}'");
            return new CameraFrame(dst, acquiredAt, CameraProviders.Folder);
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _disposed = true;
                _isConnected = false;
                _files = Array.Empty<string>();
            }
        }

        private string ResolveOutputPath(string sourcePath, DateTimeOffset acquiredAt)
        {
            var outputDirectory = Config.OutputDirectory;
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                outputDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BrakeDiscInspector",
                    "captures");
            }

            var extension = Path.GetExtension(sourcePath);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".bmp";
            }

            var name = $"capture_{acquiredAt:yyyyMMdd_HHmmss_fff}{extension}";
            return Path.Combine(outputDirectory, name);
        }

        private void EnsureConnected()
        {
            ThrowIfDisposed();
            if (!_isConnected)
            {
                throw new InvalidOperationException("Camera folder provider is not connected");
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(FolderCameraClient));
            }
        }
    }
}
