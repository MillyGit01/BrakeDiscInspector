
# 📌 Actualización — 2025-10-07

**Cambios clave (GUI):**
- Corrección de salto del frame al clicar adorner (círculo/annulus): cálculo y propagación del centro reales en `SyncModelFromShape` y sincronización `X,Y = CX,CY` en `CreateLayoutShape`.
- Bbox SIEMPRE cuadrado para circle/annulus; overlay heatmap alineado.
- Decisiones del proyecto y parámetros vigentes documentados.

**Cambios clave (Backend):**
- PatchCore + DINOv2 ViT-S/14; endpoints `/health`, `/fit_ok`, `/calibrate_ng`, `/infer`; persistencia por `(role_id, roi_id)`.

# agents.md — Project Playbook (GUI + Backend Anomaly Detection)

This document defines **roles, scope, constraints, workflows, and acceptance criteria** for assistants/agents (e.g., GitHub Copilot/Codex) collaborating on this repository.

> **Context**: The project consists of a WPF GUI for **ROI drawing and export** (crop + rotation) and a Python FastAPI backend implementing an **anomaly detection pipeline (PatchCore + DINOv2)**.
> The GUI is responsible for **canonical ROI** generation; the backend **does not** crop/rotate images.

---

## Quick index

- [Repository layout](#0-repository-layout-expected)
- [Roles](#1-roles-agents)
- [Critical constraints](#2-critical-constraints-non-regression)
- [Backend contract](#3-backend-contract-stable)
- [GUI workflows](#4-gui-workflows)
- [Shape JSON](#5-shape-json-canonical-image-coordinates)
- [Coding standards & UX](#6-coding-standards--ux-gui)
- [Acceptance criteria](#7-acceptance-criteria)
- [Test plan](#8-test-plan-qa)
- [Do / Don’t](#9-do--dont-quick-reference)
- [Open questions](#10-open-questions-ask-before-coding-if-unclear)
- [Backend quick-start](#11-backend-quick-start-for-local-dev)
- [Glossary](#12-glossary)
- [Contact points](#13-contact-points)

---

## 0) Repository layout (expected)

```
/backend/
  app.py
  features.py
  patchcore.py
  infer.py
  calib.py
  roi_mask.py
  storage.py
  utils.py
  requirements.txt
  README_backend.md

/gui/   (name may vary: e.g., BrakeDiscInspector_GUI_ROI/)
  # WPF (C#) solution/projects live here.
  # Important classes (names may differ slightly):
  # - MainWindow.xaml / MainWindow.xaml.cs
  # - RoiOverlay.cs, RoiAdorner.cs, ResizeAdorner.cs, RoiRotateAdorner.cs
  # - MasterLayout.cs, PathUtils.cs, RoiCropUtils.cs, ROI.cs, ROIShape.cs, AnnulusShape.cs, etc.
```

If any folder names differ, agents must **detect** and **adapt** paths without changing design intent.

---

## 1) Roles (Agents)

### Agent A — **GUI Integrator (WPF)**
- Add dataset management, training, calibration, and inference flows to the WPF app.
- **Must not modify** adorner/overlay geometry or coordinate transforms.
- **Reuses** the existing canonical ROI export path (crop + rotation).

### Agent B — **Backend Maintainer (FastAPI)**
- Keep endpoints **stable**: `/fit_ok`, `/calibrate_ng`, `/infer`, `/health`.
- Ensure persistence per `(role_id, roi_id)` in `models/`.
- No new breaking parameters or renamed routes.

### Agent C — **Data Curator**
- Organize datasets per `(role_id, roi_id)`:
  - `datasets/<role>/<roi>/ok/*.png` (+ metadata JSON)
  - `datasets/<role>/<roi>/ng/*.png` (+ metadata JSON)

### Agent D — **QA/Validation**
- Build & run tests to verify no regressions in ROI placement, scaling, or overlay alignment.
- Validate backend responses and GUI overlays visually and via logs.

---

## 2) Critical Constraints (Non‑Regression)

1. **Do not touch** adorner codepaths: `RoiAdorner`, `ResizeAdorner`, `RoiRotateAdorner`, `RoiOverlay`.
2. **Do not alter** canvas/image letterboxing or coordinate systems.
3. **Canonical ROI export** (crop + rotation) must follow the **existing** pipeline (e.g., `TryBuildRoiCropInfo(...)`, `TryGetRotatedCrop(...)`).  
   The same pipeline used by “Save Master/Pattern” should be reused.
4. The backend **does not** crop/rotate; it expects the canonical ROI image as input.
5. Preserve public method signatures used elsewhere (avoid breaking events/commands).
6. All network I/O must be **async** (no UI thread blocking).

---

## 3) Backend Contract (stable)

**FastAPI service** (Python) in `/backend/`:

- `GET /health` → `{ status, device, model, version }`
- `POST /fit_ok` (multipart):
  - fields: `role_id`, `roi_id`, `mm_per_px`, `images[]` (one or many PNG/JPG of canonical ROI)
  - returns: `{ n_embeddings, coreset_size, token_shape }`
- `POST /calibrate_ng` (JSON):
  - body: `{ role_id, roi_id, mm_per_px, ok_scores[], ng_scores?[], area_mm2_thr?, score_percentile? }`
  - returns: `{ threshold, p99_ok?, p5_ng?, ... }`
- `POST /infer` (multipart):
  - fields: `role_id`, `roi_id`, `mm_per_px`, `image`, `shape?` (JSON string)
  - returns: `{ score, threshold, heatmap_png_base64, regions[], token_shape }`

> **Note**: `shape` masks the heatmap and supports `rect`, `circle`, `annulus` (see §5).

---

## 4) GUI Workflows

### 4.1 Dataset (per role/ROI)
- UI controls:
  - `RoleId`, `RoiId`, numeric `MmPerPx`
  - Lists (thumbnails): **OK samples**, **NG samples** (optional)
  - Buttons:
    - “Add OK from Current ROI”
    - “Add NG from Current ROI” (optional)
    - “Remove Selected”, “Open Dataset Folder”
- On add:
  1. **Canonicalize** ROI via existing pipeline (crop + rotation).
  2. Save PNG → `datasets/<role>/<roi>/<ok|ng>/SAMPLE_yyyyMMdd_HHmmssfff.png`
  3. Save metadata JSON (same base name):
     ```json
     {
       "role_id": "Master1",
       "roi_id": "Pattern",
       "mm_per_px": 0.20,
       "shape": { "kind": "circle", "cx": 192, "cy": 192, "r": 180 },
       "source_path": "C:\images\raw.png",
       "angle": 32.0,
       "timestamp": "2025-09-28T12:34:56.789Z"
     }
     ```

### 4.2 Train (fit_ok)
- Gather all OK PNGs for current `(role, roi)`.
- POST `/fit_ok` with all images.
- Display `{ n_embeddings, coreset_size, token_shape }` and mark model as trained (store a `model_version` timestamp if desired).

### 4.3 Calibrate (optional, with or without NG)
- If NG exist, compute `scores` for OK/NG by temporarily calling `/infer` (without threshold) and collect returned `score`.
- POST `/calibrate_ng` with arrays and show returned `threshold`.
- Persist calibration info (e.g., per ROI manifest).

### 4.4 Inference
- Canonicalize current ROI → PNG
- Build `shape` JSON (see §5) in **canonical image space**.
- POST `/infer`
- Decode `heatmap_png_base64`, overlay on ROI preview (opacity slider).
- Show `score`, `threshold`, and `regions` with areas in px/mm².
- Provide a **local threshold slider** for exploration (does not change backend threshold unless user clicks “Save Threshold”).

---

## 5) Shape JSON (canonical image coordinates)

- **Rectangle**:
  ```json
  {"kind":"rect","x":0,"y":0,"w":W,"h":H}
  ```

- **Circle**:
  ```json
  {"kind":"circle","cx":CX,"cy":CY,"r":R}
  ```

- **Annulus**:
  ```json
  {"kind":"annulus","cx":CX,"cy":CY,"r":R_OUTER,"r_inner":R_INNER}
  ```

All numbers are in **pixels** of the **canonical ROI image** (post crop+rotation).

---

## 6) Coding Standards & UX (GUI)

- C# 10+, **async/await** for all HTTP operations (`HttpClient`, `MultipartFormDataContent`).
- MVVM: Commands + ViewModels; minimize code-behind.
- Use `ObservableCollection<>` for sample lists; generate thumbnails off the UI thread.
- Provide progress and logs (reuse `AppendLog(...)`).
- Disable actions while a request is running.
- Use `Path.Combine(...)`; avoid hard-coded separators.
- Localize UI strings if the project already uses resources.

---

## 7) Acceptance Criteria

- Can add ≥50 OK samples and see them listed (with thumbnails and metadata).
- `/fit_ok` returns positive `n_embeddings` and `coreset_size`.
- `/calibrate_ng` (if used) returns a valid `threshold`.
- `/infer` returns a non-empty heatmap (base64) and a numeric score.
- Heatmap overlay aligns with ROI visualization (no drift on resize/reload).
- **No regressions** in adorner behavior or ROI placement after window maximization or image reload.

---

## 8) Test Plan (QA)

1. **Startup**: call `/health`; show device/model/version in status bar.
2. **Dataset**: add 5 OK samples from different areas; verify PNG+JSON files exist.
3. **Train**: run `/fit_ok`; check counts > 0. Add more samples and retrain.
4. **Calibrate**: if NG available, compute scores via `/infer` and call `/calibrate_ng`; verify `threshold` persists.
5. **Infer**: run on current ROI; verify visual overlay; test local threshold slider.
6. **Resize/Reload**: after maximize + reload image, overlays remain aligned (verify logs and visual).
7. **Error paths**: disconnect backend / invalid input; GUI shows readable errors, no crashes.

---

## 9) Do / Don’t (Quick Reference)

- ✅ **Do** reuse **existing** canonical ROI export (crop + rotation).
- ✅ **Do** keep adorner/overlay code untouched.
- ✅ **Do** send `shape` in canonical coordinates when calling `/infer`.
- ✅ **Do** run HTTP on background (`async`) and log actions/results.
- ❌ **Don’t** change backend route names or required fields.
- ❌ **Don’t** invent new coordinate transforms or DPI scaling in overlays.
- ❌ **Don’t** block UI thread during uploads/inference.

---

## 10) Open Questions (ask before coding if unclear)

- Which exact methods return the **canonical ROI** in the current branch? (e.g., `TryBuildRoiCropInfo(...)`, `TryGetRotatedCrop(...)` names may vary).
- Where is `mm_per_px` sourced in the GUI (camera config, layout, or per-ROI)?
- Desired **canonical ROI size** (e.g., 384×384 or 448×448). Consistency is beneficial.
- Should the GUI persist a per-ROI **manifest** (counts, model_version, threshold) for quick status?

---

## 11) Backend quick-start (for local dev)

```bash
cd backend
python -m venv .venv
# Windows: .venv\Scripts\activate
# Linux/macOS: source .venv/bin/activate
pip install -r requirements.txt
uvicorn backend.app:app --reload
# http://127.0.0.1:8000/docs
```

---

## 12) Glossary

- **Canonical ROI**: The cropped and rotated image patch that exactly corresponds to the drawn ROI on the GUI, in its own pixel space.
- **Shape JSON**: A JSON object describing the mask (rect/circle/annulus) **in canonical ROI coordinates**.
- **Coreset**: A compact subset of embeddings selected by k-center greedy to approximate the full OK distribution.
- **Score (percentile)**: A robust statistic (e.g., p99) computed on the masked heatmap to summarize anomaly intensity.
- **Threshold**: A scalar decided via calibration to classify ROIs.
- **Letterboxing**: The black/empty areas added around an image to fit aspect ratio; coordination must be preserved between image and overlay.

---

## 13) Contact points

- For ROI alignment issues (drift after maximize/reload): consult the GUI logs and the `SyncOverlayToImage` scheduling/debounce logic. **Do not** modify adorners without explicit approval.
- For backend model behavior: `backend/README_backend.md`, `app.py` endpoints, and `features.py`/`patchcore.py` explain internals.

---

## 14) Actualización Octubre 2025 — Contrato consolidado

- **Frontend ↔ Backend**:
  - `GET /health` → `{ status, device, model, version, uptime_s }`.
  - `POST /fit_ok` → multipart con `role_id`, `roi_id`, `mm_per_px`, `images[]`.
  - `POST /calibrate_ng` → JSON con `ok_scores`, `ng_scores?`, `score_percentile`, `area_mm2_thr`.
  - `POST /infer` → multipart con `image`, `shape` (`rect|circle|annulus`), `mm_per_px`.
  - Todas las respuestas incluyen `token_shape`, `model_version`, `request_id` (header).
- **Persistencia**: `datasets/{role}/{roi}/ok|ng`, `models/{role}/{roi}/` con `manifest.json` y `calibration.json`.
- **Escala física**: `mm_per_px` obligatorio en todas las operaciones.
- **Shape JSON**: siempre en coordenadas de ROI canónica (post-rotación).
- **Logging**: correlacionar `request_id` entre GUI y backend.
- **Pruebas**: ejecutar `pytest` (backend) + validación manual GUI tras cambios en contrato.
