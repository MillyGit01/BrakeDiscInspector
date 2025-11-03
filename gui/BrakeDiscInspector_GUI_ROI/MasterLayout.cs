using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        public bool UseLocalMatcher { get; set; } = true;
    }

    public sealed class UiOptions
    {
        public double HeatmapOverlayOpacity { get; set; } = 0.6;
        public double HeatmapGain { get; set; } = 1.0;
        public double HeatmapGamma { get; set; } = 1.0;
    }

    public static class MasterLayoutManager
    {
        private static JsonSerializerOptions s_opts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static string GetLayoutsFolder(PresetFile preset)
            => System.IO.Path.Combine(preset.Home, "Layouts");

        public static string GetDefaultPath(PresetFile preset)
            => System.IO.Path.Combine(GetLayoutsFolder(preset), "last.layout.json");

        public static string GetTimestampedPath(PresetFile preset)
            => System.IO.Path.Combine(GetLayoutsFolder(preset),
                                      DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".layout.json");

        public static MasterLayout LoadFromFile(string filePath)
        {
            try
            {
                var raw = File.ReadAllText(filePath, Encoding.UTF8);
                var json = (raw ?? string.Empty)
                    .Replace("\0", string.Empty)
                    .TrimStart('\uFEFF', ' ', '\t', '\r', '\n');

                MasterLayout layout;
                if (string.IsNullOrWhiteSpace(json))
                {
                    layout = new MasterLayout();
                }
                else
                {
                    layout = JsonSerializer.Deserialize<MasterLayout>(json, s_opts) ?? new MasterLayout();
                }

                EnsureInspectionRoiDefaults(layout);
                EnsureOptionDefaults(layout);

                layout.Master2Pattern ??= new RoiModel { Role = RoiRole.Master2Pattern };
                layout.Master2Search ??= new RoiModel { Role = RoiRole.Master2Search };
                return layout;
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException)
            {
                Debug.WriteLine($"[MasterLayoutManager] LoadFromFile: contenido inválido, se cargan valores por defecto. {ex.Message}");
                var layout = new MasterLayout();
                EnsureInspectionRoiDefaults(layout);
                EnsureOptionDefaults(layout);
                layout.Master2Pattern ??= new RoiModel { Role = RoiRole.Master2Pattern };
                layout.Master2Search ??= new RoiModel { Role = RoiRole.Master2Search };
                return layout;
            }
        }

        public static (MasterLayout layout, bool loadedFromFile) LoadOrNew(PresetFile preset)
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
                    layout = LoadFromFile(path);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException)
            {
                Debug.WriteLine($"[MasterLayoutManager] LoadOrNew: contenido inválido, se cargan valores por defecto. {ex.Message}");
                layout = new MasterLayout();
                loadedFromFile = false;
            }

            EnsureInspectionRoiDefaults(layout);
            EnsureOptionDefaults(layout);

            if (loadedFromFile && (layout.Master1Pattern == null || layout.Master1Search == null))
            {
                layout.Master1Pattern ??= new RoiModel { Role = RoiRole.Master1Pattern };
                layout.Master1Search ??= new RoiModel { Role = RoiRole.Master1Search };

                System.Diagnostics.Debug.WriteLine(
                    "[layout] Archivo incompleto: faltaba Master1Pattern o Master1Search. " +
                    "Se han creado placeholders. Se recomienda volver a Guardar el layout.");

                loadedFromFile = false;
            }

            layout.Master2Pattern ??= new RoiModel { Role = RoiRole.Master2Pattern };
            layout.Master2Search ??= new RoiModel { Role = RoiRole.Master2Search };
            return (layout, loadedFromFile);
        }

        public static void Save(PresetFile preset, MasterLayout layout)
        {
            var dir = GetLayoutsFolder(preset);
            Directory.CreateDirectory(dir);

            if (layout.Master1Pattern == null || layout.Master1Search == null)
            {
                throw new InvalidOperationException(
                    "No se puede guardar el Layout: falta Master 1 (Pattern y/o Search). " +
                    "Dibuja/guarda Master 1 antes de 'Save Layout'.");
            }

            var path = GetDefaultPath(preset);
            System.Diagnostics.Debug.WriteLine(
                $"[layout:save] path={path} " +
                $"M1P={(layout.Master1Pattern != null)} M1S={(layout.Master1Search != null)} " +
                $"M2P={(layout.Master2Pattern != null)} M2S={(layout.Master2Search != null)}");

            var json = JsonSerializer.Serialize(layout, s_opts);

            File.WriteAllText(path, json);

            var snapshot = GetTimestampedPath(preset);
            File.WriteAllText(snapshot, json);
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

                var expectedId = $"Inspection_{i + 1}";
                if (string.IsNullOrWhiteSpace(roi.Id) || !string.Equals(roi.Id, expectedId, StringComparison.OrdinalIgnoreCase))
                {
                    roi.Id = expectedId;
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
