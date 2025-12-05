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
  - `BDI_BACKEND_BASEURL` is consumed by the GUI client, not the server.
- Optional `configs/app.yaml` is parsed by `backend/config.py` if `pyyaml` is installed; it merges shallowly with the defaults.
- There is **no API key, manifest synchronisation or `/metrics` endpoint** in this codebase.

## Persistence layout (`ModelStore`)
```
<BDI_MODELS_DIR>/
  <role>__<roi>.npz            # embeddings + token grid (+metadata JSON string)
  <role>__<roi>_index.faiss    # optional FAISS index (if faiss is installed)
  <role>__<roi>_calib.json     # stored output of /calibrate_ng
  datasets/<role>/<roi>/ok|ng  # if scripts choose to reuse ModelStore for datasets
```
File names use urlsafe base64 encoding of `role_id`/`roi_id` (`ModelStore._base_name`), so the exact ids sent by the GUI remain round-trippable even if they contain dashes. Legacy layouts (`models/<role>/<roi>/memory.npz`, etc.) are still read for backward compatibility.

## Endpoint behaviour
- `GET /health`: returns `{status, device, model, version}`; device is `cuda` when `torch.cuda.is_available()`.
- `POST /fit_ok`:
  - Input: multipart form with `role_id`, `roi_id`, `mm_per_px`, optional `memory_fit` flag, multiple `images` (PNG/JPG ROI crops).
  - For each image `_extractor.extract` is called; the embeddings are concatenated, a coreset is built via `PatchCoreMemory.build` and persisted through `ModelStore.save_memory`. If FAISS is installed its serialised index is also saved.
  - Output: JSON with `n_embeddings`, `coreset_size`, `token_shape`, `coreset_rate_requested`, `coreset_rate_applied`.
- `POST /calibrate_ng`:
  - Input JSON: `{role_id, roi_id, mm_per_px?, ok_scores[], ng_scores?[], area_mm2_thr?, score_percentile?}`.
  - Uses `choose_threshold`, writes the result via `ModelStore.save_calib` and returns `{threshold, ok_mean, ng_mean?, p99_ok?, p5_ng?, mm_per_px, area_mm2_thr, score_percentile}`.
- `POST /infer`:
  - Multipart form with `role_id`, `roi_id`, `mm_per_px`, single `image`, optional `shape` JSON string. The server decodes the image with OpenCV, verifies the token grid matches the stored memory, reconstructs the coreset (optionally loading FAISS) and runs `InferenceEngine.run`.
  - Response: `{score, threshold?, token_shape, heatmap_png_base64?, regions[]}`. `heatmap_png_base64` is the grayscale PNG produced by encoding `heatmap_u8`.
  - Errors: `400` for missing memory or token mismatch (message in the `error` field); `500` responses include `{error, trace}`.

## Logging
`app.py` uses the helper `slog(event, **kw)` which prints JSON lines to stdout/stderr. Fields include `ts`, the logical event (`fit_ok.request`, `fit_ok.response`, etc.), `role_id`, `roi_id`, `elapsed_ms`, and error information. There is no request-id propagation, so correlate logs by `(role_id, roi_id)` and timestamp.

## Running tests
`pytest` is configured under `backend/tests/`. The suite expects a working Python environment with NumPy, Torch, etc. If you only need to validate HTTP contracts, use the real FastAPI server plus the curl snippets from `docs/API_CONTRACTS.md`.
