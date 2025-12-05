# Front-end (WPF) reference

The GUI is implemented in `gui/BrakeDiscInspector_GUI_ROI`. This guide describes the structure that actually exists in the codebase and the workflows exposed to operators.

## Application structure
- **Entry point:** `App.xaml` loads `MainWindow`. `AppConfigLoader` merges `config/appsettings.json`, `appsettings.json` and environment overrides (`BDI_BACKEND_BASEURL`, `BDI_ANALYZE_*`, `BDI_HEATMAP_OPACITY`).
- **Main window:** `MainWindow.xaml.cs` instantiates `WorkflowViewModel`, sets up ROI overlays, handles image loading and delegates log output to `GuiLog`. It resolves the recipe root via `EnsureDataRoot`, which maps the current layout name to `<exe>/Recipes/<LayoutName or DefaultLayout>/`.
- **View model:** `WorkflowViewModel` contains all commands bound in XAML:
  - Dataset commands: `AddRoiToDatasetOkCommand`, `AddRoiToDatasetNgCommand`, `RemoveSelectedCommand`, `RefreshDatasetCommand`, `BrowseDatasetCommand`, `OpenDatasetFolderCommand`.
  - Training/calibration: `TrainSelectedRoiCommand`, `CalibrateSelectedRoiCommand`, `InferFromCurrentRoiCommand`, `EvaluateSelectedRoiCommand`, `EvaluateAllRoisCommand`, `InferEnabledRoisCommand`.
  - Batch: `BrowseBatchFolderCommand`, `StartBatchCommand`, `PauseBatchCommand`, `StopBatchCommand`.
  - Health: `RefreshHealthCommand`.
- **Backend client:** `Workflow/BackendClient` wraps `HttpClient` with strongly typed methods for `/health`, `/fit_ok`, `/calibrate_ng`, `/infer` and provides helper APIs such as `EnsureFittedAsync`.

## ROI editing and persistence
- **Shapes:** `RoiModel` supports `Rectangle`, `Circle` and `Annulus`. Each ROI tracks both geometric data (center, radii, angle) and frozen state.
- **Overlay/adorner:** `RoiOverlay` renders active shapes; `RoiAdorner`/`ResizeAdorner`/`RoiRotateAdorner` manipulate them. The adorner code is shared between manual and batch canvases and must stay untouched per `agents.md`.
- **Master layouts:** `MasterLayoutManager` reads/writes `Layouts/*.layout.json`. `MasterLayout` includes Master 1/2 pattern/search ROIs, inspection baselines and UI/analyse options. When loading a layout, `MainWindow` calls `EnsureInspectionDatasetStructure`, which assigns `Inspection_<n>` directories under the recipe’s `Dataset` folder to each slot.
- **Alignment:** `InspectionAlignmentHelper.MoveInspectionTo` applies translation/rotation/scale from the saved Master anchors (`Master1Pattern`/`Master2Pattern`) to the currently detected anchors before batch inspection. If the transform fails the ROI falls back to the midpoint between the anchors.

## Dataset layout
- Root per layout: `RecipePathHelper` creates `<AppContext.BaseDirectory>/Recipes/<LayoutName or DefaultLayout>/` with `Dataset/`, `Model/` and `Master/` subfolders. `WorkflowViewModel.SetLayoutName` propagates the layout name to `DatasetManager` so every command reads/writes inside that recipe tree.
- For every inspection slot (1–4) the GUI creates `Dataset/Inspection_<n>/{ok,ng}` for the on-disk dataset plus a `Model/Inspection_<n>/` directory for backend artefacts. Samples shown in the UI come from `Dataset/datasets/<roi_id>/<ok|ng>/` where `roi_id` is the stable identifier used in backend calls (e.g. `inspection-1`).
- `DatasetManager.SaveSampleAsync` writes:
  - PNG file: `SAMPLE_<roleId>_<roiId>_<UTC timestamp>.png` under `Dataset/datasets/<roi_id>/ok` or `Dataset/datasets/<roi_id>/ng` (folder created on demand).
  - Metadata JSON (same basename) containing `{role_id, roi_id, mm_per_px, shape_json, source_path, angle, timestamp}`.
