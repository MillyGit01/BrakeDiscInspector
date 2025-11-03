# BrakeDiscInspector — dossier 2025

> **Estado**: Octubre 2025. Este repositorio implementa una célula de inspección visual para discos de freno con un **frontend WPF** centrado en el operario y un **backend FastAPI** especializado en detección de anomalías mediante PatchCore + DINOv2. Toda la documentación está sincronizada con la última iteración funcional descrita en `agents.md`.

## 1. Visión general
BrakeDiscInspector cubre de punta a punta el flujo operativo de una célula industrial:

1. **Captura y anotación**: la GUI renderiza la pieza, permite definir ROI rectangulares, circulares o anulares y genera la **ROI canónica** (crop + rotación) respetando la escala física (`mm_per_px`).
2. **Gestión de datasets**: cada ROI mantiene carpetas `ok/` y `ng/` con PNG y metadatos JSON; la GUI muestra contadores, miniaturas y estado de calibración.
3. **Entrenamiento**: el backend ejecuta `fit_ok` en GPU/CPU (PatchCore con backbone DINOv2 ViT-S/14) y persiste embeddings + coreset por `(role_id, roi_id)`.
4. **Calibración**: con muestras NG o percentiles, se fija un `threshold` operativo que se sincroniza con la GUI.
5. **Inferencia**: en producción, la GUI envía ROI canónicas por HTTP, recibe `score`, `regions` y `heatmap` y genera overlays alineados.

Los objetivos clave siguen siendo **latencia mínima**, **operatividad offline** y **trazabilidad total** de cada ROI.

## 2. Contrato frontend ↔ backend (Oct-2025)

| Endpoint | Método | Consumido por | Payload mínimo | Respuesta principal | Notas críticas |
| --- | --- | --- | --- | --- | --- |
| `/health` | `GET` | GUI al arrancar y monitor de planta | — | `{ "status": "ok", "device": "cuda:0", "model": "patchcore-dinov2-s14", "version": "2025.4", "uptime_s": 12 }` | Usado para mostrar estado en la barra de estado. No bloquea otras llamadas. |
| `/fit_ok` | `POST multipart/form-data` | GUI al entrenar memoria OK | `role_id`, `roi_id`, `mm_per_px`, `images[]` (PNG/JPG ROI canónica) | `{ "n_embeddings": 512, "coreset_size": 128, "token_shape": [24, 24] }` | Ejecuta incrementalmente; persiste en `models/{role}/{roi}/fit/`. `mm_per_px` obligatorio para trazabilidad. |
| `/calibrate_ng` | `POST application/json` | GUI tras evaluar scores NG | `{ "role_id": "master", "roi_id": "inspection-1", "mm_per_px": 0.021, "ok_scores": [...], "ng_scores": [...?], "area_mm2_thr": 12.5, "score_percentile": 0.995 }` | `{ "threshold": 0.61, "p99_ok": 0.47, "p5_ng": 0.73, "effective_area_mm2_thr": 12.5 }` | Permite calibrar solo con OK (`score_percentile`) o con NG reales. Backend guarda `calibration.json`. |
| `/infer` | `POST multipart/form-data` | GUI en inspección on-line | `role_id`, `roi_id`, `mm_per_px`, `image` (ROI canónica), `shape` (JSON string en pixeles canónicos) | `{ "score": 0.38, "threshold": 0.61, "is_anomaly": false, "heatmap_png_base64": "iVBOR...", "regions": [{"kind":"blob","x":42,"y":88,"w":30,"h":24,"area_mm2":9.4,"score":0.52}], "token_shape": [24, 24] }` | `shape` admite `rect`, `circle`, `annulus`. El backend máscara la heatmap y respeta `mm_per_px` en áreas. |

### Convenciones adicionales
- **Formato ROI canónica**: PNG 8-bit o JPG sin compresión agresiva, ejes alineados tras rotación. Dimensión típica 448×448 px.
- **Persistencia**: Cada endpoint escribe metadatos en `models/{role_id}/{roi_id}/manifest.json` (estado) y `datasets/{role}/{roi}/`. No renombrar carpetas manualmente.
- **Comunicación**: Todas las llamadas son **asíncronas** en la GUI (uso de `HttpClient` con `await`). Retries con backoff exponencial.

