from __future__ import annotations
import json
import logging
import os
import sys
import traceback
import time
import uuid
import threading
import datetime
from collections import OrderedDict
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, List, Optional, Sequence, Tuple

import numpy as np
import cv2
import torch

try:
    from fastapi import FastAPI, UploadFile, File, Form, Request, HTTPException
    from fastapi.responses import JSONResponse, FileResponse
    from starlette.middleware.cors import CORSMiddleware
except ModuleNotFoundError as exc:  # pragma: no cover - import guard
    missing = exc.name or "fastapi"
    raise ModuleNotFoundError(
        f"Missing optional dependency '{missing}'. "
        "Install backend requirements with 'python -m pip install -r backend/requirements.txt'."
    ) from exc

if __package__ in (None, ""):
    # Allow running as a script: `python app.py`
    backend_dir = Path(__file__).resolve().parent
    project_root = backend_dir.parent
    if str(project_root) not in sys.path:
        sys.path.insert(0, str(project_root))

    from backend.features import DinoV2Features  # type: ignore[no-redef]
    from backend.patchcore import PatchCoreMemory  # type: ignore[no-redef]
    from backend.storage import ModelStore  # type: ignore[no-redef]
    from backend.infer import InferenceEngine  # type: ignore[no-redef]
    from backend.calib import choose_threshold  # type: ignore[no-redef]
    from backend.utils import ensure_dir, base64_from_bytes  # type: ignore[no-redef]
else:
    from .features import DinoV2Features
    from .patchcore import PatchCoreMemory
    from .storage import ModelStore
    from .infer import InferenceEngine
    from .calib import choose_threshold
    from .utils import ensure_dir, base64_from_bytes

log = logging.getLogger(__name__)

# Carpeta para artefactos persistentes por (role_id, roi_id)
def _env_var(name: str, *, legacy: str | None = None, default: str | None = None) -> str | None:
    """Return environment variable prioritising the new BDI_* keys."""
    value = os.environ.get(name)
    if not value and legacy:
        value = os.environ.get(legacy)
    if value is None or value == "":
        return default
    return value


def _env_str(name: str, default: str, legacy_names: Sequence[str] = ()) -> str:
    """Return env var as a non-empty string, never None.
    - Checks `name` first, then any `legacy_names`.
    - Falls back to `default` if missing/empty.
    """
    v = os.getenv(name)
    if v is None or v.strip() == "":
        for ln in legacy_names:
            lv = os.getenv(ln)
            if lv is not None and lv.strip() != "":
                v = lv
                break
    if v is None or v.strip() == "":
        v = default
    return v

app = FastAPI(title="Anomaly Backend (PatchCore + DINOv2)")

# --- CORS (solo afecta a clientes web / navegador) ---
# Coma-separado, ej: "http://localhost:5173,http://127.0.0.1:5173"
_raw = _env_var("BDI_CORS_ORIGINS", legacy="BRAKEDISC_CORS_ORIGINS", default="*")  # helper ya existente
origins = ["*"]
if _raw and _raw.strip() != "*":
    origins = [o.strip() for o in _raw.split(",") if o.strip()]

# Nota: allow_credentials no puede usarse con origins="*" (los navegadores lo bloquean).
allow_credentials = False if origins == ["*"] else True

app.add_middleware(
    CORSMiddleware,
    allow_origins=origins,
    allow_credentials=allow_credentials,
    allow_methods=["*"],
    allow_headers=["*"],
    expose_headers=["x-request-id", "x-recipe-id"],
)


def slog(event: str, **kw):
    rec = {"ts": time.time(), "event": event}
    rec.update(kw)
    print(json.dumps(rec, ensure_ascii=False), flush=True)


def _resolve_request_context(request: Request, recipe_from_payload: str | None = None) -> tuple[str, str]:
    # Resolve request_id and recipe_id for routing/storage.
    # If recipe_id is invalid/reserved (e.g. 'last'), raise HTTPException(400).
    request_id = request.headers.get("X-Request-Id") or str(uuid.uuid4())

    header_name = "X-Recipe-Id"
    recipe_from_header = request.headers.get(header_name)

    # Payload wins over header if provided
    recipe_id = recipe_from_payload or recipe_from_header

    try:
        recipe_id_s = ModelStore._sanitize_recipe_id(recipe_id)
    except ValueError as e:
        # Important: this must be a 400, and must NOT fall back silently.
        raise HTTPException(
            status_code=400,
            detail={
                "error": str(e),
                "request_id": request_id,
                "recipe_id": "default",
            },
        )

    return request_id, recipe_id_s


def _resolve_request_context_safe(request: Request, recipe_from_payload: str | None = None) -> tuple[str, str]:
    # Safe variant for logging inside exception handlers.
    # Never raises (returns recipe_id='default' if invalid).
    request_id = request.headers.get("X-Request-Id") or str(uuid.uuid4())

    header_name = "X-Recipe-Id"
    recipe_from_header = request.headers.get(header_name)
    recipe_id = recipe_from_payload or recipe_from_header

    try:
        recipe_id_s = ModelStore._sanitize_recipe_id(recipe_id)
    except Exception:
        recipe_id_s = "default"

    return request_id, recipe_id_s

MODELS_DIR = Path(
    _env_str("BDI_MODELS_DIR", default="models", legacy_names=("BRAKEDISC_MODELS_DIR",))
)

# Optional config (YAML + env) for default parameters.
try:
    from backend.config import load_settings  # type: ignore
    SETTINGS = load_settings()
except Exception:
    SETTINGS = {
        "inference": {
            "coreset_rate": 0.10,
            "score_percentile": 99,
            "area_mm2_thr": 1.0,
        }
    }
ensure_dir(MODELS_DIR)
store = ModelStore(MODELS_DIR)

MM_PER_PX_EPS = 1e-6

# --- GPU requirement ---
REQUIRE_CUDA = os.getenv("BDI_REQUIRE_CUDA", "1").strip() == "1"
if REQUIRE_CUDA and not torch.cuda.is_available():
    raise RuntimeError("CUDA is required (BDI_REQUIRE_CUDA=1). No GPU detected.")
if torch.cuda.is_available():
    torch.backends.cudnn.benchmark = True

# Carga única del extractor (congelado)
_extractor = DinoV2Features(
    model_name="vit_small_patch14_dinov2.lvd142m",
    device="cuda" if torch.cuda.is_available() else "cpu",
    half=torch.cuda.is_available(),
    input_size=448,   # múltiplo de 14; si envías 384, el extractor reescala internamente
    patch_size=14
)


