# Architecture

This document summarises how the current codebase is wired: which processes exist, how data flows between them and where the persistent artefacts live. All statements below reference concrete classes and files in this repository.

## Components
### WPF front-end (`gui/BrakeDiscInspector_GUI_ROI`)
- `MainWindow` hosts the ROI canvas, dataset panes and batch grid. It loads `MasterLayout`/`InspectionRoiConfig` records from `Layouts/*.layout.json`, wires `WorkflowViewModel` to XAML commands and forwards log messages to `GuiLog`. Layout names double as recipe identifiers for on-disk folders under `<exe>/Recipes/`. The **Clear canvas** command now resets masters, inspection slots, cached baselines and overlays, disables every inspection ROI and reinitialises the wizard state before persisting the empty layout.
- `WorkflowViewModel` coordinates user actions: exporting canonical ROIs (`_exportRoiAsync` delegates to `RoiCropUtils`), calling the backend through `Workflow.BackendClient`, refreshing dataset previews and driving manual/batch inference while keeping the active layout name in sync with `DatasetManager`. Analyze search options (rotation range, scale min/max, matcher thresholds) are synchronised from the layout back into the preset UI so the search sliders mirror what was persisted with the layout instead of reverting to defaults.
- `RoiOverlay` plus the `RoiAdorner`/`ResizeAdorner`/`RoiRotateAdorner` family render and edit ROI shapes. `InspectionAlignmentHelper.MoveInspectionTo` transforms each inspection ROI using the detected Master 1/2 centers and the ROI’s configured anchor (`InspectionRoiConfig.AnchorMaster`), applying translation/rotation/scale per ROI instead of a single global transform.

### Python backend (`backend/`)
- `app.py` is the FastAPI entry point exposing `GET /health`, `POST /fit_ok`, `POST /calibrate_ng` and `POST /infer`.
- `features.py` (DINOv2), `patchcore.py` (coreset and kNN) and `infer.py` (heatmap post-processing) make up the inference pipeline.
- `ModelStore` (`storage.py`) persists embeddings as `.npz`, optional FAISS indices and calibration JSON files under `BDI_MODELS_DIR` (default `models/`).

## Manual data flow
1. The operator loads a single image; `WorkflowViewModel.BeginManualInspection` keeps track of the current file.
2. When **Evaluate** is triggered, `WorkflowViewModel.EvaluateRoiAsync` calls `_exportRoiAsync`, which crops and rotates the ROI (`RoiCropUtils.TryBuildRoiCropInfo` + `TryGetRotatedCrop`) and serialises the ROI mask as JSON.
3. The resulting PNG bytes, ROI-specific `role_id`/`roi_id` and `mm_per_px` are sent to `BackendClient.InferAsync`, which builds a multipart request for `/infer`. If the memory was not fitted yet, `EnsureFittedAsync` runs `/fit_ok` first using the OK samples already present for that ROI so manual evaluation can proceed without manual retries.
4. `backend/app.py` loads the requested memory via `ModelStore`, reconstructs the coreset, runs `InferenceEngine.run` and returns `{score, threshold?, token_shape, regions[], heatmap_png_base64?}`.
5. The GUI updates `Regions`, `InferenceScore` and repaints the overlay through `ShowHeatmapAsync`, which writes the decoded heatmap into the `HeatmapOverlay` image.

## Batch data flow
1. The user selects a folder; `WorkflowViewModel.LoadBatchListFromFolder` enumerates every image recursively and creates `BatchRow` entries.
2. `RunBatchAsync` loops through the snapshot. For each image it calls `RepositionInspectionRoisForImageAsync`, which invokes `InspectionAlignmentHelper` to align the saved inspection ROIs using Master 1/2 baselines. Each inspection slot uses its own anchor selection (`AnchorMaster`), and the transform derives scale/rotation from the vector between the detected master centers; when anchors are missing the ROI is left at its saved position.
3. For each enabled inspection slot: `ExportRoiFromFileAsync` crops the ROI from the batch image, `BackendClient.EnsureFittedAsync` ensures `/fit_ok` was run at least once, and `/infer` is executed. `BatchCellStatus` is set to `Ok` or `Nok` based on the returned score vs. the calibrated threshold.
4. Batch heatmaps share the same overlay control; `SetBatchHeatmapForRoi` and `UpdateBatchHeatmapIndex` keep manual and batch canvases aligned while logging placement metadata.

## Datasets and persistence
- Each layout name is a recipe stored at `<exe>/Recipes/<LayoutName or DefaultLayout>/`. `EnsureInspectionDatasetStructure` creates `Dataset/Inspection_<slot>/{ok,ng}` folders plus a per-slot `Dataset/Inspection_<slot>/Model/` directory. `DatasetManager.SaveSampleAsync` writes PNG+JSON samples into the recipe dataset tree, mapping backend-facing inspection IDs (`inspection-1..4`) to `Inspection_1..4` folder names (non-inspection ROIs use the ROI id directly).
- The backend stores embeddings, indices and calibration files under `BDI_MODELS_DIR/recipes/<recipe_id>/<model_key>/` (default `models/`), using urlsafe base64 names for `<role>__<roi>`; legacy flat/legacy directory layouts are still read for backwards compatibility.
- Batch results can be exported through whatever pipeline consumes the GUI logs; no intermediate CSV is written by the code.

## Configuration and contracts
- GUI: `AppConfig` merges `config/appsettings.json`, `appsettings.json` and environment variables (`BDI_BACKEND_BASEURL`, `BDI_ANALYZE_*`, `BDI_HEATMAP_OPACITY`). Dataset roots are derived from the active layout name, not from `BDI_DATASET_ROOT`. Every backend call carries `role_id`, `roi_id`, `mm_per_px` and the ROI mask (`shape` JSON) so backend regions/heatmaps line up with the GUI overlay without extra transforms.
- Backend: `_env_var` in `app.py` plus `backend/config.py` read the `BDI_*` environment variables. No API keys or `/metrics` routes are implemented in the checked-in code; `/manifest` is available for inspecting stored memory/calibration/dataset counts.

## Logging overview
- GUI logs go to `%LocalAppData%/BrakeDiscInspector/logs/`: `gui.log` (general), `gui_heatmap.log`, `roi_load_coords.log`, `roi_analyze_master.log`. They are plain text with `yyyy-MM-dd HH:mm:ss.fff [LEVEL] message` format (see `Util/GuiLog.cs`).
- Backend logs are emitted through `slog` in `app.py` and printed to stdout as JSON lines containing `ts`, `event`, `role_id`, `roi_id`, `request_id`, `recipe_id`, etc.

## Recipe-aware backend artifacts
- Backend artifacts are stored per recipe under `BDI_MODELS_DIR/recipes/<recipe_id>/...`.
- Recipe context is resolved from an explicit `recipe_id` field (when present) or the `X-Recipe-Id` header.
- `last` is reserved and invalid as a backend recipe id (400).
