# Docker — Backend FastAPI (Octubre 2025)

## 1. Imágenes disponibles
- `Dockerfile` — imagen CPU.
- `Dockerfile.gpu` — imagen con soporte CUDA 12.x (NVIDIA).
- `docker-compose.gpu.yml` — despliegue backend + watchtower opcional.

## 2. Build CPU
```bash
cd docker
docker build -t brakedisc-backend:cpu -f Dockerfile ..
```

## 3. Build GPU
```bash
cd docker
docker build -t brakedisc-backend:gpu -f Dockerfile.gpu ..
```
- Requiere `nvidia-container-toolkit` instalado.

## 4. docker-compose (GPU)
```bash
docker compose -f docker-compose.gpu.yml up -d
```
- Monta volúmenes:
  - `/data/brakedisc/datasets:/app/datasets`
  - `/data/brakedisc/models:/app/models`
  - `/data/brakedisc/logs:/app/logs`
- Variables (`.env`):
  - `BACKEND_DEVICE=cuda:0`
  - `BACKEND_API_KEY=<token>`
  - `BACKEND_ALLOW_ORIGINS=*`

## 5. Healthcheck
- `GET http://localhost:8000/health`
- Revisar logs con `docker logs brakedisc-backend`

## 6. Actualizaciones
```bash
docker compose -f docker-compose.gpu.yml pull
docker compose -f docker-compose.gpu.yml up -d
```

## 7. Integración con GUI
- Configurar GUI con `http://<host>:8000`.
- Validar handshake `/health`.
- Asegurar API Key si está habilitada.

## 8. Troubleshooting
- Falta GPU → revisar `docker info | grep -i nvidia`.
- Permisos volumen → usar `chown -R 1000:1000 /data/brakedisc`.
- Latencia alta → ajustar `UVICORN_WORKERS`.
