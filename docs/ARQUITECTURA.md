# Arquitectura detallada — Octubre 2025

Este documento amplía `ARCHITECTURE.md` con diagramas y flujos específicos.

## 1. Visión general
- GUI WPF (frontend) en estación Windows.
- Backend FastAPI (Python) en servidor o misma estación.
- Comunicación vía HTTP (REST + multipart), contrato detallado en `API_REFERENCE.md`.

## 2. Capas GUI
1. **Presentación**: `MainWindow.xaml`, controles `WorkflowControl`, `RoiOverlay`.
2. **ViewModels**: `WorkflowViewModel`, `BackendClientService`, `DatasetViewModel`.
3. **Servicios**: `BackendClient` (HTTP), `FileStorage`, `ThumbnailGenerator`.
4. **Modelos**: `RoiShape`, `RoiManifest`, `DatasetEntry`.

## 3. Capas backend
1. **API**: `app.py` (routers), `schemas.py` (pydantic, si aplica).
2. **Dominio**: `infer.py`, `calib.py`, `patchcore.py`.
3. **Infraestructura**: `storage.py`, `roi_mask.py`, `utils.py`.
4. **ML**: `features.py` (DINOv2, torch).

## 4. Flujo `fit_ok`
1. GUI prepara ROI canónica (`image`, `mm_per_px`, `shape`).
2. Envia `POST /fit_ok` con multipart.
3. Backend genera embeddings + coreset.
4. Persistencia en `models/{role}/{roi}/`.
5. Respuesta con `n_embeddings`, `coreset_size`, `token_shape`.
6. GUI actualiza manifest y contadores.

## 5. Flujo `calibrate_ng`
1. GUI recopila `ok_scores`/`ng_scores` de evaluaciones previas.
2. Llama `POST /calibrate_ng` (JSON).
3. Backend calcula `threshold`, actualiza `calibration.json`.
4. GUI sincroniza manifest, muestra `threshold` y `p99_ok/p5_ng`.

## 6. Flujo `infer`
1. GUI envía ROI canónica + `shape`.
2. Backend produce heatmap, `score`, `regions`.
3. GUI renderiza overlay (alpha) y lista regiones.
4. Logs correlacionados por `request_id`.

## 7. Diagramas
```
GUI (WPF) --/health--> Backend (FastAPI)
GUI (WPF) --/fit_ok--> Backend (PatchCore)
GUI (WPF) --/calibrate_ng--> Backend (Calib)
GUI (WPF) --/infer--> Backend (Inferencia)
```

## 8. Persistencia
- `datasets/`: muestras OK/NG + metadatos JSON.
- `models/`: embeddings, coreset, manifest, calibration.
- `logs/`: JSON (backend) y texto (GUI).

## 9. Integraciones futuras
- Exportación OPC-UA (en evaluación).
- Monitoreo Prometheus (`/metrics`).
- Caching local de heatmaps.
