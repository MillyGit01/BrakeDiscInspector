using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BrakeDiscInspector_GUI_ROI.Helpers;
using BrakeDiscInspector_GUI_ROI.Models;
using BrakeDiscInspector_GUI_ROI.Util;

namespace BrakeDiscInspector_GUI_ROI
{
    public class MasterLayout
    {
        public MasterLayout()
        {
            MasterLayoutManager.EnsureInspectionRoiDefaults(this);
        }

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

        public ObservableCollection<InspectionRoiConfig> InspectionRois { get; set; } = new();

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
                    DisableRot = Analyze?.DisableRot ?? new AnalyzeOptions().DisableRot,
                    UseLocalMatcher = Analyze?.UseLocalMatcher ?? new AnalyzeOptions().UseLocalMatcher,
                    FeatureM1 = Analyze?.FeatureM1 ?? new AnalyzeOptions().FeatureM1,
                    FeatureM2 = Analyze?.FeatureM2 ?? new AnalyzeOptions().FeatureM2,
                    ThrM1 = Analyze?.ThrM1 ?? new AnalyzeOptions().ThrM1,
                    ThrM2 = Analyze?.ThrM2 ?? new AnalyzeOptions().ThrM2,
                    RotRange = Analyze?.RotRange ?? new AnalyzeOptions().RotRange,
                    ScaleMin = Analyze?.ScaleMin ?? new AnalyzeOptions().ScaleMin,
                    ScaleMax = Analyze?.ScaleMax ?? new AnalyzeOptions().ScaleMax
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

            clone.InspectionRois = new ObservableCollection<InspectionRoiConfig>();
            foreach (var cfg in InspectionRois ?? Enumerable.Empty<InspectionRoiConfig>())
            {
                if (cfg == null)
                {
                    continue;
                }

                clone.InspectionRois.Add(cfg.Clone());
            }

