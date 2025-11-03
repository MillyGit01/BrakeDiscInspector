# Referencia API — Backend FastAPI (Octubre 2025)

Este documento detalla el contrato estable entre la GUI WPF y el backend de inspección. Todos los parámetros son obligatorios salvo que se indique lo contrario. Los ejemplos emplean `curl` y responden con JSON. Para más contexto ver `docs/API.md`.

## 1. Convenciones generales
- Base URL por defecto: `http://127.0.0.1:8000` (configurable via `BACKEND_BASE_URL`).
- Autenticación opcional: header `X-API-Key: <token>` (habilitable vía variables de entorno, ver `DEPLOYMENT.md`).
- Las respuestas incluyen únicamente los campos indicados en cada sección.
- Los campos numéricos usan punto decimal (`.`).
- Imágenes enviadas en multipart deben ser ROI canónicas (PNG/JPG) generadas por la GUI sin compresión agresiva.

## 2. Endpoints principales

### 2.1 `GET /health`
- **Uso**: verificación de disponibilidad al iniciar la GUI y monitoreo en planta.
- **Respuesta 200**:
```json
{
  "status": "ok",
  "device": "cuda",
  "model": "vit_small_patch14_dinov2.lvd142m",
  "version": "0.1.0"
}
```
- **Errores**: `503` si la carga del modelo falla (campo `detail`).

### 2.2 `POST /fit_ok`
- **Objetivo**: almacenar embeddings OK y actualizar coreset para un `(role_id, roi_id)`.
- **Content-Type**: `multipart/form-data`.
- **Campos**:
  - `role_id` (texto) — Identificador lógico del rol/planta.
  - `roi_id` (texto) — Identificador de la ROI (ej. `inspection-1`).
  - `mm_per_px` (float) — Escala física de la ROI canónica.
  - `images[]` (uno o más archivos) — ROI canónica en PNG/JPG.
  - `operator_id` (opcional) — Identificador del operario.
- **Respuesta 200**:
```json
{
  "role_id": "master",
  "roi_id": "inspection-1",
  "n_embeddings": 512,
  "coreset_size": 128,
  "token_shape": [24, 24],
  "fit_started_at": "2025-10-07T10:11:12Z",
  "fit_duration_s": 6.21,
  "model_version": "2025.4"
}
```
- **Errores**: `400` parámetros faltantes, `409` si el backend detecta mezcla de `mm_per_px` incoherentes.

### 2.3 `POST /calibrate_ng`
- **Objetivo**: calcular y persistir un `threshold` basado en scores OK/NG.
- **Content-Type**: `application/json`.
- **Body mínimo**:
```json
{
  "role_id": "master",
  "roi_id": "inspection-1",
  "mm_per_px": 0.021,
  "ok_scores": [0.21, 0.25, 0.19],
  "ng_scores": [0.74, 0.82],
  "score_percentile": 0.995,
  "area_mm2_thr": 12.5,
  "operator_id": "tech-42"
}
```
- **Respuesta 200**:
```json
{
  "threshold": 0.61,
  "p99_ok": 0.47,
  "p5_ng": 0.73,
  "effective_area_mm2_thr": 12.5,
  "samples_ok": 256,
  "samples_ng": 42,
  "calibrated_at": "2025-10-07T10:18:05Z"
}
```
- **Notas**:
  - `ng_scores` puede omitirse; en ese caso se emplea `score_percentile` sobre OK.
  - El backend persiste `calibration.json` y actualiza `manifest.json`.

### 2.4 `POST /infer`
- **Objetivo**: ejecutar inferencia para una ROI específica.
- **Content-Type**: `multipart/form-data`.
- **Campos**:
  - `role_id`, `roi_id`, `mm_per_px`, `operator_id` (igual que en `fit_ok`).
  - `image` — archivo PNG/JPG (ROI canónica).
  - `shape` — string JSON describiendo la máscara en pixeles canónicos.
    - Ejemplo: `{"kind":"annulus","cx":224,"cy":224,"r":210,"r_inner":140}`.
- **Respuesta 200**:
```json
{
  "score": 0.38,
  "threshold": 0.61,
  "heatmap_png_base64": "iVBOR...",
  "regions": [
    {
      "kind": "blob",
      "x": 42,
      "y": 88,
      "w": 30,
      "h": 24,
      "score": 0.52,
      "area_px": 720,
      "area_mm2": 9.4
    }
  ],
  "token_shape": [24, 24]
}
```
- **Errores**: `428` si no existe calibración previa, `404` si el combo `(role_id, roi_id)` no está entrenado.

## 3. Máscaras (`shape`)
- **Rectángulo**: `{ "kind": "rect", "x": 0, "y": 0, "w": 448, "h": 448 }`
- **Círculo**: `{ "kind": "circle", "cx": 224, "cy": 224, "r": 200 }`
- **Anillo**: `{ "kind": "annulus", "cx": 224, "cy": 224, "r": 210, "r_inner": 120 }`

El backend valida que los valores estén dentro de los límites de la imagen. En caso contrario responde `400` con detalle.

## 4. Versionado del contrato
- Las claves de payload/respuesta están congeladas y versionadas en `model_version`.
- Cualquier cambio requiere actualizar `agents.md` y las guías (`docs/API.md`, `docs/GUI.md`).
- Las rutas legacy (`/datasets/upload_ng`) permanecen disponibles pero la GUI estándar usa los endpoints descritos arriba.

## 5. Ejemplos `curl`
Ver `docs/curl_examples.md` para scripts listos (`fit_ok`, `calibrate_ng`, `infer`).

## 6. Checklist de integración
1. Confirmar `mm_per_px` consistente entre GUI y backend.
2. Enviar `shape` siempre en pixeles de ROI canónica.
3. Propagar errores HTTP al usuario con mensajes traducidos (ver `docs/GUI.md`).
