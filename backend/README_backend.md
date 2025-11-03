# Backend FastAPI — Manual 2025

## 1. Descripción
Servicio FastAPI que implementa PatchCore + DINOv2 para inferencia de anomalías sobre ROIs canónicas enviadas por la GUI WPF.

## 2. Requisitos
- Python 3.11/3.12
- CUDA 12.x + drivers NVIDIA (opcional pero recomendado)
- `pip install -r requirements.txt`

## 3. Puesta en marcha
```bash
cd backend
python -m venv .venv
source .venv/bin/activate  # Windows: .venv\Scripts\activate
pip install -r requirements.txt
uvicorn backend.app:app --host 0.0.0.0 --port 8000
```
- Variables: `BACKEND_DEVICE=cuda:0`, `BACKEND_DATA_ROOT=../datasets`.

## 4. Endpoints
- `GET /health` → estado y metadatos del modelo.
- `POST /fit_ok` → entrenamiento incremental (multipart).
- `POST /calibrate_ng` → cálculo de threshold (JSON).
- `POST /infer` → inferencia con heatmap (multipart).

Ver `API_REFERENCE.md` para payloads completos.

## 5. Estructura
```
app.py          # Routers FastAPI
infer.py        # pipeline inferencia
calib.py        # cálculos de threshold
patchcore.py    # clase PatchMemory
features.py     # extractor DINOv2
storage.py      # persistencia (datasets/models)
roi_mask.py     # máscaras ROI
utils.py        # utilidades comunes
```

## 6. Datos
- `datasets/<role>/<roi>/ok|ng` — muestras recibidas.
- `models/<role>/<roi>/` — embeddings, coreset, manifests.
- `logs/` — salida JSON (std out).

## 7. Tests
```bash
pytest
```
- Fixtures con datasets sintéticos.

## 8. Contrato GUI ↔ Backend
- La GUI envía ROI canónica + `mm_per_px` + `shape` JSON.
- El backend devuelve `score`, `threshold`, `heatmap_png_base64`, `regions[]`, `token_shape`.
- `request_id` en headers para correlación.

## 9. Troubleshooting
- `503 /health`: revisar carga de modelo (`logs/`).
- `409 /fit_ok`: `mm_per_px` inconsistente.
- `428 /infer`: falta calibración.
- `400 /infer`: `shape` inválido.

## 10. Roadmap backend
- Añadir compresión de coreset.
- Soporte multi-GPU con pool de workers.
- Exportar métricas detalladas (`/metrics`).
