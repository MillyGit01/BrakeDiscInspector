# Backend (FastAPI) reference

The backend located in `backend/` exposes the same endpoints implemented in `backend/app.py`. This document describes what the code actually does.

## Runtime overview
- **Process:** FastAPI app served via `uvicorn backend.app:app`. Dependencies are listed in `backend/requirements.txt`; the provided Dockerfile already includes CUDA 12.1 + PyTorch 2.2.2.
- **Modules:**
  - `app.py`: HTTP layer, request validation, logging (`slog`).
  - `features.py`: wraps the DINOv2 ViT-S/14 extractor (`DinoV2Features`).
  - `patchcore.py`: PatchCore memory, coreset building, FAISS integration.
  - `infer.py`: `InferenceEngine` running PatchCore, applying Gaussian blur, ROI masks (`roi_mask.py`) and generating regions.
  - `calib.py`: `choose_threshold` helper used by `/calibrate_ng`.
  - `storage.py`: `ModelStore` for `.npz` embeddings, FAISS blobs and calibration files.

## Configuration
- Environment variables prefixed with `BDI_` (or legacy `BRAKEDISC_`) configure the server (see `_env_var` in `app.py` and `backend/config.py`). Relevant keys:
  - `BDI_BACKEND_HOST`, `BDI_BACKEND_PORT`: default `127.0.0.1:8000`.
  - `BDI_MODELS_DIR`: directory for embeddings/calibration (default `models`).
  - `BDI_CORESET_RATE`, `BDI_SCORE_PERCENTILE`, `BDI_AREA_MM2_THR`: override inference defaults.
  - `BDI_CORS_ORIGINS`: comma-separated list for CORS (`*` by default).
  - `BDI_BACKEND_BASEURL` is consumed by the GUI client, not the server.
- Optional `configs/app.yaml` is parsed by `backend/config.py` if `pyyaml` is installed; it merges shallowly with the defaults.
- There is **no API key or `/metrics` endpoint** in this codebase. A lightweight `/manifest` endpoint is implemented to inspect stored memory/calibration/dataset counts.

## Persistence layout (`ModelStore`)
```
<BDI_MODELS_DIR>/
  recipes/<recipe_id>/<model_key>/
    <role>__<roi>.npz          # embeddings + token grid (+metadata JSON string)
    <role>__<roi>_index.faiss  # optional FAISS index (if faiss is installed)
    <role>__<roi>_calib.json   # stored output of /calibrate_ng
  datasets/<role>/<roi>/ok|ng  # if scripts choose to reuse ModelStore for datasets
```
File names use urlsafe base64 encoding of `role_id`/`roi_id` (`ModelStore._base_name`), so the exact ids sent by the GUI remain round-trippable even if they contain dashes. `recipe_id` and `model_key` (defaults to `roi_id`) are sanitized and used to namespace artefacts under `recipes/`. Legacy layouts (`models/<role>/<roi>/memory.npz`, or flat `<role>_<roi>.npz`) are still read for backward compatibility.

## Endpoint behaviour
- `GET /health`: returns `{status, device, model, version, request_id, recipe_id}`; device is `cuda` when `torch.cuda.is_available()`. Includes `reason: cuda_not_available` on CPU-only hosts.
- `POST /fit_ok`:
  - Input: multipart form with `role_id`, `roi_id`, `mm_per_px`, optional `memory_fit` flag, multiple `images` (PNG/JPG ROI crops).
  - For each image `_extractor.extract` is called; the embeddings are concatenated, a coreset is built via `PatchCoreMemory.build` and persisted through `ModelStore.save_memory`. If FAISS is installed its serialised index is also saved.
  - Output: JSON with `n_embeddings`, `coreset_size`, `token_shape`, `coreset_rate_requested`, `coreset_rate_applied`, `request_id`, `recipe_id`.
- `POST /calibrate_ng`:
  - Input JSON: `{role_id, roi_id, mm_per_px?, ok_scores[], ng_scores?[], area_mm2_thr?, score_percentile?}`.
  - Uses `choose_threshold`, writes the result via `ModelStore.save_calib` and returns `{threshold, ok_mean, ng_mean?, p99_ok?, p5_ng?, mm_per_px, area_mm2_thr, score_percentile, request_id, recipe_id}`.
- `POST /infer`:
  - Multipart form with `role_id`, `roi_id`, `mm_per_px`, single `image`, optional `shape` JSON string. The server decodes the image with OpenCV, verifies the token grid matches the stored memory, reconstructs the coreset (optionally loading FAISS) and runs `InferenceEngine.run`.
  - Response: `{score, threshold?, token_shape, heatmap_png_base64?, regions[], request_id, recipe_id}`. `heatmap_png_base64` is the grayscale PNG produced by encoding `heatmap_u8`. The GUI always sends the ROI mask (`shape`) and `mm_per_px`, so `roi_mask.py` uses the exact crop geometry while `infer` converts `area_px` to `area_mm2` for the returned regions.
  - Errors: `400` for missing memory or token mismatch (message in the `error` field); `500` responses include `{error, trace, request_id, recipe_id}`.
- `GET /manifest`: query `role_id`, `roi_id` and returns `ModelStore` availability plus calibration and dataset counts.
- Dataset utilities: `/datasets/ok/upload`, `/datasets/ng/upload`, `/datasets/list`, `/datasets/file` (DELETE), `/datasets/clear` (DELETE).

## Logging
`app.py` uses the helper `slog(event, **kw)` which prints JSON lines to stdout/stderr. Fields include `ts`, the logical event (`fit_ok.request`, `fit_ok.response`, etc.), `role_id`, `roi_id`, `request_id`, `recipe_id`, `elapsed_ms`, and error information. Request/recipe IDs are returned in every JSON response and mirrored in log lines for correlation.

## Running tests
`pytest` is configured under `backend/tests/`. The suite expects a working Python environment with NumPy, Torch, etc. If you only need to validate HTTP contracts, use the real FastAPI server plus the curl snippets from `docs/API_CONTRACTS.md`.

## Recipe ids and normalization
- Recipe ids are sanitized for filesystem safety and normalized to lowercase for storage.
- Treat recipe ids as case-insensitive identifiers; do not rely on casing differences.
- `last` is reserved and MUST be rejected with HTTP 400.

## Concurrency and multi-worker deployments
- The backend can be deployed with multiple Uvicorn workers (`--workers N`).
- Each worker has its own process memory; avoid relying on in-process caches for correctness. All durable state must be persisted under `BDI_MODELS_DIR`.
