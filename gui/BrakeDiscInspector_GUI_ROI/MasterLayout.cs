using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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

        public MasterLayout DeepClone()
        {
            var clone = new MasterLayout
            {
                Master1Pattern = Master1Pattern?.Clone(),
                Master1PatternImagePath = Master1PatternImagePath,
                Master1Search = Master1Search?.Clone(),
                Master2Pattern = Master2Pattern?.Clone(),
                Master2PatternImagePath = Master2PatternImagePath,
                Master2Search = Master2Search?.Clone(),
                Inspection = Inspection?.Clone(),
                InspectionBaseline = InspectionBaseline?.Clone(),
                Inspection1 = Inspection1?.Clone(),
                Inspection2 = Inspection2?.Clone(),
                Inspection3 = Inspection3?.Clone(),
                Inspection4 = Inspection4?.Clone(),
                Analyze = new AnalyzeOptions
                {
                    PosTolPx = Analyze?.PosTolPx ?? new AnalyzeOptions().PosTolPx,
                    AngTolDeg = Analyze?.AngTolDeg ?? new AnalyzeOptions().AngTolDeg,
                    ScaleLock = Analyze?.ScaleLock ?? new AnalyzeOptions().ScaleLock,
                    UseLocalMatcher = Analyze?.UseLocalMatcher ?? new AnalyzeOptions().UseLocalMatcher
                },
                Ui = new UiOptions
                {
                    HeatmapGamma = Ui?.HeatmapGamma ?? new UiOptions().HeatmapGamma,
                    HeatmapGain = Ui?.HeatmapGain ?? new UiOptions().HeatmapGain,
                    HeatmapOverlayOpacity = Ui?.HeatmapOverlayOpacity ?? new UiOptions().HeatmapOverlayOpacity
                },
                InspectionBaselinesByImage = new Dictionary<string, List<RoiModel>>(StringComparer.OrdinalIgnoreCase)
            };

            if (InspectionBaselinesByImage != null)
            {
                foreach (var kvp in InspectionBaselinesByImage)
                {
                    var list = kvp.Value?.Where(r => r != null).Select(r => r!.Clone()).ToList() ?? new List<RoiModel>();
                    clone.InspectionBaselinesByImage[kvp.Key] = list;
                }
            }

            clone.InspectionRois.Clear();
            foreach (var cfg in InspectionRois)
            {
                clone.InspectionRois.Add(cfg.Clone());
            }

            return clone;
        }
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

        public static string GetLayoutsFolder(Preset preset)
            => System.IO.Path.Combine(preset.Home, "Layouts");

        public static string GetDefaultPath(Preset preset)
            => System.IO.Path.Combine(GetLayoutsFolder(preset), "last.layout.json");

        public static string GetTimestampedPath(Preset preset)
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

        public static (MasterLayout layout, bool loadedFromFile) LoadOrNew(Preset preset)
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

        public static void Save(Preset preset, MasterLayout layout)
        {
            var dir = GetLayoutsFolder(preset);
            Directory.CreateDirectory(dir);

            if (layout.Master1Pattern == null || layout.Master1Search == null)
            {
                throw new InvalidOperationException(
                    "No se puede guardar el Layout: falta Master 1 (Pattern y/o Search). " +
                    "Dibuja/guarda Master 1 antes de 'Save Layout'.");
            }

            // === Sanitizar antes de serializar ===
            SanitizeForSave(layout);

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

        private static readonly Regex s_inspectionKeyRegex = new("inspection[\\s_\\-]?([1-4])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static string NormalizeInspectionKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return key ?? string.Empty;
            }

            var match = s_inspectionKeyRegex.Match(key);
            return match.Success ? $"inspection-{match.Groups[1].Value}" : key.Trim();
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

                var normalizedKey = NormalizeInspectionKey(roi.ModelKey);
                if (string.IsNullOrWhiteSpace(normalizedKey))
                {
                    normalizedKey = $"inspection-{i + 1}";
                }
                roi.ModelKey = normalizedKey;

                var expectedId = $"Inspection_{i + 1}";
                if (string.IsNullOrWhiteSpace(roi.Id) || !string.Equals(roi.Id, expectedId, StringComparison.OrdinalIgnoreCase))
                {
                    roi.Id = expectedId;
                }
            }

            // Migración legacy: si sólo existe 'Inspection' global y no hay slots, volcar a slot 1 y borrar el global.
            if (layout.Inspection1 == null && layout.Inspection2 == null && layout.Inspection3 == null && layout.Inspection4 == null)
            {
                if (layout.Inspection != null)
                {
                    layout.Inspection1 = layout.Inspection.CloneDeep();
                    layout.Inspection1.Role = RoiRole.Inspection;
                    layout.Inspection1.Id = "Inspection_1";
                    System.Diagnostics.Debug.WriteLine("[layout:migrate] Inspection -> Inspection1");
                }
            }

            // A partir de aquí, no usar 'Inspection' global como fuente de verdad.
            layout.Inspection = null;
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

        // ====== Sanitización previa al guardado ======
        private static void SanitizeForSave(MasterLayout layout)
        {
            // 1) Nunca persistir 'Inspection' global: sólo por slots.
            if (layout.Inspection != null)
            {
                System.Diagnostics.Debug.WriteLine("[layout:sanitize] dropping global 'Inspection' (use slots)");
                layout.Inspection = null;
            }

            // 2) Asegurar IDs de slot y rol correcto
            void FixSlot(ref RoiModel? slotRef, int idx)
            {
                if (slotRef == null) return;
                slotRef.Role = RoiRole.Inspection;
                slotRef.Id = $"Inspection_{idx}";
            }
            var inspection1 = layout.Inspection1;
            FixSlot(ref inspection1, 1);
            layout.Inspection1 = inspection1;

            var inspection2 = layout.Inspection2;
            FixSlot(ref inspection2, 2);
            layout.Inspection2 = inspection2;

            var inspection3 = layout.Inspection3;
            FixSlot(ref inspection3, 3);
            layout.Inspection3 = inspection3;

            var inspection4 = layout.Inspection4;
            FixSlot(ref inspection4, 4);
            layout.Inspection4 = inspection4;

            // 3) Normalizar baselines por imagen (sólo 'Inspection_1..4', sin duplicados, clon profundo)
            if (layout.InspectionBaselinesByImage != null)
            {
                foreach (var key in layout.InspectionBaselinesByImage.Keys.ToList())
                {
                    var list = layout.InspectionBaselinesByImage[key] ?? new List<RoiModel>();
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var normalized = new List<RoiModel>(capacity: list.Count);

                    foreach (var roi in list)
                    {
                        if (roi == null) continue;
                        var id = roi.Id;
                        if (string.IsNullOrWhiteSpace(id)) continue;

                        // Aceptar solo Ids 'Inspection_n' con n=1..4
                        const string prefix = "Inspection_";
                        if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!int.TryParse(id.Substring(prefix.Length), out int idx) || idx < 1 || idx > 4) continue;

                        var fixedId = $"Inspection_{idx}";
                        if (!seen.Add(fixedId)) continue; // evitar duplicados por imagen

                        var clone = roi.CloneDeep();
                        clone.Role = RoiRole.Inspection;
                        clone.Id = fixedId;
                        normalized.Add(clone);
                    }

                    layout.InspectionBaselinesByImage[key] = normalized;
                }
            }
        }
    }
}
