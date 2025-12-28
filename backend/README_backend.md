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
Environment variables such as `BDI_BACKEND_HOST`, `BDI_BACKEND_PORT`, `BDI_MODELS_DIR`, `BDI_CORESET_RATE`, `BDI_SCORE_PERCENTILE` and `BDI_AREA_MM2_THR` override defaults (see `app.py`/`config.py`).

## Endpoints
`/health`, `/fit_ok`, `/calibrate_ng`, `/infer`, `/manifest` and the dataset helper routes — payloads and responses exactly match [`docs/API_CONTRACTS.md`](../docs/API_CONTRACTS.md). The GUI always supplies `role_id`, `roi_id`, `mm_per_px` and the ROI `shape` mask so `roi_mask.py` can apply the same crop geometry used on the WPF side.
