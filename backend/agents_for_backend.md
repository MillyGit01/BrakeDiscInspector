# Backend Playbook — Octubre 2025

Este playbook orienta a asistentes que modifican el backend FastAPI. Mantiene las restricciones de `agents.md` y resalta el contrato con la GUI.

## 1. Layout
```
backend/
  app.py              # FastAPI: /health, /fit_ok, /calibrate_ng, /infer, /manifest, /datasets/*
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
- `GET /health` → `{ status, device, model, version, request_id, recipe_id, reason? }`
- `POST /fit_ok` (multipart) → campos `role_id`, `roi_id`, `mm_per_px`, `images[]`, opcional `memory_fit`, `recipe_id`, `model_key`
- `POST /calibrate_ng` (JSON) → `{ role_id, roi_id, mm_per_px, ok_scores[], ng_scores?[], score_percentile?, area_mm2_thr?, recipe_id? }`
- `POST /infer` (multipart) → `role_id`, `roi_id`, `mm_per_px`, `image`, `shape`, opcional `recipe_id`, `model_key`
- Respuestas incluyen `request_id` y `recipe_id` **en el JSON** (no headers). Campos detallados en `docs/API_CONTRACTS.md`.
- `/manifest` y `/datasets/*` existen para inspeccionar el estado y gestionar datasets.

## 3. Persistencia
- Directorio raíz configurable vía `BDI_MODELS_DIR` (legacy `BRAKEDISC_MODELS_DIR`).
- Estructura:
```
<BDI_MODELS_DIR>/
  recipes/<recipe_id>/<model_key>/
    <role>__<roi>.npz          # embeddings + token grid (+metadata)
    <role>__<roi>_index.faiss  # índice FAISS opcional
    <role>__<roi>_calib.json   # salida de /calibrate_ng
  datasets/<role>/<roi>/ok|ng  # imágenes de dataset (si se usan los helpers /datasets/*)
```
- `ModelStore.manifest` devuelve memoria, calibración y resumen de datasets (compatibilidad legacy incluida).

## 4. Pipeline de inferencia
1. Validar existencia de memoria y la grilla de tokens.
2. Procesar imagen (normalización + DINOv2).
3. Calcular distancias kNN con coreset.
4. Upsample del mapa a tamaño ROI.
5. Aplicar máscara `shape`.
6. Calcular `score` y detectar `regions`.
7. Serializar heatmap (PNG base64) y responder JSON con `request_id`/`recipe_id`.

## 5. Calibración
- Si `ng_scores` presente: umbral = punto medio entre `p99_ok` y `p5_ng`.
- Si solo hay OK: usar percentil (`score_percentile`, por defecto 99).
- Guardar calibración en `BDI_MODELS_DIR/recipes/<recipe_id>/<model_key>/<role>__<roi>_calib.json`.

## 6. Configuración
- Variables: `BDI_BACKEND_HOST`, `BDI_BACKEND_PORT`, `BDI_MODELS_DIR`, `BDI_CORESET_RATE`, `BDI_SCORE_PERCENTILE`, `BDI_AREA_MM2_THR`, `BDI_CORS_ORIGINS`.
- Config YAML opcional (`configs/app.yaml`) para overrides.
- Logs: `slog` imprime JSON con `request_id`, `recipe_id`, `role_id`, `roi_id`.

## 7. Validaciones
- `400` si falta memoria (`/infer`) o hay token grid mismatch.
- `500` ante excepciones inesperadas con `{error, trace, request_id, recipe_id}`.

## 8. QA
- Ejecutar `pytest` antes de PR.
- Usar `docs/API_CONTRACTS.md` para validar manualmente.
- Revisar `docs/BACKEND.md` para detalles extendidos.
