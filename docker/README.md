# Docker instructions

This directory only contains a single `Dockerfile` that builds the FastAPI backend on top of `pytorch/pytorch:2.2.2-cuda12.1-cudnn8-runtime`. It mirrors what the source code actually supports.

## Build image
```bash
cd docker
docker build -t brakedisc-backend ..
```
The resulting image includes CUDA-enabled PyTorch, installs `backend/requirements.txt` (with any CPU-only torch indexes removed) and copies the `backend/` package.

## Run container
```bash
docker run --gpus all -p 8000:8000 \
  -v /data/brakedisc/models:/app/models \
  --name brakedisc-backend brakedisc-backend
```
- The container executes `python -m uvicorn backend.app:app --host 0.0.0.0 --port 8000`.
- Logs are written to stdout (see `LOGGING.md`).
- Mount a volume for `/app/models` if you want model persistence.

## Configure the GUI
Point `Backend.BaseUrl` to the host/port exposed above (for example `http://server-ip:8000`). No additional headers or API keys are required.

## Multiple workers in Docker
The default container command may start a single worker. To run multiple workers, override the command, for example:
```bash
docker run --rm -p 8000:8000 -v "$(pwd)/models:/app/models" brakedisc-backend   python -m uvicorn backend.app:app --host 0.0.0.0 --port 8000 --workers 2
```
Ensure the mounted models directory is shared across workers/containers.
