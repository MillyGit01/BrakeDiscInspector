using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading.Tasks;
using OpenCvSharp;
using BrakeDiscInspector_GUI_ROI.Workflow;

namespace BrakeDiscInspector_GUI_ROI
{
    public sealed class InferRequest
    {
        public InferRequest(string roleId, string roiId, double mmPerPx, byte[] imageBytes)
        {
            RoleId = roleId ?? throw new ArgumentNullException(nameof(roleId));
            RoiId = roiId ?? throw new ArgumentNullException(nameof(roiId));
            if (mmPerPx <= 0)
                throw new ArgumentOutOfRangeException(nameof(mmPerPx));
            MmPerPx = mmPerPx;
            ImageBytes = imageBytes ?? throw new ArgumentNullException(nameof(imageBytes));
        }

        public string RoleId { get; }
        public string RoiId { get; }
        public double MmPerPx { get; }
        public byte[] ImageBytes { get; }
        public string? ShapeJson { get; set; }
        public string FileName { get; set; } = "roi.png";
    }

    public sealed class InferResult
    {
        public double score { get; set; }
        public double? threshold { get; set; }
        public string? heatmap_png_base64 { get; set; }
        public InferRegion[]? regions { get; set; }
        public int[]? token_shape { get; set; }
        public InferParams? @params { get; set; }
        public string? role_id { get; set; }
        public string? roi_id { get; set; }
        public string? error { get; set; }
        public string? trace { get; set; }
    }

    public sealed class InferRegion
    {
        public double x { get; set; }
        public double y { get; set; }
        public double w { get; set; }
        public double h { get; set; }
        public double area_px { get; set; }
        public double area_mm2 { get; set; }
        public double[]? bbox { get; set; }
    }

    public sealed class InferParams
    {
        public double mm_per_px { get; set; }
        public double? blur_sigma { get; set; }
        public int? score_percentile { get; set; }
    }

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

    [Obsolete("Use Workflow.BackendClient en su lugar.")]
    public static class BackendAPI
    {
        public static string BaseUrl { get; private set; } = "http://127.0.0.1:8000";
        public const string InferEndpoint = "infer";
        public const string TrainStatusEndpoint = "health";

        private static readonly BackendClient s_client = new BackendClient();
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

        public static double DefaultMmPerPx { get; private set; } = 0.20;

        static BackendAPI()
        {
            try
            {
                string? envBaseUrl =
                    Environment.GetEnvironmentVariable("BDI_BACKEND_BASEURL") ??
                    Environment.GetEnvironmentVariable("BDI_BACKEND_BASE_URL") ??
                    Environment.GetEnvironmentVariable("BDI_BACKEND_URL") ??
                    Environment.GetEnvironmentVariable("BRAKEDISC_BACKEND_BASEURL") ??
                    Environment.GetEnvironmentVariable("BRAKEDISC_BACKEND_BASE_URL") ??
                    Environment.GetEnvironmentVariable("BRAKEDISC_BACKEND_URL");

                if (!string.IsNullOrWhiteSpace(envBaseUrl))
                {
                    ApplyBaseUrl(NormalizeBaseUrl(envBaseUrl));
                }
                else
                {
                    var host = Environment.GetEnvironmentVariable("BDI_BACKEND_HOST") ??
                               Environment.GetEnvironmentVariable("BRAKEDISC_BACKEND_HOST") ??
                               Environment.GetEnvironmentVariable("HOST");
                    var port = Environment.GetEnvironmentVariable("BDI_BACKEND_PORT") ??
                               Environment.GetEnvironmentVariable("BRAKEDISC_BACKEND_PORT") ??
                               Environment.GetEnvironmentVariable("PORT");

                    if (!string.IsNullOrWhiteSpace(host) || !string.IsNullOrWhiteSpace(port))
                    {
                        host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
                        port = string.IsNullOrWhiteSpace(port) ? "8000" : port.Trim();
                        ApplyBaseUrl(NormalizeBaseUrl($"{host}:{port}"));
                    }
                }

                var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
                if (File.Exists(settingsPath))
                {
                    var json = File.ReadAllText(settingsPath);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("Backend", out var be) &&
                        be.TryGetProperty("BaseUrl", out var baseUrlEl))
                    {
                        var fileUrl = baseUrlEl.GetString();
                        if (!string.IsNullOrWhiteSpace(fileUrl))
                        {
                            ApplyBaseUrl(NormalizeBaseUrl(fileUrl));
                        }
                    }

                    if (doc.RootElement.TryGetProperty("Backend", out var backendSection) &&
                        backendSection.TryGetProperty("MmPerPx", out var mmPerPxElement) &&
                        mmPerPxElement.TryGetDouble(out var mmFromFile) &&
                        mmFromFile > 0)
                    {
                        DefaultMmPerPx = mmFromFile;
                    }
                }

                var mmEnv = Environment.GetEnvironmentVariable("BDI_MM_PER_PX") ??
                            Environment.GetEnvironmentVariable("BRAKEDISC_MM_PER_PX");
                if (!string.IsNullOrWhiteSpace(mmEnv) &&
                    double.TryParse(mmEnv, NumberStyles.Float, CultureInfo.InvariantCulture, out var mmValue) &&
                    mmValue > 0)
                {
                    DefaultMmPerPx = mmValue;
                }
            }
            catch
            {
                /* fallback */
            }

