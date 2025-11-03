# Workflow para agentes (GUI + Backend) — Actualización Octubre 2025

Este documento está orientado a asistentes IA que colaboran en tareas GUI/back-end. Resume restricciones de `agents.md` e incluye el contrato frontend ↔ backend.

## 1. Restricciones críticas
- **No modificar** `RoiAdorner`, `ResizeAdorner`, `RoiRotateAdorner`, `RoiOverlay` salvo instrucción explícita.
- Respetar pipeline de ROI canónica (`TryBuildRoiCropInfo`, `TryGetRotatedCrop`).
- Mantener endpoints estables (`/health`, `/fit_ok`, `/calibrate_ng`, `/infer`).

## 2. Pasos sugeridos para tareas GUI
1. Identificar ViewModel implicado (`WorkflowViewModel`, `BackendClientService`).
2. Confirmar que las llamadas HTTP son `async` y utilizan `CancellationToken` cuando corresponda.
3. Validar actualizaciones de UI: logs (`AppendLog`), contadores de datasets, overlays.
4. Al añadir campos nuevos, actualizar manifests y docs (`DATA_FORMATS.md`).

## 3. Pasos sugeridos para tareas backend
1. Revisar `backend/app.py` para rutas afectadas.
2. Actualizar lógica en `infer.py`, `calib.py`, `storage.py` sin romper contrato.
3. Añadir tests en `backend/tests/` y ejecutar `pytest`.
4. Documentar cambios en `docs/BACKEND.md` y `API_REFERENCE.md`.

## 4. Contrato HTTP (resumen)
- `GET /health` → status + device + version.
- `POST /fit_ok` → multipart con `role_id`, `roi_id`, `mm_per_px`, `images[]`.
- `POST /calibrate_ng` → JSON con `ok_scores`, `ng_scores?`, `score_percentile`, `area_mm2_thr`.
- `POST /infer` → multipart con `image`, `shape` (rect/circle/annulus) en pixeles canónicos.
- Todas las respuestas devuelven `token_shape`, `model_version`, `request_id` (header).

## 5. Documentación obligatoria a revisar antes de intervenir
- `README.md`, `ARCHITECTURE.md`, `API_REFERENCE.md`.
- `docs/GUI.md`, `docs/BACKEND.md`, `docs/PIPELINE_DETECCION.md`.

## 6. Checklist final antes de PR
- [ ] Tests backend (`pytest`).
- [ ] Validación manual GUI (fit/calibrate/infer en dataset demo).
- [ ] Documentación actualizada (este archivo + relevantes).
- [ ] Capturas/heatmaps si hay cambios visuales.
