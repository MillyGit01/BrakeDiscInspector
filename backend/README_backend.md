# Backend quick reference

This folder implements the FastAPI service described in:
- `docs/BACKEND.md`
- `docs/API_CONTRACTS.md`

Use those docs for full details; the summary below focuses on local setup.

## Local run
```bash
cd backend
python -m venv .venv
source .venv/bin/activate  # Windows: .venv\Scripts\activate
pip install -r requirements.txt
uvicorn backend.app:app --host 0.0.0.0 --port 8000
```

## Common env vars
- `BDI_BACKEND_HOST`, `BDI_BACKEND_PORT`
- `BDI_MODELS_DIR`
- `BDI_REQUIRE_CUDA`
- `BDI_CORESET_RATE`, `BDI_SCORE_PERCENTILE`, `BDI_AREA_MM2_THR`
- `BDI_MIN_OK_SAMPLES`, `BDI_TRAIN_DATASET_ONLY`
- `BDI_CACHE_MAX_ENTRIES`
- `BDI_CORS_ORIGINS`
- `BDI_GUI_LOG_DIR`

## Logging
Backend diagnostics are written as JSONL to `backend_diagnostics.jsonl` in the resolved log directory. See `LOGGING.md`.

## Recipes and reserved ids
- `recipe_id` is normalized and validated; `last` is reserved and rejected with HTTP 400.
- Artifacts live under `BDI_MODELS_DIR/recipes/<recipe_id>/...`.