            ApplyBaseUrl(BaseUrl);
        }

        private static void ApplyBaseUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            BaseUrl = url.TrimEnd('/');
            try
            {
                s_client.BaseUrl = BaseUrl;
            }
            catch
            {
                // ignore invalid uri errors; BaseUrl still exposed for legacy usage
            }
        }

        private static string NormalizeBaseUrl(string value)
        {
            var trimmed = value.Trim();
            if (!trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = "http://" + trimmed.TrimStart('/');
            }

            return trimmed.TrimEnd('/');
        }

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
            if (roi == null) return "DefaultRole";
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
            if (roi == null) return "ROI";

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

        public static async Task<InferResult> InferAsync(InferRequest request, Action<string>? log = null)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var sw = Stopwatch.StartNew();
            try
            {
                var backendResult = await s_client
                    .InferAsync(
                        request.RoleId,
                        request.RoiId,
                        request.MmPerPx,
                        request.ImageBytes,
                        string.IsNullOrWhiteSpace(request.FileName) ? "roi.png" : request.FileName,
                        request.ShapeJson)
                    .ConfigureAwait(false);

                sw.Stop();

                var result = new InferResult
                {
                    score = backendResult.score,
                    threshold = backendResult.threshold,
                    heatmap_png_base64 = backendResult.heatmap_png_base64,
                    regions = backendResult.regions?.Select(r => new InferRegion
                    {
                        x = r.x,
                        y = r.y,
                        w = r.w,
                        h = r.h,
                        area_px = r.area_px,
                        area_mm2 = r.area_mm2,
                        bbox = null
                    }).ToArray(),
                    token_shape = null,
                    @params = null,
                    role_id = request.RoleId,
                    roi_id = request.RoiId
                };

                log?.Invoke($"[infer] role={request.RoleId} roi={request.RoiId} score={result.score:0.###} thr={(result.threshold ?? 0):0.###} regions={(result.regions?.Length ?? 0)} dt={sw.ElapsedMilliseconds}ms");
                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                log?.Invoke($"[infer] EX: {ex.Message}");
                throw;
            }
        }

        public static async Task<(bool ok, InferResult? result, string? error)> InferAsync(
            string imagePathWin,
            RoiModel roi,
            Preset preset,
            Action<string>? log = null,
            string? roleId = null,
            string? roiId = null,
            double? mmPerPx = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(imagePathWin) || !File.Exists(imagePathWin))
                {
                    return (false, null, "image not found");
                }

                if (roi == null)
                {
                    return (false, null, "roi not defined");
                }

                if (!TryPrepareCanonicalRoi(imagePathWin, roi, out var payload, out var fileName, log) || payload == null)
                {
                    return (false, null, "roi export failed");
                }

                var resolvedRoleId = roleId ?? ResolveRoleId(roi);
                var resolvedRoiId = roiId ?? ResolveRoiId(roi);
                var resolvedMmPerPx = ResolveMmPerPx(preset, mmPerPx);

                var request = new InferRequest(resolvedRoleId, resolvedRoiId, resolvedMmPerPx, payload.PngBytes)
                {
                    FileName = fileName,
                    ShapeJson = payload.ShapeJson
                };

                var result = await InferAsync(request, log).ConfigureAwait(false);
                return (true, result, null);
            }
            catch (BackendBadRequestException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log?.Invoke("[infer] EX: " + ex.Message);
                return (false, null, ex.Message);
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
                if (roi == null)
                {
                    log?.Invoke("[infer] ROI null");
                    return false;
                }

                using var src = Cv2.ImRead(imagePathWin, ImreadModes.Unchanged);
                if (src.Empty())
                {
                    log?.Invoke("[infer] failed to load image");
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
            catch (Exception ex)
            {
                log?.Invoke("[infer] " + ex.Message);
                return false;
            }
        }

        public static bool TryCropToPng(string imagePathWin, RoiModel roi, out MemoryStream pngStream, out MemoryStream? maskStream, out string fileName, Action<string>? log = null)
        {
            pngStream = null;
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
            if (result < 0)
            {
                result = 0;
            }
            return result;
        }

        public static async Task<string> TrainStatusAsync()
        {
            var info = await s_client.GetHealthAsync().ConfigureAwait(false);
            if (info == null)
            {
                return "{\"status\":\"offline\"}";
            }

            return JsonSerializer.Serialize(info, JsonOptions);
        }
    }
}
