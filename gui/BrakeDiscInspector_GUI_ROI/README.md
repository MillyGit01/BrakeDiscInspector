# GUI ROI — Manual técnico Octubre 2025

## 1. Objetivo
Aplicación WPF (.NET 6/7) para definir ROIs, gestionar datasets y consumir los endpoints del backend FastAPI (`/health`, `/fit_ok`, `/calibrate_ng`, `/infer`).

## 2. Flujo operativo
1. **Cargar imagen** en la vista principal.
2. **Dibujar ROI** usando adorners (`RoiAdorner`, `ResizeAdorner`, `RoiRotateAdorner`).
3. **Guardar ROI** (Freeze) para generar crop canónico y bloquear adorners.
4. **Add to OK/NG**: envía ROI canónica al backend, guarda PNG + JSON (`datasets/<role>/<roi>/<ok|ng>/`).
5. **Train memory fit**: llama a `/fit_ok`, muestra `n_embeddings`, `coreset_size`, `token_shape`.
6. **Calibrate threshold**: envía `ok_scores`/`ng_scores` a `/calibrate_ng`, actualiza `threshold`.
7. **Evaluate**: llama a `/infer`, renderiza heatmap (`heatmap_png_base64`) y regiones.

## 3. Contrato con backend
- Todas las llamadas incluyen `role_id`, `roi_id`, `mm_per_px`.
- `shape` JSON (rect/circle/annulus) siempre en coordenadas de ROI canónica.
- Se registra `request_id` devuelto por backend en logs GUI.
- Los manifests (`manifest.json`) se sincronizan tras cada operación.

## 4. Estructura del proyecto
```
App.xaml / App.xaml.cs
MainWindow.xaml / .cs
ViewModels/WorkflowViewModel.cs
Services/BackendClientService.cs
Models/RoiShape.cs, RoiManifest.cs
Controls/RoiOverlay.xaml(.cs)
```

## 5. Configuración
- `settings.json`: backend URL, idioma, opacidad overlay.
- Variables de entorno: `BDI_BACKEND_BASE_URL`, `BDI_BACKEND_API_KEY`.
- Barra de estado muestra `/health` (`status`, `device`, `model_version`).

## 6. Logs
- `logs/gui/<yyyy-mm-dd>.log`.
- Formato: `timestamp [level] request_id=<id> action=<fit_ok|calibrate|infer> role=<role> roi=<roi> ...`.

## 7. Buenas prácticas
- Mantener llamadas HTTP `async/await`.
- No modificar adorners sin aprobación (ver `agents.md`).
- Validar `model_version` devuelto por backend; si difiere mostrar advertencia.
- Generar miniaturas en background (`Task.Run`).

## 8. Troubleshooting
- **Backend no responde**: revisar URL/API Key.
- **Heatmap desalineado**: confirmar ROI congelada y `shape` correcto.
- **`409` en `/fit_ok`**: `mm_per_px` incoherente (ver calibración).

## 9. Referencias
- [`README.md`](../../README.md)
- [`API_REFERENCE.md`](../../API_REFERENCE.md)
- [`docs/GUI.md`](../../docs/GUI.md)
- [`docs/DATASET_Y_ROI.md`](../../docs/DATASET_Y_ROI.md)