# --- In-process caches (per uvicorn worker) ---------------------------------
# NOTE: With `uvicorn --workers > 1` each worker has its own process+GPU context.
# These caches reduce disk I/O and avoid rebuilding sklearn/FAISS indices on every request.

@dataclass
class _MemCacheEntry:
    mem: PatchCoreMemory
    token_hw: Tuple[int, int]
    metadata: Dict[str, Any]
    mem_mtime: float
    index_mtime: Optional[float]


@dataclass
class _CalibCacheEntry:
    calib: Dict[str, Any]
    calib_mtime: float


_CACHE_LOCK = threading.RLock()
_MEM_CACHE: "OrderedDict[str, _MemCacheEntry]" = OrderedDict()
_CALIB_CACHE: "OrderedDict[str, _CalibCacheEntry]" = OrderedDict()


def _env_int(name: str, default: int) -> int:
    raw = os.environ.get(name)
    if raw is None or raw == "":
        return int(default)
    try:
        return int(raw)
    except Exception:
        return int(default)


_CACHE_MAX_ENTRIES = _env_int("BDI_CACHE_MAX_ENTRIES", 32)


def _cache_key(recipe_id: str, model_key: str, role_id: str, roi_id: str) -> str:
    return f"{recipe_id}::{model_key}::{role_id}::{roi_id}"


def _evict_lru(cache: "OrderedDict[str, Any]"):
    while len(cache) > _CACHE_MAX_ENTRIES:
        cache.popitem(last=False)


def _invalidate_memory_cache(recipe_id: str, model_key: str, role_id: str, roi_id: str):
    key = _cache_key(recipe_id, model_key, role_id, roi_id)
    with _CACHE_LOCK:
        _MEM_CACHE.pop(key, None)


def _invalidate_calib_cache(recipe_id: str, model_key: str, role_id: str, roi_id: str):
    key = _cache_key(recipe_id, model_key, role_id, roi_id)
    with _CACHE_LOCK:
        _CALIB_CACHE.pop(key, None)


def _get_patchcore_memory_cached(role_id: str, roi_id: str, *, recipe_id: str, model_key: str):
    key = _cache_key(recipe_id, model_key, role_id, roi_id)

    mem_path = store.resolve_memory_path_existing(role_id, roi_id, recipe_id=recipe_id, model_key=model_key)
    if mem_path is None:
        with _CACHE_LOCK:
            _MEM_CACHE.pop(key, None)
        return None

    mem_mtime = float(mem_path.stat().st_mtime)

    idx_path = store.resolve_index_path_existing(role_id, roi_id, recipe_id=recipe_id, model_key=model_key)
    idx_mtime = float(idx_path.stat().st_mtime) if idx_path is not None and idx_path.exists() else None

    with _CACHE_LOCK:
        entry = _MEM_CACHE.get(key)
        if entry and entry.mem_mtime == mem_mtime and entry.index_mtime == idx_mtime:
            _MEM_CACHE.move_to_end(key)
            return entry.mem, entry.token_hw, entry.metadata

    loaded = store.load_memory(role_id, roi_id, recipe_id=recipe_id, model_key=model_key)
    if loaded is None:
        with _CACHE_LOCK:
            _MEM_CACHE.pop(key, None)
        return None
    emb_mem, token_hw_mem, metadata = loaded

    # Build PatchCoreMemory once (sklearn NearestNeighbors fit is expensive)
    mem_obj = PatchCoreMemory(embeddings=emb_mem, index=None, coreset_rate=(metadata or {}).get("coreset_rate"))

    # Optional FAISS index (if available)
    try:
        import faiss  # type: ignore
        blob = store.load_index_blob(role_id, roi_id, recipe_id=recipe_id, model_key=model_key)
        if blob is not None:
            idx = faiss.deserialize_index(np.frombuffer(blob, dtype=np.uint8))
            mem_obj.index = idx
            mem_obj.nn = None
    except Exception:
        pass

    token_hw_tup = (int(token_hw_mem[0]), int(token_hw_mem[1]))
    meta_dict = dict(metadata or {})

    with _CACHE_LOCK:
        _MEM_CACHE[key] = _MemCacheEntry(
            mem=mem_obj,
            token_hw=token_hw_tup,
            metadata=meta_dict,
            mem_mtime=mem_mtime,
            index_mtime=idx_mtime,
        )
        _MEM_CACHE.move_to_end(key)
        _evict_lru(_MEM_CACHE)

    return mem_obj, token_hw_tup, meta_dict


def _get_calib_cached(role_id: str, roi_id: str, *, recipe_id: str, model_key: str):
    key = _cache_key(recipe_id, model_key, role_id, roi_id)

    calib_path = store.resolve_calib_path_existing(role_id, roi_id, recipe_id=recipe_id, model_key=model_key)
    if calib_path is None:
        with _CACHE_LOCK:
            _CALIB_CACHE.pop(key, None)
        return None

    calib_mtime = float(calib_path.stat().st_mtime)

    with _CACHE_LOCK:
        entry = _CALIB_CACHE.get(key)
        if entry and entry.calib_mtime == calib_mtime:
            _CALIB_CACHE.move_to_end(key)
            return entry.calib

    data = store.load_calib(role_id, roi_id, default=None, recipe_id=recipe_id, model_key=model_key)
    if data is None:
        with _CACHE_LOCK:
            _CALIB_CACHE.pop(key, None)
        return None

    data_dict = dict(data)

    with _CACHE_LOCK:
        _CALIB_CACHE[key] = _CalibCacheEntry(calib=data_dict, calib_mtime=calib_mtime)
        _CALIB_CACHE.move_to_end(key)
        _evict_lru(_CALIB_CACHE)

    return data_dict


def _scores_1d_finite(raw) -> np.ndarray:
    if raw is None:
        return np.asarray([], dtype=float)
    x = np.asarray(raw, dtype=float).reshape(-1)
    if x.size == 0:
        return x
    return x[np.isfinite(x)]


def _read_image_file(file: UploadFile) -> np.ndarray:
    data = file.file.read()
    img_array = np.frombuffer(data, dtype=np.uint8)
    img = cv2.imdecode(img_array, cv2.IMREAD_COLOR)
    if img is None:
        raise ValueError("No se pudo decodificar la imagen")
    return img


def _read_image_path(path: Path) -> np.ndarray:
    data = path.read_bytes()
    img_array = np.frombuffer(data, dtype=np.uint8)
    img = cv2.imdecode(img_array, cv2.IMREAD_COLOR)
    if img is None:
        raise ValueError(f"No se pudo decodificar la imagen: {path.name}")
    return img


