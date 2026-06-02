using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace BrakeDiscInspector_GUI_ROI
{
    public sealed class AppConfig
    {
        public BackendConfig Backend { get; set; } = new();
        public DatasetConfig Dataset { get; set; } = new();
        public AnalyzeConfig Analyze { get; set; } = new();
        public UiConfig UI { get; set; } = new();
        public CommsConfig Comms { get; set; } = new();

        public sealed class BackendConfig
        {
            public string BaseUrl { get; set; } = "http://127.0.0.1:8000";
            public bool AutoStart { get; set; }
            public string AutoStartMode { get; set; } = "WslVenv";
            public string WslDistro { get; set; } = "Ubuntu";
            public string WslCondaEnvironment { get; set; } = "BrakeDisc";
            public string WslVenvPath { get; set; } = "/home/millylinux/venv";
            public string AutoStartWorkingDirectory { get; set; } = string.Empty;
            public string AutoStartModelsDirectory { get; set; } = string.Empty;
            public string AutoStartHost { get; set; } = "0.0.0.0";
            public int AutoStartPort { get; set; } = 8000;
            public int StartupTimeoutSeconds { get; set; } = 45;
        }

        public sealed class DatasetConfig
        {
            public string Root { get; set; } = string.Empty;
        }

        public sealed class AnalyzeConfig
        {
            public double PosTolPx { get; set; } = 1.0;
            public double AngTolDeg { get; set; } = 0.5;
            public bool ScaleLockDefault { get; set; } = true;
            public int AnchorScoreMin { get; set; } = 85;
        }

        public sealed class UiConfig
        {
            public double HeatmapOverlayOpacity { get; set; } = 0.6;
        }

        public sealed class CommsConfig
        {
            public bool Enabled { get; set; } = true;
            public bool AutoConnectOnStartup { get; set; }
            public bool AutoRunInspectionOnTrigger { get; set; }
            public bool RequirePartPresent { get; set; } = true;
            public PlcSettings Plc { get; set; } = new();
            public CameraSettings Camera { get; set; } = new();
        }

        public sealed class PlcSettings
        {
            public string Mode { get; set; } = "Simulation";
            public string IpAddress { get; set; } = "192.168.0.1";
            public short Rack { get; set; } = 0;
            public short Slot { get; set; } = 1;
            public int DbNumber { get; set; } = 150;
            public int PlcToPcDbNumber { get; set; } = 151;
            public int DiagnosticDbNumber { get; set; } = 8;
            public int PollIntervalMs { get; set; } = 100;
        }

        public sealed class CameraSettings
        {
            public string Provider { get; set; } = "Disabled";
            public string Source { get; set; } = string.Empty;
            public string OutputDirectory { get; set; } = string.Empty;
            public int TimeoutMs { get; set; } = 5000;
        }
    }

    public static class AppConfigLoader
    {
        public static string UserConfigPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BrakeDiscInspector",
            "appsettings.user.json");

        private static readonly string[] ConfigPaths =
        {
            Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json"),
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            UserConfigPath
        };

        public static AppConfig Load()
        {
            var config = new AppConfig();
            foreach (var path in ConfigPaths)
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    using var stream = File.OpenRead(path);
                    var fileConfig = JsonSerializer.Deserialize<AppConfig>(stream, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (fileConfig != null)
                    {
                        Merge(config, fileConfig);
                    }
                }
                catch
                {
                    // Ignore malformed config files and continue with defaults/environment overrides.
                }
            }

            ApplyEnvironmentOverrides(config);
            return config;
        }

        public static void SaveUserConfig(AppConfig config)
        {
            Save(config, UserConfigPath);
        }

        public static void Save(AppConfig config, string path)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Config path is required.", nameof(path));
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(path, json);
        }

        private static void Merge(AppConfig target, AppConfig source)
        {
            if (!string.IsNullOrWhiteSpace(source.Backend?.BaseUrl))
            {
                target.Backend.BaseUrl = source.Backend.BaseUrl;
            }

            if (source.Backend != null)
            {
                target.Backend.AutoStart = source.Backend.AutoStart;

                if (!string.IsNullOrWhiteSpace(source.Backend.AutoStartMode))
                {
                    target.Backend.AutoStartMode = source.Backend.AutoStartMode;
                }

                if (!string.IsNullOrWhiteSpace(source.Backend.WslDistro))
                {
                    target.Backend.WslDistro = source.Backend.WslDistro;
                }

                if (!string.IsNullOrWhiteSpace(source.Backend.WslCondaEnvironment))
                {
                    target.Backend.WslCondaEnvironment = source.Backend.WslCondaEnvironment;
                }

                if (!string.IsNullOrWhiteSpace(source.Backend.WslVenvPath))
                {
                    target.Backend.WslVenvPath = source.Backend.WslVenvPath;
                }

                if (!string.IsNullOrWhiteSpace(source.Backend.AutoStartWorkingDirectory))
                {
                    target.Backend.AutoStartWorkingDirectory = source.Backend.AutoStartWorkingDirectory;
                }

                if (!string.IsNullOrWhiteSpace(source.Backend.AutoStartModelsDirectory))
                {
                    target.Backend.AutoStartModelsDirectory = source.Backend.AutoStartModelsDirectory;
                }

                if (!string.IsNullOrWhiteSpace(source.Backend.AutoStartHost))
                {
                    target.Backend.AutoStartHost = source.Backend.AutoStartHost;
                }

                if (source.Backend.AutoStartPort > 0)
                {
                    target.Backend.AutoStartPort = source.Backend.AutoStartPort;
                }

                if (source.Backend.StartupTimeoutSeconds > 0)
                {
                    target.Backend.StartupTimeoutSeconds = source.Backend.StartupTimeoutSeconds;
                }
            }

            if (source.Dataset != null && !string.IsNullOrWhiteSpace(source.Dataset.Root))
            {
                target.Dataset.Root = source.Dataset.Root;
            }

                if (source.Analyze != null)
                {
                    if (source.Analyze.PosTolPx > 0)
                    {
                        target.Analyze.PosTolPx = source.Analyze.PosTolPx;
                    }

                    if (source.Analyze.AngTolDeg > 0)
                    {
                        target.Analyze.AngTolDeg = source.Analyze.AngTolDeg;
                    }

                    target.Analyze.ScaleLockDefault = source.Analyze.ScaleLockDefault;

                    if (source.Analyze.AnchorScoreMin > 0)
                    {
                        target.Analyze.AnchorScoreMin = source.Analyze.AnchorScoreMin;
                    }
                }

            if (source.UI != null && source.UI.HeatmapOverlayOpacity >= 0)
            {
                target.UI.HeatmapOverlayOpacity = Clamp01(source.UI.HeatmapOverlayOpacity);
            }

            if (source.Comms != null)
            {
                target.Comms.Enabled = source.Comms.Enabled;
                target.Comms.AutoConnectOnStartup = source.Comms.AutoConnectOnStartup;
                target.Comms.AutoRunInspectionOnTrigger = source.Comms.AutoRunInspectionOnTrigger;
                target.Comms.RequirePartPresent = source.Comms.RequirePartPresent;

                if (source.Comms.Plc != null)
                {
                    if (!string.IsNullOrWhiteSpace(source.Comms.Plc.Mode))
                    {
                        target.Comms.Plc.Mode = source.Comms.Plc.Mode;
                    }

                    if (!string.IsNullOrWhiteSpace(source.Comms.Plc.IpAddress))
                    {
                        target.Comms.Plc.IpAddress = source.Comms.Plc.IpAddress;
                    }

                    target.Comms.Plc.Rack = source.Comms.Plc.Rack;
                    target.Comms.Plc.Slot = source.Comms.Plc.Slot;

                    if (source.Comms.Plc.DbNumber > 0)
                    {
                        target.Comms.Plc.DbNumber = source.Comms.Plc.DbNumber;
                    }

                    if (source.Comms.Plc.PlcToPcDbNumber > 0)
                    {
                        target.Comms.Plc.PlcToPcDbNumber = source.Comms.Plc.PlcToPcDbNumber;
                    }

                    if (source.Comms.Plc.DiagnosticDbNumber > 0)
                    {
                        target.Comms.Plc.DiagnosticDbNumber = source.Comms.Plc.DiagnosticDbNumber;
                    }

                    if (source.Comms.Plc.PollIntervalMs > 0)
                    {
                        target.Comms.Plc.PollIntervalMs = source.Comms.Plc.PollIntervalMs;
                    }
                }

                if (source.Comms.Camera != null)
                {
                    if (!string.IsNullOrWhiteSpace(source.Comms.Camera.Provider))
                    {
                        target.Comms.Camera.Provider = source.Comms.Camera.Provider;
                    }

                    if (!string.IsNullOrWhiteSpace(source.Comms.Camera.Source))
                    {
                        target.Comms.Camera.Source = source.Comms.Camera.Source;
                    }

                    if (!string.IsNullOrWhiteSpace(source.Comms.Camera.OutputDirectory))
                    {
                        target.Comms.Camera.OutputDirectory = source.Comms.Camera.OutputDirectory;
                    }

                    if (source.Comms.Camera.TimeoutMs > 0)
                    {
                        target.Comms.Camera.TimeoutMs = source.Comms.Camera.TimeoutMs;
                    }
                }
            }
        }

        private static void ApplyEnvironmentOverrides(AppConfig config)
        {
            OverrideString("BDI_BACKEND_BASEURL", value => config.Backend.BaseUrl = value);
            OverrideBool("BDI_BACKEND_AUTOSTART", value => config.Backend.AutoStart = value);
            OverrideString("BDI_BACKEND_AUTOSTART_MODE", value => config.Backend.AutoStartMode = value);
            OverrideString("BDI_BACKEND_WSL_DISTRO", value => config.Backend.WslDistro = value);
            OverrideString("BDI_BACKEND_WSL_ENV", value => config.Backend.WslCondaEnvironment = value);
            OverrideString("BDI_BACKEND_WSL_VENV", value => config.Backend.WslVenvPath = value);
            OverrideString("BDI_BACKEND_WORKDIR", value => config.Backend.AutoStartWorkingDirectory = value);
            OverrideString("BDI_BACKEND_MODELS_DIR", value => config.Backend.AutoStartModelsDirectory = value);
            OverrideString("BDI_BACKEND_HOST", value => config.Backend.AutoStartHost = value);
            OverrideInt("BDI_BACKEND_PORT", value => config.Backend.AutoStartPort = value);
            OverrideInt("BDI_BACKEND_STARTUP_TIMEOUT_SECONDS", value => config.Backend.StartupTimeoutSeconds = value);
            OverrideString("BDI_DATASET_ROOT", value => config.Dataset.Root = value);
            OverrideDouble("BDI_ANALYZE_POS_TOL_PX", value => config.Analyze.PosTolPx = value);
            OverrideDouble("BDI_ANALYZE_ANG_TOL_DEG", value => config.Analyze.AngTolDeg = value);
            OverrideBool("BDI_SCALELOCK_DEFAULT", value => config.Analyze.ScaleLockDefault = value);
            OverrideInt("BDI_ANCHOR_SCORE_MIN", value => config.Analyze.AnchorScoreMin = value);
            OverrideDouble("BDI_HEATMAP_OPACITY", value => config.UI.HeatmapOverlayOpacity = Clamp01(value));
            OverrideBool("BDI_COMMS_ENABLED", value => config.Comms.Enabled = value);
            OverrideBool("BDI_COMMS_AUTOCONNECT", value => config.Comms.AutoConnectOnStartup = value);
            OverrideBool("BDI_COMMS_AUTO_INSPECT", value => config.Comms.AutoRunInspectionOnTrigger = value);
            OverrideBool("BDI_COMMS_REQUIRE_PART_PRESENT", value => config.Comms.RequirePartPresent = value);
            OverrideString("BDI_PLC_MODE", value => config.Comms.Plc.Mode = value);
            OverrideString("BDI_PLC_IP", value => config.Comms.Plc.IpAddress = value);
            OverrideInt("BDI_PLC_RACK", value => config.Comms.Plc.Rack = (short)value);
            OverrideInt("BDI_PLC_SLOT", value => config.Comms.Plc.Slot = (short)value);
            OverrideInt("BDI_PLC_DB", value => config.Comms.Plc.DbNumber = Math.Max(1, value));
            OverrideInt("BDI_PLC_TO_PC_DB", value => config.Comms.Plc.PlcToPcDbNumber = Math.Max(1, value));
            OverrideInt("BDI_PLC_DIAG_DB", value => config.Comms.Plc.DiagnosticDbNumber = Math.Max(1, value));
            OverrideInt("BDI_PLC_POLL_MS", value => config.Comms.Plc.PollIntervalMs = Math.Max(50, value));
            OverrideString("BDI_CAMERA_PROVIDER", value => config.Comms.Camera.Provider = value);
            OverrideString("BDI_CAMERA_SOURCE", value => config.Comms.Camera.Source = value);
            OverrideString("BDI_CAMERA_OUTPUT_DIR", value => config.Comms.Camera.OutputDirectory = value);
            OverrideInt("BDI_CAMERA_TIMEOUT_MS", value => config.Comms.Camera.TimeoutMs = Math.Max(1, value));
        }

        private static void OverrideString(string envVar, Action<string> assign)
        {
            var value = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrWhiteSpace(value))
            {
                assign(value.Trim());
            }
        }

        private static void OverrideDouble(string envVar, Action<double> assign)
        {
            var value = Environment.GetEnvironmentVariable(envVar);
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                assign(parsed);
            }
        }

        private static void OverrideBool(string envVar, Action<bool> assign)
        {
            var value = Environment.GetEnvironmentVariable(envVar);
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            value = value.Trim();
            if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase))
            {
                assign(true);
            }
            else if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(value, "no", StringComparison.OrdinalIgnoreCase))
            {
                assign(false);
            }
        }

        private static void OverrideInt(string envVar, Action<int> assign)
        {
            var value = Environment.GetEnvironmentVariable(envVar);
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                assign(parsed);
            }
        }

        private static double Clamp01(double value)
            => Math.Max(0.0, Math.Min(1.0, value));
    }
}
