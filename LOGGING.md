# Política de logging — Octubre 2025

La trazabilidad es clave para auditar decisiones de inspección. Este documento describe cómo la GUI y el backend registran eventos, cómo se correlacionan y qué metadatos se incluyen.

## 1. Principios
- Todos los eventos relevantes deben incluir `role_id`, `roi_id`, `request_id` (cuando aplique) y `operator_id`.
- Logs estructurados en formato JSON para backend; texto estructurado para GUI.
- Niveles consistentes (`DEBUG`, `INFO`, `WARNING`, `ERROR`, `CRITICAL`).

## 2. GUI
- Ubicación: `logs/gui/<yyyy-mm-dd>.log`.
- Formato:
```
2025-10-07T10:18:07.123Z [INFO] [request_id=af23c9] fit_ok role=master roi=inspection-1 n_embeddings=512 coreset=128 elapsed_ms=6421
```
- Eventos clave:
  - Inicio/final de `fit_ok`, `calibrate_ng`, `infer`.
  - Resultado (`score`, `threshold`, `decision`).
  - Errores HTTP (incluye `status_code`, `detail`).
- La GUI propaga `request_id` devuelto por el backend (header `X-Request-Id`).

## 3. Backend
- Salida estándar en JSON (configurable para volcar a archivo).
- Ejemplo:
```json
{
  "ts": "2025-10-07T10:18:07.114Z",
  "level": "info",
  "route": "infer",
  "request_id": "af23c9",
  "role_id": "master",
  "roi_id": "inspection-1",
  "elapsed_ms": 87.4,
  "score": 0.38,
  "threshold": 0.61,
  "decision": "ok"
}
```
- Integración con `structlog` para añadir campos dinámicos.

## 4. Correlación
1. Backend genera `request_id` (UUID base62) y lo devuelve vía header.
2. GUI registra el mismo `request_id` en su log.
3. Para auditorías se cruzan ambos archivos filtrando por `request_id`.

## 5. Alertas
- `WARNING`: tiempo de inferencia > 250 ms, `coreset_size` inesperadamente bajo, `mm_per_px` divergente.
- `ERROR`: excepciones no recuperables, fallo de lectura de dataset, faltan pesos.
- `CRITICAL`: imposibilidad de cargar modelo (detiene servicio).

## 6. Retención
- GUI: rotación diaria, mantener 30 días.
- Backend: rotación semanal, retener 90 días o según normativa del cliente.

## 7. Integraciones externas
- Exportar logs backend a Splunk/ELK mediante Fluent Bit.
- Alerting via Grafana/Prometheus usando métricas derivadas.

## 8. Checklist
- [ ] GUI incluye `request_id` en todos los mensajes relacionados con backend.
- [ ] Backend añade `role_id`, `roi_id`, `mm_per_px` a logs de negocio.
- [ ] Se revisan logs tras actualizaciones (`DEPLOYMENT.md`).
