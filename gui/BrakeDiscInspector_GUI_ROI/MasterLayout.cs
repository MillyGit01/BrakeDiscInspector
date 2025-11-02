using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using BrakeDiscInspector_GUI_ROI.Models;

namespace BrakeDiscInspector_GUI_ROI
{
    public class MasterLayout
    {
        public RoiModel? Master1Pattern { get; set; }
        public string? Master1PatternImagePath { get; set; }
        public RoiModel? Master1Search { get; set; }
        public RoiModel? Master2Pattern { get; set; }
        public string? Master2PatternImagePath { get; set; }
        public RoiModel? Master2Search { get; set; }
        public RoiModel? Inspection { get; set; }
        public RoiModel? InspectionBaseline { get; set; }

        public RoiModel? Inspection1 { get; set; }
        public RoiModel? Inspection2 { get; set; }
        public RoiModel? Inspection3 { get; set; }
        public RoiModel? Inspection4 { get; set; }

        public AnalyzeOptions Analyze { get; set; } = new();
        public UiOptions Ui { get; set; } = new();
        public Dictionary<string, List<RoiModel>> InspectionBaselinesByImage { get; set; }
            = new(StringComparer.OrdinalIgnoreCase);

        public ObservableCollection<InspectionRoiConfig> InspectionRois { get; }
            = new ObservableCollection<InspectionRoiConfig>
            {
                new InspectionRoiConfig(1),
                new InspectionRoiConfig(2),
                new InspectionRoiConfig(3),
                new InspectionRoiConfig(4),
            };
    }

    public sealed class AnalyzeOptions
    {
        public double PosTolPx { get; set; } = 1.0;
        public double AngTolDeg { get; set; } = 0.5;
        public bool ScaleLock { get; set; } = true;
    }

    public sealed class UiOptions
    {
        public double HeatmapOverlayOpacity { get; set; } = 0.6;
        public double HeatmapGain { get; set; } = 1.0;
        public double HeatmapGamma { get; set; } = 1.0;
    }

    public static class MasterLayoutManager
    {
        public static string GetDefaultPath(PresetFile preset)
            => Path.Combine(preset.Home, "configs", "master_layout.json");

        public static MasterLayout LoadOrNew(PresetFile preset)
        {
            var path = GetDefaultPath(preset);
            MasterLayout layout;
            bool loadedFromFile = File.Exists(path);

            try
            {
                if (!loadedFromFile)
                {
                    layout = new MasterLayout();
                }
                else
                {
                    var raw = File.ReadAllText(path, Encoding.UTF8);
                    var json = (raw ?? string.Empty)
                        .Replace("\0", string.Empty)
                        .TrimStart('\uFEFF', ' ', '\t', '\r', '\n');

                    if (string.IsNullOrWhiteSpace(json))
                    {
                        layout = new MasterLayout();
                    }
                    else
                    {
                        layout = JsonSerializer.Deserialize<MasterLayout>(json) ?? new MasterLayout();
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException)
            {
                Debug.WriteLine($"[MasterLayoutManager] LoadOrNew: contenido inválido, se cargan valores por defecto. {ex.Message}");
                layout = new MasterLayout();
            }

            EnsureInspectionRoiDefaults(layout);
            EnsureOptionDefaults(layout);

            if (loadedFromFile && (layout.Master1Pattern == null || layout.Master1Search == null))
            {
                throw new InvalidOperationException("Preset incompleto: falta Master 1.");
            }

            layout.Master2Pattern ??= new RoiModel { Role = RoiRole.Master2Pattern };
            layout.Master2Search ??= new RoiModel { Role = RoiRole.Master2Search };
            return layout;
        }

        public static void Save(PresetFile preset, MasterLayout layout)
        {
            var path = GetDefaultPath(preset);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(layout, options);

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
#if NET6_0_OR_GREATER
            File.Move(tmp, path, overwrite: true);
#else
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
#endif
        }

        private static void EnsureInspectionRoiDefaults(MasterLayout layout)
        {
            for (int i = 0; i < layout.InspectionRois.Count; i++)
            {
                var roi = layout.InspectionRois[i];
                if (string.IsNullOrWhiteSpace(roi.Name))
                {
                    roi.Name = $"Inspection {i + 1}";
                }

                if (string.IsNullOrWhiteSpace(roi.ModelKey))
                {
                    roi.ModelKey = $"inspection-{i + 1}";
                }
            }

            layout.Inspection1 ??= layout.Inspection;
            layout.Inspection ??= layout.Inspection1;

            layout.Inspection2 ??= null;
            layout.Inspection3 ??= null;
            layout.Inspection4 ??= null;
        }

        private static void EnsureOptionDefaults(MasterLayout layout)
        {
            layout.Analyze ??= new AnalyzeOptions();
            layout.Ui ??= new UiOptions();

            if (layout.InspectionBaselinesByImage == null)
            {
                layout.InspectionBaselinesByImage = new Dictionary<string, List<RoiModel>>(StringComparer.OrdinalIgnoreCase);
            }
            else if (!Equals(layout.InspectionBaselinesByImage.Comparer, StringComparer.OrdinalIgnoreCase))
            {
                layout.InspectionBaselinesByImage = new Dictionary<string, List<RoiModel>>(layout.InspectionBaselinesByImage, StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}
