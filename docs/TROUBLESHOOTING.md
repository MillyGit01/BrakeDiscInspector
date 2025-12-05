# Troubleshooting

This checklist is derived from the current GUI/Backend code.

## GUI-side issues
- **Backend unavailable:** `WorkflowViewModel.RefreshHealthAsync` reports the exception and `GuiLog` prints `[health] EX`. Verify `Backend.BaseUrl` (appsettings or `BDI_BACKEND_BASEURL`) and ensure `BackendClient.BaseUrl` points to the running FastAPI instance.
- **Dataset path rejected:** dataset panels read from the active recipe (`Recipes/<LayoutName>/Dataset/`). If folders were deleted manually, `RefreshDatasetCommand` will show `Select a dataset`; recreate `Inspection_<n>/ok` and `Inspection_<n>/ng` under the recipe or reload the layout to let `EnsureInspectionDatasetStructure` rebuild them.
- **Cannot add sample:** `AddRoiToDatasetAsync` logs `AddToDataset aborted: ...`. Usually no image is loaded (`_getSourceImagePath()` null) or the ROI is not frozen; fix the ROI, re-export and retry.
- **Heatmap missing or misaligned:** check `%LocalAppData%/BrakeDiscInspector/logs/gui_heatmap.log` for the `[heatmap:tag]` entry. If `Transform Img→Canvas` shows `sx=0`, wait for the canvas to finish measuring (resize window or trigger a redraw). Batch mode also waits for `_batchAnchorReadyForStep`; ensure Master ROIs are visible.

## Backend-side issues
- **`400` with "Memoria no encontrada":** `/infer` could not load `<role>__<roi>.npz`. Run `/fit_ok` again via the GUI or copy the expected file into `BDI_MODELS_DIR`.
- **Token grid mismatch:** `/infer` returns `error` mentioning `Token grid mismatch`. The embedding grid stored during `/fit_ok` does not match the new input resolution. Rebuild the memory by running `/fit_ok` on crops with the current size.
- **Calibration missing:** The GUI will still display scores even if `threshold` is `null`. Either run `/calibrate_ng` (button **Calibrate**) or set `InspectionRoiConfig.ThresholdDefault` to a conservative value.
- **Slow inference:** Heatmaps are generated with Gaussian blur (`blur_sigma=1.0`). If using CPU only, consider reducing image size in the GUI, ensuring `torch` is compiled for CPU and that the Docker image has enough CPU shares.

## Logs to inspect
1. `%LocalAppData%/BrakeDiscInspector/logs/gui.log` – look for `[eval]` or `[batch]` entries.
2. Backend stdout (or Docker logs) – `slog("infer.response", ...)` includes `elapsed_ms`, `score`, `threshold`.
3. `%LocalAppData%/BrakeDiscInspector/logs/gui_heatmap.log` – overlay placement details.
4. `%LocalAppData%/BrakeDiscInspector/logs/roi_analyze_master.log` – master anchor detection status when batch alignment fails.
