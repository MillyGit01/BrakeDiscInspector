# API contracts

This document describes the backend HTTP contracts used by the GUI, and the recipe-aware behavior implemented in the FastAPI backend (`backend/app.py`).

## Common headers

- `X-Request-Id` (optional): opaque request correlation id. If missing, the backend generates one.
- `X-Recipe-Id` (optional): recipe context for recipe-aware artifacts (memory, index, calibration, datasets).

### Recipe resolution precedence

For endpoints that accept `recipe_id` in the payload/query/form:

1. Explicit `recipe_id` field/query parameter (if provided), else
2. Header `X-Recipe-Id` (standard HTTP header; case-insensitive match), else
3. `"default"`.

### Reserved recipe ids

The following values are reserved and MUST NOT be used as recipes by clients:

- `last` — reserved for GUI layout state (e.g., `last.layout.json`). If provided as `X-Recipe-Id` or `recipe_id`, the backend will return **HTTP 400**.

### Artifact fallback (recipe -> default)

For recipe-aware artifacts (`memory`, `index`, `calib`, `datasets`), when `recipe_id != "default"` and the artifact does **not** exist for that recipe, the backend falls back to `"default"` (and finally to legacy layouts when applicable).

This allows new recipes to inherit baseline models/calibration/datasets until they are trained.

---

## `GET /health`

- **Request:** none (optional headers above).
- **Response (200):**
```json
{
  "status": "ok",
  "device": "cuda",
  "model": "vit_small_patch14_dinov2.lvd142m",
  "version": "0.1.0",
  "request_id": "a9d1f7d7-6b94-4e1c-9b6b-acde0a0b9c0d",
  "recipe_id": "default",
  "reason": "cuda_not_available"
}
```
- `reason` is only present when CUDA is unavailable.

---

## `POST /fit_ok`

Fits/updates PatchCore memory for a given `(role_id, roi_id)`.

- **Content type:** `multipart/form-data`.
- **Fields:**
  - `role_id` (string, required)
  - `roi_id` (string, required)
  - `mm_per_px` (float, required) – currently informational; stored in calibration.
  - `images` (file[], required) – ROI crops (PNG/JPG).
  - `memory_fit` (bool-string, optional) – when `true`, disables coreset subsampling (`coreset_rate = 1.0`).
  - `recipe_id` (string, optional) – overrides recipe context.
  - `model_key` (string, optional) – logical model slot under the recipe. If omitted, defaults to `roi_id`.
- **Response (200):**
```json
{
  "n_embeddings": 1234,
  "coreset_size": 120,
  "token_shape": [32, 32],
  "coreset_rate_requested": 0.10,
  "coreset_rate_applied": 0.0972,
  "request_id": "…",
  "recipe_id": "default"
}
```
- **Error responses:**
  - `400` for malformed requests (e.g., no images, token grid mismatch across images).
  - `500` for unexpected failures.

---

## `POST /calibrate_ng`

Computes and stores a calibration JSON for a given `(role_id, roi_id)`.

- **Content type:** `application/json`.
- **Body:**
  - `role_id` (string, required)
  - `roi_id` (string, required)
  - `model_key` (string, optional) – defaults to `roi_id`
  - `recipe_id` (string, optional)
  - `mm_per_px` (float, optional, default `0.2`)
  - `ok_scores` (float[], required)
  - `ng_scores` (float[] | null, optional) – **null is accepted** and treated as "no NG".
  - `score_percentile` (int, optional, default from config, typically 99)
  - `area_mm2_thr` (float, optional, default from config)
- **Response (200):**
```json
{
  "threshold": 12.34,
  "area_mm2_thr": 1.0,
  "score_percentile": 99,
  "mm_per_px": 0.2,
  "recipe_id": "default",
  "model_key": "Pattern",
  "request_id": "…"
}
```
- **Error responses:**
  - `400` when required fields are missing or values are invalid.
  - `500` for unexpected failures.

---

## `POST /infer`

Runs inference on a single ROI crop.

- **Content type:** `multipart/form-data`.
- **Fields:**
  - `role_id` (string, required)
  - `roi_id` (string, required)
  - `mm_per_px` (float, required)
  - `image` (file, required) – ROI crop (PNG/JPG)
  - `shape` (string, optional) – JSON string for ROI shape/mask (see Shape schema).
  - `recipe_id` (string, optional)
  - `model_key` (string, optional) – defaults to `roi_id`
