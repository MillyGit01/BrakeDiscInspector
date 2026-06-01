using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenCvSharp;

namespace BrakeDiscInspector_GUI_ROI
{
    public sealed class CanonicalRoiPayload
    {
        public CanonicalRoiPayload(byte[] pngBytes, byte[]? maskBytes, string? shapeJson, int width, int height)
        {
            PngBytes = pngBytes;
            MaskBytes = maskBytes;
            ShapeJson = shapeJson;
            Width = width;
            Height = height;
        }

        public byte[] PngBytes { get; }

        public byte[]? MaskBytes { get; }

        public string? ShapeJson { get; }

        public int Width { get; }

        public int Height { get; }
    }

    public static class BackendPayloadBuilder
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

        public static double DefaultMmPerPx { get; } = ResolveConfiguredDefaultMmPerPx();

        public static double ResolveMmPerPx(Preset? preset, double? overrideValue = null)
        {
            if (overrideValue.HasValue && overrideValue.Value > 0)
            {
                return overrideValue.Value;
            }

            if (preset != null && preset.MmPerPx > 0)
            {
                return preset.MmPerPx;
            }

            return DefaultMmPerPx;
        }

        public static string ResolveRoleId(RoiModel roi)
        {
            if (roi == null)
            {
                return "DefaultRole";
            }

            return roi.Role switch
            {
                RoiRole.Master1Pattern or RoiRole.Master1Search => "Master1",
                RoiRole.Master2Pattern or RoiRole.Master2Search => "Master2",
                RoiRole.Inspection => "Inspection",
                _ => "DefaultRole",
            };
        }

        public static string ResolveRoiId(RoiModel roi)
        {
            if (roi == null)
            {
                return "ROI";
            }

            if (roi.Role == RoiRole.Inspection)
            {
                var label = roi.Label;
                if (!string.IsNullOrWhiteSpace(label))
                {
                    var sanitized = SanitizeId(label, string.Empty);
                    var labelMatch = Regex.Match(sanitized, @"inspection[\-_]?([1-4])", RegexOptions.IgnoreCase);
                    if (labelMatch.Success)
                    {
                        return $"inspection-{labelMatch.Groups[1].Value}";
                    }
                }

                if (!string.IsNullOrWhiteSpace(roi.Id))
                {
                    var idMatch = Regex.Match(roi.Id, @"inspection[\s_\-]?([1-4])", RegexOptions.IgnoreCase);
                    if (idMatch.Success)
                    {
                        return $"inspection-{idMatch.Groups[1].Value}";
                    }
                }

                return "inspection-1";
            }

            if (!string.IsNullOrWhiteSpace(roi.Label))
            {
                return SanitizeId(roi.Label, "ROI");
            }

            return roi.Role switch
            {
                RoiRole.Master1Pattern => "Pattern",
                RoiRole.Master1Search => "Search",
                RoiRole.Master2Pattern => "Pattern",
                RoiRole.Master2Search => "Search",
                _ => "ROI",
            };
        }

        public static bool TryPrepareCanonicalRoi(
            Mat src,
            RoiModel roi,
            out CanonicalRoiPayload? payload,
            out string fileName,
            Action<string>? log = null)
        {
            payload = null;
            fileName = $"roi_{DateTime.Now:yyyyMMdd_HHmmssfff}.png";

            try
            {
                if (src == null || src.Empty())
                {
                    log?.Invoke("[infer] src empty");
                    return false;
                }

                return TryPrepareCanonicalRoiCore(src, roi, out payload, out fileName, log);
            }
            catch (Exception ex)
            {
                log?.Invoke("[infer] " + ex.Message);
                return false;
            }
        }

        public static bool TryPrepareCanonicalRoi(
            string imagePathWin,
            RoiModel roi,
            out CanonicalRoiPayload? payload,
            out string fileName,
            Action<string>? log = null)
        {
            payload = null;
            fileName = $"roi_{DateTime.Now:yyyyMMdd_HHmmssfff}.png";

            try
            {
                using var src = Cv2.ImRead(imagePathWin, ImreadModes.Unchanged);
                if (src.Empty())
                {
                    log?.Invoke("[infer] failed to load image");
                    return false;
                }

                return TryPrepareCanonicalRoiCore(src, roi, out payload, out fileName, log);
            }
            catch (Exception ex)
            {
                log?.Invoke("[infer] " + ex.Message);
                return false;
            }
        }

        public static bool TryCropToPng(
            string imagePathWin,
            RoiModel roi,
            out MemoryStream pngStream,
            out MemoryStream? maskStream,
            out string fileName,
            Action<string>? log = null)
        {
            pngStream = null!;
            maskStream = null;
            if (!TryPrepareCanonicalRoi(imagePathWin, roi, out var payload, out fileName, log) || payload == null)
            {
                return false;
            }

            pngStream = new MemoryStream(payload.PngBytes, writable: false);
            if (payload.MaskBytes != null)
            {
                maskStream = new MemoryStream(payload.MaskBytes, writable: false);
            }

            return true;
        }

