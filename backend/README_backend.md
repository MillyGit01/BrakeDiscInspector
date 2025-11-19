# Backend quick reference

This folder implements the FastAPI service described in [`docs/BACKEND.md`](../docs/BACKEND.md) and [`docs/API_CONTRACTS.md`](../docs/API_CONTRACTS.md). Use those files for full details; the summary below only covers local setup.

## Local run
```bash
cd backend
python -m venv .venv
source .venv/bin/activate  # Windows: .venv\Scripts\activate
pip install -r requirements.txt
uvicorn backend.app:app --host 0.0.0.0 --port 8000
```
Environment variables such as `BDI_MODELS_DIR`, `BDI_CORESET_RATE` and `BDI_BACKEND_HOST` override defaults (see `app.py`/`config.py`).

## Endpoints
`/health`, `/fit_ok`, `/calibrate_ng`, `/infer` — payloads and responses exactly match [`docs/API_CONTRACTS.md`](../docs/API_CONTRACTS.md).
