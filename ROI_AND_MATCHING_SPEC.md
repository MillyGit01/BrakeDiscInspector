# Especificación ROI y matching espacial — 2025

Este documento describe la representación geométrica de las Regiones de Interés, la estrategia de matching entre imágenes y la relación con la exportación de ROI canónica. Mantiene compatibilidad con las restricciones descritas en `agents.md`.

## 1. Formas admitidas
- **Rectángulo** (`rect`): definido por `x`, `y`, `w`, `h` en pixeles de la ROI canónica. Se espera que `x=y=0`, `w=h` cuando se exporta ROI completa.
- **Círculo** (`circle`): `cx`, `cy`, `r`. La GUI garantiza que `r` no supere el borde de la imagen.
- **Anillo** (`annulus`): `cx`, `cy`, `r`, `r_inner`. `r_inner < r`. Ambos radios se expresan en pixeles canónicos.

Todas las coordenadas están en el sistema de la imagen canónica (post-crop y rotación). La GUI es la única responsable de convertir desde coordenadas de layout original.

## 2. Pipeline de matching
1. **Definición Master ROI**: la GUI guarda un preset con ROI maestros (rectas de referencia, ejes, etc.).
2. **Analyze Master**: cuando se analiza un nuevo frame, la GUI calcula transformaciones (rotación/traslación) usando matching de características.
3. **Aplicación a Inspection ROIs**: las Inspection ROIs heredan la transformación del Master salvo que estén en estado `frozen`.
4. **Exportación**: cada ROI se recorta y rota generando la ROI canónica que se envía al backend.

> ⚠️ El backend **no** aplica transformaciones; confía en que la ROI canónica está alineada.

## 3. Campos complementarios
- `mm_per_px`: escala física. Debe obtenerse de calibraciones de cámara y almacenarse en manifest.
- `rotation_deg`: la GUI puede almacenar el ángulo aplicado para trazabilidad, pero **no** se envía al backend (ya se exporta rotada).
- `roi_uuid`: identificador único por ROI para auditoría (opcional, utilizado en manifests y logs).

## 4. Matching de overlays
- El backend devuelve `heatmap_png_base64` y `regions[]` en el mismo sistema de coordenadas de la ROI canónica.
- La GUI genera una textura WPF y la superpone al ROI original (con opacidad ajustable), garantizando alineación exacta.
- Para la máscara `annulus`, la GUI aplica un stencil al overlay para ocultar zonas fuera del anillo.

## 5. Persistencia y versionado
- `manifest.json` almacena `shape_default` para cada ROI.
- Cambios en shape deben acompañarse de incremento en `version` dentro del manifest y reentrenamiento (`fit_ok`).
- La GUI bloquea cambios de shape si existen muestras pendientes de sincronizar.

## 6. Tolerancias y validaciones
- Radios mínimos: `r >= 24 px`, `r_inner >= 8 px`.
- Rectángulos mínimos: `w,h >= 64 px`.
- Todos los valores deben ser números enteros; la GUI hace round.
- El backend valida y responde `400` si `shape` es inválido.

## 7. Referencias
- `docs/DATASET_Y_ROI.md` describe ejemplos completos de ROI con capturas.
- `docs/GUI.md` documenta la UX para dibujar y congelar ROIs.
- `docs/PIPELINE_DETECCION.md` profundiza en cómo se usa el `shape` durante inferencia.
