# Troubleshooting

This checklist is derived from current GUI and backend behavior. See `LOGGING.md` for log locations.

## GUI-side issues
- **Backend offline:** verify `Backend.BaseUrl` (`config/appsettings.json`) and network connectivity. Check `gui.log` for `[backend]` and `[infer]` entries.
- **Dataset preview empty:** ensure dataset samples exist in the backend (`/datasets/list`) and that the GUI cache under `%LOCALAPPDATA%\BrakeDiscInspector\cache\datasets\...` is writable.
- **Cannot add a sample:** confirm a source image is loaded and the ROI is valid; see `gui.log` for `AddToDataset` messages.
- **Heatmap missing:**
  - If the result is **OK**, the spec requires *no overlay*.
  - For NG results, check `gui_heatmap.log` for placement errors.
- **ROI disabled unexpectedly:** check the **Enabled** checkbox in both the inspection panel and the dataset tab (they are bound to the same property).

### Enabled vs fitted
- **Enabled** is UI-only; it does **not** imply the backend has a fitted model.
- Legacy `HasFitOk` fields in layout files are ignored and should be treated as **stale**.

## Backend-side issues
- **HTTP 400: "Memoria no encontrada"** — no fitted memory at the expected path. Run `/fit_ok` or verify `recipe_id` + `model_key`.
- **HTTP 409: mm_per_px mismatch** — the recipe is locked to a different `mm_per_px`. Align GUI `mm_per_px` or delete recipe metadata to reset.
- **Insufficient OK samples** — `BDI_MIN_OK_SAMPLES` not met for dataset-based training.
- **Calibration missing** — `/infer` returns `threshold=null` if no calibration is stored.

## Batch issue: ROI2
If ROI2 heatmaps are missing after the first batch image:
1. Inspect `gui.log` / `gui_heatmap.log` for ROI2 placement messages.
2. Confirm ROI2 is enabled.
3. Check for guard messages indicating ROI2 geometry reuse.

## Logs to inspect
1. `%LOCALAPPDATA%\BrakeDiscInspector\logs\gui.log`
2. `%LOCALAPPDATA%\BrakeDiscInspector\logs\gui_heatmap.log`
3. `%LOCALAPPDATA%\BrakeDiscInspector\logs\roi_analyze_master.log`
4. Backend `backend_diagnostics.jsonl` (see `LOGGING.md`)
