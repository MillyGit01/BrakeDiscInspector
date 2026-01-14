from __future__ import annotations
import json
import os
from pathlib import Path
from typing import Any, Dict

try:
    import yaml  # type: ignore
except Exception:
    yaml = None  # optional dependency

def _env(new_key: str, legacy_key: str | None, default: str) -> str:
    value = os.getenv(new_key)
    if not value and legacy_key:
        value = os.getenv(legacy_key)
    return value if value not in (None, "") else default


DEFAULTS: Dict[str, Any] = {
    "server": {
        "host": _env("BDI_BACKEND_HOST", "BRAKEDISC_BACKEND_HOST", "127.0.0.1"),
        "port": int(_env("BDI_BACKEND_PORT", "BRAKEDISC_BACKEND_PORT", "8000")),
    },
    "models_dir": _env("BDI_MODELS_DIR", "BRAKEDISC_MODELS_DIR", "models"),
    "inference": {
        "coreset_rate": float(_env("BDI_CORESET_RATE", "BRAKEDISC_CORESET_RATE", "0.10")),
        "score_percentile": int(_env("BDI_SCORE_PERCENTILE", "BRAKEDISC_SCORE_PERCENTILE", "99")),
        "area_mm2_thr": float(_env("BDI_AREA_MM2_THR", "BRAKEDISC_AREA_MM2_THR", "1.0")),
    },
    "training": {
        "min_ok_samples": int(_env("BDI_MIN_OK_SAMPLES", None, "10")),
        "dataset_only": _env("BDI_TRAIN_DATASET_ONLY", None, "1"),
    },
}

def load_settings(config_path: str | os.PathLike[str] | None = None) -> Dict[str, Any]:
    cfg = json.loads(json.dumps(DEFAULTS))  # deep copy via JSON
    path = Path(config_path) if config_path else Path(__file__).resolve().parents[1] / "configs" / "app.yaml"
    if path.exists() and yaml is not None:
        with open(path, "r", encoding="utf-8") as f:
            data = yaml.safe_load(f) or {}
        # shallow merge
        for k, v in data.items():
            if isinstance(v, dict) and isinstance(cfg.get(k), dict):
                cfg[k].update(v)
            else:
                cfg[k] = v
    return cfg
