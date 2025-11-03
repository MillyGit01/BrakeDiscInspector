# GUI WPF — Manual de operación 2025

## 1. Objetivo
Permitir al operario definir ROIs, gestionar datasets y lanzar entrenamiento/inferencia respetando el contrato con el backend.

## 2. Componentes clave
- `MainWindow.xaml` / `.cs`: contenedor principal.
- `WorkflowViewModel`: estado global, comandos (`FitOkCommand`, `CalibrateCommand`, `InferCommand`).
- `BackendClientService`: cliente HTTP asíncrono.
- `RoiOverlay`, `RoiAdorner`, `ResizeAdorner`, `RoiRotateAdorner`: interacción gráfica (no modificar sin aprobación).

## 3. Flujo operativo
1. Cargar imagen base.
2. Seleccionar preset (Masters) o crear nuevo.
3. Dibujar Inspection ROIs (1..4). Guardar para congelar adorners.
4. Añadir muestras OK/NG.
5. Ejecutar `Train memory fit` (→ `/fit_ok`).
6. Ejecutar `Calibrate threshold` (→ `/calibrate_ng`).
7. Ejecutar `Evaluate` (→ `/infer`).
8. Analizar heatmap, regiones y logs.

## 4. Contrato con backend
- Todas las llamadas incluyen `role_id`, `roi_id`, `mm_per_px`.
- `shape` JSON se genera en coordenadas ROI canónica.
- Se maneja `request_id` devuelto por backend y se registra en logs GUI.
- Errores se muestran en panel inferior con mensaje amigable + detalle técnico.

## 5. Gestión de datasets
- Miniaturas generadas en background (`Task.Run`).
- Los archivos se guardan en `datasets/<role>/<roi>/`.
- Botón “Open Folder” abre ubicación actual.
- Los manifests se actualizan tras cada operación (`manifest.json`).

## 6. UX y rendimiento
- No bloquear UI durante llamadas HTTP → uso de `async/await`.
- Botones se deshabilitan mientras se ejecuta una operación.
- Barra de estado muestra `status`, `device`, `model_version` (usando `/health`).
- Heatmap overlay: slider de opacidad (0–100%).

## 7. Configuración
- `settings.json`: último backend URL, idioma, opacidad default.
- `mm_per_px` puede fijarse manualmente o provenir del preset.

## 8. Troubleshooting
- No hay conexión backend → verificar URL y API Key.
- Score alto inesperado → revisar `shape` y `mm_per_px`.
- Heatmap desalineado → confirmar que ROI no fue reeditada tras congelar.

## 9. Roadmap GUI
- Migrar a .NET 8.
- Añadir panel histórico de inferencias.
- Exportar reportes PDF.

## 10. Referencias
- `docs/SETUP.md`: instalación GUI.
- `docs/DATASET_Y_ROI.md`: datasets.
- `API_REFERENCE.md`: contrato.
