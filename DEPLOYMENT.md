# Deployment guide

The goal of this document is to explain how to run the checked-in backend in realistic environments. Only features present in the repository are covered.

## Modes of operation
### Standalone (PC with GUI + backend)
1. Install Python 3.11+, CUDA/cuDNN if you plan to use GPU acceleration.
2. Follow the *Launch the backend* steps in `README.md` (run `uvicorn backend.app:app --host 0.0.0.0 --port 8000`).
3. Start the GUI from Visual Studio or publish it. Configure `Backend.BaseUrl` to `http://127.0.0.1:8000` or set `BDI_BACKEND_BASEURL`.

### Separate backend server
1. Provision a Linux host with NVIDIA drivers if GPU is needed.
2. Clone this repository and run the backend in a virtual environment **or** build the provided Docker image:
   ```bash
   docker build -t brakedisc-backend -f docker/Dockerfile .
   docker run --gpus all -p 8000:8000 -v /data/brakedisc/models:/app/models brakedisc-backend
   ```
3. Point each GUI workstation to `http://<server-ip>:8000` by editing `appsettings.json` or the environment variable `BDI_BACKEND_BASEURL`.

## Environment variables
The backend honours the variables defined in `backend/app.py` and `backend/config.py`:
- `BDI_BACKEND_HOST`, `BDI_BACKEND_PORT` – only relevant when running `uvicorn` programmatically.
- `BDI_MODELS_DIR` – where `.npz`/`.faiss`/`_calib.json` files are stored.
- `BDI_CORESET_RATE`, `BDI_SCORE_PERCENTILE`, `BDI_AREA_MM2_THR` – inference defaults.
There is no API-key enforcement or TLS termination inside the app; use a reverse proxy if you need those features.

## GUI configuration
- The executable reads `config/appsettings.json` first, then `appsettings.json`, then environment overrides (see `AppConfigLoader`).
- Dataset root is derived from the current layout name and always expands to `<exe>/Recipes/<LayoutName or DefaultLayout>/`; `BDI_DATASET_ROOT` is parsed by `AppConfig` but not used by the current recipe pipeline.
- The GUI sends HTTP requests asynchronously; no additional services are required. Each inference/training call includes `role_id`, `roi_id`, `mm_per_px` and the ROI `shape`, so make sure the backend address is reachable from the workstation to keep overlays aligned with backend heatmaps/regions.

## Verification checklist
1. Start the backend and run `curl http://<host>:8000/health`.
2. From the GUI, open **Tools → Health** (or whichever control is bound to `RefreshHealthCommand`). The status bar shows the backend model and device.
3. Run a manual inference on a known OK sample and confirm `gui.log` contains `[eval] done ... OK`.
4. Optional: run a small batch folder to confirm anchor alignment and dataset counters behave as expected.
