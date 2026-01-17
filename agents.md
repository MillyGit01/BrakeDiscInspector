# Project Playbook (GUI + Backend)

This file defines **roles, scope, constraints, workflows, and acceptance criteria** for assistants working in this repository.

> **Context**: WPF GUI for ROI drawing/export + Python FastAPI backend for PatchCore + DINOv2 inference.

## 0) Quick index
- [Source of truth](#1-source-of-truth)
- [Critical constraints](#2-critical-constraints)
- [Backend contract](#3-backend-contract)
- [Persistence layout](#4-persistence-layout)
- [Logging](#5-logging)
- [UI specs (heatmap/badge)](#6-ui-specs-heatmapbadge)
- [Master patterns](#7-master-patterns)
- [ROI enable/disable](#8-roi-enabled-vs-fitted)
- [Testing](#9-testing)

## 1) Source of truth
- **GUI** is the source of truth for **ROI geometry** and **canonical ROI export**.
- **Backend** is the source of truth for **datasets, models, calibration, and inference**.

The backend **does not** crop or rotate images; it trusts the GUI crop + `shape` mask.

## 2) Critical constraints
1. **Do not modify** adorner/overlay geometry classes (`RoiAdorner`, `ResizeAdorner`, `RoiRotateAdorner`, `RoiOverlay`) without explicit approval.
2. Preserve the canonical ROI export pipeline (crop + rotation). Reuse existing utilities.
3. Keep endpoint names and required fields stable.
4. Do not invent new coordinate transforms between GUI and backend.

## 3) Backend contract
Endpoints implemented in `backend/app.py`:
- `GET /health`
- `POST /fit_ok`
- `POST /calibrate_ng`
- `POST /calibrate_dataset`
- `POST /infer`
- `POST /infer_dataset`
- `GET /manifest`, `GET /state`
- `/datasets/*` helpers (upload/list/file/meta/delete/clear)

See `docs/API_CONTRACTS.md` for exact payloads and responses.

## 4) Persistence layout
Backend artifacts live under `BDI_MODELS_DIR` (default `models/`):
```
<BDI_MODELS_DIR>/
  recipes/<recipe_id>/<model_key>/
    <base_name>.npz
    <base_name>_index.faiss
    <base_name>_calib.json
  recipes/<recipe_id>/datasets/<base_name>/{ok,ng}/*
```
- `base_name = base64(role_id) + "__" + base64(roi_id)` (urlsafe, no padding).
- `recipe_id` is lowercased, validated by `^[a-z0-9][a-z0-9_-]{0,63}$`, and must not be `last`.
- Legacy fallbacks exist (flat files and `models/datasets/<role>/<roi>`).

## 5) Logging
- GUI logs are plain text under `%LOCALAPPDATA%\BrakeDiscInspector\logs\`.
- Backend diagnostics are JSONL in `backend_diagnostics.jsonl`.
- **Source of truth:** `LOGGING.md`.

## 6) UI specs (heatmap/badge)
> Spec only; if implementation diverges, mark TODO and update UI later.

- Heatmap overlay: show **red** zones **only** when result is **NG** and heatmap is the NG cause.
- OK/NG badge: square badge, white bold `OK`/`NG` on green/red background.

## 7) Master patterns
- GUI-only; never uploaded to backend.
- Saved as `master1_pattern.png` / `master2_pattern.png` under `<exe>/Recipes/<LayoutName>/Master/`.
- Older versions are moved to `Master/obsolete/`.
- Cache invalidation relies on **path + mtime + size**; change any to avoid stale cache.

## 8) ROI Enabled vs fitted
- **Enabled** is a GUI-only toggle (checkbox in inspection panels and dataset tab).
- Backend fitted state is independent and comes from `/state` or `/manifest`.
- Legacy `HasFitOk` fields in layout files are ignored and considered **stale**.

## 9) Testing
- Backend: run `pytest` when logic changes.
- GUI: manual check for ROI export, dataset upload, `fit_ok`, calibration, and batch alignment.

## References
- `docs/INDEX.md` (documentation map)
- `docs/AI_ONBOARDING.md` (quickstart + glossary)
- `docs/ARCHITECTURE.md`
- `docs/FRONTEND.md`
- `docs/BACKEND.md`
