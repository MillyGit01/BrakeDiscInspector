# BrakeDiscInspector

BrakeDiscInspector is a two-part inspection system:

- **GUI (WPF)** under `gui/` for ROI drawing, canonical ROI export (crop + rotation), master patterns, batch visualization, and operator workflows.
- **Backend (FastAPI)** under `backend/` for dataset storage, model persistence, `fit_ok`, calibration, inference, and heatmap generation.

The GUI is the **source of truth for ROI geometry and canonical crops**; the backend is the **source of truth for datasets, models, and calibration artifacts**. See `docs/ARCHITECTURE.md` for a full data-flow overview and `docs/FRONTEND.md` / `docs/BACKEND.md` for details.

## What the system does (high level)
- **ROI drawing and export (GUI):** ROIs are drawn in WPF and exported as canonical crops (post-rotation) with a `shape` JSON mask that matches the exported image space.
- **Dataset management (backend):** OK/NG samples are uploaded to the backend dataset endpoints and persisted under `BDI_MODELS_DIR` (see `docs/BACKEND.md`). The GUI caches previews locally under `%LOCALAPPDATA%\BrakeDiscInspector\cache\datasets\...`.
- **Training and inference (backend):** The backend runs PatchCore + DINOv2, stores model artifacts per `recipe_id` and `model_key`, and returns `score`, `threshold`, optional `heatmap_png_base64`, and `regions`.

## Quick start

### Prerequisites
- **GUI:** Windows 10/11, Visual Studio 2022+, .NET 8 SDK.
- **Backend:** Python 3.11+; CUDA GPU is required by default (`BDI_REQUIRE_CUDA=1`). Set `BDI_REQUIRE_CUDA=0` for CPU-only startup.

### Launch the backend
```bash
cd backend
python -m venv .venv
source .venv/bin/activate      # Windows: .venv\Scripts\activate
pip install -r requirements.txt
uvicorn backend.app:app --host 0.0.0.0 --port 8000
```
Config keys are defined in `backend/config.py` and `backend/app.py` (see `docs/BACKEND.md`).

### Launch the GUI
1. Open `gui/BrakeDiscInspector_GUI_ROI/BrakeDiscInspector_GUI_ROI.sln` in Visual Studio.
2. Update `config/appsettings.json` or set `BDI_BACKEND_BASEURL` if the backend is not running on `http://127.0.0.1:8000`.
3. Run the app. Layouts and master patterns are stored under `<exe>/Recipes/<LayoutName>/`.

### Minimal end-to-end run
1. Load an image, define Master 1/2 ROIs and at least one inspection ROI.
2. Add OK/NG samples (uploads the canonical ROI to the backend dataset).
3. Run **fit_ok** and **calibrate** from the dataset tab (backend operations).
4. Evaluate a single image or run batch analysis.

## Documentation map
- [`docs/INDEX.md`](docs/INDEX.md) — complete documentation index.
- [`docs/AI_ONBOARDING.md`](docs/AI_ONBOARDING.md) — 10-minute onboarding + glossary.
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — component boundaries and data flow.
- [`docs/FRONTEND.md`](docs/FRONTEND.md) — WPF UI behavior, ROI workflows, UI specs.
- [`docs/BACKEND.md`](docs/BACKEND.md) — FastAPI configuration, persistence, failure modes.
- [`docs/API_CONTRACTS.md`](docs/API_CONTRACTS.md) — exact HTTP contracts.
- [`LOGGING.md`](LOGGING.md) — **source of truth** for logs and diagnostics.

## Recipe ids (backend)
- `recipe_id` can be passed via payload or `X-Recipe-Id`. It is normalized to lowercase and validated against `^[a-z0-9][a-z0-9_-]{0,63}$`.
- `last` is **reserved** and rejected with HTTP 400.
- Artifacts are stored under `BDI_MODELS_DIR/recipes/<recipe_id>/...` (see `docs/BACKEND.md`).

## Logging
Use [`LOGGING.md`](LOGGING.md) as the single source of truth for log locations and fields.
