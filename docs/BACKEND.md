# Backend — Guía extendida 2025

## 1. Objetivo
Procesar ROIs canónicas enviadas por la GUI, aplicar PatchCore + DINOv2 y devolver scores, heatmaps y regiones anómalas.

## 2. Arquitectura
- FastAPI (`app.py`) con routers para `/health`, `/fit_ok`, `/calibrate_ng`, `/infer`.
- Motor PatchCore (`patchcore.py`, `infer.py`).
- Persistencia (`storage.py`) bajo `models/` y `datasets/`.
- Máscaras ROI (`roi_mask.py`).

## 3. Ciclo de vida de un `(role_id, roi_id)`
1. **Inicialización**: GUI crea carpeta `datasets/<role>/<roi>/` vacía.
2. **Entrenamiento**: `POST /fit_ok` acumula embeddings OK → `embeddings.npy` + `coreset.faiss`.
3. **Calibración**: `POST /calibrate_ng` calcula `threshold` y lo guarda.
4. **Inferencia**: `POST /infer` usa coreset + threshold para decidir.

## 4. Contrato HTTP
Ver `API_REFERENCE.md`. Resumen:
- Siempre se recibe `mm_per_px` y `shape` (cuando aplica).
- Respuestas devuelven `token_shape`, `heatmap_png_base64`, `regions[]`, `threshold`, `model_version`.
- Header `X-Request-Id` usado por GUI para logs.

## 5. Persistencia
```
models/<role>/<roi>/
  embeddings.npy        # embeddings completos
  coreset.faiss         # índice FAISS (opcional)
  manifest.json         # estado general
  calibration.json      # thresholds
  fit.log               # log opcional
```

## 6. Configuración
- Variables de entorno: `BACKEND_DEVICE`, `BACKEND_DATA_ROOT`, `PATCHCORE_CORESET_RATIO`, `PATCHCORE_DISTANCE`, `BACKEND_API_KEY`.
- `backend/config.yaml` (opcional) para overrides.

## 7. Logs y métricas
- Logs JSON en stdout (`structlog`).
- Métricas Prometheus en `/metrics` (si se habilita `ENABLE_METRICS=true`).
- Campos clave: `elapsed_ms`, `n_embeddings`, `coreset_size`, `score`, `threshold`.

## 8. Tests
```bash
pytest
```
- Fixtures con ROI sintéticas.
- Tests principales: `test_fit_ok.py`, `test_calibrate.py`, `test_infer.py`.

## 9. Troubleshooting
- `503 /health`: modelo no cargado → revisar `BACKEND_DEVICE` y drivers.
- `409 /fit_ok`: `mm_per_px` inconsistente → revisar calibración GUI.
- `428 /infer`: falta calibración → ejecutar `POST /calibrate_ng`.
- `400 /infer`: `shape` fuera de rango.

## 10. Roadmap
- Batch infer para múltiples ROIs.
- Exportación gRPC opcional.
- Compresión embeddings (`float16`).