- Dataset validation performed by `RefreshDatasetCommand` walks the current recipe tree; CSV imports remain available but the canonical layout is the recipe folder. Older `<data root>/rois/Inspection_<n>` locations are only kept for backward compatibility paths already present in the layout files.

## Manual vs batch inspection
### Manual
1. Load an image through the toolbar. `WorkflowViewModel.BeginManualInspection` remembers the path.
2. Select an inspection ROI; the canvas shows adorners so it can be edited/frozen.
3. Click **Evaluate** (or **Evaluate all enabled ROIs**). `EvaluateRoiAsync` exports the canonical ROI via `_exportRoiAsync`, builds `InferRequest` with `RoleId`, `RoiId`, `MmPerPx` and `ShapeJson`, calls `BackendClient.InferAsync` (multipart POST to `/infer`), then updates `Regions`, `InferenceScore` and the heatmap overlay.
4. `BackendMemoryNotFittedException` triggers an automatic `/fit_ok` using the OK samples already present under that ROI’s dataset path, so the UI can recover without manual intervention.

### Batch
1. Use **Browse batch folder** to pick a directory. `LoadBatchListFromFolder` collects every supported image and displays it in the batch grid.
2. Press **Start batch**. `RunBatchAsync` iterates through all `BatchRow`s, calls `RepositionInspectionRoisForImageAsync` to align the saved ROIs and exports each enabled ROI via `ExportRoiFromFileAsync`.
3. Before the first inference per ROI the view-model calls `BackendClient.EnsureFittedAsync` to make sure `/fit_ok` was executed. Each result updates the per-row `BatchCellStatus` and the shared heatmap overlay; optional pauses between ROIs are controlled by `BatchPausePerRoiSeconds`.
4. Batch commands support pause/stop; they use `_pauseGate` plus a cancellation token to stop the loop gracefully.

## Backend contract from the GUI
The GUI only calls the four routes implemented in `backend/app.py`. Every request includes:
- `role_id`: derived from the ROI role (`Master1`, `Master2`, `Inspection`) or the inspection slot (e.g. `inspection-1`).
- `roi_id`: inspection slot key (default `inspection-<n>`).
- `mm_per_px`: resolved from `Preset` or the override UI field.
- `shape`: JSON string describing the ROI in canonical coordinates (rectangles use `{kind:"rect", x, y, w, h}`, circles `{kind:"circle", cx, cy, r}`, annulus `{kind:"annulus", cx, cy, r, r_inner}`).
`BackendClient` serialises these fields into multipart (`/fit_ok`, `/infer`) or JSON (`/calibrate_ng`) using `HttpClient`.

## Error handling
- All HTTP calls run on background threads (`AsyncCommand`, `await`). Failure paths call `ShowMessageAsync` and clear heatmap/region state via `ResetAfterFailureAsync`.
- Dataset errors mark `InspectionRoiConfig.DatasetStatus` with human-readable messages such as `Folder must contain /ok and /ko subfolders` or `CSV needs both OK and KO samples`.
- Heatmap rendering issues are logged to `%LocalAppData%/BrakeDiscInspector/logs/gui_heatmap.log`; the UI falls back to hiding the overlay when a PNG fails to decode.

## Useful files
- ROI geometry: `RoiCropUtils.cs`, `InspectionAlignmentHelper.cs`, `RoiOverlay.cs`.
- Dataset management: `Workflow/DatasetManager.cs`, `Workflow/DatasetSample.cs`.
- Batch orchestration: `Workflow/WorkflowViewModel.cs` (`RunBatchAsync`, `UpdateBatchHeatmapIndex`, etc.).
- Backend communications: `Workflow/BackendClient.cs`.
