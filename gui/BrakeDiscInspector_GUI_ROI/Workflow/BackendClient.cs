using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BrakeDiscInspector_GUI_ROI.Helpers;
using BrakeDiscInspector_GUI_ROI.Util;

namespace BrakeDiscInspector_GUI_ROI.Workflow
{
    /// <summary>
    /// BackendClient revisado: API igual, robustez en JSON, MIME y errores.
    /// </summary>
    public sealed class BackendClient
    {
        private const string InferImageFieldName = "image";

        private readonly HttpClient _httpClient;
        private readonly HttpClient _httpTrainClient;

        public string? RecipeId { get; set; }

        public readonly struct FitImage
        {
            public FitImage(byte[] bytes, string fileName, string? mediaType = null)
            {
                Bytes = bytes ?? Array.Empty<byte>();
                FileName = string.IsNullOrWhiteSpace(fileName) ? "sample.png" : fileName;
                MediaType = string.IsNullOrWhiteSpace(mediaType) ? null : mediaType;
            }

            public byte[] Bytes { get; }
            public string FileName { get; }
            public string? MediaType { get; }

            public bool HasContent => Bytes.Length > 0;
        }

        public BackendClient(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(120);
            _httpTrainClient = new HttpClient
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            BaseUrl = ResolveDefaultBaseUrl();
        }

        public string BaseUrl
        {
            get => _httpClient.BaseAddress?.ToString() ?? "";
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    _httpClient.BaseAddress = null;
                    _httpTrainClient.BaseAddress = null;
                    return;
                }

                var trimmed = value.TrimEnd('/');
                if (!trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    trimmed = "http://" + trimmed;
                }

