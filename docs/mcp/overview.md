# MCP Overview — Octubre 2025

Este documento resume la información clave para agentes MCP encargados de coordinar tareas multi-agente.

## 1. Estado actual
- Backend PatchCore + DINOv2 operativo (versión `2025.4`).
- GUI WPF sincronizada con contrato HTTP.
- Datasets organizados por `role_id`/`roi_id`.

## 2. Contrato crítico
- `GET /health` → handshake + metadatos.
- `POST /fit_ok` → multipart (`role_id`, `roi_id`, `mm_per_px`, `images[]`).
- `POST /calibrate_ng` → JSON (`ok_scores`, `ng_scores?`, `score_percentile`, `area_mm2_thr`).
- `POST /infer` → multipart (`image`, `shape` rect/circle/annulus).
- Respuestas devuelven `token_shape`, `model_version`, `request_id`.

## 3. Restricciones
- No tocar adorners ni pipeline ROI en GUI.
- No romper compatibilidad de endpoints.
- `mm_per_px` siempre requerido.

## 4. Checklists
- Backend: `pytest` + pruebas `curl`.
- GUI: fit + calibrate + infer manual.
- Docs: actualizar `.md` afectados.

## 5. Escalamiento
- Consultar responsables listados en `agents.md`.
