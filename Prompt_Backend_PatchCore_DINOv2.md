# PatchCore + DINOv2 — Decisiones 2025

Este archivo documenta el stack de visión por computador utilizado en el backend y cómo se integra con la GUI.

## 1. Modelo base
- Backbone: `dinov2_vits14` (`timm`), weights `dinov2_vits14` (Meta 2023).
- PatchCore: `k-center greedy` para coreset (ratio 0.01–0.05 configurable).
- Embedding dim: 384.
- Token grid típica: 24×24 (según resolución de entrada).

## 2. Preprocesamiento
- Normalización ImagenNet (`mean=[0.485,0.456,0.406]`, `std=[0.229,0.224,0.225]`).
- Resize + center crop según tamaño ROI (p.ej. 448 px → grid 24×24).
- Augmentations (opcional) durante fit: rotación ±5°, jitter color ligero.

## 3. Flujo de entrenamiento (`/fit_ok`)
1. Cargar imágenes OK desde multipart.
2. Generar embeddings con DINOv2.
3. Actualizar memoria PatchCore (`PatchMemory.update`).
4. Construir coreset con ratio configurable (`PATCHCORE_CORESET_RATIO`).
5. Persistir `embeddings.npy`, `coreset.faiss`, `manifest.json`.
6. Responder a la GUI con métricas (`n_embeddings`, `coreset_size`).

## 4. Inferencia (`/infer`)
1. Generar embedding de la ROI canónica.
2. Comparar contra coreset (distancia Euclidiana / Mahalanobis aprox.).
3. Producir mapa de anomalía (upsample a tamaño ROI).
4. Aplicar máscara `shape` (rect/circle/annulus).
5. Calcular score global (`percentil 99` o `max`) y áreas anómalas.
6. Serializar heatmap a PNG base64.

## 5. Calibración (`/calibrate_ng`)
- Usa scores `ok`/`ng` + `score_percentile` para definir `threshold`.
- Persiste `calibration.json` incluyendo `area_mm2_thr`.

## 6. Hyperparámetros clave
- `PATCHCORE_CORESET_RATIO=0.02`
- `PATCHCORE_PATCH_SIZE=3`
- `PATCHCORE_STRIDE=1`
- `PATCHCORE_DISTANCE="l2"`
- `PATCHCORE_BATCH_SIZE=32`
- `PATCHCORE_USE_GPU=True`
- `PATCHCORE_FAISS_FP16=True`

Todos se pueden sobreescribir via variables de entorno o `backend/config.yaml`.

## 7. Integración con GUI
- La GUI necesita `token_shape` para mapear heatmap → overlay.
- `mm_per_px` asegura que el backend puede convertir `area_px` a `area_mm2`.
- La GUI guarda `model_version` en manifest para detectar cambios (ej. `2025.4`).

## 8. Validación
- Tests en `backend/tests/test_infer.py` (ejecutar `pytest`).
- Scripts de QA en `docs/curl_examples.md`.

## 9. Roadmap
- Evaluar `dinov2_vitg14` para resoluciones mayores.
- Explorar compresión de embeddings para despliegues edge.
