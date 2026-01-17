# Workflow para agentes (GUI + Backend) — Actualización

Este documento orienta a asistentes IA que colaboran en tareas GUI/back-end. Resume restricciones de `agents.md` y el contrato frontend ↔ backend.

## 1. Restricciones críticas
- **No modificar** `RoiAdorner`, `ResizeAdorner`, `RoiRotateAdorner`, `RoiOverlay` salvo instrucción explícita.
- Reutilizar la **ROI canónica** (crop + rotación) existente.
- Mantener endpoints estables (`/health`, `/fit_ok`, `/calibrate_ng`, `/infer`, `/calibrate_dataset`, `/infer_dataset`, `/manifest`, `/datasets/*`).

## 2. Pasos sugeridos para tareas GUI
1. Identificar el ViewModel o control implicado.
2. Confirmar llamadas HTTP `async` y no bloquear el UI thread.
3. Validar UI: overlays, dataset previews, batch alignment.
4. Actualizar documentación (`docs/FRONTEND.md`, `docs/API_CONTRACTS.md`, `LOGGING.md`).

## 3. Pasos sugeridos para tareas backend
1. Revisar `backend/app.py` para rutas afectadas.
2. Mantener compatibilidad de `recipe_id`, `role_id`, `roi_id`, `model_key`.
3. Actualizar `docs/BACKEND.md` y `docs/API_CONTRACTS.md`.
4. Ejecutar `pytest` cuando aplique.

## 4. Contrato HTTP (resumen)
- `GET /health` → `{ status, device, model, version, request_id, recipe_id, reason? }`.
- `POST /fit_ok` (multipart) → `role_id`, `roi_id`, `mm_per_px`, `images[]`, `use_dataset`, `memory_fit`, `recipe_id`, `model_key`.
- `POST /calibrate_ng` (JSON) → `role_id`, `roi_id`, `ok_scores[]`, `ng_scores?`, `score_percentile`, `area_mm2_thr`.
- `POST /calibrate_dataset` (JSON) → calibración usando datasets backend.
- `POST /infer` (multipart) → `image`, `shape` (rect/circle/annulus) en coordenadas canónicas.
- `POST /infer_dataset` (JSON) → inferencia sobre datasets backend.

Todos los responses incluyen `request_id` y `recipe_id` en el JSON; el backend también devuelve `X-Request-Id` en headers.

## 5. Logs (fuente de verdad)
- Ver `LOGGING.md` para rutas y formatos.
- Backend escribe JSONL en `backend_diagnostics.jsonl` (directorio resuelto por `BDI_GUI_LOG_DIR` o `%LOCALAPPDATA%`).

## 6. Recipe IDs
- `last` está **reservado** y el backend debe responder 400 si se envía.
- `recipe_id` se normaliza a minúsculas y debe cumplir `^[a-z0-9][a-z0-9_-]{0,63}$`.

## 7. Checklist final antes de PR
- [ ] Tests backend (`pytest`) si aplica.
- [ ] Validación manual GUI (fit/calibrate/infer/batch).
- [ ] Documentación actualizada (incluye `docs/INDEX.md`).
- [ ] Logs revisados (GUI + backend).
