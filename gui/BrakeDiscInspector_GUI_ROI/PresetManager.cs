using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrakeDiscInspector_GUI_ROI
{
    public static class PresetManager
    {
        public static string GetDefaultPath(Preset preset)
            => Path.Combine(preset.Home, "configs", "preset.json");

        public static void Save(Preset preset, string? path = null)
        {
            path ??= GetDefaultPath(preset);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(preset, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }

        public static Preset Load(string path)
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Preset>(json) ?? new Preset();
        }

        public static Preset LoadOrDefault(Preset preset)
        {
            var path = GetDefaultPath(preset);
            return File.Exists(path) ? Load(path) : preset;
        }

        public static void SaveInspection(Preset preset, int slot, RoiModel roi, Action<string>? log = null)
        {
            if (preset == null) throw new ArgumentNullException(nameof(preset));
            if (roi == null) throw new ArgumentNullException(nameof(roi));
            if (slot < 1 || slot > 4) throw new ArgumentOutOfRangeException(nameof(slot));

            var clone = roi.CloneDeep();
            clone.Role = RoiRole.Inspection;
            clone.Id = slot switch
            {
                1 => "Inspection_1",
                2 => "Inspection_2",
                3 => "Inspection_3",
                4 => "Inspection_4",
                _ => clone.Id
            };

            switch (slot)
            {
                case 1:
                    preset.Inspection1 = clone.CloneDeep();
                    break;
                case 2:
                    preset.Inspection2 = clone.CloneDeep();
                    break;
            }

            SyncInspectionList(preset, clone);

            log?.Invoke(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "[preset] SaveInspection slot={0} id={1} shape={2} base=({3}x{4})",
                slot,
                clone.Id ?? "<null>",
                clone.Shape,
                clone.BaseImgW,
                clone.BaseImgH));
        }

        public static RoiModel? GetInspection(Preset preset, int slot, Action<string>? log = null)
        {
            if (preset == null) return null;

            RoiModel? source = slot switch
            {
                1 => preset.Inspection1,
                2 => preset.Inspection2,
                _ => null
            };

            if (source == null && preset.Rois != null)
            {
                string id = $"Inspection_{slot}";
                source = preset.Rois.FirstOrDefault(r =>
                    r != null && !string.IsNullOrWhiteSpace(r.Id) &&
                    string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
            }

            if (source == null)
            {
                return null;
            }

            var clone = source.CloneDeep();
            log?.Invoke(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "[preset] LoadInspection slot={0} id={1} shape={2} base=({3}x{4})",
                slot,
                clone.Id ?? "<null>",
                clone.Shape,
                clone.BaseImgW,
                clone.BaseImgH));
            return clone;
        }

        private static void SyncInspectionList(Preset preset, RoiModel clone)
        {
            if (preset.Rois == null)
            {
                preset.Rois = new List<RoiModel>();
            }

            string id = clone.Id ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            int existing = preset.Rois.FindIndex(r =>
                r != null && !string.IsNullOrWhiteSpace(r.Id) &&
                string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

            var stored = clone.CloneDeep();
            if (existing >= 0)
            {
                preset.Rois[existing] = stored;
            }
            else
            {
                preset.Rois.Add(stored);
            }
        }
    }

    public static class CloneExtensions
    {
        public static T CloneDeep<T>(this T obj)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));

            var opts = new JsonSerializerOptions
            {
                PropertyNamingPolicy = null,
                WriteIndented = false,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never
            };

            var json = JsonSerializer.Serialize(obj, opts);
            return JsonSerializer.Deserialize<T>(json, opts)!;
        }
    }
}