        private static bool TryPrepareCanonicalRoiCore(
            Mat src,
            RoiModel roi,
            out CanonicalRoiPayload? payload,
            out string fileName,
            Action<string>? log)
        {
            payload = null;
            fileName = $"roi_{DateTime.Now:yyyyMMdd_HHmmssfff}.png";

            if (roi == null)
            {
                log?.Invoke("[infer] ROI null");
                return false;
            }

            if (!RoiCropUtils.TryBuildRoiCropInfo(roi, out var info))
            {
                log?.Invoke("[infer] unsupported ROI shape");
                return false;
            }

            if (!RoiCropUtils.TryGetRotatedCrop(src, info, roi.AngleDeg, out var cropMat, out var cropRect))
            {
                log?.Invoke("[infer] failed to get rotated crop");
                return false;
            }

            Mat? maskMat = null;
            Mat? encodeMat = null;
            try
            {
                bool needsMask = roi.Shape == RoiShape.Circle || roi.Shape == RoiShape.Annulus;
                if (needsMask)
                {
                    maskMat = RoiCropUtils.BuildRoiMask(info, cropRect);
                }

                encodeMat = RoiCropUtils.ConvertCropToBgra(cropMat, maskMat);

                if (!Cv2.ImEncode(".png", encodeMat, out var pngBytes) || pngBytes == null || pngBytes.Length == 0)
                {
                    log?.Invoke("[infer] failed to encode PNG");
                    return false;
                }

                byte[]? maskBytes = null;
                if (maskMat != null && Cv2.ImEncode(".png", maskMat, out var maskPng) && maskPng != null && maskPng.Length > 0)
                {
                    maskBytes = maskPng;
                }

                var shapeJson = BuildShapeJson(roi, info, cropRect);
                payload = new CanonicalRoiPayload(pngBytes, maskBytes, shapeJson, cropRect.Width, cropRect.Height);

                log?.Invoke($"[infer] ROI={roi.Shape} rect=({info.Left:0.##},{info.Top:0.##},{info.Width:0.##},{info.Height:0.##}) pivot=({info.PivotX:0.##},{info.PivotY:0.##}) crop=({cropRect.X},{cropRect.Y},{cropRect.Width},{cropRect.Height}) angle={roi.AngleDeg:0.##}");
                return true;
            }
            finally
            {
                if (encodeMat != null && !ReferenceEquals(encodeMat, cropMat))
                {
                    encodeMat.Dispose();
                }

                maskMat?.Dispose();
                cropMat.Dispose();
            }
        }

        private static string BuildShapeJson(RoiModel roi, RoiCropInfo cropInfo, Rect cropRect)
        {
            double w = cropRect.Width;
            double h = cropRect.Height;

            object shape = roi.Shape switch
            {
                RoiShape.Rectangle => new { kind = "rect", x = 0, y = 0, w, h },
                RoiShape.Circle => new
                {
                    kind = "circle",
                    cx = w / 2.0,
                    cy = h / 2.0,
                    r = Math.Min(w, h) / 2.0
                },
                RoiShape.Annulus => new
                {
                    kind = "annulus",
                    cx = w / 2.0,
                    cy = h / 2.0,
                    r = ResolveOuterRadiusPx(cropInfo, cropRect),
                    r_inner = ResolveInnerRadiusPx(cropInfo, cropRect)
                },
                _ => new { kind = "rect", x = 0, y = 0, w, h }
            };

            return JsonSerializer.Serialize(shape, JsonOptions);
        }

        private static double ResolveOuterRadiusPx(RoiCropInfo cropInfo, Rect cropRect)
        {
            double outer = cropInfo.Radius > 0 ? cropInfo.Radius : Math.Max(cropInfo.Width, cropInfo.Height) / 2.0;
            double scale = Math.Min(
                cropRect.Width / Math.Max(cropInfo.Width, 1.0),
                cropRect.Height / Math.Max(cropInfo.Height, 1.0));
            double result = outer * scale;
            if (result <= 0)
            {
                result = Math.Min(cropRect.Width, cropRect.Height) / 2.0;
            }

            return result;
        }

        private static double ResolveInnerRadiusPx(RoiCropInfo cropInfo, Rect cropRect)
        {
            if (cropInfo.Shape != RoiShape.Annulus)
            {
                return 0;
            }

            double scale = Math.Min(
                cropRect.Width / Math.Max(cropInfo.Width, 1.0),
                cropRect.Height / Math.Max(cropInfo.Height, 1.0));
            double inner = Math.Clamp(cropInfo.InnerRadius, 0, cropInfo.Radius);
            double result = inner * scale;
            return Math.Max(result, 0);
        }

        private static string SanitizeId(string? value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            var sb = new StringBuilder();
            foreach (var ch in value.Trim())
            {
                if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
                {
                    sb.Append(ch);
                }
                else if (char.IsWhiteSpace(ch))
                {
                    sb.Append('_');
                }
            }

            return sb.Length > 0 ? sb.ToString() : fallback;
        }

        private static double ResolveConfiguredDefaultMmPerPx()
        {
            const double fallback = 0.20;

            try
            {
                var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
                if (File.Exists(settingsPath))
                {
                    var json = File.ReadAllText(settingsPath);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("Backend", out var backendSection) &&
                        backendSection.TryGetProperty("MmPerPx", out var mmPerPxElement) &&
                        mmPerPxElement.TryGetDouble(out var mmFromFile) &&
                        mmFromFile > 0)
                    {
                        return mmFromFile;
                    }
                }

                var mmEnv = Environment.GetEnvironmentVariable("BDI_MM_PER_PX") ??
                            Environment.GetEnvironmentVariable("BRAKEDISC_MM_PER_PX");
                if (!string.IsNullOrWhiteSpace(mmEnv) &&
                    double.TryParse(mmEnv, NumberStyles.Float, CultureInfo.InvariantCulture, out var mmValue) &&
                    mmValue > 0)
                {
                    return mmValue;
                }
            }
            catch
            {
                // Keep fallback when optional config cannot be read.
            }

            return fallback;
        }
    }
}
