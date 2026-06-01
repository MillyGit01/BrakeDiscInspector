using System;

namespace BrakeDiscInspector_GUI_ROI.Comms
{
    public sealed class CameraConfig
    {
        public CameraConfig(
            string provider,
            string source,
            string outputDirectory,
            int timeoutMs = 5000)
        {
            Provider = string.IsNullOrWhiteSpace(provider) ? CameraProviders.Disabled : provider.Trim();
            Source = source?.Trim() ?? string.Empty;
            OutputDirectory = outputDirectory?.Trim() ?? string.Empty;
            TimeoutMs = timeoutMs > 0 ? timeoutMs : 5000;
        }

        public string Provider { get; }

        public string Source { get; }

        public string OutputDirectory { get; }

        public int TimeoutMs { get; }
    }

    public sealed class CameraFrame
    {
        public CameraFrame(string filePath, DateTimeOffset acquiredAt, string provider)
        {
            FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
            AcquiredAt = acquiredAt;
            Provider = provider ?? string.Empty;
        }

        public string FilePath { get; }

        public DateTimeOffset AcquiredAt { get; }

        public string Provider { get; }
    }

    public static class CameraProviders
    {
        public const string Disabled = "Disabled";
        public const string Folder = "Folder";
        public const string FlirBlackfly = "FlirBlackfly";
        public const string Cognex = "Cognex";

        public static readonly string[] All =
        {
            Disabled,
            Folder,
            FlirBlackfly,
            Cognex
        };
    }
}