def _validate_mm_per_px(value: float) -> float:
    mm = float(value)
    if not np.isfinite(mm) or mm <= 0:
        raise HTTPException(status_code=400, detail="mm_per_px must be > 0 and finite")
    return mm


def _is_truthy(value: Any) -> bool:
    if isinstance(value, bool):
        return value
    if value is None:
        return False
    return str(value).strip().lower() in {"1", "true", "yes", "on"}


def _ensure_recipe_mm_per_px(request_id: str, recipe_id: str, mm_per_px: float) -> float:
    try:
        return store.ensure_recipe_mm_per_px(recipe_id, mm_per_px, tol=MM_PER_PX_EPS)
    except ValueError as exc:
        raise HTTPException(
            status_code=409,
            detail={
                "error": str(exc),
                "request_id": request_id,
                "recipe_id": recipe_id,
            },
        )


def _parse_shape_value(raw: Any) -> Optional[Dict[str, Any]]:
    if raw is None:
        return None
    if isinstance(raw, dict):
        return raw
    if isinstance(raw, str):
        raw = raw.strip()
        if not raw:
            return None
        return json.loads(raw)
    return raw  # best-effort for unexpected types

@app.get("/health")
def health(request: Request):
    try:
        import torch

        cuda_available = torch.cuda.is_available()
    except Exception:
        cuda_available = False

    request_id, recipe_id = _resolve_request_context(request)
    # CPU-only deployments are valid: report OK while surfacing CUDA absence separately.
    status = "ok"
    resp = {
        "status": status,
        "device": "cuda" if cuda_available else "cpu",
        "model": "vit_small_patch14_dinov2.lvd142m",
        "version": "0.1.0",
        "request_id": request_id,
        "recipe_id": recipe_id,
    }
    if not cuda_available:
        resp["reason"] = "cuda_not_available"
    return resp

@app.post("/fit_ok")
def fit_ok(
    request: Request,
    role_id: str = Form(...),
    roi_id: str = Form(...),
    mm_per_px: float = Form(...),
    images: Optional[List[UploadFile]] = File(None),
    memory_fit: bool = Form(False),
    use_dataset: bool = Form(False),
    recipe_id: Optional[str] = Form(None),
    model_key: Optional[str] = Form(None),
):
    """
    Acumula OKs para construir la memoria PatchCore (coreset + kNN).
    Guarda (role_id, roi_id): memoria (embeddings), token grid y, si hay FAISS, el índice.
    """
    try:
        request_id, recipe_resolved = _resolve_request_context(request, recipe_id)
        model_key_effective = model_key or roi_id
        mm_per_px = _validate_mm_per_px(mm_per_px)
        training_settings = SETTINGS.get("training", {}) or {}
        min_ok_samples = int(training_settings.get("min_ok_samples", 10))
        train_dataset_only = _is_truthy(training_settings.get("dataset_only", "1"))
        slog(
            "fit_ok.request",
            role_id=role_id,
            roi_id=roi_id,
            recipe_id=recipe_resolved,
            model_key=model_key_effective,
            request_id=request_id,
            n_files=len(images or []),
            memory_fit=bool(memory_fit),
            use_dataset=bool(use_dataset),
        )
        t0 = time.time()
        if train_dataset_only and not use_dataset:
            return JSONResponse(
                status_code=400,
                content={
                    "error": "Training only supported from dataset (use_dataset=true) in this deployment.",
                    "request_id": request_id,
                    "recipe_id": recipe_resolved,
                },
            )
        if not use_dataset and not images:
            return JSONResponse(
                status_code=400,
                content={"error": "No images provided", "request_id": request_id, "recipe_id": recipe_resolved},
            )

        all_emb: List[np.ndarray] = []
        token_hw: tuple[int, int] | None = None
        _ensure_recipe_mm_per_px(request_id, recipe_resolved, mm_per_px)

        if use_dataset:
            listing = store.list_dataset(role_id, roi_id, recipe_id=recipe_resolved)
            ok_files = listing.get("classes", {}).get("ok", {}).get("files", [])
            if not ok_files:
                return JSONResponse(
                    status_code=400,
                    content={
                        "error": "No OK dataset samples found",
                        "request_id": request_id,
                        "recipe_id": recipe_resolved,
                    },
                )
            if len(ok_files) < min_ok_samples:
                return JSONResponse(
                    status_code=400,
                    content={
                        "error": f"Insufficient OK samples: need at least {min_ok_samples}, found {len(ok_files)}",
                        "request_id": request_id,
                        "recipe_id": recipe_resolved,
                    },
                )

            for fn in ok_files:
                p = store.resolve_dataset_file_existing(role_id, roi_id, "ok", fn, recipe_id=recipe_resolved)
                if p is None:
                    continue
                img = _read_image_path(p)
                emb, token_hw_local = _extractor.extract(img)
                if token_hw is None:
                    token_hw = token_hw_local
                elif (int(token_hw_local[0]), int(token_hw_local[1])) != (int(token_hw[0]), int(token_hw[1])):
                    return JSONResponse(
                        status_code=400,
                        content={"error": f"Token grid mismatch: got {token_hw_local}, expected {token_hw}", "request_id": request_id},
                    )
                token_hw = token_hw_local
                all_emb.append(emb)
        else:
            for uf in images or []:
                img = _read_image_file(uf)
                emb, token_hw_local = _extractor.extract(img)
                if token_hw is None:
                    token_hw = token_hw_local
                elif (int(token_hw_local[0]), int(token_hw_local[1])) != (int(token_hw[0]), int(token_hw[1])):
                    return JSONResponse(
                        status_code=400,
                        content={"error": f"Token grid mismatch: got {token_hw_local}, expected {token_hw}", "request_id": request_id},
                    )
                token_hw = token_hw_local
                all_emb.append(emb)

        if not all_emb:
            return JSONResponse(
                status_code=400,
                content={"error": "No valid images", "request_id": request_id, "recipe_id": recipe_resolved},
            )

        E = np.concatenate(all_emb, axis=0)  # (N, D)

        # Coreset (puedes ajustar coreset_rate)
        coreset_rate = float(SETTINGS.get("inference", {}).get("coreset_rate", 0.02))
        if memory_fit:
            coreset_rate = 1.0
        mem = PatchCoreMemory.build(E, coreset_rate=coreset_rate, seed=0)

        # Persistir memoria + token grid
        applied_rate = float(mem.emb.shape[0]) / float(E.shape[0]) if E.shape[0] > 0 else 0.0
        if token_hw is None:
            raise ValueError("No valid OK images received; token grid (token_hw) is undefined.")

        store.save_memory(
            role_id,
            roi_id,
            mem.emb,
            token_hw,
            metadata={
                "coreset_rate": float(coreset_rate),
                "applied_rate": float(applied_rate),
            },
            recipe_id=recipe_resolved,
            model_key=model_key_effective,
        )

        # Persistir índice FAISS si está disponible
        try:
            import faiss  # type: ignore
            if mem.index is not None:
                buf = faiss.serialize_index(mem.index)
                store.save_index_blob(role_id, roi_id, bytes(buf), recipe_id=recipe_resolved, model_key=model_key_effective)
        except Exception:
            pass

        # Invalidate caches for this (recipe, model_key, role, roi) after re-fit
        _invalidate_memory_cache(recipe_resolved, model_key_effective, role_id, roi_id)
        _invalidate_calib_cache(recipe_resolved, model_key_effective, role_id, roi_id)

        response = {
            "n_embeddings": int(E.shape[0]),
            "coreset_size": int(mem.emb.shape[0]),
            "token_shape": [int(token_hw[0]), int(token_hw[1])],
            "coreset_rate_requested": float(coreset_rate),
            "coreset_rate_applied": float(applied_rate),
            "request_id": request_id,
            "recipe_id": recipe_resolved,
        }
        slog(
            "fit_ok.response",
            role_id=role_id,
            roi_id=roi_id,
            recipe_id=recipe_resolved,
            model_key=model_key_effective,
            request_id=request_id,
            elapsed_ms=int(1000 * (time.time() - t0)),
            n_embeddings=int(E.shape[0]),
            coreset_size=int(mem.emb.shape[0]),
        )
        return response
    except HTTPException:
        raise
    except Exception as e:
        request_id2, recipe_id2 = _resolve_request_context_safe(request, recipe_id)
        slog("fit_ok.error", request_id=request_id2, recipe_id=recipe_id2, error=str(e), trace=traceback.format_exc())
        return JSONResponse(status_code=500, content={"error": str(e), "request_id": request_id2, "recipe_id": recipe_id2})

