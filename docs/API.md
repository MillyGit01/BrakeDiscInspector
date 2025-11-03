# API Backend — Manual completo 2025

Este documento complementa `API_REFERENCE.md` con explicación de flujos, ejemplos y códigos de error.

## 1. Resumen de endpoints
| Método | Ruta | Descripción |
| --- | --- | --- |
| GET | `/health` | Estado del servicio, device, versión y roles cargados |
| POST | `/fit_ok` | Entrenamiento incremental con muestras OK |
| POST | `/calibrate_ng` | Calibración de threshold usando scores OK/NG |
| POST | `/infer` | Inferencia y generación de heatmap |
| GET | `/manifests/{role}/{roi}` | Estado persistido (opcional) |

## 2. `/health`
- **Uso**: handshake inicial de la GUI, monitorización.
- **Respuesta 200**: ver `API_REFERENCE.md`.
- **Errores**: `503` (`detail`: motivo), `401` si API Key inválida.

## 3. `/fit_ok`
- **Content-Type**: `multipart/form-data`.
- **Campos**: `role_id`, `roi_id`, `mm_per_px`, `images[]`, `operator_id?`.
- **Proceso**:
  1. Validar `mm_per_px` consistente con manifest (si existe).
  2. Cargar imágenes a memoria y convertir a tensores.
  3. Extraer embeddings DINOv2 (GPU si disponible).
  4. Actualizar memoria PatchCore + coreset.
  5. Guardar `embeddings.npy`, `coreset.faiss`, `manifest.json`.
  6. Responder JSON con métricas.
- **Errores comunes**: `400` (faltan campos), `409` (`mm_per_px` mismatched), `500` (error interno guardando coreset).

## 4. `/calibrate_ng`
- **Content-Type**: `application/json`.
- **Body**: ver `API_REFERENCE.md`.
- **Proceso**:
  1. Obtener `p99_ok` (o percentil configurado).
  2. Si hay `ng_scores`: calcular `p5_ng` y promediar.
  3. Guardar `calibration.json` + actualizar manifest.
- **Errores**: `400` (scores vacíos), `404` (no hay fit previo), `500` (no se puede persistir).

## 5. `/infer`
- **Content-Type**: `multipart/form-data`.
- **Campos**: `role_id`, `roi_id`, `mm_per_px`, `image`, `shape`, `operator_id?`.
- **Proceso**:
  1. Validar existencia de coreset y calibración.
  2. Generar embedding → distancia vs coreset.
  3. Upsample + máscara `shape`.
  4. Calcular `score` (p99) y threshold.
  5. Detectar regiones (contornos) y convertir a `area_mm2`.
  6. Serializar heatmap PNG base64.
- **Errores**: `404` (sin modelo), `428` (sin calibración), `409` (`mm_per_px` mismatch), `400` (`shape` inválido), `500` (error interno).

## 6. Ejemplos `curl`
Ver `docs/curl_examples.md`.

## 7. Seguridad
- API Key (`X-API-Key`).
- Rate limiting opcional vía reverse proxy.

## 8. Versionado
- `model_version` devuelto en cada respuesta.
- Cambios de contrato requieren actualizar docs + `agents.md`.

## 9. Buenas prácticas
- Enviar `shape` correcto para evitar falsos positivos.
- Siempre recalibrar tras cambios de lote.
- Revisar logs (GUI + backend) si hay `score` inesperado.
