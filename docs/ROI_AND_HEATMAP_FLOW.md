# ROI and heatmap workflow

This document explains how ROIs are defined, exported, aligned and how heatmaps are rendered in both manual and batch modes. Every step references the code in this repository.

## ROI identifiers and roles
- Roles: `Inspection`, `Master1`, `Master2` (`RoiRole` enum). `BackendClient` forwards `role_id` unchanged to the FastAPI routes.
- ROI ids: inspection slots keep `inspection-1..4` (see `MasterLayout.InspectionRois`), while Master anchors inherit their ids from the layout file. These ids are used in dataset file names (`SAMPLE_<role>_<roi>_*`) and in backend calls, so layout JSON, dataset JSON and recipes must match.
- Recipes: each layout name resolves to `<exe>/Recipes/<LayoutName or DefaultLayout>/` with `Dataset/` and `Master/` subfolders. Dataset samples live under `Dataset/Inspection_<n>/<ok|ng>/` for inspection slots (mapped from `inspection-1..4`) or `Dataset/<roi_id>/<ok|ng>/` for other ROIs. Masters and ROI exports are versioned by moving older files into `obsolete/`.

## 1. Canonical ROI export
1. `WorkflowViewModel` calls `_exportRoiAsync`, which ultimately uses `RoiCropUtils.TryBuildRoiCropInfo` to capture the ROI geometry (`shape`, `Left/Top/Width/Height`, rotation pivot).
2. `RoiCropUtils.TryGetRotatedCrop` rotates the full image around the ROI pivot (OpenCV warp affine) and extracts a crop with the exact same width/height as the drawn ROI. Rectangles use the `Left/Top/Width/Height` bounds; circles/annulus use `CX/CY/R/RInner` with a **square** bounding box (diameter) so the crop stays centered.
3. `RoiCropUtils.BuildRoiMask` generates a binary mask in crop coordinates that matches the shape (rectangles fill the crop, circles/annulus draw concentric discs). This mask is embedded into the metadata and used both by dataset saves and backend requests.
4. `RoiExportResult` wraps the PNG bytes, `shape_json`, the `RoiModel` snapshot (with base image dimensions) and the integer crop rectangle. Those fields are logged before sending them to the backend or saving them to disk.

## 2. Shape JSON conventions
- Rectangles: `{ "kind": "rect", "x": <left>, "y": <top>, "w": <width>, "h": <height> }`.
- Circles: `{ "kind": "circle", "cx": <centerX>, "cy": <centerY>, "r": <outer radius> }`.
- Annulus: `{ "kind": "annulus", "cx": <centerX>, "cy": <centerY>, "r": <outer radius>, "r_inner": <inner radius> }`.
Values are expressed in pixels of the *canonical crop* (after rotation). Backend masks use the same coordinates, so there is no extra transform required (`backend/roi_mask.py`).

## 3. Master anchors and inspection alignment
- `MasterLayout` keeps baseline ROIs for Master 1/2 (`Master1Pattern`, `Master1Search`, etc.) and the inspection slots (`Inspection1..4` plus `InspectionBaselinesByImage`).
- During batch analysis `RepositionInspectionRoisForImageAsync` passes the saved baselines and the detected Master anchor positions into `InspectionAlignmentHelper.MoveInspectionTo`. The helper computes the vector from Master1→Master2 in both baseline and current images, extracts scale and rotation deltas, and applies them to each inspection ROI according to its `AnchorMaster` preference (per-slot anchor selection).
- If anchors are missing or below threshold no repositioning occurs (ROI stays at its saved position) instead of applying a midpoint fallback. Scale now relies on explicit vector length math (no `Point2d.Length` dependency) to keep anchor transforms stable on older .NET builds.
- Annulus and circle radii are scaled with the same factor; inner radii are clamped so they remain smaller than the outer radius.

## 4. Manual overlays
- Manual ROI placement is controlled by the `RoiOverlay` bound to `ImgManual`. Letterboxing is handled via `_scale` and `_offX/_offY`; the overlay converts image coordinates to screen coordinates (`ToScreen`), draws the rotated rectangle/circle/annulus and annotates it with the ROI label.
- When `EvaluateRoiAsync` receives a heatmap from the backend, `ShowHeatmapOverlayAsync` decodes the PNG, caches the grayscale bitmap and multiplies it by the user-controlled `HeatmapOverlayOpacity`/`HeatmapGain`/`HeatmapGamma`. The transformed bitmap is positioned using `GetImageToCanvasTransform` so it lines up with the ROI on the current zoom level.

## 5. Batch overlays
- Batch and manual canvases share the same overlay control. `WorkflowViewModel.SetBatchHeatmapForRoi` stores the heatmap bytes per ROI index and `UpdateBatchHeatmapIndex` switches the shared overlay to the ROI currently being evaluated.
- Placement is debounced: if anchors have not been confirmed (`_batchAnchorReadyForStep`), the heatmap is not rendered yet to avoid misalignment. Once anchors are ready, `PlaceBatchFinalAsync` logs the applied transform in `gui_heatmap.log` (`TraceBatchHeatmapPlacement`).
- `BatchPausePerRoiSeconds` can be used to keep heatmaps visible for a short time per ROI.

## 6. Heatmap interpretation
- Scores: `InferenceResult.score` is compared against `InspectionRoiConfig.CalibratedThreshold` (or `ThresholdDefault` when calibration is missing). `WorkflowViewModel` marks the ROI as NG when `score > threshold`.
- Regions: backend regions contain `bbox` in canonical crop coordinates and `area_mm2` derived from `mm_per_px`. The GUI lists them in the `Regions` observable collection.
- Logs: `MainWindow` writes placement and scaling parameters into `%LocalAppData%/BrakeDiscInspector/logs/gui_heatmap.log`, while the backend emits `{score, threshold}` via `slog("infer.response", ...)`.

## 7. Saving and reloading ROIs
- `PresetManager.SaveInspection` clones the selected ROI into `Preset.Inspection1/2` with ids `Inspection_<slot>` to keep dataset and backend IDs consistent.
- Layouts are saved through `MasterLayoutManager.Save` into `Layouts/<timestamp>.layout.json`. When reloading, `EnsureInspectionRoiDefaults` reinstates the four inspection slots even if they were missing in older files.
- `SyncModelFromShape` keeps `X/Y` aligned with `CX/CY` for circle/annulus ROIs so canvas geometry and persisted layouts stay consistent after edits.

## Note on recipes (GUI vs backend)
- The GUI recipe folder (`<exe>/Recipes/<LayoutName>/...`) is for local layout/dataset persistence.
- Backend recipe ids are independent and normalized for storage under `BDI_MODELS_DIR/recipes/<recipe_id>/...`.
