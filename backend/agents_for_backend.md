# Backend Playbook — Octubre 2025

Este playbook orienta a asistentes que modifican el backend FastAPI. Mantiene las restricciones de `agents.md` y resalta el contrato con la GUI.

## 1. Layout
```
backend/
  app.py              # FastAPI: /health, /fit_ok, /calibrate_ng, /infer
  infer.py            # Orquestación de inferencia PatchCore
  calib.py            # Lógica de calibración
  patchcore.py        # Memoria PatchCore + coreset
  features.py         # Extractor DINOv2 ViT-S/14
  storage.py          # Persistencia datasets/modelos
  roi_mask.py         # Generación de máscaras rect/circle/annulus
  utils.py            # Utilidades (logging, timers, base64)
  requirements.txt
  README_backend.md
```

## 2. Contrato API (estable)
- `GET /health` → `{ status, device, model, version, uptime_s, roles_loaded, rois }`
- `POST /fit_ok` (multipart) → campos `role_id`, `roi_id`, `mm_per_px`, `images[]`, `operator_id?`
- `POST /calibrate_ng` (JSON) → `{ role_id, roi_id, mm_per_px, ok_scores[], ng_scores?[], score_percentile, area_mm2_thr }`
- `POST /infer` (multipart) → `role_id`, `roi_id`, `mm_per_px`, `image`, `shape`, `operator_id?`
- Respuestas incluyen `token_shape`, `model_version`, `request_id` (header).

## 3. Persistencia
- Directorio raíz configurable vía `BACKEND_DATA_ROOT`.
- Estructura:
```
models/<role>/<roi>/
  embeddings.npy
  coreset.faiss
  manifest.json
  calibration.json
```
- `datasets/<role>/<roi>/ok|ng` se sincroniza con la GUI.
- `manifest.json` guarda `mm_per_px`, `n_embeddings`, `coreset_size`, `threshold`, `model_version`.

## 4. Pipeline de inferencia
1. Validar existencia de coreset/calibración.
2. Procesar imagen (normalización + DINOv2).
3. Calcular distancias kNN con coreset.
4. Upsample mapa a tamaño ROI.
5. Aplicar máscara `shape`.
6. Calcular `score` (`percentil 99` por defecto) y detectar `regions`.
7. Serializar heatmap (PNG base64) y responder JSON.

## 5. Calibración
- Si `ng_scores` presente: umbral = punto medio entre `p99_ok` y `p5_ng`.
- Si solo hay OK: usar `score_percentile` (0.995 por defecto) multiplicado por factor de seguridad.
- Guardar `calibration.json` y actualizar manifest.

## 6. Configuración
- Variables: `BACKEND_DEVICE`, `PATCHCORE_CORESET_RATIO`, `PATCHCORE_DISTANCE`, `PATCHCORE_BATCH_SIZE`, `BACKEND_API_KEY`.
- Config YAML opcional para overrides.
- Logs: usar `structlog` con campos `role_id`, `roi_id`, `request_id`.

## 7. Validaciones
- Rechazar `mm_per_px` inconsistentes (`409`).
- Validar `shape` dentro de límites (`400`).
- Responder `428` si falta calibración, `404` si no existe modelo.

## 8. QA
- Ejecutar `pytest` antes de PR.
- Usar scripts `docs/curl_examples.md` para validar manualmente.
- Revisar `docs/BACKEND.md` para detalles extendidos.