@app.post("/calibrate_ng")
async def calibrate_ng(payload: Dict[str, Any], request: Request):
    """
    Fija umbral por ROI/rol con 0–3 NG.
    Si hay NG: umbral entre p99(OK) y p5(NG). Si no: p99(OK).
    Devuelve siempre 'threshold' como float (nunca null).
    """
    try:
        _raw_recipe = payload.get("recipe_id")
        recipe_from_payload = _raw_recipe if isinstance(_raw_recipe, str) else None

        request_id, recipe_resolved = _resolve_request_context(request, recipe_from_payload)

        role_id = payload["role_id"]
        roi_id = payload["roi_id"]

        _raw_model_key = payload.get("model_key")
        model_key_from_payload = _raw_model_key if isinstance(_raw_model_key, str) else None

        model_key = model_key_from_payload or roi_id
        mm_per_px = float(payload.get("mm_per_px", 0.2))
        ok_scores = _scores_1d_finite(payload.get("ok_scores"))
        ng_scores = _scores_1d_finite(payload.get("ng_scores")) if "ng_scores" in payload else None
        area_mm2_thr = float(payload.get("area_mm2_thr", SETTINGS.get("inference", {}).get("area_mm2_thr", 1.0)))
        p_score = int(payload.get("score_percentile", SETTINGS.get("inference", {}).get("score_percentile", 99)))

        slog(
            "calibrate_ng.request",
            role_id=role_id,
            roi_id=roi_id,
            recipe_id=recipe_resolved,
            model_key=model_key,
            request_id=request_id,
            ok_count=int(ok_scores.size),
            ng_count=int(ng_scores.size) if ng_scores is not None else 0,
        )
        t0 = time.time()

        t = choose_threshold(
            ok_scores,
            ng_scores if (ng_scores is not None and ng_scores.size > 0) else None,
            percentile=p_score,
        )

        ok_mean = float(np.mean(ok_scores)) if ok_scores.size else 0.0
        ng_mean = float(np.mean(ng_scores)) if (ng_scores is not None and ng_scores.size > 0) else 0.0
        p_ok = float(np.percentile(ok_scores, p_score)) if ok_scores.size else None
        p_ng = (
            float(np.percentile(ng_scores, 5))
            if (ng_scores is not None and ng_scores.size > 0)
            else None
        )

        calib = {
            "threshold": float(t),  # <- siempre float
            "ok_mean": ok_mean,
            "ng_mean": ng_mean,
            "p99_ok": p_ok,
            "p5_ng": p_ng,
            "mm_per_px": float(mm_per_px),
            "area_mm2_thr": float(area_mm2_thr),
            "score_percentile": int(p_score),
            "request_id": request_id,
            "recipe_id": recipe_resolved,
        }
        store.save_calib(role_id, roi_id, calib, recipe_id=recipe_resolved, model_key=model_key)
        _invalidate_calib_cache(recipe_resolved, model_key, role_id, roi_id)

        slog(
            "calibrate_ng.response",
            role_id=role_id,
            roi_id=roi_id,
            recipe_id=recipe_resolved,
            model_key=model_key,
            request_id=request_id,
            elapsed_ms=int(1000 * (time.time() - t0)),
            threshold=float(t),
        )
        return calib
    except HTTPException:
        raise
    except (KeyError, ValueError) as e:
        request_id2, recipe_id2 = _resolve_request_context_safe(
            request,
            payload.get("recipe_id") if isinstance(payload, dict) else None,
        )
        slog("calibrate_ng.bad_request", request_id=request_id2, recipe_id=recipe_id2, error=str(e))
        return JSONResponse(status_code=400, content={"error": str(e), "request_id": request_id2, "recipe_id": recipe_id2})
    except Exception as e:
        request_id2, recipe_id2 = _resolve_request_context_safe(
            request,
            payload.get("recipe_id") if isinstance(payload, dict) else None,
        )
        slog("calibrate_ng.error", request_id=request_id2, recipe_id=recipe_id2, error=str(e), trace=traceback.format_exc())
        return JSONResponse(status_code=500, content={"error": str(e), "request_id": request_id2, "recipe_id": recipe_id2})


