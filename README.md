# BrakeDiscInspector

BrakeDiscInspector is a two-part inspection cell: a WPF front-end (`gui/BrakeDiscInspector_GUI_ROI`, target framework `net8.0-windows`) that lets an operator draw ROIs, run manual or batch analysis and manage datasets, and a Python FastAPI backend (`backend/`) that serves PatchCore+DINOv2 inference. The code in this repository is the current implementation described in `agents.md`.

## What the system does
- **Manual inspection:** `WorkflowViewModel` exports a *canonical ROI* from the currently loaded image via `RoiCropUtils` and sends it to the backend using `BackendClient.InferAsync`. Heatmaps and regions are re-projected on top of the canvas (`MainWindow.xaml.cs`).
- **Batch inspection:** the view-model iterates over every image under the selected folder, repositions the inspection ROIs with `InspectionAlignmentHelper.MoveInspectionTo` using Master 1/2 anchors, exports each ROI and evaluates it asynchronously while tracking per-row status (`BatchRow`, `BatchCellStatus`).
- **Dataset management:** every inspection slot (1–4) owns a folder under `<data root>/rois/Inspection_<n>/{ok,ng,Model}` and a CSV/preview cache managed by `DatasetManager`. Adding to OK/NG stores `PNG + metadata JSON` locally and updates dataset summaries shown in the GUI.
- **Backend inference:** `backend/app.py` exposes `GET /health`, `POST /fit_ok`, `POST /calibrate_ng` and `POST /infer`. Images are decoded with OpenCV, features are extracted with `DinoV2Features`, PatchCore coreset is persisted through `ModelStore`, and responses always contain `{score, threshold?, token_shape, heatmap_png_base64?, regions[]}`.

## Quick start
### Prerequisites
- **GUI:** Windows 10/11 x64, Visual Studio 2022 with *Desktop development with C#*. The project targets .NET 8 and references OpenCvSharp (see `gui/BrakeDiscInspector_GUI_ROI.csproj`).
- **Backend:** Python 3.11+, CUDA 12.1 if you want GPU acceleration (the provided Dockerfile already targets `pytorch/pytorch:2.2.2-cuda12.1-cudnn8-runtime`).

### Launch the backend
```bash
cd backend
python -m venv .venv
source .venv/bin/activate      # Windows: .venv\Scripts\activate
pip install -r requirements.txt
uvicorn backend.app:app --host 0.0.0.0 --port 8000
```
Environment variables such as `BDI_MODELS_DIR`, `BDI_BACKEND_HOST`, `BDI_BACKEND_PORT` and `BDI_CORESET_RATE` can override defaults (see `backend/config.py`).

### Launch the GUI
1. Open `gui/BrakeDiscInspector_GUI_ROI/BrakeDiscInspector_GUI_ROI.sln` in Visual Studio.
2. Ensure `appsettings.json` or environment variables define `Backend.BaseUrl` if you are not using `http://127.0.0.1:8000`.
3. Run the app. On first launch it will create `<exe folder>/data/rois/Inspection_1..4/{ok,ng,Model}`.

### Minimal end-to-end run
1. Load a demo image, draw Master 1/2 anchors and one inspection ROI, then freeze it.
2. Press **Add to OK** to persist a PNG plus metadata JSON under the inspection dataset (`DatasetManager.SaveSampleAsync`).
3. Use **Train memory fit** (calls `/fit_ok`) and **Calibrate** (calls `/calibrate_ng`).
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
