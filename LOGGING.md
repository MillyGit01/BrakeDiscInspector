# Logging guide

The repository already contains concrete logging utilities. This page summarises what they write so you can troubleshoot issues quickly.

## GUI logs (`GuiLog`)
- File location: `%LocalAppData%/BrakeDiscInspector/logs/`.
- Files produced:
  - `gui.log`: all general messages routed through `GuiLog.Info/Warn/Error` (dataset saves, batch progress, backend calls).
  - `gui_heatmap.log`: heatmap placement diagnostics from `MainWindow` (transform parameters, cutoff, opacity).
  - `roi_load_coords.log`: emitted when the UI loads/saves ROI coordinates.
  - `roi_analyze_master.log`: master-anchor analysis traces.
  - `gui_setup.log`: GUI setup persistence events (`GuiSetupSettingsService` load/save).
- Format: `yyyy-MM-dd HH:mm:ss.fff [LEVEL] message`. Messages are plain text composed inside the GUI code (`WorkflowViewModel`, `MainWindow`, `DatasetManager`, etc.). There is no request-id, so correlate entries by timestamp and ROI name.
- Usage tips:
  - Search for `[eval]`, `[batch]`, `[dataset]` prefixes when investigating inference/batch/dataset flows.
  - Heatmap alignment issues always write a `[heatmap:tag] ...` line into `gui_heatmap.log` before showing the overlay.
  - Anchor transforms during batch repositioning emit `[ANCHORS] scale=... angleDeltaGlobal=...` from `WorkflowViewModel`; use them to confirm Master1/Master2 detections before aligning inspection ROIs.
  - The **Clear canvas** action logs `[align] Reset solicitado...` / `[align] Reset completado...` around the full wipe of masters and inspection slots, which helps verify the layout was intentionally reset.

## Backend logs (`backend/app.py`)
- Implemented through the helper `slog(event, **kw)` which simply prints `json.dumps` to stdout/stderr.
- Typical events: `fit_ok.request`, `fit_ok.response`, `calibrate_ng.request`, `infer.response`, `infer.error`.
- Fields always include `ts` (epoch seconds) and whatever keyword arguments were passed (e.g. `role_id`, `roi_id`, `elapsed_ms`, `score`).
- `slog` fields include `request_id` and `recipe_id`, which also appear in every JSON response. Use these IDs to correlate GUI requests with backend logs when troubleshooting.
- There is no log rotation; use your process supervisor (systemd, Docker) to capture stdout if you need persistence.

## Troubleshooting workflow
1. Check `gui.log` for obvious frontend validation errors (dataset missing, ROI export failed). These are emitted before sending any HTTP request.
2. Look at backend stdout for the matching `event`/`role_id` combination. If `fit_ok` failed you will see the exception in `fit_ok.error`.
3. If heatmaps look misaligned or blank, inspect `gui_heatmap.log` to verify the calculated transform (`sx`, `sy`, `offX`, `offY`) matches the current canvas size.
4. For dataset issues, `DatasetManager` logs every file it writes plus the reason when validation fails (e.g. missing `/ok` directory).

## Correlation fields
Backend JSON responses include `request_id` and `recipe_id` in the response body. Use these fields (and the backend’s structured logs) to correlate GUI actions to backend operations.
