# Dataset y ROI — Manual 2025

## 1. Estructura de carpetas
```
datasets/
  <role_id>/
    <roi_id>/
      ok/
      ng/
      manifest.json
      calibration.json
```
- Cada PNG tiene metadato JSON asociado (mismo nombre) con `mm_per_px`, `shape`, `operator_id`.
- `manifest.json` resume contadores y fechas.

## 2. Flujo GUI para datasets
1. Operario selecciona ROI activa.
2. Botón “Add to OK/NG” envía ROI al backend y guarda archivo local.
3. GUI actualiza contadores `ok_count`/`ng_count`.
4. Se pueden borrar entradas desde GUI (mueve a `trash/`).

## 3. ROI canónica
- Se genera a partir de imagen base + adorners.
- Dimensión configurable (default 448×448).
- `shape` describe máscara (rect/circle/annulus) en pixeles canónicos.
- `mm_per_px` se almacena en manifest.

## 4. Reentrenamiento y recalibración
- Cada vez que se agregan nuevas muestras OK, ejecutar `Train memory fit` (→ `/fit_ok`).
- Tras añadir NGs o cambiar lote, ejecutar `Calibrate threshold` (→ `/calibrate_ng`).
- La GUI muestra `threshold` y fecha de calibración.

## 5. Manifiestos
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
  "token_shape": [24,24],
  "model_version": "2025.4"
}
```

## 6. Integración backend
- `storage.py` gestiona lectura/escritura de manifests y datasets.
- Al recibir `fit_ok`, añade entradas a manifest y actualiza contadores.
- `calibrate_ng` persiste `calibration.json`.

## 7. Buenas prácticas
- Mantener `mm_per_px` consistente.
- Registrar `operator_id` y `notes` en JSON.
- No modificar manualmente `manifest.json`.
- Hacer backup diario de `datasets/` y `models/`.

## 8. Referencias
- `DATA_FORMATS.md` — definición de JSON.
- `ROI_AND_MATCHING_SPEC.md` — geometría ROI.
- `docs/PIPELINE_DETECCION.md` — uso de datasets en pipeline.
