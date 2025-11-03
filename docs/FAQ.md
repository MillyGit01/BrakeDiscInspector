# FAQ — Octubre 2025

## ¿Qué diferencia hay entre Master y Inspection ROIs?
- Master define la transformación global (anclaje). Inspection 1..4 son zonas específicas evaluadas. Solo las Inspection se envían al backend.

## ¿Por qué debo enviar `mm_per_px`?
- Para convertir áreas en mm² y validar consistencia del dataset. Se guarda en manifest y calibración.

## ¿Puedo usar el backend sin la GUI?
- Sí, enviando ROI canónicas y `shape` válidos vía API. Ver `docs/curl_examples.md`.

## ¿Cómo manejo múltiples clientes?
- Usar `role_id` distinto por planta/cliente. Cada combinación mantiene su dataset y modelo.

## ¿Qué ocurre si cambio la forma de la ROI?
- Debes reentrenar (`/fit_ok`) y recalibrar (`/calibrate_ng`). La GUI bloqueará inferencias si detecta mismatch.

## ¿Cómo integro autenticación?
- Habilita `BACKEND_API_KEY` y añade header `X-API-Key` en la GUI. Para OAuth/proxy ver `DEPLOYMENT.md`.

## ¿Por qué recibo `428` en `/infer`?
- No existe `calibration.json` para esa ROI. Ejecuta `Calibrate threshold` desde GUI.

## ¿Se puede ejecutar todo en CPU?
- Sí, aunque la inferencia será más lenta. Configura `BACKEND_DEVICE=cpu`.

## ¿Dónde están los logs?
- GUI: `logs/gui/`. Backend: stdout JSON (configurable). Ambos comparten `request_id`.

## ¿Cuál es el roadmap?
- Migración a .NET 8, batch inferencia, reportes PDF, métricas avanzadas.
