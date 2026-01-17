# API contracts

This document describes the **current FastAPI HTTP contracts** implemented in `backend/app.py`.

## Common headers
- `X-Request-Id` (optional): client-provided request correlation id.
- `X-Recipe-Id` (optional): recipe context for artifact routing.

### Recipe resolution precedence
For endpoints that accept `recipe_id` in payload/query/form:
1. Explicit `recipe_id` in payload/query/form (if provided).
2. `X-Recipe-Id` header.
3. `default`.

### Reserved recipe ids
- `last` is **reserved** and rejected with HTTP 400.

### Artifact fallback (recipe → default → legacy)
When `recipe_id != "default"` and a recipe-specific artifact does not exist, the backend attempts:
1. the requested recipe,
2. the `default` recipe (for memory/index/calib and datasets),
3. legacy layouts (flat or legacy directories).

---

## `GET /health`
**Response (200):**
```json
{
  "status": "ok",
  "device": "cuda",
  "model": "vit_small_patch14_dinov2.lvd142m",
  "version": "0.1.0",
  "request_id": "...",
  "recipe_id": "default",
  "reason": "cuda_not_available"
}
```
`reason` is only present when CUDA is unavailable.

---

## `POST /fit_ok`
Fits/updates PatchCore memory for a given `(role_id, roi_id)`.

- **Content type:** `multipart/form-data`
- **Fields:**
  - `role_id` (string, required)
  - `roi_id` (string, required)
  - `mm_per_px` (float, required)
  - `images` (file[], optional) — required when `use_dataset=false`
  - `memory_fit` (bool-string, optional) — set `true` to use full coreset
  - `use_dataset` (bool-string, optional) — train from backend dataset
  - `recipe_id` (string, optional)
  - `model_key` (string, optional; defaults to `roi_id`)

**Notes:**
- If `BDI_TRAIN_DATASET_ONLY=1`, the backend rejects image uploads and requires `use_dataset=true`.
- When `use_dataset=true`, `BDI_MIN_OK_SAMPLES` is enforced.
- `mm_per_px` is locked per `recipe_id`; mismatches return HTTP 409.

**Response (200):**
```json
{
  "n_embeddings": 1234,
  "coreset_size": 120,
  "token_shape": [32, 32],
  "coreset_rate_requested": 0.1,
  "coreset_rate_applied": 0.097,
  "request_id": "...",
  "recipe_id": "default"
}
```

---

## `POST /calibrate_ng`
Computes and stores a calibration threshold for `(role_id, roi_id)`.

- **Content type:** `application/json`
- **Body:**
  - `role_id` (string, required)
  - `roi_id` (string, required)
  - `model_key` (string, optional; defaults to `roi_id`)
  - `recipe_id` (string, optional)
  - `mm_per_px` (float, optional, default `0.2`)
  - `ok_scores` (float[], required)
  - `ng_scores` (float[] or null, optional)
  - `score_percentile` (int, optional)
  - `area_mm2_thr` (float, optional)

**Response (200):**
```json
{
  "threshold": 12.34,
  "ok_mean": 0.12,
  "ng_mean": 1.23,
  "p99_ok": 0.98,
  "p5_ng": 0.05,
  "mm_per_px": 0.2,
  "area_mm2_thr": 1.0,
  "score_percentile": 99,
  "request_id": "...",
  "recipe_id": "default"
}
```

---

## `POST /infer`
Runs inference on a single ROI crop.

- **Content type:** `multipart/form-data`
- **Fields:**
  - `role_id` (string, required)
  - `roi_id` (string, required)
  - `mm_per_px` (float, required)
  - `image` (file, required)
  - `shape` (string, optional) — JSON shape mask in canonical ROI coordinates
  - `recipe_id` (string, optional)
  - `model_key` (string, optional; defaults to `roi_id`)

**Response (200):**
```json
{
  "score": 0.123,
  "threshold": 12.34,
  "token_shape": [32, 32],
  "heatmap_png_base64": "iVBORw0K...",
  "regions": [{"bbox": [10, 20, 30, 40], "area_px": 123.0, "area_mm2": 4.92}],
  "request_id": "...",
  "recipe_id": "default"
}
```

