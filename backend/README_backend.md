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
- `BDI_FAISS_PREFER_GPU`, `BDI_FAISS_GPU_DEVICE`, `BDI_FAISS_REQUIRE`, `BDI_FAISS_ALLOW_SKLEARN_FALLBACK`

## Logging
Backend diagnostics are written as JSONL to `backend_diagnostics.jsonl` in the resolved log directory. See `LOGGING.md`.

## WSL + GPU FAISS
1) Verify GPU visibility:
```bash
nvidia-smi
```

2) FAISS GPU install (examples):
```bash
conda install -c conda-forge faiss-gpu
# or
conda install -c pytorch -c nvidia faiss-gpu=1.9.0
```

3) Optional PyPI (unofficial) example:
```bash
pip install faiss-gpu-cu12
```

4) Runtime verification:
```bash
python -c "import faiss; print('gpu_api', hasattr(faiss,'StandardGpuResources')); print('ngpu', getattr(faiss,'get_num_gpus', lambda:0)())"
```

Note: if you use conda for `faiss-gpu`, prefer installing the rest of the Python deps in the same conda environment to avoid binary conflicts.

## Recipes and reserved ids
- `recipe_id` is normalized and validated; `last` is reserved and rejected with HTTP 400.
- Artifacts live under `BDI_MODELS_DIR/recipes/<recipe_id>/...`.
