# Arquitectura integral — BrakeDiscInspector 2025

> Documento maestro que describe la arquitectura lógica, física y operacional del sistema. Complementa `README.md` y `docs/ARQUITECTURA.md` aportando una vista resumida de alto nivel.

## 1. Componentes principales

### 1.1 Frontend (GUI WPF)
- Proyecto `.NET 6/7` orientado a escritorio Windows.
- Arquitectura MVVM ligera (`MainWindow`, `WorkflowViewModel`, `BackendClientService`, modelos de dominio `RoiShape`, `DatasetEntry`).
- Renderizado de imagen base y adorners para ROIs (rect, circle, annulus) sin modificar geometría legacy (`RoiAdorner`, `ResizeAdorner`, `RoiRotateAdorner`).
- Generación de ROI canónica mediante `TryBuildRoiCropInfo` → `TryGetRotatedCrop`, asegurando alineación y escala (`mm_per_px`).
- Gestión de presets: maestros, inspections (1..4), estado “frozen”.
- Cliente HTTP asíncrono para interactuar con endpoints `GET /health`, `POST /fit_ok`, `POST /calibrate_ng`, `POST /infer`.
- Persistencia local: carpetas `datasets/{role}/{roi}/ok|ng`, manifiestos JSON, thumbnails PNG.

### 1.2 Backend (FastAPI + PatchCore/DINOv2)
- Servicio Python 3.11+ desplegable via `uvicorn` o Docker.
- Capas:
  - **API Layer** (`app.py`): routers, validación de payloads, logging estructurado (JSON).
  - **Domain Layer** (`infer.py`, `calib.py`, `patchcore.py`): ejecución de PatchCore, cálculos de thresholds.
  - **Infrastructure Layer** (`storage.py`, `roi_mask.py`, `utils.py`): IO datasets, cachés FAISS, máscaras ROI.
- Modelo `PatchCore` con backbone `dinov2_vits14` + pre-procesamiento `torchvision`.
- Persistencia en `models/{role_id}/{roi_id}/`: embeddings `.npy`, coreset `.faiss`, `calibration.json`, `manifest.json`.
- Escalable a GPU (CUDA 12.x) con device negotiation (`torch.cuda.is_available`).

### 1.3 Infraestructura auxiliar
- Directorio `configs/` con YAML de cámaras, presets.
- `docker/` contiene Dockerfile para backend + compose con GPU.
- `scripts/` incluye utilidades CLI (sincronización datasets, conversión imagen).
- CI/CD (`docs/CI_CD.md`) se basa en GitHub Actions: lint + `pytest` backend.

## 2. Flujo de datos extremo a extremo

```
Imagen cruda cámara
   ↓ (GUI)                             Estado actual (manifiestos JSON)
Renderizado WPF + adorners --------> Persistencia local datasets
   ↓ crop/rotación                            ↓ sincronización
ROI canónica (PNG + shape JSON) --------> Backend FastAPI
                                       ↓
                                 PatchCore (fit/calib/infer)
                                       ↓
                             Respuesta JSON + heatmap (b64)
                                       ↓
                             GUI: overlay, logs, dashboards
```

## 3. Contrato y responsabilidades
- **Frontend**
  - Debe generar siempre la ROI canónica y enviar `mm_per_px` real.
  - Ejecuta llamadas HTTP de forma asíncrona, captura errores y muestra feedback.
  - No realiza inferencia ni entrenamiento local (solo reenvía datasets a backend).
- **Backend**
  - No realiza cropping/rotación: confía en el input de la GUI.
  - Mantiene estructura de persistencia y versión de modelo (`model_version` en manifest).
  - Devuelve `heatmap_png_base64` alineada con ROI canónica y `regions[]` ya mapeadas en pixeles canónicos.

## 4. Diagramas de despliegue

```
[Operario PC Windows] --HTTP--> [Servidor Backend]
      |                               |
  GUI WPF (.NET)            FastAPI + Torch + FAISS
      |                               |
  Local datasets            GPU (opcional) / CPU
```

- **Modo standalone**: GUI y backend en la misma estación (localhost) con GPU compartida.
- **Modo célula**: GUI en estación de inspección; backend en servidor con múltiples GPUs; comunicación LAN segura.

## 5. Escenarios clave
- **Entrenamiento incremental**: el operario agrega muestras, lanza `fit_ok`. Backend actualiza coreset sin borrar histórico.
- **Recalibración**: cambios de lote requieren recalcular threshold; GUI consolida `ok_scores`/`ng_scores` y llama `/calibrate_ng`.
- **Producción continua**: cada ROI evaluada genera logs y heatmaps. Threshold se puede ajustar manualmente en GUI pero se registra.

## 6. Seguridad y observabilidad
- Autenticación opcional mediante header API Key (ver `DEPLOYMENT.md`).
- Logs estructurados (JSON) tanto en GUI (archivo local) como en backend (stdout). Integración con Splunk/ELK.
- Métricas: backend expone `/metrics` (Prometheus) opcional, con tiempos de inferencia, memoria GPU, tamaño coreset.

## 7. Referencias cruzadas
- Detalles técnicos exhaustivos del backend: `docs/BACKEND.md`, `backend/README_backend.md`.
- Especificaciones GUI y UX: `docs/GUI.md`.
- Diagramas detallados: `docs/ARQUITECTURA.md`.
- Contrato API extendido: `API_REFERENCE.md` y `docs/API.md`.