@app.post("/infer")
def infer(
    request: Request,
    role_id: str = Form(...),
    roi_id: str = Form(...),
    mm_per_px: float = Form(...),
    image: UploadFile = File(...),
    shape: Optional[str] = Form(None),
    recipe_id: Optional[str] = Form(None),
    model_key: Optional[str] = Form(None),
):
    try:
        request_id, recipe_resolved = _resolve_request_context(request, recipe_id)
        model_key_effective = model_key or roi_id
        mm_per_px = _validate_mm_per_px(mm_per_px)
        _ensure_recipe_mm_per_px(request_id, recipe_resolved, mm_per_px)
        slog(
            "infer.request",
            role_id=role_id,
            roi_id=roi_id,
            recipe_id=recipe_resolved,
            model_key=model_key_effective,
            request_id=request_id,
        )
        t0 = time.time()

        import base64, json

        # 1) Imagen ROI canónica
        img = _read_image_file(image)

        # 2) Cargar memoria/coreset (cacheado por worker)
        cached = _get_patchcore_memory_cached(role_id, roi_id, recipe_id=recipe_resolved, model_key=model_key_effective)
        if cached is None:
            return JSONResponse(
                status_code=400,
                content={
                    "error": "Memoria no encontrada. Ejecuta /fit_ok antes de /infer.",
                    "request_id": request_id,
                    "recipe_id": recipe_resolved,
                },
            )
        mem, token_hw_mem, metadata = cached

        # 3) Calibración (opcional, también cacheada)
        calib = _get_calib_cached(role_id, roi_id, recipe_id=recipe_resolved, model_key=model_key_effective)
        thr = calib.get("threshold") if calib else None
        area_mm2_thr = calib.get("area_mm2_thr", SETTINGS.get("inference", {}).get("area_mm2_thr", 1.0)) if calib else SETTINGS.get("inference", {}).get("area_mm2_thr", 1.0)
        p_score = calib.get("score_percentile", SETTINGS.get("inference", {}).get("score_percentile", 99)) if calib else SETTINGS.get("inference", {}).get("score_percentile", 99)

        # 4) Shape/máscara (opcional)
        shape_obj = json.loads(shape) if shape else None

        # 5) Inferencia (1 sola extracción DINO por request)
        engine = InferenceEngine(
            _extractor,
            mem,
            token_hw_mem,
            mm_per_px=float(mm_per_px),
            memory_metadata=metadata,
        )

        token_shape_expected: tuple[int, int] | None = None
        token_hw_source = getattr(mem, "token_hw", None) or token_hw_mem
        if token_hw_source is not None:
            token_shape_expected = (int(token_hw_source[0]), int(token_hw_source[1]))

        try:
            res = engine.run(
                img,
                token_shape_expected=token_shape_expected,
                shape=shape_obj,
                threshold=thr,
                area_mm2_thr=float(area_mm2_thr),
                score_percentile=int(p_score),
            )
        except ValueError as ve:
            # Token grid mismatch u otras validaciones de entrada
            return JSONResponse(
                status_code=400,
                content={"error": str(ve), "request_id": request_id, "recipe_id": recipe_resolved},
            )

        score = float(res.get("score", 0.0))
        heat_u8 = res.get("heatmap_u8", None)
        regions = res.get("regions", []) or []
        token_shape_out = res.get("token_shape", [int(token_hw_mem[0]), int(token_hw_mem[1])])

        # 6) Heatmap -> PNG base64
        heatmap_png_b64 = None
        if heat_u8 is not None:
            heat_u8 = np.asarray(heat_u8, dtype=np.uint8)
            try:
                ok, buf = cv2.imencode(".png", heat_u8)
                if ok:
                    heatmap_png_b64 = base64.b64encode(buf.tobytes()).decode("ascii")
            except Exception:
                # PIL fallback (evita dependencias GL en algunos entornos)
                from PIL import Image
                import io
                im = Image.fromarray(heat_u8)
                bio = io.BytesIO()
                im.save(bio, format="PNG")
                heatmap_png_b64 = base64.b64encode(bio.getvalue()).decode("ascii")

        # 7) Normalizar regiones (solo para visualización)
        normalized_regions = []
        for r in regions:
            if isinstance(r, dict):
                region = dict(r)
                bbox = region.get("bbox")
                if bbox and isinstance(bbox, (list, tuple)) and len(bbox) == 4:
                    region.setdefault("x", float(bbox[0]))
                    region.setdefault("y", float(bbox[1]))
                    region.setdefault("w", float(bbox[2]))
                    region.setdefault("h", float(bbox[3]))
                elif {"x", "y", "w", "h"}.issubset(region.keys()):
                    region["bbox"] = [region.get("x"), region.get("y"), region.get("w"), region.get("h")]
                normalized_regions.append(region)
            else:
                normalized_regions.append(r)

        response = {
            "score": float(score),
            "threshold": (float(thr) if thr is not None else None),
            "token_shape": [int(token_shape_out[0]), int(token_shape_out[1])],
            "heatmap_png_base64": heatmap_png_b64,
            "regions": normalized_regions,
            "request_id": request_id,
            "recipe_id": recipe_resolved,
        }
        slog(
            "infer.response",
            role_id=role_id,
            roi_id=roi_id,
            recipe_id=recipe_resolved,
            model_key=model_key_effective,
            request_id=request_id,
            elapsed_ms=int(1000 * (time.time() - t0)),
            score=float(score),
            threshold=(float(thr) if thr is not None else None),
        )
        return response

    except HTTPException:
        raise
    except Exception as e:
        request_id2, recipe_id2 = _resolve_request_context_safe(request, recipe_id)
        slog("infer.error", request_id=request_id2, recipe_id=recipe_id2, error=str(e), trace=traceback.format_exc())
        return JSONResponse(status_code=500, content={"error": str(e), "request_id": request_id2, "recipe_id": recipe_id2})


