# Formatos de datos — BrakeDiscInspector 2025

Este documento consolida los formatos de intercambio entre la GUI y el backend, así como la disposición en disco de datasets y modelos. Complementa `ROI_AND_MATCHING_SPEC.md` y `docs/DATASET_Y_ROI.md`.

## 1. Imágenes
- **ROI canónica**: PNG (8-bit RGB) recomendado, JPG permitido (calidad ≥ 95). Dimensiones típicas 448×448 px.
- **Heatmap**: generado por el backend como PNG 8-bit con colormap Jet. Se devuelve en base64.
- **Miniaturas**: la GUI guarda thumbnails PNG de 256 px para cada muestra.

## 2. Carpetas de datasets
```
datasets/
  <role_id>/
    <roi_id>/
      ok/
        2025-10-07T10-15-22Z_ok_001.png
        2025-10-07T10-15-22Z_ok_001.json
      ng/
        2025-10-07T11-03-02Z_ng_001.png
        2025-10-07T11-03-02Z_ng_001.json
      manifest.json
      calibration.json
```

### 2.1 Metadatos por muestra (`*.json`)
```json
{
  "role_id": "master",
  "roi_id": "inspection-1",
  "kind": "ok",                      // ok | ng
  "mm_per_px": 0.0213,
  "width": 448,
  "height": 448,
  "shape": {"kind":"circle","cx":224,"cy":224,"r":210},
  "captured_at": "2025-10-07T10:15:22.513Z",
  "operator_id": "tech-42",
  "notes": "lote 123"
}
```

### 2.2 `manifest.json`
```json
{
  "role_id": "master",
  "roi_id": "inspection-1",
  "mm_per_px": 0.0213,
  "ok_count": 512,
  "ng_count": 42,
  "last_fit_at": "2025-10-07T10:11:12Z",
  "last_calibration_at": "2025-10-07T10:18:05Z",
  "threshold": 0.61,
  "model_version": "2025.4",
  "coreset_size": 128,
  "token_shape": [24, 24]
}
```

### 2.3 `calibration.json`
```json
{
  "threshold": 0.61,
  "percentile": 0.995,
  "samples_ok": 256,
  "samples_ng": 42,
  "area_mm2_thr": 12.5,
  "mm_per_px": 0.0213,
  "created_at": "2025-10-07T10:18:05Z"
}
```

## 3. Respuesta `/infer`
- Campo `regions[]` contiene blobs post-proceso:
```json
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
```
- `token_shape` indica la rejilla PatchCore (normalmente `[24,24]`).
- `heatmap_png_base64` debe decodificarse y renderizarse con opacidad configurable en la GUI.

## 4. Requests multipart (`fit_ok`, `infer`)
```
Content-Type: multipart/form-data; boundary=----frontier
------frontier
Content-Disposition: form-data; name="role_id"
master
------frontier
Content-Disposition: form-data; name="roi_id"
inspection-1
------frontier
Content-Disposition: form-data; name="mm_per_px"
0.021
------frontier
Content-Disposition: form-data; name="image"; filename="roi.png"
<bytes PNG>
------frontier
Content-Disposition: form-data; name="shape"
{"kind":"annulus","cx":224,"cy":224,"r":210,"r_inner":140}
------frontier--
```

## 5. Configuración
- `config/cameras.yaml`: parámetros físicos de cámaras (distancia, mm_per_px nominal).
- `configs/roles/<role_id>.yaml`: presets de ROI maestros y defaults.
- `backend/config.yaml` (opcional): límites de memoria, device prioritario.

## 6. Logs
- GUI: `logs/gui/<yyyy-mm-dd>.log` (nivel Info). Cada entrada incluye `request_id` devuelto por backend.
- Backend: stdout JSON (`ts`, `level`, `route`, `role_id`, `roi_id`, `elapsed_ms`). Ver `LOGGING.md` para niveles y enriquecedores.

## 7. Versionado y compatibilidad
- Todos los JSON incluyen `model_version` cuando aplica.
- Cambios en estructuras requieren actualizar `agents.md` y bump de `model_version`.
- La GUI valida `model_version` antes de habilitar inferencia; discrepancias generan advertencia.
