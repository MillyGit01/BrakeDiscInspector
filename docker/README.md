# Docker instructions

This directory contains a Dockerfile for the **FastAPI backend**.

## Build image
```bash
cd docker
docker build -t brakedisc-backend ..
```

## Run container
```bash
docker run --gpus all -p 8000:8000 \
  -v /data/brakedisc/models:/app/models \
  -e BDI_MODELS_DIR=/app/models \
  -e BDI_GUI_LOG_DIR=/app/logs \
  -v /data/brakedisc/logs:/app/logs \
  --name brakedisc-backend brakedisc-backend
```

- The container runs `python -m uvicorn backend.app:app --host 0.0.0.0 --port 8000`.
- Diagnostics logs are written to `BDI_GUI_LOG_DIR` as `backend_diagnostics.jsonl` (see `LOGGING.md`).
- Mount volumes for `/app/models` and `/app/logs` to persist data and logs.

## Configure the GUI
Point `Backend.BaseUrl` to the host/port exposed above (e.g. `http://server-ip:8000`).

## Multiple workers in Docker
Override the command to increase workers:
```bash
docker run --rm -p 8000:8000 -v "$(pwd)/models:/app/models" brakedisc-backend \
  python -m uvicorn backend.app:app --host 0.0.0.0 --port 8000 --workers 2
```
Ensure `BDI_MODELS_DIR` points to a shared volume.