@app.post("/infer_dataset")
def infer_dataset(payload: Dict[str, Any], request: Request):
    try:
        _raw_recipe = payload.get("recipe_id")
        recipe_from_payload = _raw_recipe if isinstance(_raw_recipe, str) else None
        request_id, recipe_resolved = _resolve_request_context(request, recipe_from_payload)

        role_id = payload["role_id"]
        roi_id = payload["roi_id"]
        model_key = payload.get("model_key") or roi_id
        labels = payload.get("labels") or ["ok", "ng"]
        include_heatmap = bool(payload.get("include_heatmap", False))
        default_mm_per_px = payload.get("default_mm_per_px")

        cached = _get_patchcore_memory_cached(role_id, roi_id, recipe_id=recipe_resolved, model_key=model_key)
        if cached is None:
            raise HTTPException(status_code=400, detail="Memoria no encontrada. Ejecuta /fit_ok antes de /infer_dataset.")
        mem, token_hw_mem, metadata = cached

        calib = _get_calib_cached(role_id, roi_id, recipe_id=recipe_resolved, model_key=model_key)
        thr = calib.get("threshold") if calib else None
        area_mm2_thr = calib.get("area_mm2_thr", SETTINGS.get("inference", {}).get("area_mm2_thr", 1.0)) if calib else SETTINGS.get("inference", {}).get("area_mm2_thr", 1.0)
        p_score = calib.get("score_percentile", SETTINGS.get("inference", {}).get("score_percentile", 99)) if calib else SETTINGS.get("inference", {}).get("score_percentile", 99)

        listing = store.list_dataset(role_id, roi_id, recipe_id=recipe_resolved)
        items: list[dict[str, Any]] = []
        n_errors = 0

        for label in labels:
            files = listing.get("classes", {}).get(label, {}).get("files", []) or []
            for fn in files:
                item: dict[str, Any] = {
                    "label": label,
                    "filename": fn,
                    "mm_per_px": None,
                    "score": None,
                    "threshold": float(thr) if thr is not None else None,
                    "regions": [],
                    "n_regions": 0,
                    "error": None,
                }
                try:
                    p = store.resolve_dataset_file_existing(role_id, roi_id, label, fn, recipe_id=recipe_resolved)
                    if p is None:
                        raise FileNotFoundError("file not found")

                    meta = store.load_dataset_meta(role_id, roi_id, label, fn, recipe_id=recipe_resolved, default={}) or {}
                    mm_value = meta.get("mm_per_px", default_mm_per_px)
                    if mm_value is None:
                        raise ValueError("mm_per_px missing and default_mm_per_px not provided")
                    mm = _validate_mm_per_px(mm_value)

                    shape_obj = _parse_shape_value(meta.get("shape_json"))
                    img = _read_image_path(p)

                    engine = InferenceEngine(
                        _extractor,
                        mem,
                        token_hw_mem,
                        mm_per_px=float(mm),
                        memory_metadata=metadata,
                    )

                    token_shape_expected = (int(token_hw_mem[0]), int(token_hw_mem[1]))
                    res = engine.run(
                        img,
                        token_shape_expected=token_shape_expected,
                        shape=shape_obj,
                        threshold=thr,
                        area_mm2_thr=float(area_mm2_thr),
                        score_percentile=int(p_score),
                    )

                    heat_u8 = res.get("heatmap_u8")
                    heatmap_png_b64 = None
                    if include_heatmap and heat_u8 is not None:
                        heat_u8 = np.asarray(heat_u8, dtype=np.uint8)
                        try:
                            ok, buf = cv2.imencode(".png", heat_u8)
                            if ok:
                                heatmap_png_b64 = base64_from_bytes(buf.tobytes())
                        except Exception:
                            from PIL import Image
                            import io
                            im = Image.fromarray(heat_u8)
                            bio = io.BytesIO()
                            im.save(bio, format="PNG")
                            heatmap_png_b64 = base64_from_bytes(bio.getvalue())

                    regions = res.get("regions", []) or []
                    item.update(
                        {
                            "mm_per_px": float(mm),
                            "score": float(res.get("score", 0.0)),
                            "regions": regions,
                            "n_regions": len(regions),
                        }
                    )
                    if include_heatmap:
                        item["heatmap_png_base64"] = heatmap_png_b64
                except Exception as exc:
                    item["error"] = str(exc)
                    n_errors += 1
                items.append(item)

        return {
            "status": "ok",
            "role_id": role_id,
            "roi_id": roi_id,
            "recipe_id": recipe_resolved,
            "model_key": model_key,
            "request_id": request_id,
            "n_total": len(items),
            "n_errors": n_errors,
            "items": items,
        }
    except HTTPException:
        raise
    except (KeyError, ValueError) as e:
        request_id2, recipe_id2 = _resolve_request_context_safe(
            request,
            payload.get("recipe_id") if isinstance(payload, dict) else None,
        )
        return JSONResponse(status_code=400, content={"error": str(e), "request_id": request_id2, "recipe_id": recipe_id2})
    except Exception as e:
        request_id2, recipe_id2 = _resolve_request_context_safe(
            request,
            payload.get("recipe_id") if isinstance(payload, dict) else None,
        )
        return JSONResponse(status_code=500, content={"error": str(e), "request_id": request_id2, "recipe_id": recipe_id2})


@app.post("/calibrate_dataset")
def calibrate_dataset(payload: Dict[str, Any], request: Request):
    try:
        _raw_recipe = payload.get("recipe_id")
        recipe_from_payload = _raw_recipe if isinstance(_raw_recipe, str) else None
        request_id, recipe_resolved = _resolve_request_context(request, recipe_from_payload)

        role_id = payload["role_id"]
        roi_id = payload["roi_id"]
        model_key = payload.get("model_key") or roi_id
        score_percentile = int(payload.get("score_percentile", SETTINGS.get("inference", {}).get("score_percentile", 99)))
        area_mm2_thr = float(payload.get("area_mm2_thr", SETTINGS.get("inference", {}).get("area_mm2_thr", 1.0)))
        default_mm_per_px = payload.get("default_mm_per_px")
        require_ng = bool(payload.get("require_ng", True))

        cached = _get_patchcore_memory_cached(role_id, roi_id, recipe_id=recipe_resolved, model_key=model_key)
        if cached is None:
            raise HTTPException(status_code=400, detail="Memoria no encontrada. Ejecuta /fit_ok antes de /calibrate_dataset.")
        mem, token_hw_mem, metadata = cached

        listing = store.list_dataset(role_id, roi_id, recipe_id=recipe_resolved)
        ok_files = listing.get("classes", {}).get("ok", {}).get("files", []) or []
        ng_files = listing.get("classes", {}).get("ng", {}).get("files", []) or []

        ok_scores: list[float] = []
        ng_scores: list[float] = []

        def _score_for(label: str, filename: str) -> Optional[float]:
            try:
                p = store.resolve_dataset_file_existing(role_id, roi_id, label, filename, recipe_id=recipe_resolved)
                if p is None:
                    return None
                meta = store.load_dataset_meta(role_id, roi_id, label, filename, recipe_id=recipe_resolved, default={}) or {}
                mm_value = meta.get("mm_per_px", default_mm_per_px)
                if mm_value is None:
                    return None
                mm = _validate_mm_per_px(mm_value)
                shape_obj = _parse_shape_value(meta.get("shape_json"))
                img = _read_image_path(p)
                engine = InferenceEngine(
                    _extractor,
                    mem,
                    token_hw_mem,
                    mm_per_px=float(mm),
                    memory_metadata=metadata,
                )
                token_shape_expected = (int(token_hw_mem[0]), int(token_hw_mem[1]))
                res = engine.run(
                    img,
                    token_shape_expected=token_shape_expected,
                    shape=shape_obj,
                    threshold=None,
                    area_mm2_thr=float(area_mm2_thr),
                    score_percentile=int(score_percentile),
                )
                return float(res.get("score", 0.0))
            except Exception:
                return None

        for fn in ok_files:
            score = _score_for("ok", fn)
            if score is not None:
                ok_scores.append(score)

        for fn in ng_files:
            score = _score_for("ng", fn)
            if score is not None:
                ng_scores.append(score)

        if not ok_scores:
            raise HTTPException(status_code=400, detail="No valid OK samples for calibration.")
        if require_ng and not ng_scores:
            raise HTTPException(status_code=400, detail="No NG samples available for calibration.")

        t = choose_threshold(
            np.asarray(ok_scores, dtype=float),
            np.asarray(ng_scores, dtype=float) if ng_scores else None,
            percentile=score_percentile,
        )

        calib = {
            "threshold": float(t),
            "score_percentile": int(score_percentile),
            "area_mm2_thr": float(area_mm2_thr),
            "recipe_id": recipe_resolved,
            "role_id": role_id,
            "roi_id": roi_id,
            "model_key": model_key,
            "created_at_utc": datetime.datetime.utcnow().isoformat() + "Z",
        }
        store.save_calib(role_id, roi_id, calib, recipe_id=recipe_resolved, model_key=model_key)
        _invalidate_calib_cache(recipe_resolved, model_key, role_id, roi_id)

        return {
            "status": "ok",
            "threshold": float(t),
            "score_percentile": int(score_percentile),
            "area_mm2_thr": float(area_mm2_thr),
            "n_ok": len(ok_scores),
            "n_ng": len(ng_scores),
            "request_id": request_id,
            "recipe_id": recipe_resolved,
            "role_id": role_id,
            "roi_id": roi_id,
            "model_key": model_key,
        }
    except HTTPException:
        raise
    except (KeyError, ValueError) as e:
        request_id2, recipe_id2 = _resolve_request_context_safe(
            request,
            payload.get("recipe_id") if isinstance(payload, dict) else None,
        )
        return JSONResponse(status_code=400, content={"error": str(e), "request_id": request_id2, "recipe_id": recipe_id2})
    except Exception as e:
        request_id2, recipe_id2 = _resolve_request_context_safe(
            request,
            payload.get("recipe_id") if isinstance(payload, dict) else None,
        )
        return JSONResponse(status_code=500, content={"error": str(e), "request_id": request_id2, "recipe_id": recipe_id2})

