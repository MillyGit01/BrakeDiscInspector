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

        public sealed class BackendConfig
        {
            public string BaseUrl { get; set; } = "http://127.0.0.1:8000";
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
    }

    public static class AppConfigLoader
    {
        private static readonly string[] ConfigPaths =
        {
            Path.Combine(AppContext.BaseDirectory, "config", "appsettings.json"),
            Path.Combine(AppContext.BaseDirectory, "appsettings.json")
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

        private static void Merge(AppConfig target, AppConfig source)
        {
            if (!string.IsNullOrWhiteSpace(source.Backend?.BaseUrl))
            {
                target.Backend.BaseUrl = source.Backend.BaseUrl;
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
        }

        private static void ApplyEnvironmentOverrides(AppConfig config)
        {
            OverrideString("BDI_BACKEND_BASEURL", value => config.Backend.BaseUrl = value);
            OverrideString("BDI_DATASET_ROOT", value => config.Dataset.Root = value);
            OverrideDouble("BDI_ANALYZE_POS_TOL_PX", value => config.Analyze.PosTolPx = value);
            OverrideDouble("BDI_ANALYZE_ANG_TOL_DEG", value => config.Analyze.AngTolDeg = value);
            OverrideBool("BDI_SCALELOCK_DEFAULT", value => config.Analyze.ScaleLockDefault = value);
            OverrideInt("BDI_ANCHOR_SCORE_MIN", value => config.Analyze.AnchorScoreMin = value);
            OverrideDouble("BDI_HEATMAP_OPACITY", value => config.UI.HeatmapOverlayOpacity = Clamp01(value));
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