## 3. Arquitectura de carpetas
```
backend/
  app.py                     # FastAPI + routers de fit/calibrate/infer/health
  infer.py                   # Orquestación de inferencia PatchCore + post-proceso
  calib.py                   # Agregación de scores y cálculo de thresholds
  patchcore.py               # Implementación núcleo PatchCore (coreset, memoria)
  features.py                # Backbone DINOv2 S/14 + normalización
  storage.py                 # Persistencia datasets, manifests y caches FAISS
  roi_mask.py                # Utilidades de máscaras (rect/circle/annulus)
  utils.py                   # Helpers comunes (logging estructurado, timers)
  requirements.txt
  README_backend.md          # Cómo ejecutar, probar y desplegar backend

gui/BrakeDiscInspector_GUI_ROI/
  App.xaml / App.xaml.cs     # Punto de entrada
  MainWindow.xaml(.cs)       # Vista/host principal
  ViewModels/WorkflowViewModel.cs
  Services/BackendClient.cs  # Cliente HTTP tipado
  Models/RoiShape.cs, RoiManifest.cs
  Resources/Strings.resx
  README.md                  # Guía específica de GUI

docs/                        # Documentación de referencia enlazada en este README
docker/, scripts/, configs/  # Automatización e infra
```

## 4. Flujo completo de operación
1. **Preparación**: se selecciona `role_id` (p. ej. `master` o `customer_X`) y `roi_id` (`inspection-1..4`). Se verifican presets y cámaras.
2. **Definición ROI**: el operario dibuja ROI con adorners; al pulsar “Guardar ROI” se genera crop canónico y se congelan adorners.
3. **Dataset OK/NG**: botones “Add to OK/NG” envían la ROI canónica al backend (`/fit_ok` o `/datasets/ng/upload` si existe). La GUI guarda copia local con manifiestos y actualiza contadores.
4. **Entrenamiento**: “Train memory fit” → `POST /fit_ok`. El backend crea o actualiza coreset; se muestra resumen en GUI (`n_embeddings`, `coreset_size`).
5. **Calibración**: “Calibrate threshold” envía `ok_scores` (previos) y `ng_scores` si existen. El backend retorna threshold recomendado.
6. **Inferencia**: botón “Evaluate” ejecuta `/infer`, dibuja heatmap y regiones, y registra log con `score`, `threshold`, `decision`.
7. **Trazabilidad**: Los manifiestos incluyen `model_version`, `fit_at`, `calibrated_at`, `operator_id`. Logs y archivos permiten auditoría.

## 5. Roadmap y estado 2025-Q4
- **UI Simplificada**: eliminación de “Load Models”. Presets solo guardan Masters; las Inspection ROIs se reconfiguran por célula.
- **Freeze/Editar ROI**: botón único con icono dual. Mantiene adorners intactos (ver restricciones en `agents.md`).
- **Recolocado robusto**: Masters e Inspection ROIs se alinean con la imagen actual respetando estado “frozen”.
- **Miniaturas reales**: se renderiza ROI canónica (square/circle/annulus) con máscara alpha correcta.
- **Logging**: se redujo ruido dejando logs clave (dataset, fit, calibrate, infer). Ver `LOGGING.md` para niveles.
- **Escalabilidad backend**: soporte multi-GPU con device negotiation y pooling de workers con `asyncio`.

## 6. Requisitos y setup rápido
- **Frontend**: Windows 10/11, .NET 6 (o 7), Visual Studio 2022 con workloads “Desktop Development with C#”.
- **Backend**: Python 3.11/3.12, CUDA 12.x opcional, Torch 2.5.x, FAISS GPU opcional. Sugerido: `python -m venv .venv && pip install -r backend/requirements.txt`.
- **Infra**: Dockerfiles listos (ver `docker/README.md`). CI ejecuta lint + unit tests backend (`pytest`).

## 7. Documentación relacionada
- [`ARCHITECTURE.md`](ARCHITECTURE.md): profundidad de arquitectura de software/hardware.
- [`API_REFERENCE.md`](API_REFERENCE.md): referencia exhaustiva de endpoints con ejemplos.
- [`DATA_FORMATS.md`](DATA_FORMATS.md): definición de metadatos, JSON y formatos ROI.
- [`ROI_AND_MATCHING_SPEC.md`](ROI_AND_MATCHING_SPEC.md): geometrías y matching espacial.
- [`DEV_GUIDE.md`](DEV_GUIDE.md) & [`CONTRIBUTING.md`](CONTRIBUTING.md): estilos de código, flujos Git.
- [`DEPLOYMENT.md`](DEPLOYMENT.md): despliegues on-prem y contenedores.
- [`Prompt_Backend_PatchCore_DINOv2.md`](Prompt_Backend_PatchCore_DINOv2.md): notas de modelo PatchCore + DINOv2.
- [`docs/`](docs/): manuales extendidos (GUI, backend, setup, pipeline, FAQ, etc.).

## 8. Contribuir
Cualquier aporte requiere respetar las restricciones de `agents.md` (no modificar adorners, mantener contrato HTTP). Abra un issue describiendo el contexto, ejecute tests/backend antes de PR y anexe logs relevantes.
