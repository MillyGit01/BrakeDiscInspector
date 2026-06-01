# Front-end (WPF) Reference

This document describes the WPF GUI under `gui/` as implemented in the repository.

## Responsibilities (GUI vs Backend)
**GUI (source of truth for geometry):**
- ROI drawing and editing (rect/circle/annulus).
- Canonical ROI export (crop + rotation) and `shape` JSON generation.
- Master patterns, layout persistence, batch visualization, image collection workflow, and communication UI.

**Backend (source of truth for data):**
- Datasets, models, calibration, and inference. See `docs/BACKEND.md`.

## Layouts and Local Storage
- Layouts and master assets are stored under `<exe>/Recipes/<LayoutName>/`.
- Master patterns are saved under `<exe>/Recipes/<LayoutName>/Master/`.
- Dataset previews are cached under `%LOCALAPPDATA%\BrakeDiscInspector\cache\datasets\...`.

The GUI still creates local dataset folders (`<exe>/Recipes/<LayoutName>/Dataset/...`) for legacy compatibility, but the authoritative dataset is the backend dataset under `BDI_MODELS_DIR`.

## Backend Startup from the GUI
- On startup, the GUI reads `config/appsettings.json` / `appsettings.json` through `AppConfig`.
- When `Backend.AutoStart=true`, the GUI probes `<Backend.BaseUrl>/health`.
- If the backend is not healthy, it launches the configured WSL backend with `Backend.AutoStartMode` (`WslVenv`, `WslConda`, or `WslAuto`) and waits up to `Backend.StartupTimeoutSeconds`.
- `Backend.AutoStartModelsDirectory` is exported as `BDI_MODELS_DIR` when configured.

The backend can still be started manually; auto-start is only a convenience for operator launch.

## Canvas Image Workflow
- The canvas has an image-folder button and a thumbnail strip under the main image.
- Opening a folder loads supported image files into the strip and shows a loading message while thumbnails are prepared.
- Selecting a thumbnail loads that image into the canvas.
- Heatmap scale/opacity controls and the Object Visibility HUD are only shown when an image is loaded.
- If an image is loaded before any layout is active, the GUI shows `Please load a layout`.
- If a layout is active, image load triggers master analysis and evaluation of enabled inspection ROIs.

## Dataset Workflow (backend source of truth)
- **Upload:** the GUI exports the canonical ROI and uploads it to `/datasets/ok/upload` or `/datasets/ng/upload`.
- **List/preview:** the GUI calls `/datasets/list` and downloads thumbnails via `/datasets/file` into its local cache.
- **Delete:** the GUI deletes remote files via `/datasets/file` (DELETE) and removes cached copies.

### Inspection ROI Dataset Lock
Once a dataset exists for an inspection ROI, that inspection ROI geometry is locked.
- Users can still add more OK/NG images to the dataset.
- Users cannot enter edit mode, move, resize, save geometry changes, delete/recreate the ROI, change its shape, clear it through canvas clear, or clear persisted geometry.
- The warning message is: `Inspection ROIs can't be modified once Dataset is created`.
- Master ROIs remain editable because changing them does not invalidate backend inspection models.

## Training and Inference Flows
- **fit_ok:** GUI calls `/fit_ok` with `use_dataset=true` to train from backend datasets.
- **calibrate:** GUI calls `/calibrate_dataset` or `/calibrate_ng` depending on the workflow.
- **infer:** GUI calls `/infer` for a single ROI crop, always including the `shape` JSON.

Exact HTTP contracts live in `docs/API_CONTRACTS.md`.

## Enabled vs Backend Fitted State
Each inspection ROI has a local **Enabled** toggle that controls UI participation in batch/inference.
- **Enabled** is a GUI-only flag; it does not change backend artifacts.
- Backend readiness is tracked separately, for example by memory/calibration presence returned by `/state`.

Legacy note: older layout files may include `HasFitOk`. This field is migrated out on load and should be treated as stale. The backend is the source of truth for fitted state.

### Where Enabled is Controlled
- **InspectionDefaultPanel:** checkbox next to each inspection ROI row (main panel).
- **InspectionDatasetTabView:** an `Enabled` checkbox in the dataset tab.

Both bind to the same `InspectionRoiConfig.Enabled` property and stay synchronized.

## Master Patterns (GUI-only)
- Master patterns are independent of the backend.
- Saved as `master1_pattern.png` / `master2_pattern.png` under `<exe>/Recipes/<LayoutName>/Master/`.
- Older versions are moved to `Master/obsolete/` (no timestamp in the base filename).
- Master matching caches rotated/scaled template variants for each pattern so batch alignment does not rebuild the same search variants repeatedly.

Cache risk: master patterns are cached by path + `mtime` + size. If you overwrite a file without changing its mtime/size, the GUI may reuse a stale cache. Recommended invalidation: update mtime or version the file name.

## Heatmap, HUD, and Badge UI

### Heatmap Visibility Rules
- Only show red zones when the final result is NG and the heatmap is the cause of NG.
- If the result is OK, no heatmap overlay should be shown.
- Apply the same rule in manual and batch views.
- Heatmap scale and opacity controls use translucent backgrounds and are only visible with a loaded image.

### Object Visibility HUD
- The HUD is titled `OBJECT VISIBILITY`.
- The HUD uses vertically aligned toggle controls.
- Selected ROI outlines are black; unselected/hidden styling uses dark gray.
- The HUD is hidden until an image is loaded.

### OK/NG Badge
- Square badge (width == height).
- **NG:** red square, white text `NG`, very bold typography.
- **OK:** green square, white text `OK`, very bold typography.
- The badge is placed inside the canvas, near the upper-right quadrant but offset from the edge.

### Edit Mode Indicator
- Active ROI editing shows a yellow canvas frame.
- The lower-left canvas label `Edit Mode` is visible only while ROI editing is active.

## Theme and Setup GUI
- Light/dark theme switching updates shared WPF brushes used by buttons, list boxes, text inputs, and common controls.
- Setup GUI can customize key colors through palette buttons next to color fields.
- Custom colors are persisted only when explicitly changed; changing theme resets custom color overrides.

## Batch Heatmap Placement
- Batch heatmaps are tracked per ROI index so ROI selection can display the corresponding stored heatmap.
- Placement diagnostics are written to `gui.log` and `gui_heatmap.log`.
- If a heatmap disappears or appears on the wrong ROI, check those logs for the ROI index, canvas rectangle, and placement guard messages.

## Related Docs
- `docs/ROI_AND_HEATMAP_FLOW.md` - ROI export + heatmap pipeline details.
- `LOGGING.md` - log locations and correlation.
- `docs/TROUBLESHOOTING.md` - operational checklist.
