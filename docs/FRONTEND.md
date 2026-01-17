# Front-end (WPF) reference

This document describes the WPF GUI under `gui/` as implemented in the repository.

## Responsibilities (GUI vs backend)
**GUI (source of truth for geometry):**
- ROI drawing and editing (rect/circle/annulus).
- Canonical ROI export (crop + rotation) and `shape` JSON generation.
- Master patterns, layout persistence, batch visualization.

**Backend (source of truth for data):**
- Datasets, models, calibration, and inference. See `docs/BACKEND.md`.

## Layouts and local storage
- Layouts and master assets are stored under `<exe>/Recipes/<LayoutName>/`.
- Master patterns are saved under `<exe>/Recipes/<LayoutName>/Master/`.
- Dataset previews are cached under `%LOCALAPPDATA%\BrakeDiscInspector\cache\datasets\...`.

> The GUI still creates local dataset folders (`<exe>/Recipes/<LayoutName>/Dataset/...`) for legacy compatibility, but the **authoritative dataset** is the backend dataset under `BDI_MODELS_DIR`.

## Dataset workflow (backend source of truth)
- **Upload:** the GUI exports the canonical ROI and uploads it to `/datasets/ok/upload` or `/datasets/ng/upload`.
- **List/preview:** the GUI calls `/datasets/list` and downloads thumbnails via `/datasets/file` into its local cache.
- **Delete:** the GUI deletes remote files via `/datasets/file` (DELETE) and removes cached copies.

## Training and inference flows
- **fit_ok:** GUI calls `/fit_ok` with `use_dataset=true` to train from backend datasets.
- **calibrate:** GUI calls `/calibrate_dataset` or `/calibrate_ng` depending on the workflow.
- **infer:** GUI calls `/infer` for a single ROI crop, always including the `shape` JSON.

Exact HTTP contracts live in `docs/API_CONTRACTS.md`.

## Enabled vs backend fitted state
Each inspection ROI has a local **Enabled** toggle that controls **UI participation** in batch/inference.
- **Enabled** is a GUI-only flag; it does **not** change backend artifacts.
- Backend readiness is tracked separately (e.g., memory/calibration presence returned by `/state`).

**Legacy note:** older layout files may include `HasFitOk`. This field is migrated out on load and should be treated as **stale**. The backend is the source of truth for fitted state.

### Where Enabled is controlled
- **InspectionDefaultPanel:** checkbox next to each inspection ROI row (main panel).
- **InspectionDatasetTabView:** an `Enabled` checkbox in the dataset tab.

Both bind to the same `InspectionRoiConfig.Enabled` property and stay synchronized.

## Master patterns (GUI-only)
- Master patterns are **independent of the backend**.
- Saved as `master1_pattern.png` / `master2_pattern.png` under `<exe>/Recipes/<LayoutName>/Master/`.
- Older versions are moved to `Master/obsolete/` (no timestamp in the base filename).

**Cache risk:** master patterns are cached by **path + mtime + size**. If you overwrite a file without changing its mtime/size, the GUI may reuse a stale cache. Recommended invalidation: update mtime or version the file name (timestamp suffix).

## Heatmap & badge UI spec (current expectation)
> This section is a UI spec. If the current implementation deviates, treat it as TODO and align UI later.

### Heatmap visibility rules
- Only show **red zones** when the **final result is NG** and the heatmap is the cause of NG.
- If the result is **OK**, no heatmap overlay should be shown.
- Apply the same rule in **manual** and **batch** views.

### OK/NG badge
- Square badge (width == height).
- **NG:** red square, white text `NG`, very bold typography.
- **OK:** green square, white text `OK`, very bold typography.

## Known batch issue (ROI2)
There is a known issue where **ROI2 heatmap may not appear after the first batch image**. The plan is:
1. Confirm ROI2 placement logs in `gui.log`/`gui_heatmap.log`.
2. Ensure the batch placement guard does not reuse ROI1 geometry for ROI2.
3. Add a regression test or manual checklist step once fixed.

## Related docs
- `docs/ROI_AND_HEATMAP_FLOW.md` — ROI export + heatmap pipeline details.
- `docs/LOGGING.md` — log locations and correlation.
- `docs/TROUBLESHOOTING.md` — operational checklist.