                var uri = new Uri(trimmed + "/");
                _httpClient.BaseAddress = uri;
                _httpTrainClient.BaseAddress = uri;
            }
        }

        private static string ResolveDefaultBaseUrl()
        {
            string defaultUrl = "http://127.0.0.1:8000";

            string? envBaseUrl =
                Environment.GetEnvironmentVariable("BDI_BACKEND_BASEURL") ??
                Environment.GetEnvironmentVariable("BDI_BACKEND_BASE_URL") ??
                Environment.GetEnvironmentVariable("BDI_BACKEND_URL") ??
                Environment.GetEnvironmentVariable("BRAKEDISC_BACKEND_BASEURL") ??
                Environment.GetEnvironmentVariable("BRAKEDISC_BACKEND_BASE_URL") ??
                Environment.GetEnvironmentVariable("BRAKEDISC_BACKEND_URL");

            if (!string.IsNullOrWhiteSpace(envBaseUrl))
            {
                return envBaseUrl;
            }

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
                return $"{host}:{port}";
            }

            return defaultUrl;
        }

        // =========================
        //  Endpoints
        // =========================

        public async Task<bool> SupportsEndpointAsync(string relativePath, CancellationToken ct = default)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Options, relativePath);
                AddCommonHeaders(request);
                using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return false;
                }

                if (response.StatusCode == HttpStatusCode.MethodNotAllowed)
                {
                    return true;
                }

                if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 400)
                {
                    return true;
                }

                return response.StatusCode != HttpStatusCode.NotFound;
            }
            catch
            {
                return false;
            }
        }

        public async Task<FitOkResult> FitOkAsync(
            string roleId,
            string roiId,
            double mmPerPx,
            IEnumerable<string> okImagePaths,
            bool memoryFit = false,
            string? datasetPath = null,
            CancellationToken ct = default)
        {
            var images = new List<FitImage>();
            foreach (var path in okImagePaths)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    continue;
                }

                var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                images.Add(new FitImage(bytes, Path.GetFileName(path)));
            }

            return await FitOkAsync(roleId, roiId, mmPerPx, images, memoryFit, datasetPath, ct).ConfigureAwait(false);
        }

        public async Task<FitOkResult> FitOkAsync(
            string roleId,
            string roiId,
            double mmPerPx,
            IEnumerable<FitImage> okImages,
            bool memoryFit = false,
            string? datasetPath = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(roleId)) throw new ArgumentException("Role id required", nameof(roleId));
            if (string.IsNullOrWhiteSpace(roiId)) throw new ArgumentException("ROI id required", nameof(roiId));

            using var form = new MultipartFormDataContent();
            var effectiveRoleId = NormalizeRoleId(roleId, roiId);
            form.Add(new StringContent(effectiveRoleId), "role_id");
            form.Add(new StringContent(roiId), "roi_id");
            form.Add(new StringContent(mmPerPx.ToString(CultureInfo.InvariantCulture)), "mm_per_px");
            form.Add(new StringContent(memoryFit ? "true" : "false"), "memory_fit");
            form.Add(new StringContent(roiId), "model_key");

            bool hasImage = false;
            foreach (var image in okImages)
            {
                if (!image.HasContent)
                {
                    continue;
                }

                var content = new ByteArrayContent(image.Bytes);
                var mediaType = !string.IsNullOrWhiteSpace(image.MediaType)
                    ? image.MediaType
                    : GuessMediaType(image.FileName);
                content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
                form.Add(content, "images", image.FileName);
                hasImage = true;
            }

            if (!hasImage)
                throw new InvalidOperationException("No OK images were provided for training.");

            var baseUrl = _httpTrainClient.BaseAddress?.ToString().TrimEnd('/') ?? string.Empty;
            var url = string.IsNullOrWhiteSpace(baseUrl) ? "fit_ok" : $"{baseUrl}/fit_ok";
            GuiLog.Info($"[backend] Calling /fit_ok url='{url}' dataset='{datasetPath}'");

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "fit_ok")
                {
                    Content = form
                };
                AddCommonHeaders(request);
                using var response = await _httpTrainClient.SendAsync(request, ct).ConfigureAwait(false);
                GuiLog.Info($"[backend] /fit_ok response StatusCode={(int)response.StatusCode} Reason='{response.ReasonPhrase}'");
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"/fit_ok {response.StatusCode}: {body}");

                using var streamResp = new MemoryStream(Encoding.UTF8.GetBytes(body));
                var payload = await JsonSerializer.DeserializeAsync<FitOkResult>(streamResp, JsonOptions, ct).ConfigureAwait(false)
                              ?? throw new InvalidOperationException("Empty or invalid JSON from fit_ok endpoint.");

                return payload;
            }
            catch (HttpRequestException ex)
            {
                GuiLog.Error($"[backend] /fit_ok HttpRequestException: {ex.Message}");
                ShowBackendNotAvailableMessage("/fit_ok");
                throw;
            }
            catch (TaskCanceledException ex)
            {
                GuiLog.Error($"[backend] /fit_ok timeout: {ex.Message}");
                ShowBackendNotAvailableMessage("/fit_ok");
                throw;
            }
        }

        public async Task<FitOkResult> FitOkFromDatasetAsync(
            string roleId,
            string roiId,
            double mmPerPx,
            bool memoryFit = false,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(roleId)) throw new ArgumentException("Role id required", nameof(roleId));
            if (string.IsNullOrWhiteSpace(roiId)) throw new ArgumentException("ROI id required", nameof(roiId));

            using var form = new MultipartFormDataContent();
            var effectiveRoleId = NormalizeRoleId(roleId, roiId);
            form.Add(new StringContent(effectiveRoleId), "role_id");
            form.Add(new StringContent(roiId), "roi_id");
            form.Add(new StringContent(mmPerPx.ToString(CultureInfo.InvariantCulture)), "mm_per_px");
            form.Add(new StringContent(memoryFit ? "true" : "false"), "memory_fit");
            form.Add(new StringContent(roiId), "model_key");
            form.Add(new StringContent("true"), "use_dataset");

            using var request = new HttpRequestMessage(HttpMethod.Post, "fit_ok")
            {
                Content = form
            };
            AddCommonHeaders(request);
            using var response = await _httpTrainClient.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"/fit_ok {response.StatusCode}: {body}");

            using var streamResp = new MemoryStream(Encoding.UTF8.GetBytes(body));
            var payload = await JsonSerializer.DeserializeAsync<FitOkResult>(streamResp, JsonOptions, ct).ConfigureAwait(false)
                          ?? throw new InvalidOperationException("Empty or invalid JSON from fit_ok endpoint.");
            return payload;
        }

        public async Task UploadDatasetSampleAsync(
            string roleId,
            string roiId,
            bool isNg,
            byte[] pngBytes,
            string fileName,
            SampleMetadata meta,
            CancellationToken ct = default)
        {
            var endpoint = isNg ? "datasets/ng/upload" : "datasets/ok/upload";
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(roleId), "role_id");
            form.Add(new StringContent(roiId), "roi_id");

            var content = new ByteArrayContent(pngBytes ?? Array.Empty<byte>());
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(content, "images", string.IsNullOrWhiteSpace(fileName) ? "sample.png" : fileName);

            var metaJson = JsonSerializer.Serialize(meta, JsonOptions);
            form.Add(new StringContent(metaJson, Encoding.UTF8, "application/json"), "metas");

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = form
            };
            AddCommonHeaders(request);
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"/{endpoint} {response.StatusCode}: {body}");
            }
        }

        public async Task<DatasetListDto> GetDatasetListAsync(string roleId, string roiId, CancellationToken ct = default)
        {
            var url = $"datasets/list?role_id={Uri.EscapeDataString(roleId)}&roi_id={Uri.EscapeDataString(roiId)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddCommonHeaders(request);
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"/datasets/list {response.StatusCode}: {body}");
            }

            var data = JsonSerializer.Deserialize<DatasetListResponse>(body, JsonOptions)
                       ?? new DatasetListResponse();
            return new DatasetListDto
            {
                ok = data.classes?.ok ?? new DatasetClassDto(),
                ng = data.classes?.ng ?? new DatasetClassDto()
            };
        }

        public async Task<byte[]> DownloadDatasetFileAsync(string roleId, string roiId, bool isNg, string filename, CancellationToken ct = default)
        {
            var label = isNg ? "ng" : "ok";
            var url = $"datasets/file?role_id={Uri.EscapeDataString(roleId)}&roi_id={Uri.EscapeDataString(roiId)}&label={label}&filename={Uri.EscapeDataString(filename)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddCommonHeaders(request);
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new HttpRequestException($"/datasets/file {response.StatusCode}: {body}");
            }
            return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }

        public async Task DeleteDatasetFileAsync(string roleId, string roiId, bool isNg, string filename, CancellationToken ct = default)
        {
            var label = isNg ? "ng" : "ok";
            var url = $"datasets/file?role_id={Uri.EscapeDataString(roleId)}&roi_id={Uri.EscapeDataString(roiId)}&label={label}&filename={Uri.EscapeDataString(filename)}";
            using var request = new HttpRequestMessage(HttpMethod.Delete, url);
            AddCommonHeaders(request);
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"/datasets/file {response.StatusCode}: {body}");
            }
        }

        public async Task<SampleMetadata> GetDatasetMetaAsync(string roleId, string roiId, bool isNg, string filename, CancellationToken ct = default)
        {
            var label = isNg ? "ng" : "ok";
            var url = $"datasets/meta?role_id={Uri.EscapeDataString(roleId)}&roi_id={Uri.EscapeDataString(roiId)}&label={label}&filename={Uri.EscapeDataString(filename)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddCommonHeaders(request);
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"/datasets/meta {response.StatusCode}: {body}");
            }

            return JsonSerializer.Deserialize<SampleMetadata>(body, JsonOptions)
                   ?? new SampleMetadata();
        }

        public async Task<CalibrateDatasetResponse> CalibrateDatasetAsync(string roleId, string roiId, double? defaultMmPerPx, CancellationToken ct = default)
        {
            var payload = new Dictionary<string, object?>
            {
                ["role_id"] = roleId,
                ["roi_id"] = roiId,
                ["default_mm_per_px"] = defaultMmPerPx
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "calibrate_dataset")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
            };
            AddCommonHeaders(request);
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"/calibrate_dataset {response.StatusCode}: {body}");
            }

            return JsonSerializer.Deserialize<CalibrateDatasetResponse>(body, JsonOptions)
                   ?? new CalibrateDatasetResponse();
        }

        public async Task<InferDatasetResponse> InferDatasetAsync(string roleId, string roiId, bool includeHeatmap, double? defaultMmPerPx, CancellationToken ct = default)
        {
            var payload = new Dictionary<string, object?>
            {
                ["role_id"] = roleId,
                ["roi_id"] = roiId,
                ["include_heatmap"] = includeHeatmap,
                ["default_mm_per_px"] = defaultMmPerPx
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "infer_dataset")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
            };
            AddCommonHeaders(request);
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"/infer_dataset {response.StatusCode}: {body}");
            }

            return JsonSerializer.Deserialize<InferDatasetResponse>(body, JsonOptions)
                   ?? new InferDatasetResponse();
        }

        public async Task<CalibResult> CalibrateAsync(
            string roleId,
            string roiId,
            double mmPerPx,
            IEnumerable<double> okScores,
            IEnumerable<double>? ngScores = null,
            double areaMm2Thr = 1.0,
            int scorePercentile = 99,
            CancellationToken ct = default)
        {
            var effectiveRoleId = NormalizeRoleId(roleId, roiId);
            var body = new
            {
                role_id = effectiveRoleId,
                roi_id = roiId,
                model_key = roiId,
                mm_per_px = mmPerPx,
                ok_scores = okScores,
                ng_scores = ngScores,
                area_mm2_thr = areaMm2Thr,
                score_percentile = scorePercentile
            };

            using var content = JsonContent.Create(body, options: JsonOptions);
            using var request = new HttpRequestMessage(HttpMethod.Post, "calibrate_ng")
            {
                Content = content
            };
            AddCommonHeaders(request);
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"/calibrate_ng {response.StatusCode}: {raw}");

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
            var payload = await JsonSerializer.DeserializeAsync<CalibResult>(stream, JsonOptions, ct).ConfigureAwait(false)
                          ?? throw new InvalidOperationException("Empty or invalid JSON from calibrate_ng endpoint.");

            // Fallback por si backend devuelve null u omite el campo (evita romper UI)
            payload.threshold ??= 0.5;

            return payload;
        }

        // --------- Inferencias (sobrecargas) ---------

        public async Task<InferResult> InferAsync(
            string roleId,
            string roiId,
            double mmPerPx,
            string imagePath,
            string? shapeJson = null,
            CancellationToken ct = default)
        {
            if (!File.Exists(imagePath))
                throw new FileNotFoundException("Inference image not found", imagePath);

            var bytes = await File.ReadAllBytesAsync(imagePath, ct).ConfigureAwait(false);
            return await _InferMultipartAsync(roleId, roiId, mmPerPx, bytes, Path.GetFileName(imagePath), shapeJson, ct)
                   .ConfigureAwait(false);
        }

        public async Task<InferResult> InferAsync(
            string roleId,
            string roiId,
            double mmPerPx,
            Stream imageStream,
            string fileName,
            string? shapeJson = null,
            CancellationToken ct = default)
        {
            if (imageStream == null) throw new ArgumentNullException(nameof(imageStream));
            var bytes = await ReadAllBytesAsync(imageStream, ct).ConfigureAwait(false);
            return await _InferMultipartAsync(roleId, roiId, mmPerPx, bytes, fileName, shapeJson, ct)
                   .ConfigureAwait(false);
        }

        public async Task<InferResult> InferAsync(
            string roleId,
            string roiId,
            double mmPerPx,
            byte[] imageBytes,
            string fileName,
            string? shapeJson = null,
            CancellationToken ct = default)
        {
            if (imageBytes == null || imageBytes.Length == 0)
                throw new ArgumentException("Image bytes are required", nameof(imageBytes));

            return await _InferMultipartAsync(roleId, roiId, mmPerPx, imageBytes, fileName, shapeJson, ct)
                   .ConfigureAwait(false);
        }

        // Core común para inferencias (evita duplicar lógica)
        private async Task<InferResult> _InferMultipartAsync(
            string roleId,
            string roiId,
            double mmPerPx,
            byte[] imageBytes,
            string fileName,
            string? shapeJson,
            CancellationToken ct)
        {
            using var form = new MultipartFormDataContent();
            var effectiveRoleId = NormalizeRoleId(roleId, roiId);
            form.Add(new StringContent(effectiveRoleId), "role_id");
            form.Add(new StringContent(roiId), "roi_id");
            form.Add(new StringContent(mmPerPx.ToString(CultureInfo.InvariantCulture)), "mm_per_px");

            if (!string.IsNullOrWhiteSpace(roiId))
            {
                form.Add(new StringContent(roiId), "model_key");
            }

            var content = new ByteArrayContent(imageBytes);
            var mediaType = GuessMediaType(fileName);
            if (mediaType == "application/octet-stream")
            {
                mediaType = "image/png";
            }
            content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            var safeFileName = string.IsNullOrWhiteSpace(fileName) ? "roi.png" : fileName;
            form.Add(content, InferImageFieldName, safeFileName);

            if (!string.IsNullOrWhiteSpace(shapeJson))
            {
                // Texto plano para FastAPI Form(str)
                form.Add(new StringContent(shapeJson, Encoding.UTF8), "shape");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "infer")
            {
                Content = form
            };
            AddCommonHeaders(request);
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    var detail = ExtractDetail(raw);
                    if (IsMemoryNotFitted(detail))
                    {
                        throw new BackendMemoryNotFittedException(detail);
                    }

                    throw new BackendBadRequestException("Backend returned 400", detail);
                }

                throw new HttpRequestException($"/infer {response.StatusCode}: {raw}");
            }

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
            var payload = await JsonSerializer.DeserializeAsync<InferResult>(stream, JsonOptions, ct).ConfigureAwait(false)
                          ?? throw new InvalidOperationException("Empty or invalid JSON from infer endpoint.");

            // Fallback: si el backend devuelve threshold null/omitido
            payload.threshold ??= 0.5;

            return payload;
        }

        private static string NormalizeRoleId(string roleId, string? roiId)
        {
            if (!string.IsNullOrWhiteSpace(roiId))
            {
                var trimmed = roiId.Trim();
                if (trimmed.StartsWith("inspection", StringComparison.OrdinalIgnoreCase))
                {
                    return "Inspection";
                }

                if (trimmed.IndexOf("master1", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return "Master1";
                }

                if (trimmed.IndexOf("master2", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return "Master2";
                }
            }

            if (!string.IsNullOrWhiteSpace(roleId))
            {
                return roleId;
            }

            return "Inspection";
        }

        public async Task<HealthInfo?> GetHealthAsync(CancellationToken ct = default)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "health");
                AddCommonHeaders(request);
                using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;

                var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
                return await JsonSerializer.DeserializeAsync<HealthInfo>(stream, JsonOptions, ct).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        public async Task<bool> EnsureFittedAsync(
            string roleId,
            string roiId,
            double mmPerPx,
            Func<CancellationToken, Task<IReadOnlyList<FitImage>>>? sampleProvider = null,
            bool memoryFit = false,
            CancellationToken ct = default)
        {
            try
            {
                var fitted = await QueryFitStateAsync(roleId, roiId, ct).ConfigureAwait(false);
                if (fitted == true)
                {
                    return true;
                }
            }
            catch
            {
                // ignore /state failures and fallback to fit
            }

            if (sampleProvider == null)
            {
                return false;
            }

            IReadOnlyList<FitImage> samples;
            try
            {
                samples = await sampleProvider(ct).ConfigureAwait(false);
            }
            catch
            {
                return false;
            }

            if (samples == null || samples.Count == 0)
            {
                return false;
            }

            try
            {
                await FitOkAsync(roleId, roiId, mmPerPx, samples, memoryFit, datasetPath: null, ct).ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // =========================
        // Utilidades
        // =========================

        private async Task<bool?> QueryFitStateAsync(string roleId, string roiId, CancellationToken ct)
        {
            try
            {
                var query = $"state?role_id={Uri.EscapeDataString(roleId)}&roi_id={Uri.EscapeDataString(roiId)}";
                using var request = new HttpRequestMessage(HttpMethod.Get, query);
                AddCommonHeaders(request);
                using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return null;
                }

                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("fitted", out var fittedEl) && fittedEl.ValueKind == JsonValueKind.True)
                    {
                        return true;
                    }

                    if (root.TryGetProperty("memory_fitted", out var memEl) && memEl.ValueKind == JsonValueKind.True)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private void AddCommonHeaders(HttpRequestMessage request)
        {
            var requestId = Guid.NewGuid().ToString();
            request.Headers.Remove("X-Request-Id");
            request.Headers.TryAddWithoutValidation("X-Request-Id", requestId);

            if (!string.IsNullOrWhiteSpace(RecipeId) &&
                !string.Equals(RecipeId, "last", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Remove("X-Recipe-Id");
                request.Headers.TryAddWithoutValidation("X-Recipe-Id", RecipeId);
            }
            else
            {
                // No enviar recipe header si no hay recipe o si es reservado ("last")
                request.Headers.Remove("X-Recipe-Id");
            }
        }

        private static string? ExtractDetail(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("error", out var errorEl))
                    {
                        var error = TryReadString(errorEl);
                        if (!string.IsNullOrWhiteSpace(error))
                        {
                            return error;
                        }
                    }

                    if (root.TryGetProperty("detail", out var detailEl))
                    {
                        var detail = TryReadString(detailEl);
                        if (!string.IsNullOrWhiteSpace(detail))
                        {
                            return detail;
                        }
                    }
                }
            }
            catch
            {
                // ignore parse errors
            }

            return raw;

            static string? TryReadString(JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    return element.GetString();
                }

                if (element.ValueKind == JsonValueKind.Object)
                {
                    if (element.TryGetProperty("message", out var messageEl) && messageEl.ValueKind == JsonValueKind.String)
                    {
                        return messageEl.GetString();
                    }

                    if (element.TryGetProperty("detail", out var detailEl) && detailEl.ValueKind == JsonValueKind.String)
                    {
                        return detailEl.GetString();
                    }
                }

                return null;
            }
        }

        private static bool IsMemoryNotFitted(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            var t = s.ToLowerInvariant();
            if (t.Contains("memoria no encontrada")) return true;
            if (t.Contains("ejecuta /fit_ok") || t.Contains("fit_ok primero")) return true;
            if (t.Contains("memory not found")) return true;
            if (t.Contains("run fit first") || t.Contains("run /fit_ok first")) return true;
            if (t.Contains("no embeddings") || t.Contains("embeddings not found")) return true;
            if (t.Contains("not fitted") || t.Contains("missing fit")) return true;
            return false;
        }

        private static void ShowBackendNotAvailableMessage(string endpoint)
        {
            try
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    MessageBox.Show($"Backend {endpoint} endpoint is not available.");
                });
            }
            catch
            {
                // ignore UI failures
            }
        }

        private static string GuessMediaType(string pathOrName)
        {
            string ext = Path.GetExtension(pathOrName)?.ToLowerInvariant() ?? "";
            return ext switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".bmp" => "image/bmp",
                ".tif" or ".tiff" => "image/tiff",
                _ => "application/octet-stream"
            };
        }

        private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken ct)
        {
            if (stream is MemoryStream memoryStream)
            {
                if (memoryStream.CanSeek)
                {
                    memoryStream.Seek(0, SeekOrigin.Begin);
                }
                return memoryStream.ToArray();
            }

            using var ms = new MemoryStream();
            if (stream.CanSeek)
            {
                stream.Seek(0, SeekOrigin.Begin);
            }
            await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
            return ms.ToArray();
        }

        // Opciones JSON robustas
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
                           | JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
    }

    // ============ DTOs (mismos nombres/campos; threshold nullable) ============

    public sealed class FitOkResult
    {
        public int n_embeddings { get; set; }
        public int coreset_size { get; set; }
        public int[]? token_shape { get; set; }
    }

    public sealed class CalibResult
    {
        public double? threshold { get; set; }      // << ahora nullable
        public double ok_mean { get; set; }
        public double ng_mean { get; set; }
        public double score_percentile { get; set; }
        public double area_mm2_thr { get; set; }
    }

    public sealed class InferResult
    {
        public double score { get; set; }
        public double? threshold { get; set; }      // << ahora nullable
        public string? heatmap_png_base64 { get; set; }
        public Region[]? regions { get; set; }
        public string? request_id { get; set; }
        public string? recipe_id { get; set; }
    }

    public sealed class Region
    {
        public double x { get; set; }
        public double y { get; set; }
        public double w { get; set; }
        public double h { get; set; }
        public double area_px { get; set; }
        public double area_mm2 { get; set; }
        public double[]? bbox { get; set; }
    }

    public sealed class HealthInfo
    {
        public string? status { get; set; }
        public string? device { get; set; }
        public string? model { get; set; }
        public string? version { get; set; }
        public string? reason { get; set; }
        public string? request_id { get; set; }
        public string? recipe_id { get; set; }
    }
}
