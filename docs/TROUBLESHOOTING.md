# Troubleshooting

This checklist is derived from current GUI and backend behavior. See `LOGGING.md` for log locations.

## GUI-side Issues
- **Backend offline:** verify `Backend.BaseUrl` (`config/appsettings.json`) and network connectivity. Check `gui.log` for `[backend]` and `[infer]` entries.
- **Backend auto-start does not launch:** check `Backend.AutoStart`, `Backend.AutoStartMode`, `Backend.WslDistro`, `Backend.WslVenvPath` / `Backend.WslCondaEnvironment`, and `Backend.AutoStartWorkingDirectory`. Check `gui.log` for `[backend-autostart]`.
- **Dataset preview empty:** ensure dataset samples exist in the backend (`/datasets/list`) and that the GUI cache under `%LOCALAPPDATA%\BrakeDiscInspector\cache\datasets\...` is writable.
- **Cannot add a sample:** confirm a source image is loaded and the ROI is valid; see `gui.log` for `AddToDataset` messages.
- **Cannot edit an inspection ROI:** if the message is `Inspection ROIs can't be modified once Dataset is created`, the ROI already has dataset samples. Add more images to the dataset or create a new ROI/model slot; master ROIs remain editable.
- **Image thumbnails take time to appear:** the folder loader is building thumbnails. The canvas should show a loading message while the collection is prepared.
- **Heatmap missing:** if the result is OK, the spec requires no overlay. For NG results, check `gui_heatmap.log` for placement errors.
- **ROI disabled unexpectedly:** check the **Enabled** checkbox in both the inspection panel and the dataset tab; they are bound to the same property.
- **Camera source is disabled:** the `Source` browser is only active when the camera provider is `Folder`. `FlirBlackfly` and `Cognex` do not use that field until their vendor SDK/protocol integration is wired.

### Enabled vs fitted
- **Enabled** is UI-only; it does not imply the backend has a fitted model.
- Legacy `HasFitOk` fields in layout files are ignored and should be treated as stale.

## Backend-side Issues
- **HTTP 400: "Memoria no encontrada"** - no fitted memory at the expected path. Run `/fit_ok` or verify `recipe_id` + `model_key`.
- **HTTP 409: mm_per_px mismatch** - the recipe is locked to a different `mm_per_px`. Align GUI `mm_per_px` or delete recipe metadata to reset.
- **Insufficient OK samples** - `BDI_MIN_OK_SAMPLES` not met for dataset-based training.
- **Calibration missing** - `/infer` returns `threshold=null` if no calibration is stored.

## Batch Heatmaps
If a ROI heatmap is missing or appears on the wrong ROI:
1. Inspect `gui.log` / `gui_heatmap.log` for placement messages.
2. Confirm the ROI is enabled.
3. Confirm the selected batch heatmap ROI index matches the result being inspected.
4. Check for guard messages indicating placement suppression or stale canvas geometry.

## Logs to Inspect
1. `%LOCALAPPDATA%\BrakeDiscInspector\logs\gui.log`
2. `%LOCALAPPDATA%\BrakeDiscInspector\logs\gui_heatmap.log`
3. `%LOCALAPPDATA%\BrakeDiscInspector\logs\roi_analyze_master.log`
4. `%LOCALAPPDATA%\BrakeDiscInspector\logs\gui_setup.log`
5. Backend `backend_diagnostics.jsonl` (see `LOGGING.md`)