@app.post("/datasets/ok/upload")
async def datasets_ok_upload(
    request: Request,
    role_id: str = Form(...),
    roi_id: str = Form(...),
    images: List[UploadFile] = File(...),
    metas: Optional[List[str]] = Form(None),
    recipe_id: Optional[str] = Form(None),
):
    request_id, recipe_resolved = _resolve_request_context(request, recipe_id)
    slog("datasets.upload.request", label="ok", role_id=role_id, roi_id=roi_id, recipe_id=recipe_resolved, request_id=request_id, n_files=len(images))
    t0 = time.time()
    if metas is not None and len(metas) not in (0, len(images)):
        raise HTTPException(status_code=400, detail="metas length must match images length")
    saved = []
    for i, up in enumerate(images):
        meta_obj: dict[str, Any] = {}
        if metas is not None and i < len(metas) and metas[i]:
            meta_obj = json.loads(metas[i])

        if "mm_per_px" in meta_obj:
            mm_value = _validate_mm_per_px(meta_obj["mm_per_px"])
            _ensure_recipe_mm_per_px(request_id, recipe_resolved, mm_value)

        data = await up.read()
        ext = Path(up.filename or "x.png").suffix or ".png"
        path = store.save_dataset_image(role_id, roi_id, "ok", data, ext, recipe_id=recipe_resolved)
        saved.append(Path(path).name)

        meta_obj.setdefault("role_id", role_id)
        meta_obj.setdefault("roi_id", roi_id)
        meta_obj.setdefault("label", "ok")
        meta_obj.setdefault("filename", path.name)
        meta_obj.setdefault("recipe_id", recipe_resolved)
        meta_obj.setdefault("created_at_utc", datetime.datetime.utcnow().isoformat() + "Z")

        store.save_dataset_meta(role_id, roi_id, "ok", path.name, meta_obj, recipe_id=recipe_resolved)
    slog(
        "datasets.upload.response",
        label="ok",
        role_id=role_id,
        roi_id=roi_id,
        recipe_id=recipe_resolved,
        request_id=request_id,
        elapsed_ms=int(1000 * (time.time() - t0)),
        saved=len(saved),
    )
    return {"status": "ok", "saved": saved, "request_id": request_id, "recipe_id": recipe_resolved}


@app.post("/datasets/ng/upload")
async def datasets_ng_upload(
    request: Request,
    role_id: str = Form(...),
    roi_id: str = Form(...),
    images: List[UploadFile] = File(...),
    metas: Optional[List[str]] = Form(None),
    recipe_id: Optional[str] = Form(None),
):
    request_id, recipe_resolved = _resolve_request_context(request, recipe_id)
    slog("datasets.upload.request", label="ng", role_id=role_id, roi_id=roi_id, recipe_id=recipe_resolved, request_id=request_id, n_files=len(images))
    t0 = time.time()
    if metas is not None and len(metas) not in (0, len(images)):
        raise HTTPException(status_code=400, detail="metas length must match images length")
    saved = []
    for i, up in enumerate(images):
        meta_obj: dict[str, Any] = {}
        if metas is not None and i < len(metas) and metas[i]:
            meta_obj = json.loads(metas[i])

        if "mm_per_px" in meta_obj:
            mm_value = _validate_mm_per_px(meta_obj["mm_per_px"])
            _ensure_recipe_mm_per_px(request_id, recipe_resolved, mm_value)

        data = await up.read()
        ext = Path(up.filename or "x.png").suffix or ".png"
        path = store.save_dataset_image(role_id, roi_id, "ng", data, ext, recipe_id=recipe_resolved)
        saved.append(Path(path).name)

        meta_obj.setdefault("role_id", role_id)
        meta_obj.setdefault("roi_id", roi_id)
        meta_obj.setdefault("label", "ng")
        meta_obj.setdefault("filename", path.name)
        meta_obj.setdefault("recipe_id", recipe_resolved)
        meta_obj.setdefault("created_at_utc", datetime.datetime.utcnow().isoformat() + "Z")

        store.save_dataset_meta(role_id, roi_id, "ng", path.name, meta_obj, recipe_id=recipe_resolved)
    slog(
        "datasets.upload.response",
        label="ng",
        role_id=role_id,
        roi_id=roi_id,
        recipe_id=recipe_resolved,
        request_id=request_id,
        elapsed_ms=int(1000 * (time.time() - t0)),
        saved=len(saved),
    )
    return {"status": "ok", "saved": saved, "request_id": request_id, "recipe_id": recipe_resolved}


