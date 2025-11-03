# Pipeline de detección — Octubre 2025

## 1. Resumen
El pipeline combina captura GUI + backend PatchCore para detectar anomalías superficiales en discos de freno.

## 2. Pasos
1. **ROI canónica**: la GUI exporta PNG rotado + `shape` JSON + `mm_per_px`.
2. **Preprocesamiento** (backend): normalización, resize, extracción de tokens DINOv2.
3. **Memoria PatchCore**: comparación embedding ↔ coreset.
4. **Mapa de anomalía**: distancias → heatmap (upsample).
5. **Máscara**: aplicar `shape` para ignorar exterior.
6. **Score**: percentil 99 (ajustable) o valor máximo.
7. **Threshold**: tomado de `calibration.json` (`p99_ok` vs `p5_ng`).
8. **Regiones**: binarización + eliminación de ruido + bounding boxes.
9. **Respuesta**: JSON con `score`, `threshold`, `regions`, `heatmap_png_base64`, `token_shape`.

## 3. Calibración
- Con solo OK: `score_percentile` (0.995) → threshold.
- Con NG: `threshold = (p99_ok + p5_ng)/2`.
- Persistir en `calibration.json`.

## 4. Manejo de errores
- `mm_per_px` incorrecto → `409`.
- Falta calibración → `428`.
- ROI sin entrenamiento → `404`.
- `shape` inválido → `400`.

## 5. Métricas y monitoreo
- `elapsed_ms` (tiempo inferencia), `gpu_mem_mb`, `n_embeddings`, `coreset_size`.
- Métricas expuestas via `/metrics` (si se habilita).

## 6. Validación manual
- Scripts `docs/curl_examples.md`.
- Revisar overlays en GUI tras cada cambio en pipeline.
- Confirmar que `regions` se corresponden con heatmap.

## 7. Roadmap
- Evaluar agregación temporal (media móvil) para reducir ruido.
- Añadir modo “batch infer” (varias ROIs en una sola llamada).
