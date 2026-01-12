# BrakeDiscInspector

BrakeDiscInspector is a two-part inspection cell: a WPF front-end (`gui/BrakeDiscInspector_GUI_ROI`, target framework `net8.0-windows`) that lets an operator draw ROIs, run manual or batch analysis and manage datasets, and a Python FastAPI backend (`backend/`) that serves PatchCore+DINOv2 inference. The code in this repository is the current implementation described in `agents.md`.

## What the system does
- **Manual inspection:** `WorkflowViewModel` exports a *canonical ROI* from the currently loaded image via `RoiCropUtils` and sends it to the backend using `BackendClient.InferAsync`. Heatmaps and regions are re-projected on top of the canvas (`MainWindow.xaml.cs`). Analyze options (rotation range, scale min/max, matcher thresholds) stay in sync between the active layout and the preset UI so manual runs reuse the same search parameters that were stored with the layout.
- **Batch inspection:** the view-model iterates over every image under the selected folder, repositions each inspection ROI according to its chosen anchor (`InspectionRoiConfig.AnchorMaster`) with `InspectionAlignmentHelper.MoveInspectionTo` and the detected Master 1/2 centers, exports each ROI and evaluates it asynchronously while tracking per-row status (`BatchRow`, `BatchCellStatus`).
- **Dataset management:** the backend is the source of truth (`BDI_MODELS_DIR/recipes/<recipe_id>/datasets/...`). The GUI uploads ROI crops + metadata via `/datasets/{ok|ng}/upload`, refreshes counts via `/datasets/list`, and caches thumbnails under `%LOCALAPPDATA%\\BrakeDiscInspector\\cache\\datasets\\...`. The **Clear canvas** action wipes masters, inspection slots, cached baselines and disables all inspection ROIs so the next edits start from a clean recipe without lingering inspection geometry.
- **Backend inference:** `backend/app.py` exposes `GET /health`, `POST /fit_ok`, `POST /calibrate_ng`, `POST /calibrate_dataset`, `POST /infer`, `POST /infer_dataset` plus `/manifest` and dataset helper routes (`/datasets/*`). Images are decoded with OpenCV, features are extracted with `DinoV2Features`, PatchCore coreset is persisted through `ModelStore`, and responses include `request_id`/`recipe_id` for correlation along with `{score, threshold?, token_shape, heatmap_png_base64?, regions[]}`. `BackendClient` always sends `role_id`, `roi_id`, per-sample `mm_per_px` and the ROI mask (`shape` JSON) so the backend evaluates the same canonical crop that the GUI rendered.

## Quick start
### Prerequisites
- **GUI:** Windows 10/11 x64, Visual Studio 2022 or newer (VS 2026 is fine) with *Desktop development with C#* and the .NET 8 SDK.
- **Backend:** Python 3.11+ with CUDA GPU required by default. Set `BDI_REQUIRE_CUDA=0` to allow CPU-only startup for tests/CI (the provided Dockerfile already targets `pytorch/pytorch:2.2.2-cuda12.1-cudnn8-runtime`).

### Launch the backend
```bash
cd backend
python -m venv .venv
source .venv/bin/activate      # Windows: .venv\Scripts\activate
pip install -r requirements.txt
uvicorn backend.app:app --host 0.0.0.0 --port 8000
```
Environment variables such as `BDI_BACKEND_HOST`, `BDI_BACKEND_PORT`, `BDI_MODELS_DIR`, `BDI_CORESET_RATE`, `BDI_SCORE_PERCENTILE`, `BDI_AREA_MM2_THR`, and `BDI_REQUIRE_CUDA` can override defaults (see `backend/config.py`).

### Launch the GUI
1. Open `gui/BrakeDiscInspector_GUI_ROI/BrakeDiscInspector_GUI_ROI.sln` in Visual Studio.
2. Ensure `appsettings.json` or environment variables define `Backend.BaseUrl` if you are not using `http://127.0.0.1:8000`.
3. Run the app. The dataset is stored in the backend under `BDI_MODELS_DIR` and the GUI caches thumbnails under `%LOCALAPPDATA%\\BrakeDiscInspector\\cache\\datasets\\...`.

### Minimal end-to-end run
1. Load a demo image, draw Master 1/2 anchors and one inspection ROI, then freeze it.
2. Press **Add to OK** to upload the ROI PNG + metadata JSON to the backend dataset.
3. Use **Train memory fit** (calls `/fit_ok` with `use_dataset=true`) and **Calibrate** (calls `/calibrate_dataset`).
4. Run **Evaluate** for manual inspection or select a batch folder and press **Start Batch** to analyze many files; the view refreshes per-row heatmaps while anchoring ROI positions from the masters.

## Documentation map
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md): component diagram and data flow across manual vs batch modes.
- [`docs/FRONTEND.md`](docs/FRONTEND.md): GUI structure, dataset layout, ROI editing and command catalogue.
- [`docs/BACKEND.md`](docs/BACKEND.md): FastAPI modules, persistence layout and configuration knobs.
- [`docs/API_CONTRACTS.md`](docs/API_CONTRACTS.md): canonical definitions for `/health`, `/fit_ok`, `/calibrate_ng`, `/infer`, plus dataset/file contracts.
- [`docs/ROI_AND_HEATMAP_FLOW.md`](docs/ROI_AND_HEATMAP_FLOW.md): ROI anchoring, canonical cropping, shape JSON and heatmap overlays.
- [`LOGGING.md`](LOGGING.md): file locations and fields written by `GuiLog` and `backend/app.py`.
- [`docs/TROUBLESHOOTING.md`](docs/TROUBLESHOOTING.md): operational checklist based on real error paths handled in code.
- [`DEPLOYMENT.md`](DEPLOYMENT.md) & [`docker/README.md`](docker/README.md): how to run the backend outside of Visual Studio.

When working on this repository remember the guardrails in `agents.md`: do not modify adorners, keep HTTP contracts intact and reuse the canonical ROI export pipeline.

## Recipes, request context, and reserved ids (backend)
- The backend is **recipe-aware**: models/memory/calibration and dataset helpers are stored per recipe under `BDI_MODELS_DIR/recipes/<recipe_id>/...`.
- The GUI’s on-disk “recipe” folder is **separate**: it uses `<exe>/Recipes/<LayoutName>/...` for local datasets and layout persistence.
- The backend accepts recipe context in two ways:
  - Explicit `recipe_id` field (form/query/JSON depending on endpoint), or
  - `X-Recipe-Id` request header (case-insensitive header match).
- Reserved recipe ids:
  - `last` is **reserved** (used by the GUI for layout state like `last.layout.json`) and MUST NOT be used as a backend recipe id. If sent, the backend returns **HTTP 400**.
- Normalization and compatibility:
  - The backend normalizes recipe ids for storage (sanitization and lowercase). Treat recipe ids as **case-insensitive**; do not create two recipes that differ only by casing.

## Deployment note: multiple Uvicorn workers
- The backend supports `uvicorn --workers N` (multiple processes).
- Important: in-memory caches are per-worker; persisted artifacts live in `BDI_MODELS_DIR`. Use a **shared volume** for `BDI_MODELS_DIR` if you run multiple workers/containers.
