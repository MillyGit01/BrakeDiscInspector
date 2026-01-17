# Backend Playbook

Este playbook orienta a asistentes que modifican el backend FastAPI. Mantiene las restricciones de `agents.md` y el contrato con la GUI.

## 1. Layout
```
backend/
  app.py              # FastAPI routes
  infer.py            # Orquestación de inferencia
  calib.py            # Lógica de calibración
  patchcore.py        # Memoria PatchCore + coreset
  features.py         # Extractor DINOv2 ViT-S/14
  storage.py          # Persistencia datasets/modelos
  roi_mask.py         # Máscaras rect/circle/annulus
  diagnostics.py      # Logs JSONL
  requirements.txt
  README_backend.md
```

## 2. Contrato API (estable)
- `GET /health`
- `POST /fit_ok`
- `POST /calibrate_ng`
- `POST /calibrate_dataset`
- `POST /infer`
- `POST /infer_dataset`
- `GET /manifest`, `GET /state`
- `/datasets/*` (upload/list/file/meta/delete/clear)

Detalles completos en `docs/API_CONTRACTS.md`.

## 3. Persistencia
- `BDI_MODELS_DIR` define la raíz.
- Layout actual (recipe-aware):
```
<BDI_MODELS_DIR>/
  recipes/<recipe_id>/<model_key>/
    <base_name>.npz
    <base_name>_index.faiss
    <base_name>_calib.json
  recipes/<recipe_id>/datasets/<base_name>/{ok,ng}/*
```
- `base_name` = `base64(role_id) + "__" + base64(roi_id)` (urlsafe, sin padding).
- Legacy fallback sigue existiendo (`models/datasets/<role>/<roi>`, `models/<role>_<roi>.*`).

## 4. Recipe ids
- `recipe_id` debe cumplir `^[a-z0-9][a-z0-9_-]{0,63}$`.
- `last` es **reservado** y debe responder 400.
- Los ids son case-insensitive (se normalizan a minúsculas).

## 5. Logging
- Logs estructurados JSONL en `backend_diagnostics.jsonl`.
- Directorio: `BDI_GUI_LOG_DIR` o `%LOCALAPPDATA%` (fallback a `backend/logs`).
- Ver `LOGGING.md`.

## 6. Validaciones y errores
- `400`: memoria inexistente, token mismatch, inputs inválidos.
- `409`: `mm_per_px` mismatch.
- `500`: errores inesperados.

## 7. QA
- Ejecutar `pytest` si aplica.
- Validar manualmente con `docs/API_CONTRACTS.md`.