            return clone;
        }
    }

    public sealed class AnalyzeOptions
    {
        public double PosTolPx { get; set; } = 50.0;
        public double AngTolDeg { get; set; } = 0.5;
        public bool ScaleLock { get; set; } = true;
        public bool DisableRot { get; set; } = false;
        public bool UseLocalMatcher { get; set; } = true;
        public string FeatureM1 { get; set; } = "auto";
        public int ThrM1 { get; set; } = 85;
        public string FeatureM2 { get; set; } = "auto";
        public int ThrM2 { get; set; } = 85;
        public int RotRange { get; set; } = 10;
        public double ScaleMin { get; set; } = 0.95;
        public double ScaleMax { get; set; } = 1.05;
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

        // NOTE (for user): Layout files (.layout/.layout.json) are stored under the
        // "Layouts" folder inside the preset home directory (preset.Home). With the
        // default preset this resolves to \\wsl$\Ubuntu\home\millylinux\BrakeDiscDefect_BACKEND\Layouts.
        public static string GetLayoutsFolder(Preset preset)
            => System.IO.Path.Combine(preset.Home, "Layouts");

        public static string GetDefaultPath(Preset preset)
            => System.IO.Path.Combine(GetLayoutsFolder(preset), "last.layout.json");

        public static string GetTimestampedPath(Preset preset)
            => System.IO.Path.Combine(GetLayoutsFolder(preset),
                                      DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".layout.json");

        public static string GetLayoutNameFromPath(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.EndsWith(".layout", StringComparison.OrdinalIgnoreCase))
            {
                name = Path.GetFileNameWithoutExtension(name);
            }

            return string.IsNullOrWhiteSpace(name) ? "DefaultLayout" : name;
        }

        public static MasterLayout LoadFromFile(string filePath)
        {
            try
            {
                var raw = File.ReadAllText(filePath, Encoding.UTF8);
                var hasLegacyFitOk = raw.Contains("\"HasFitOk\"", StringComparison.Ordinal);
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

                var layoutName = GetLayoutNameFromPath(filePath);
                NormalizeLayoutPathsForRecipe(layout, RecipePathHelper.GetLayoutFolder(layoutName), filePath);
                EnsureInspectionRoiDefaults(layout);
                EnsureOptionDefaults(layout);
                TraceInspectionRois("load", layout);

                if (hasLegacyFitOk)
                {
                    SanitizeForSave(layout);
                    try
                    {
                        var migratedJson = JsonSerializer.Serialize(layout, s_opts);
                        File.WriteAllText(filePath, migratedJson, Encoding.UTF8);
                        Debug.WriteLine($"[layout:migrate] removed legacy HasFitOk from {filePath}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[layout:migrate] failed to rewrite {filePath}: {ex.Message}");
                    }
                }

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
                TraceInspectionRois("load", layout);
                layout.Master2Pattern ??= new RoiModel { Role = RoiRole.Master2Pattern };
                layout.Master2Search ??= new RoiModel { Role = RoiRole.Master2Search };
                return layout;
            }
        }

        public static (MasterLayout layout, bool loadedFromFile) LoadOrNew(Preset preset)
        {
            var path = EnsureLayoutJsonExtension(GetDefaultPath(preset));
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
                    NormalizeLayoutPathsForRecipe(layout, RecipePathHelper.GetLayoutFolder(GetLayoutNameFromPath(path)), path);
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
            TraceInspectionRois("load", layout);

            if (loadedFromFile && (layout.Master1Pattern == null || layout.Master1Search == null))
            {
                layout.Master1Pattern ??= new RoiModel { Role = RoiRole.Master1Pattern };
                layout.Master1Search ??= new RoiModel { Role = RoiRole.Master1Search };

                System.Diagnostics.Debug.WriteLine(
                    "[layout] Archivo incompleto: faltaba Master1Pattern o Master1Search. " +
                    "Se han creado placeholders. Se recomienda volver a Guardar el layout.");

                loadedFromFile = false;
            }

            // If we are effectively in "no valid layout loaded" mode, do NOT preload any inspection ROI as enabled.
            if (!loadedFromFile && layout.InspectionRois != null)
            {
                foreach (var roi in layout.InspectionRois)
                {
                    if (roi != null)
                    {
                        roi.Enabled = false;
                    }
                }
            }

            layout.Master2Pattern ??= new RoiModel { Role = RoiRole.Master2Pattern };
            layout.Master2Search ??= new RoiModel { Role = RoiRole.Master2Search };
            return (layout, loadedFromFile);
        }

        public static void Save(Preset preset, MasterLayout layout)
        {
            var dir = GetLayoutsFolder(preset);
            Directory.CreateDirectory(dir);

            // Sólo exigimos que exista Master 1 Pattern. Master1Search pasa a ser opcional
            if (layout.Master1Pattern == null)
            {
                throw new InvalidOperationException(
                    "No se puede guardar el Layout: falta Master 1 Pattern. " +
                    "Dibuja/guarda Master 1 Pattern antes de 'Save Layout'.");
            }

            // === Sanitizar antes de serializar ===
            EnsureInspectionRoiDefaults(layout);
            SanitizeForSave(layout);
            TraceInspectionRois("save", layout);

            var path = EnsureLayoutJsonExtension(GetDefaultPath(preset));
            System.Diagnostics.Debug.WriteLine(
                $"[layout:save] path={path} " +
                $"M1P={(layout.Master1Pattern != null)} M1S={(layout.Master1Search != null)} " +
                $"M2P={(layout.Master2Pattern != null)} M2S={(layout.Master2Search != null)}");

            var json = JsonSerializer.Serialize(layout, s_opts);

            File.WriteAllText(path, json);

            var snapshot = EnsureLayoutJsonExtension(GetTimestampedPath(preset));
            File.WriteAllText(snapshot, json);
        }

        public static string EnsureLayoutJsonExtension(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            if (path.EndsWith(".layout.json", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            var withExtension = Path.ChangeExtension(path, ".layout.json");
            if (withExtension.EndsWith(".layout.json", StringComparison.OrdinalIgnoreCase))
            {
                return withExtension;
            }

            return path + ".layout.json";
        }

        public static void SaveAs(Preset preset, MasterLayout layout, string path)
        {
            var targetPath = EnsureLayoutJsonExtension(path);
            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Reuse the same guardrails as the standard Save, pero sólo para Master1Pattern
            if (layout.Master1Pattern == null)
            {
                throw new InvalidOperationException(
                    "No se puede guardar el Layout: falta Master 1 Pattern. " +
                    "Dibuja/guarda Master 1 Pattern antes de 'Save Layout'.");
            }

            EnsureInspectionRoiDefaults(layout);
            var layoutName = GetLayoutNameFromPath(targetPath);
            NormalizeLayoutPathsForRecipe(layout, RecipePathHelper.GetLayoutFolder(layoutName), targetPath);
            SanitizeForSave(layout);
            TraceInspectionRois("save", layout);

            var json = JsonSerializer.Serialize(layout, s_opts);
            File.WriteAllText(targetPath, json);
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

        public sealed record InspectionRoiNormalizationResult(int OriginalCount, int NormalizedCount, bool DuplicatesRemoved);

        private static int TryParseInspectionIndex(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return -1;
            }

            var match = s_inspectionKeyRegex.Match(key);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int idx) && idx >= 1 && idx <= 4)
            {
                return idx;
            }

            return -1;
        }

        private static int ResolveInspectionSlot(InspectionRoiConfig roi)
        {
            if (roi == null)
            {
                return -1;
            }

            if (roi.Index is >= 1 and <= 4)
            {
                return roi.Index;
            }

            var parsed = TryParseInspectionIndex(roi.Id);
            if (parsed is >= 1 and <= 4)
            {
                return parsed;
            }

            parsed = TryParseInspectionIndex(roi.ModelKey);
            return parsed;
        }

        private static void ForceInspectionIndex(InspectionRoiConfig roi, int index)
        {
            if (roi == null)
            {
                return;
            }

            var prop = typeof(InspectionRoiConfig).GetProperty("Index", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            prop?.SetValue(roi, index);
        }

        public static InspectionRoiNormalizationResult EnsureInspectionRoiDefaults(MasterLayout layout)
        {
            layout.InspectionRois ??= new ObservableCollection<InspectionRoiConfig>();
            var originalCount = layout.InspectionRois.Count;

            var slotMap = new Dictionary<int, InspectionRoiConfig>();
            foreach (var cfg in layout.InspectionRois)
            {
                var slot = ResolveInspectionSlot(cfg);
                if (slot < 1 || slot > 4)
                {
                    continue;
                }

                slotMap[slot] = cfg; // last one wins
            }

            var normalized = new List<InspectionRoiConfig>(capacity: 4);
            for (int i = 1; i <= 4; i++)
            {
                slotMap.TryGetValue(i, out var cfg);
                cfg ??= new InspectionRoiConfig(i);

                ForceInspectionIndex(cfg, i);

                if (string.IsNullOrWhiteSpace(cfg.Name))
                {
                    cfg.Name = $"Inspection {i}";
                }

                var normalizedKey = NormalizeInspectionKey(cfg.ModelKey);
                cfg.ModelKey = string.IsNullOrWhiteSpace(normalizedKey) ? $"inspection-{i}" : normalizedKey;

                var expectedId = $"Inspection_{i}";
                if (string.IsNullOrWhiteSpace(cfg.Id) || !string.Equals(cfg.Id, expectedId, StringComparison.OrdinalIgnoreCase))
                {
                    cfg.Id = expectedId;
                }

                normalized.Add(cfg);
            }

            layout.InspectionRois.Clear();
            foreach (var cfg in normalized)
            {
                layout.InspectionRois.Add(cfg);
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

            var duplicatesRemoved = originalCount > slotMap.Count;
            if (duplicatesRemoved)
            {
                Debug.WriteLine(FormattableString.Invariant(
                    $"[layout:rois] WARNING duplicates detected: originalCount={originalCount} -> normalizedCount={layout.InspectionRois.Count}"));
            }

            return new InspectionRoiNormalizationResult(originalCount, layout.InspectionRois.Count, duplicatesRemoved);
        }

        private static void TraceInspectionRois(string label, MasterLayout layout)
        {
            if (layout?.InspectionRois == null)
            {
                Debug.WriteLine(FormattableString.Invariant($"[layout:rois] {label} count=0"));
                return;
            }

            Debug.WriteLine(FormattableString.Invariant(
                $"[layout:rois] {label} count={layout.InspectionRois.Count}"));

            foreach (var roi in layout.InspectionRois.OrderBy(r => r.Index))
            {
                if (roi == null)
                {
                    continue;
                }

                Debug.WriteLine(FormattableString.Invariant(
                    $"[layout:roi] idx={roi.Index} id={roi.Id} modelKey={roi.ModelKey} anchorMaster={(int)roi.AnchorMaster} datasetPath={roi.DatasetPath}"));
            }
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

        private static void NormalizeLayoutPathsForRecipe(MasterLayout layout, string recipeRoot, string? layoutFilePath)
        {
            if (layout == null || string.IsNullOrWhiteSpace(recipeRoot))
            {
                return;
            }

            var layoutName = ExtractLayoutNameFromRecipeRoot(recipeRoot);
            string ReplaceLastSegment(string? original)
            {
                if (string.IsNullOrWhiteSpace(original))
                {
                    return original ?? string.Empty;
                }

                var updated = original;
                var lastSegment = $"{Path.DirectorySeparatorChar}Recipes{Path.DirectorySeparatorChar}last{Path.DirectorySeparatorChar}";
                var recipeSegment = $"{Path.DirectorySeparatorChar}Recipes{Path.DirectorySeparatorChar}{layoutName}{Path.DirectorySeparatorChar}";

                if (updated.IndexOf(lastSegment, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    updated = updated.Replace(lastSegment, recipeSegment, StringComparison.OrdinalIgnoreCase);
                }

                var lastAlt = $"{Path.AltDirectorySeparatorChar}Recipes{Path.AltDirectorySeparatorChar}last{Path.AltDirectorySeparatorChar}";
                var recipeAlt = $"{Path.AltDirectorySeparatorChar}Recipes{Path.AltDirectorySeparatorChar}{layoutName}{Path.AltDirectorySeparatorChar}";

                if (updated.IndexOf(lastAlt, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    updated = updated.Replace(lastAlt, recipeAlt, StringComparison.OrdinalIgnoreCase);
                }

                return updated;
            }

            string? NormalizeMaster(string? field, string tag)
            {
                if (string.IsNullOrWhiteSpace(field))
                {
                    return field;
                }

                var candidate = ReplaceLastSegment(field);
                if (string.Equals(candidate, field, StringComparison.OrdinalIgnoreCase))
                {
                    return field;
                }

                if (File.Exists(candidate))
                {
                    Debug.WriteLine(FormattableString.Invariant(
                        $"[layout:path] Redirecting {tag} from 'last' to '{layoutName}': '{candidate}' (layout='{layoutFilePath}')"));
                    return candidate;
                }

                if (!File.Exists(field))
                {
                    Debug.WriteLine(FormattableString.Invariant(
                        $"[layout:path] Redirecting {tag} to recipe folder even if file is missing: '{candidate}' (layout='{layoutFilePath}')"));
                    return candidate;
                }

                Debug.WriteLine(FormattableString.Invariant(
                    $"[layout:path] WARNING {tag}: candidate missing for layout '{layoutName}' -> '{candidate}' (layout='{layoutFilePath}')"));

                return field;
            }

            layout.Master1PatternImagePath = NormalizeMaster(layout.Master1PatternImagePath, "M1");
            layout.Master2PatternImagePath = NormalizeMaster(layout.Master2PatternImagePath, "M2");

            AlignInspectionDatasetPaths(layout, recipeRoot, layoutFilePath);
        }

        internal static void AlignInspectionDatasetPaths(MasterLayout layout, string recipeRoot, string? layoutFilePath)
        {
            if (layout == null || string.IsNullOrWhiteSpace(recipeRoot) || layout.InspectionRois == null)
            {
                return;
            }

            var layoutName = ExtractLayoutNameFromRecipeRoot(recipeRoot);
            var datasetRoot = RecipePathHelper.GetDatasetFolder(layoutName);
            var inspectionRegex = new Regex(@"^inspection-(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

            foreach (var roi in layout.InspectionRois)
            {
                if (roi == null)
                {
                    continue;
                }

                var roiId = roi.ModelKey ?? string.Empty;
                var match = inspectionRegex.Match(roiId);
                if (!match.Success)
                {
                    continue;
                }

                var folder = $"Inspection_{match.Groups[1].Value}";
                var expected = Path.Combine(datasetRoot, folder);
                var oldPath = roi.DatasetPath;
                if (!string.Equals(oldPath, expected, StringComparison.OrdinalIgnoreCase))
                {
                    roi.DatasetPath = expected;
                    GuiLog.Info($"NormalizeLayoutPathsForRecipe: Realigned inspection dataset path roi='{roiId}' old='{oldPath ?? string.Empty}' new='{roi.DatasetPath ?? string.Empty}'");
                }

                EnsureDatasetSubfolders(expected, roiId, layoutFilePath);
            }
        }

        private static void EnsureDatasetSubfolders(string expected, string roiId, string? layoutFilePath)
        {
            try
            {
                Directory.CreateDirectory(expected);
                Directory.CreateDirectory(Path.Combine(expected, "ok"));
                Directory.CreateDirectory(Path.Combine(expected, "ng"));
            }
            catch (Exception ex)
            {
                GuiLog.Warn($"NormalizeLayoutPathsForRecipe: failed to ensure dataset folders roi='{roiId}' path='{expected}' layout='{layoutFilePath ?? "<null>"}' error='{ex.Message}'");
            }
        }

        private static string ExtractLayoutNameFromRecipeRoot(string recipeRoot)
        {
            if (string.IsNullOrWhiteSpace(recipeRoot))
            {
                return "DefaultLayout";
            }

            var trimmed = recipeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var layoutName = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(layoutName) ? "DefaultLayout" : layoutName;
        }
    }
}