@app.get("/datasets/list")
def datasets_list(request: Request, role_id: str, roi_id: str, recipe_id: Optional[str] = None):
    request_id, recipe_resolved = _resolve_request_context(request, recipe_id)
    slog("datasets.list.request", role_id=role_id, roi_id=roi_id, recipe_id=recipe_resolved, request_id=request_id)
    data = store.list_dataset(role_id, roi_id, recipe_id=recipe_resolved)
    data["request_id"] = request_id
    data["recipe_id"] = recipe_resolved
    slog(
        "datasets.list.response",
        role_id=role_id,
        roi_id=roi_id,
        recipe_id=recipe_resolved,
        request_id=request_id,
        classes=list((data.get("classes") or {}).keys()),
    )
    return data


@app.get("/datasets/file")
def datasets_get_file(
    request: Request,
    role_id: str,
    roi_id: str,
    label: str,
    filename: str,
    recipe_id: Optional[str] = None,
):
    request_id, recipe_resolved = _resolve_request_context(request, recipe_id)
    p = store.resolve_dataset_file_existing(role_id, roi_id, label, filename, recipe_id=recipe_resolved)
    if p is None:
        raise HTTPException(status_code=404, detail="file not found")
    return FileResponse(str(p), filename=p.name)


@app.get("/datasets/meta")
def datasets_get_meta(
    request: Request,
    role_id: str,
    roi_id: str,
    label: str,
    filename: str,
    recipe_id: Optional[str] = None,
):
    request_id, recipe_resolved = _resolve_request_context(request, recipe_id)
    meta = store.load_dataset_meta(role_id, roi_id, label, filename, recipe_id=recipe_resolved, default=None)
    if meta is None:
        raise HTTPException(status_code=404, detail="meta not found")
    meta.setdefault("request_id", request_id)
    meta.setdefault("recipe_id", recipe_resolved)
    return meta


@app.get("/manifest")
def manifest(
    request: Request,
    role_id: str,
    roi_id: str,
    recipe_id: Optional[str] = None,
    model_key: Optional[str] = None,
):
    request_id, recipe_resolved = _resolve_request_context(request, recipe_id)
    model_key_effective = model_key or roi_id
    slog("manifest.request", role_id=role_id, roi_id=roi_id, recipe_id=recipe_resolved, model_key=model_key_effective, request_id=request_id)
    data = store.manifest(role_id, roi_id, recipe_id=recipe_resolved, model_key=model_key_effective)
    data["request_id"] = request_id
    slog(
        "manifest.response",
        role_id=role_id,
        roi_id=roi_id,
        recipe_id=recipe_resolved,
        model_key=model_key_effective,
        request_id=request_id,
        has_memory=bool(data.get("memory")),
        datasets=list((data.get("datasets", {}).get("classes", {}) or {}).keys()) if isinstance(data.get("datasets"), dict) else [],
    )
    return data


@app.get("/state")
def state(
    request: Request,
    role_id: str,
    roi_id: str,
    recipe_id: Optional[str] = None,
    model_key: Optional[str] = None,
):
    """Lightweight readiness/status endpoint used by the GUI (optional)."""
    request_id, recipe_resolved = _resolve_request_context(request, recipe_id)
    model_key_effective = model_key or roi_id
    mem_present = store.load_memory(role_id, roi_id, recipe_id=recipe_resolved, model_key=model_key_effective) is not None
    calib_present = store.load_calib(role_id, roi_id, default=None, recipe_id=recipe_resolved, model_key=model_key_effective) is not None
    return {
        "status": "ok",
        "memory_fitted": bool(mem_present),
        "calib_present": bool(calib_present),
        "request_id": request_id,
        "recipe_id": recipe_resolved,
        "role_id": role_id,
        "roi_id": roi_id,
        "model_key": model_key_effective,
    }


@app.delete("/datasets/file")
def datasets_delete_file(request: Request, role_id: str, roi_id: str, label: str, filename: str, recipe_id: Optional[str] = None):
    request_id, recipe_resolved = _resolve_request_context(request, recipe_id)
    ok = store.delete_dataset_file(role_id, roi_id, label, filename, recipe_id=recipe_resolved)
    slog(
        "datasets.delete",
        role_id=role_id,
        roi_id=roi_id,
        recipe_id=recipe_resolved,
        request_id=request_id,
        label=label,
        filename=filename,
        deleted=bool(ok),
    )
    return {"deleted": ok, "filename": filename, "request_id": request_id, "recipe_id": recipe_resolved}


@app.delete("/datasets/clear")
def datasets_clear_class(request: Request, role_id: str, roi_id: str, label: str, recipe_id: Optional[str] = None):
    request_id, recipe_resolved = _resolve_request_context(request, recipe_id)
    n = store.clear_dataset_class(role_id, roi_id, label, recipe_id=recipe_resolved)
    slog(
        "datasets.clear",
        role_id=role_id,
        roi_id=roi_id,
        recipe_id=recipe_resolved,
        request_id=request_id,
        label=label,
        cleared=int(n),
    )
    return {"cleared": n, "label": label, "request_id": request_id, "recipe_id": recipe_resolved}


if __name__ == "__main__":
    if not logging.getLogger().handlers:
        logging.basicConfig(level=logging.INFO)

    host = _env_var("BDI_BACKEND_HOST", legacy="BRAKEDISC_BACKEND_HOST")
    if not host:
        host = os.environ.get("HOST")
    host = host or "127.0.0.1"

    raw_port = _env_var("BDI_BACKEND_PORT", legacy="BRAKEDISC_BACKEND_PORT")
    if not raw_port:
        raw_port = os.environ.get("PORT")
    raw_port = raw_port or "8000"
    try:
        port = int(raw_port)
    except (TypeError, ValueError):
        log.warning("Invalid port '%s' provided via environment, falling back to 8000", raw_port)
        port = 8000

    log.info("Starting backend service on %s:%s", host, port)

    # Enable cuDNN autotuner for variable input sizes (improves latency on GPU)
    try:
        import torch  # type: ignore
        torch.backends.cudnn.benchmark = True  # noqa: F401
    except Exception:
        pass

    import uvicorn
    uvicorn.run("backend.app:app", host=host, port=port, reload=False)
