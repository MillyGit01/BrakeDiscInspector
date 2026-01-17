# Backend (FastAPI) reference

This document describes the backend in `backend/` as implemented in `backend/app.py` and related modules.

## Runtime overview
- **Server:** FastAPI, typically via `uvicorn backend.app:app`.
- **Pipeline:** PatchCore + DINOv2 ViT-S/14 (`features.py`, `patchcore.py`, `infer.py`).
- **Persistence:** `ModelStore` in `storage.py`.
- **Diagnostics:** structured JSONL logs (`diagnostics.py`). See `LOGGING.md`.

## Configuration
Configuration is resolved from environment variables and optionally `configs/app.yaml` (if PyYAML is installed). Defaults live in `backend/config.py` and are used by `backend/app.py`.

### Environment variables (current)
- **Server:**
  - `BDI_BACKEND_HOST` / `BDI_BACKEND_PORT` (legacy: `BRAKEDISC_BACKEND_HOST`, `BRAKEDISC_BACKEND_PORT`)
- **Storage:**
  - `BDI_MODELS_DIR` (legacy: `BRAKEDISC_MODELS_DIR`)
- **Training / inference:**
  - `BDI_CORESET_RATE`
  - `BDI_SCORE_PERCENTILE`
  - `BDI_AREA_MM2_THR`
  - `BDI_MIN_OK_SAMPLES`
  - `BDI_TRAIN_DATASET_ONLY`
- **Runtime constraints:**
  - `BDI_REQUIRE_CUDA` (default `1`; set to `0` for CPU-only)
  - `BDI_CACHE_MAX_ENTRIES` (per-worker in-memory cache size)
- **CORS:**
  - `BDI_CORS_ORIGINS` (legacy: `BRAKEDISC_CORS_ORIGINS`)
- **Logging:**
  - `BDI_GUI_LOG_DIR` (optional override for diagnostics log directory)

## Persistence layout (`ModelStore`)
`BDI_MODELS_DIR` defaults to `models/` relative to the backend.

```
<BDI_MODELS_DIR>/
  recipes/<recipe_id>/<model_key>/
    <base_name>.npz
    <base_name>_index.faiss
    <base_name>_calib.json
  recipes/<recipe_id>/datasets/<base_name>/
    ok/*.png
    ok/*.json
    ng/*.png
    ng/*.json
```

**Naming rules:**
- `recipe_id` is lowercased, validated by `^[a-z0-9][a-z0-9_-]{0,63}$`, and **must not** be `last`.
- `model_key` defaults to `roi_id` and is sanitized for filesystem use.
- `base_name` is `base64(role_id) + "__" + base64(roi_id)` using urlsafe base64 without padding.

**Legacy compatibility:**
- Datasets: `models/datasets/<role>/<roi>`.
- Memory/index/calibration: `models/<role>_<roi>.*` or `models/<role>/<roi>/...`.

## Endpoint behavior (summary)
See `docs/API_CONTRACTS.md` for exact request/response schemas.

- `GET /health`: returns `{status, device, model, version, request_id, recipe_id}`.
- `POST /fit_ok`:
  - Can train from uploaded images or from datasets (`use_dataset=true`).
  - When `BDI_TRAIN_DATASET_ONLY=1`, image uploads are rejected.
  - Enforces `BDI_MIN_OK_SAMPLES` when using datasets.
  - Locks `mm_per_px` per `recipe_id` and returns HTTP 409 on mismatches.
- `POST /calibrate_ng`: computes and stores threshold using OK/NG score arrays.
- `POST /infer`: runs inference on a single ROI crop; returns `score`, optional `threshold`, optional `heatmap_png_base64`, and `regions`.
- `POST /infer_dataset` / `POST /calibrate_dataset`: operate on backend datasets.
- `GET /manifest` and `GET /state`: report artifact availability and readiness.
- `/datasets/*` endpoints: upload, list, download, delete, and clear dataset files.

## Recipe id rules
- Reserved id: `last` (HTTP 400 if provided).
- Case-insensitive for storage; do **not** create two recipes with the same letters in different casing.

## Failure modes (common)
- **400**: missing memory, token grid mismatch, missing OK samples, invalid recipe id.
- **409**: `mm_per_px` mismatch for a recipe.
- **500**: unexpected server errors.

## Logging
The backend uses **structured JSONL diagnostics** (not stdout-only). See `LOGGING.md` for log locations and fields.
