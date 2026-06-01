# Architecture

This document summarizes how the current codebase is wired and where data lives. It only describes behavior that is visible in the repository.

## Components

### WPF GUI (`gui/`)
**Responsibilities (source of truth):**
- ROI drawing and editing (rect/circle/annulus).
- Canonical ROI export (crop + rotation) and `shape` JSON generation in canonical ROI coordinates.
- Master patterns, inspection ROI layout, batch visualization.
- UI state, layout persistence, local previews, and image-folder thumbnail workflow.
- Backend auto-start orchestration for local WSL deployments.
- Production-cell communication state for PLC/camera integration.

**On-disk GUI artifacts:**
- Layout and master assets live under `<exe>/Recipes/<LayoutName>/` (see `RecipePathHelper`).
- Master patterns are saved under `<exe>/Recipes/<LayoutName>/Master/`.
- Dataset previews are cached under `%LOCALAPPDATA%\BrakeDiscInspector\cache\datasets\...`.

### FastAPI backend (`backend/`)
**Responsibilities (source of truth):**
- Persistent storage of datasets, model artifacts, and calibration.
- `fit_ok` training, calibration, inference, and heatmap generation.
- Recipe-aware storage and artifact resolution with fallback to legacy layouts.

**Key modules:**
- `app.py` - FastAPI routes and request validation.
- `storage.py` - `ModelStore` persistence layout and recipe rules.
- `features.py`, `patchcore.py`, `infer.py`, `calib.py` - PatchCore + DINOv2 pipeline.
- `diagnostics.py` - structured JSONL diagnostics.

### Communication layer (`gui/BrakeDiscInspector_GUI_ROI/Comms/`)
**Responsibilities:**
- Abstract PLC polling and output writes through `IPlcClient`.
- Provide simulation mode for development without wiring.
- Provide Siemens S7-1200 connectivity through S7.NetPlus.
- Abstract image acquisition through `ICameraClient`.
- Implement the `Folder` camera provider for integration testing and expose `FlirBlackfly` / `Cognex` placeholders until vendor SDK wiring is selected.

## GUI to Backend Boundary
- The GUI exports canonical ROI crops and supplies `shape` masks in the crop's coordinate system.
- The backend does not crop or rotate images; it trusts the GUI-provided crop and `shape` mask.

This separation is fundamental for consistent overlays and debugging.

## Data Flow
1. **Manual inspection:** GUI exports a canonical ROI crop, backend `/infer` evaluates it, GUI overlays the result.
2. **Batch inspection:** GUI aligns ROIs using master anchors, exports crops per image, and calls backend `/infer` per ROI.
3. **Dataset management:** GUI uploads samples to backend `/datasets/*`, backend persists the dataset, GUI downloads previews for display.
4. **PLC-triggered inspection:** PLC start edge, GUI acquires a camera frame, GUI loads/analyzes/evaluates, GUI writes ready/busy/result/error bits back to the PLC.

## ROI Edit Policy
- Inspection ROI geometry is mutable until a dataset exists for that ROI.
- After a dataset exists, the GUI locks inspection ROI geometry to protect the backend model contract.
- Additional OK/NG samples can still be uploaded to the dataset.
- Master ROIs remain mutable because they are GUI-only alignment assets and are never uploaded as backend model geometry.

## Persistence Layout (backend)
Artifacts are stored under `BDI_MODELS_DIR` (default `models/`).

```text
<BDI_MODELS_DIR>/
  recipes/<recipe_id>/<model_key>/
    <base_name>.npz
    <base_name>_index.faiss
    <base_name>_calib.json
  recipes/<recipe_id>/datasets/<base_name>/
    ok/*.png
    ng/*.png
```

Where:
- `recipe_id` is validated, lowercased, and must not be `last`.
- `model_key` defaults to `roi_id` and is sanitized for filesystem use.
- `base_name` is `base64(role_id) + "__" + base64(roi_id)` (urlsafe base64 without `=` padding).

Legacy fallbacks still exist for older layouts (`models/datasets/<role>/<roi>`, `models/<role>_<roi>.npz`, etc.).

## Caching
- Backend caches model memory and calibration per worker process. Cache size is capped by `BDI_CACHE_MAX_ENTRIES`.
- GUI caches master patterns by path + `mtime` + size to avoid reloading identical images; stale caches are invalidated when files change.
- GUI master matching caches rotated/scaled template variants for batch alignment.

## Logging
Logging is centralized in `LOGGING.md` and should be treated as the single source of truth.
