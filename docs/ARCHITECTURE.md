# Architecture

This document summarizes **how the current codebase is wired** and where data lives. It only describes behavior that is visible in the repository.

## Components

### WPF GUI (`gui/`)
**Responsibilities (source of truth):**
- ROI drawing and editing (rect/circle/annulus).
- Canonical ROI export (crop + rotation) and `shape` JSON generation in canonical ROI coordinates.
- Master patterns, inspection ROI layout, batch visualization.
- UI state, layout persistence, and local previews.

**On-disk GUI artifacts:**
- Layout and master assets live under `<exe>/Recipes/<LayoutName>/` (see `RecipePathHelper`).
- Master patterns are saved under `<exe>/Recipes/<LayoutName>/Master/`.
- Dataset *previews* are cached under `%LOCALAPPDATA%\BrakeDiscInspector\cache\datasets\...`.

### FastAPI backend (`backend/`)
**Responsibilities (source of truth):**
- Persistent storage of datasets, model artifacts, and calibration.
- `fit_ok` training, calibration, inference, and heatmap generation.
- Recipe-aware storage and artifact resolution with fallback to legacy layouts.

**Key modules:**
- `app.py` — FastAPI routes and request validation.
- `storage.py` — `ModelStore` persistence layout and recipe rules.
- `features.py`, `patchcore.py`, `infer.py`, `calib.py` — PatchCore + DINOv2 pipeline.
- `diagnostics.py` — structured JSONL diagnostics.

## GUI ↔ backend boundary
- **GUI** exports **canonical ROI crops** and supplies `shape` masks in the crop's coordinate system.
- **Backend** **does not** crop or rotate images; it trusts the GUI-provided crop and `shape` mask.

This separation is fundamental for consistent overlays and debugging.

## Data flow (high level)
1. **Manual inspection**: GUI exports a canonical ROI crop → backend `/infer` → GUI overlays result.
2. **Batch inspection**: GUI aligns ROIs using master anchors → exports crops per image → backend `/infer` per ROI.
3. **Dataset management**: GUI uploads samples to backend `/datasets/*` → backend persists dataset → GUI downloads previews for display.

## Persistence layout (backend)
Artifacts are stored under `BDI_MODELS_DIR` (default `models/`).

```
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
- `recipe_id` is validated, lowercased, and **must not** be `last`.
- `model_key` defaults to `roi_id` and is sanitized for filesystem use.
- `base_name` is `base64(role_id) + "__" + base64(roi_id)` (urlsafe base64 without `=` padding).

Legacy fallbacks still exist for older layouts (`models/datasets/<role>/<roi>`, `models/<role>_<roi>.npz`, etc.).

## Caching (current implementation)
- **Backend caches** model memory and calibration per worker process. Cache size is capped by `BDI_CACHE_MAX_ENTRIES`.
- **GUI caches** master patterns by path + `mtime` + size to avoid reloading identical images; stale caches are invalidated when files change.

## Logging
Logging is centralized in `LOGGING.md` and should be treated as the single source of truth.
