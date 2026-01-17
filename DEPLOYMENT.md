# Deployment guide

This document covers how to run the **backend** and connect the GUI.

## Modes of operation

### Standalone (GUI + backend on one machine)
1. Install Python 3.11+ and (optionally) CUDA.
2. Run the backend (`uvicorn backend.app:app --host 0.0.0.0 --port 8000`).
3. Launch the GUI and point `Backend.BaseUrl` to `http://127.0.0.1:8000`.

### Separate backend server
1. Provision a Linux host with NVIDIA drivers if GPU is required.
2. Build or run the Docker image (see `docker/README.md`).
3. Point GUI workstations to `http://<server>:8000`.

## Environment variables (backend)
- `BDI_BACKEND_HOST`, `BDI_BACKEND_PORT`
- `BDI_MODELS_DIR`
- `BDI_CORESET_RATE`, `BDI_SCORE_PERCENTILE`, `BDI_AREA_MM2_THR`
- `BDI_MIN_OK_SAMPLES`, `BDI_TRAIN_DATASET_ONLY`
- `BDI_REQUIRE_CUDA`
- `BDI_CACHE_MAX_ENTRIES`
- `BDI_CORS_ORIGINS`
- `BDI_GUI_LOG_DIR` (optional diagnostics log directory)

## Storage and volumes
- Model artifacts and datasets are stored under `BDI_MODELS_DIR`.
- For Docker or multi-worker deployments, mount a shared volume for `BDI_MODELS_DIR`.

## Logs
- GUI logs: `%LOCALAPPDATA%\BrakeDiscInspector\logs\`.
- Backend diagnostics: `backend_diagnostics.jsonl` (see `LOGGING.md`).

## Verification checklist
1. `curl http://<host>:8000/health` returns `status=ok`.
2. GUI can add samples to the backend dataset.
3. `fit_ok`, `calibrate`, and `infer` succeed for a test ROI.

## Multiple Uvicorn workers
Example:
```bash
uvicorn backend.app:app --host 0.0.0.0 --port 8000 --workers 2
```
Notes:
- Each worker has its own in-memory cache.
- `BDI_MODELS_DIR` must be shared across workers.