- **Response (200):**
```json
{
  "score": 0.123,
  "threshold": 12.34,
  "token_shape": [32, 32],
  "heatmap_png_base64": "iVBORw0K…",
  "regions": [
    {
      "bbox": [10, 20, 30, 40],
      "area_px": 123.0,
      "area_mm2": 4.92,
      "contour": [[…],[…]]
    }
  ],
  "request_id": "…",
  "recipe_id": "default"
}
```
- `regions` are provided for visualization only.
- **Error responses:**
  - `400` if memory is missing, token grid mismatches, or inputs are invalid.
  - `500` for unexpected failures.

---

## `GET /manifest`

Aggregates readiness information for a given `(role_id, roi_id)` in a recipe-aware manner.

- **Query params:**
  - `role_id` (string, required)
  - `roi_id` (string, required)
  - `recipe_id` (string, optional)
  - `model_key` (string, optional; defaults to `roi_id`)
- **Response (200):**
```json
{
  "role_id": "Inspection",
  "roi_id": "inspection-1",
  "recipe_id": "default",
  "model_key": "inspection-1",
  "memory": true,
  "calib": { "threshold": 12.34, "area_mm2_thr": 1.0, "score_percentile": 99, "mm_per_px": 0.2 },
  "datasets": { "role_id": "Inspection", "roi_id": "inspection-1", "classes": { "ok": { "count": 10, "files": [...] } } },
  "request_id": "…"
}
```

---

## `GET /state`

Lightweight endpoint used by the GUI as an optional optimization (avoid re-fitting if already fitted).

- **Query params:** `role_id`, `roi_id` (required), `recipe_id` (optional), `model_key` (optional).
- **Response (200):**
```json
{
  "status": "ok",
  "memory_fitted": true,
  "calib_present": false,
  "request_id": "…",
  "recipe_id": "default",
  "role_id": "Inspection",
  "roi_id": "inspection-1",
  "model_key": "inspection-1"
}
```

---

## Dataset endpoints

Datasets are stored recipe-aware under:
`models/recipes/<recipe_id>/datasets/<base64(role_id + '|' + roi_id)>/<ok|ng>/*`

Read/list operations apply recipe fallback to `"default"` when the recipe-specific dataset folder does not exist.

### `POST /datasets/ok/upload` and `POST /datasets/ng/upload`

- **Content type:** `multipart/form-data`.
- **Fields:**
  - `role_id` (string, required)
  - `roi_id` (string, required)
  - `images` (file[], required)
  - `recipe_id` (string, optional)
- **Response (200):**
```json
{ "status": "ok", "saved": ["20250101-120000-000001.png"], "request_id": "…", "recipe_id": "default" }
```

### `GET /datasets/list`

- **Query params:** `role_id`, `roi_id` (required), `recipe_id` (optional).
- **Response (200):**
```json
{
  "role_id": "Inspection",
  "roi_id": "inspection-1",
  "classes": { "ok": { "count": 10, "files": [...] }, "ng": { "count": 2, "files": [...] } },
  "request_id": "…",
  "recipe_id": "default"
}
```

### `DELETE /datasets/file`

- **Query params:** `role_id`, `roi_id`, `label` (`ok|ng`), `filename` (required), `recipe_id` (optional).
- **Response (200):**
```json
{ "deleted": true, "filename": "…", "request_id": "…", "recipe_id": "default" }
```

### `DELETE /datasets/clear`

- **Query params:** `role_id`, `roi_id`, `label` (`ok|ng`), `recipe_id` (optional).
- **Response (200):**
```json
{ "cleared": 10, "label": "ok", "request_id": "…", "recipe_id": "default" }
```

---

## Shape JSON schema

When present, the GUI sends the ROI mask to the backend in canonical ROI coordinates (448×448). The backend recognizes three shapes (see `backend/roi_mask.py`):

```json
{ "kind": "rect", "x": 0, "y": 0, "w": 448, "h": 448 }
{ "kind": "circle", "cx": 224, "cy": 224, "r": 210 }
{ "kind": "annulus", "cx": 224, "cy": 224, "r": 210, "r_inner": 150 }
```

Unknown kinds fall back to a full mask.

## Recipe id normalization (implementation note)
- The backend normalizes recipe ids (sanitization and lowercase) before using them as on-disk keys under `BDI_MODELS_DIR/recipes/<recipe_id>/...`.
- Clients should treat recipe ids as case-insensitive; do not create two recipes that differ only by casing.
- `last` is reserved and invalid (HTTP 400) when provided via header or payload.
