# AI Onboarding (10-minute quickstart)

This guide is optimized for new contributors and other AI agents. It is aligned with current code and avoids unverifiable claims.

## 10-minute quickstart

### 1) Start the backend
```bash
cd backend
python -m venv .venv
source .venv/bin/activate  # Windows: .venv\Scripts\activate
pip install -r requirements.txt
uvicorn backend.app:app --host 0.0.0.0 --port 8000
```

### 2) Start the GUI
1. Open `gui/BrakeDiscInspector_GUI_ROI/BrakeDiscInspector_GUI_ROI.sln` in Visual Studio.
2. Verify `config/appsettings.json` points to `http://127.0.0.1:8000` or set `BDI_BACKEND_BASEURL`.
3. Run the app.

### 3) Minimal flow
1. Load an image and draw Master 1/2 and at least one inspection ROI.
2. Add OK/NG samples (uploads to backend dataset).
3. Run **fit_ok** (train from dataset) and **calibrate**.
4. Run **Evaluate** (single image) or **Batch**.

## Architecture: GUI vs backend
- **GUI (WPF)**: ROI drawing, canonical crop/warp, layout persistence, master patterns, batch visualization.
- **Backend (FastAPI)**: dataset/model storage, `fit_ok`, calibration, inference, heatmaps.

The backend does **not** crop or rotate images; it consumes the canonical crop and `shape` JSON that the GUI produces.

## Flow: dataset → fit_ok → calibrate → evaluate → batch
1. **Dataset**: GUI uploads ROI crops to `/datasets/ok/upload` or `/datasets/ng/upload`.
2. **fit_ok**: GUI calls `/fit_ok` with `use_dataset=true`.
3. **Calibrate**: GUI calls `/calibrate_dataset` (or `/calibrate_ng` if using precomputed scores).
4. **Evaluate**: GUI sends a single ROI crop to `/infer`.
5. **Batch**: GUI aligns ROIs with master anchors and repeats `/infer` per ROI per image.

## Persistence (backend)
Artifacts are stored under `BDI_MODELS_DIR` (default `models/`):
```
<BDI_MODELS_DIR>/
  recipes/<recipe_id>/<model_key>/
    <base_name>.npz
    <base_name>_index.faiss
    <base_name>_calib.json
  recipes/<recipe_id>/datasets/<base_name>/{ok,ng}/*
```
- `base_name = base64(role_id) + "__" + base64(roi_id)` (urlsafe, no padding).
- `model_key` defaults to `roi_id`.

### Recipe id rules
- Valid: `^[a-z0-9][a-z0-9_-]{0,63}$`.
- Reserved: `last` (HTTP 400 if used).
- Stored case-insensitively (normalized to lowercase).

## Debugging cookbook

### HasFitOk (legacy)
- `HasFitOk` may appear in old layout files.
- It is removed during layout load and should be treated as **stale**.
- Backend state (via `/state` or `/manifest`) is authoritative.

### ROI2 batch heatmap missing
- Known issue: ROI2 may not show after the first batch image.
- Check `gui.log` and `gui_heatmap.log` for ROI2 placement messages.
- TODO: verify and fix placement guard if ROI2 shares ROI1 geometry.

### mm_per_px mismatch (HTTP 409)
- The backend locks `mm_per_px` per `recipe_id` when training or dataset metadata includes it.
- Fix by aligning GUI `mm_per_px` to the recipe, or reset recipe metadata if needed.

### Logs and correlation
- GUI logs: `%LOCALAPPDATA%\BrakeDiscInspector\logs\`.
- Backend JSONL: `backend_diagnostics.jsonl` (see `LOGGING.md`).
- Backend responses include `request_id` and `recipe_id` for correlation.

## Glossary
- **recipe_id**: backend namespace for datasets/models; validated and normalized.
- **role_id**: logical role (Master1, Master2, Inspection).
- **roi_id**: logical ROI identifier (often `inspection-<n>`).
- **model_key**: backend model slot, defaults to `roi_id`.
- **dataset**: OK/NG samples stored in the backend under `BDI_MODELS_DIR`.
- **fit_ok**: PatchCore training step (OK-only memory/coreset).
- **calibrate**: threshold selection using OK/NG scores or dataset sampling.
- **evaluate**: run `/infer` on a single ROI crop.
- **Enabled**: GUI-only toggle for whether a ROI participates in evaluation.