**Errors:**
- `400` when memory is missing or token grid mismatches.
- `409` when `mm_per_px` mismatches the recipe lock.

---

## `GET /manifest`
Reports current memory/calibration/dataset status.

- **Query params:** `role_id`, `roi_id` (required), `recipe_id`/`model_key` (optional).
- **Response (200):** contains `memory` (bool), `calib` (object or null), and dataset summary.

---

## `GET /state`
Lightweight readiness endpoint.

- **Query params:** `role_id`, `roi_id` (required), `recipe_id`/`model_key` (optional).
- **Response (200):**
```json
{
  "status": "ok",
  "memory_fitted": true,
  "calib_present": false,
  "request_id": "...",
  "recipe_id": "default",
  "role_id": "Inspection",
  "roi_id": "inspection-1",
  "model_key": "inspection-1"
}
```

---

## Dataset endpoints

### Storage layout
Datasets are stored under:
```
<BDI_MODELS_DIR>/recipes/<recipe_id>/datasets/<base_name>/{ok,ng}/*
```
`base_name` is `base64(role_id) + "__" + base64(roi_id)` (urlsafe, no padding).

### `POST /datasets/ok/upload` and `POST /datasets/ng/upload`
- **Content type:** `multipart/form-data`
- **Fields:**
  - `role_id`, `roi_id` (required)
  - `images` (file[], required)
  - `metas` (string[], optional) — JSON strings aligned to `images`
  - `recipe_id` (string, optional)

**Notes:**
- If `metas[].mm_per_px` is present, it is validated and locked per recipe (HTTP 409 on mismatch).

### `GET /datasets/list`
- **Query params:** `role_id`, `roi_id` (required), `recipe_id` (optional).
- **Response:** dataset classes, counts, and file lists.

### `GET /datasets/file`
- **Query params:** `role_id`, `roi_id`, `label` (`ok|ng`), `filename`, `recipe_id` (optional).
- **Response:** image bytes.

### `GET /datasets/meta`
- **Query params:** `role_id`, `roi_id`, `label`, `filename`, `recipe_id` (optional).
- **Response:** per-sample metadata JSON.

### `DELETE /datasets/file`
- **Query params:** `role_id`, `roi_id`, `label`, `filename`, `recipe_id` (optional).
- **Response:** `{ "deleted": true|false, ... }`.

### `DELETE /datasets/clear`
- **Query params:** `role_id`, `roi_id`, `label` (`ok|ng`), `recipe_id` (optional).
- **Response:** `{ "cleared": <count>, ... }`.

---

## `POST /infer_dataset`
Runs inference over backend dataset files.

- **Content type:** `application/json`
- **Body:**
  - `role_id`, `roi_id` (required)
  - `recipe_id`, `model_key` (optional)
  - `labels` (optional; default `[
    "ok",
    "ng"
  ]`)
  - `include_heatmap` (bool, optional; default `false`)
  - `default_mm_per_px` (float, optional)

**Response:**
```json
{
  "status": "ok",
  "n_total": 3,
  "n_errors": 0,
  "items": [{"label": "ok", "score": 1.23, "error": null}]
}
```

---

## `POST /calibrate_dataset`
Calibrates threshold using backend datasets.

- **Content type:** `application/json`
- **Body:**
  - `role_id`, `roi_id` (required)
  - `recipe_id`, `model_key` (optional)
  - `score_percentile`, `area_mm2_thr` (optional)
  - `default_mm_per_px` (optional)
  - `require_ng` (bool, optional; default `true`)

**Response:**
```json
{
  "status": "ok",
  "threshold": 0.9,
  "n_ok": 10,
  "n_ng": 2,
  "request_id": "...",
  "recipe_id": "default"
}
```

---

## Shape JSON schema
The GUI sends a `shape` JSON string in **canonical ROI coordinates** matching the uploaded ROI crop. Supported shapes:
```json
{ "kind": "rect", "x": 0, "y": 0, "w": 448, "h": 448 }
{ "kind": "circle", "cx": 224, "cy": 224, "r": 210 }
{ "kind": "annulus", "cx": 224, "cy": 224, "r": 210, "r_inner": 150 }
```
**Important:** these coordinates must match the ROI crop size you send (do **not** assume a fixed 448×448 unless your GUI always exports that size).
