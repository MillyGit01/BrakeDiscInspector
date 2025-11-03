# Guía de despliegue — Octubre 2025

Este documento describe cómo desplegar el backend y la GUI en distintos escenarios (standalone, célula, servidor GPU). Incluye la integración del contrato frontend ↔ backend.

## 1. Modos de despliegue

### 1.1 Standalone (PC único)
- GUI y backend en la misma máquina Windows con GPU.
- Backend ejecutado vía `uvicorn` o Docker Desktop.
- Comunicación `http://127.0.0.1:8000`.

Pasos rápidos:
```powershell
# Ventana PowerShell
cd backend
python -m venv .venv
.\.venv\Scripts\activate
pip install -r requirements.txt
uvicorn backend.app:app --host 0.0.0.0 --port 8000
```
- Configurar GUI con `Backend Base URL = http://localhost:8000`.

### 1.2 Célula de inspección (GUI → servidor GPU)
- GUI en estación Windows, backend en servidor Linux con GPU.
- Recomendado: Docker + NVIDIA Container Toolkit.
- Comunicación cifrada mediante reverse proxy (Traefik/Nginx) y API Key.

Pasos:
```bash
# Servidor Linux (root)
sudo docker compose -f docker/docker-compose.gpu.yml up -d
# expone backend en 0.0.0.0:8000
```
- Configurar `GUI → Ajustes → Backend URL` con `http://server-ip:8000`.
- Asegurar latencia < 10 ms y ancho de banda ≥ 100 Mbps.

### 1.3 Multi-ROI / Multi-cámara
- Escalar backend con varios workers (`UVICORN_WORKERS=4`).
- Usa colas (opcional) si múltiples GUI comparten backend.

## 2. Variables de entorno backend
- `BACKEND_DEVICE=cuda:0` (o `cpu`).
- `BACKEND_DATA_ROOT=/data/brakedisc` (datasets + models).
- `BACKEND_API_KEY=<token>` para exigir header `X-API-Key`.
- `BACKEND_ALLOW_ORIGINS=*` para CORS cuando se exponen dashboards.
- `PATCHCORE_CORES` (opcional) para limitar hilos.

## 3. Seguridad
- Reverse proxy con TLS (Let's Encrypt / certificados planta).
- Autenticación: header `X-API-Key`, rotado periódicamente.
- Logs auditables: montar volumen persistente `logs/`.
- Backups diarios de `datasets/` y `models/`.

## 4. Monitorización
- Exportar métricas Prometheus (`/metrics`).
- Integrar con Grafana para visualizar `latencia_infer_ms`, `n_embeddings`, `gpu_mem_mb`.
- Alertas: `score > threshold` repetido → notificación.

## 5. Actualización de versiones
1. Notificar a operaciones y congelar entrenamiento.
2. Crear backup `tar.gz` de `datasets/` y `models/`.
3. Actualizar código (Git pull + `pip install -r requirements.txt`).
4. Validar `/health` y realizar `infer` de prueba.
5. Actualizar GUI si hay cambios en `model_version`.

## 6. Integración con GUI
- Confirmar que la GUI usa URLs correctas y envía API Key.
- Validar handshake `/health` al arrancar (se muestra en barra de estado).
- La GUI reintenta llamadas con backoff (ver `docs/GUI.md`).

## 7. Troubleshooting rápido
- `503` en `/health`: verificar que el modelo haya cargado (logs backend).
- `409` en `/fit_ok`: mezcla de `mm_per_px`. Revisar calibración cámara.
- `428` en `/infer`: falta calibración. Ejecutar `POST /calibrate_ng`.

## 8. Referencias
- `docker/README.md`: instrucciones detalladas de contenedores.
- `docs/SETUP.md`: configuración local (CUDA, drivers, .NET).
- `docs/TROUBLESHOOTING.md`: casos avanzados.
