# Troubleshooting — Octubre 2025

## 1. GUI
- **No conecta al backend**: verificar URL, API Key, firewall.
- **Heatmap desalineado**: confirmar que ROI está congelada; revisar `shape` guardado.
- **UI bloqueada**: revisar que las llamadas HTTP sean `async/await` y usen `ConfigureAwait(false)` cuando corresponda.

## 2. Backend
- **`503 /health`**: revisar logs; normalmente indica fallo al cargar modelo (drivers CUDA).
- **`409 /fit_ok`**: `mm_per_px` distinto al registrado. Volver a calibrar cámara o ajustar manifest.
- **`428 /infer`**: no existe `calibration.json`. Ejecutar `/calibrate_ng`.
- **`404 /infer`**: no se ha ejecutado `/fit_ok`.
- **`400 /infer`**: `shape` inválido (coordenadas fuera de rango).

## 3. Rendimiento
- **Inferencia lenta**: habilitar GPU (`BACKEND_DEVICE=cuda:0`), revisar `PATCHCORE_BATCH_SIZE`, optimizar red.
- **`n_embeddings` muy alto**: ajustar `PATCHCORE_CORESET_RATIO` o limpiar dataset OK.
- **Uso de memoria**: habilitar `PATCHCORE_FAISS_FP16` y `float16` en embeddings.

## 4. Datasets
- **Archivos corruptos**: regenerar miniaturas, verificar PNG.
- **Contadores incorrectos**: borrar `manifest.json` y regenerar usando script `scripts/rebuild_manifest.py`.

## 5. Logs
- Revisar `logs/gui/*.log` y stdout backend (JSON).
- Buscar `request_id` compartido.

## 6. Herramientas
- `docs/curl_examples.md` para replicar llamadas.
- `scripts/check_backend.py` para healthcheck.

## 7. Escalamiento
- Contactar responsable indicado en `agents.md` si persiste.
