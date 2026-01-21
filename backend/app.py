from __future__ import annotations
import json
import logging
import os
import shutil
import subprocess
import sys
import traceback
import time
import uuid
import threading
import datetime
from collections import OrderedDict
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, List, Optional, Sequence, Tuple, cast

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
    from backend.diagnostics import (
        bind_request_id,
        reset_request_id,
        diag_event,
        init_diagnostics_logger,
        diagnostics_log_path,
    )  # type: ignore[no-redef]
else:
    from .features import DinoV2Features
    from .patchcore import PatchCoreMemory
    from .storage import ModelStore
    from .infer import InferenceEngine
    from .calib import choose_threshold
    from .utils import ensure_dir, base64_from_bytes
    from .diagnostics import (
        bind_request_id,
        reset_request_id,
        diag_event,
        init_diagnostics_logger,
        diagnostics_log_path,
    )

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
    diag_event(event, **kw)


def _linux_fallback_logs_dir() -> Path:
    xdg = os.environ.get("XDG_DATA_HOME")
    base = Path(xdg).expanduser() if xdg else Path.home() / ".local" / "share"
    return (base / "BrakeDiscInspector" / "logs").resolve()


def is_wsl() -> bool:
    if os.environ.get("WSL_DISTRO_NAME") or os.environ.get("WSL_INTEROP"):
        return True
    try:
        version_text = Path("/proc/version").read_text(encoding="utf-8").lower()
    except Exception:
        return False
    return "microsoft" in version_text


def _windows_to_wsl_path(raw_path: str) -> Path | None:
    cleaned = raw_path.strip().strip('"')
    if not cleaned:
        return None
    cleaned = cleaned.replace("\\", "/")
    if ":" not in cleaned:
        return None
    drive, rest = cleaned.split(":", 1)
    if not drive:
        return None
    rest = rest.lstrip("/")
    if not rest:
        return None
    return Path(f"/mnt/{drive.lower()}/{rest}")


def try_get_windows_localappdata_in_wsl() -> Path | None:
    try:
        output = subprocess.check_output(
            ["cmd.exe", "/c", "echo %LOCALAPPDATA%"],
            timeout=2,
            text=True,
        ).strip()
    except Exception:
        return None
    if not output or output.startswith("%") or (":\\" not in output and ":/" not in output):
        return None
    output = output.strip().strip('"')
    if shutil.which("wslpath"):
        try:
            translated = subprocess.check_output(
                ["wslpath", "-u", output],
                timeout=2,
                text=True,
            ).strip()
            if translated:
                return Path(translated)
        except Exception:
            pass
    return _windows_to_wsl_path(output)


def resolve_gui_logs_dir() -> Path:
    override = os.environ.get("BDI_GUI_LOG_DIR")
    if override:
        return Path(override).expanduser().resolve()
    local_appdata = os.environ.get("LOCALAPPDATA")
    if local_appdata:
        if os.name == "nt":
            return (Path(local_appdata) / "BrakeDiscInspector" / "logs").resolve()
        translated = None
        if ":" in local_appdata:
            translated = _windows_to_wsl_path(local_appdata)
        if translated is not None:
            return (translated / "BrakeDiscInspector" / "logs").resolve()
    if is_wsl():
        base = try_get_windows_localappdata_in_wsl()
        if base is not None:
            return (base / "BrakeDiscInspector" / "logs").resolve()
    return (Path(__file__).resolve().parent / "logs").resolve()


_DIAG_LOG_DIR: Path | None = None


def _pick_writable_log_dir(primary: Path) -> Path | None:
    candidates = []
    seen = set()
    for candidate in (primary, (Path(__file__).resolve().parent / "logs").resolve()):
        if candidate in seen:
            continue
        candidates.append(candidate)
        seen.add(candidate)

    for log_dir in candidates:
        try:
            log_dir.mkdir(parents=True, exist_ok=True)
        except Exception as exc:
            log.warning("Failed to create log dir %s: %s", log_dir, exc)
            continue
        log_path = log_dir / "backend_diagnostics.jsonl"
        try:
            with open(log_path, "a", encoding="utf-8"):
                pass
        except Exception as exc:
            log.warning("Failed to open diagnostics log file %s: %s", log_path, exc)
            continue
        return log_dir
    return None


def _init_diag_logger() -> None:
    global _DIAG_LOG_DIR
    if diagnostics_log_path() is not None:
        return
    try:
        requested_dir = resolve_gui_logs_dir()
        log_dir = _pick_writable_log_dir(requested_dir)
        if log_dir is None:
            _DIAG_LOG_DIR = None
            log.warning("Diagnostics file logging disabled: no writable log directory found")
            return
        init_diagnostics_logger(log_dir)
        _DIAG_LOG_DIR = log_dir
    except Exception as exc:
        log.warning("Diagnostics logger initialization failed: %s", exc)
        _DIAG_LOG_DIR = None


def _artifact_fallback_reason(
    *,
    expected_path: Path,
    resolved_path: Path,
    role_id: str,
    roi_id: str,
    recipe_id_effective: str,
    model_key_effective: str,
    artifact: str,
) -> tuple[str | None, str | None]:
    if resolved_path == expected_path:
        return None, None

    if recipe_id_effective != "default":
        default_path_lookup = {
            "memory": store.expected_memory_path,
            "index": store.expected_index_path,
            "calib": store.expected_calib_path,
        }
        default_path = default_path_lookup[artifact](
            role_id,
            roi_id,
            recipe_id="default",
            model_key=model_key_effective,
            create=False,
        )
        if resolved_path == default_path:
            return artifact, "default_recipe_fallback"

    alt_recipe_dir = store._find_recipe_dir_case_insensitive(recipe_id_effective)
    if alt_recipe_dir and alt_recipe_dir != recipe_id_effective:
        alt_base = store.root / "recipes" / alt_recipe_dir / store._sanitize_model_key(model_key_effective)
        filename_lookup = {
            "memory": f"{store._base_name(role_id, roi_id)}.npz",
            "index": f"{store._base_name(role_id, roi_id)}_index.faiss",
            "calib": f"{store._base_name(role_id, roi_id)}_calib.json",
        }
        alt_path = alt_base / filename_lookup[artifact]
        if resolved_path == alt_path:
            return artifact, "case_insensitive_recipe_dir"

    legacy_lookup = {
        "memory": (store.root / f"{store._legacy_flat_base_name(role_id, roi_id)}.npz"),
        "index": (store.root / f"{store._legacy_flat_base_name(role_id, roi_id)}_index.faiss"),
        "calib": (store.root / f"{store._legacy_flat_base_name(role_id, roi_id)}_calib.json"),
    }
    if resolved_path == legacy_lookup[artifact]:
        return artifact, "legacy_flat"

    legacy_dir_lookup = {
        "memory": store._legacy_dir(role_id, roi_id) / "memory.npz",
        "index": store._legacy_dir(role_id, roi_id) / "index.faiss",
        "calib": store._legacy_dir(role_id, roi_id) / "calib.json",
    }
    if resolved_path == legacy_dir_lookup[artifact]:
        return artifact, "legacy_dir"

    return artifact, "other"


def _diag_artifacts(
    *,
    store: ModelStore,
    request_id: str,
    role_id: str,
    roi_id: str,
    recipe_id_raw: str | None,
    model_key_raw: str | None,
) -> dict[str, Any]:
    recipe_id_effective = store._sanitize_recipe_id(recipe_id_raw)
    model_key_effective = model_key_raw or roi_id

    expected_memory = store.expected_memory_path(
        role_id,
        roi_id,
        recipe_id=recipe_id_effective,
        model_key=model_key_effective,
        create=False,
    )
    expected_index = store.expected_index_path(
        role_id,
        roi_id,
        recipe_id=recipe_id_effective,
        model_key=model_key_effective,
        create=False,
    )
    expected_calib = store.expected_calib_path(
        role_id,
        roi_id,
        recipe_id=recipe_id_effective,
        model_key=model_key_effective,
        create=False,
    )

    resolved_memory = store.resolve_memory_path_existing(
        role_id,
        roi_id,
        recipe_id=recipe_id_effective,
        model_key=model_key_effective,
    )
    resolved_index = store.resolve_index_path_existing(
        role_id,
        roi_id,
        recipe_id=recipe_id_effective,
        model_key=model_key_effective,
    )
    resolved_calib = store.resolve_calib_path_existing(
        role_id,
        roi_id,
        recipe_id=recipe_id_effective,
        model_key=model_key_effective,
    )

    fallback_from = None
    fallback_reason = None
    if resolved_memory is not None and resolved_memory != expected_memory:
        fallback_from, fallback_reason = _artifact_fallback_reason(
            expected_path=expected_memory,
            resolved_path=resolved_memory,
            role_id=role_id,
            roi_id=roi_id,
            recipe_id_effective=recipe_id_effective,
            model_key_effective=model_key_effective,
            artifact="memory",
        )
    elif resolved_index is not None and resolved_index != expected_index:
        fallback_from, fallback_reason = _artifact_fallback_reason(
            expected_path=expected_index,
            resolved_path=resolved_index,
            role_id=role_id,
            roi_id=roi_id,
            recipe_id_effective=recipe_id_effective,
            model_key_effective=model_key_effective,
            artifact="index",
        )
    elif resolved_calib is not None and resolved_calib != expected_calib:
        fallback_from, fallback_reason = _artifact_fallback_reason(
            expected_path=expected_calib,
            resolved_path=resolved_calib,
            role_id=role_id,
            roi_id=roi_id,
            recipe_id_effective=recipe_id_effective,
            model_key_effective=model_key_effective,
            artifact="calib",
        )

    payload = {
        "request_id": request_id,
        "role_id": role_id,
        "roi_id": roi_id,
        "recipe_id_raw": recipe_id_raw,
        "recipe_id_effective": recipe_id_effective,
        "model_key_raw": model_key_raw,
        "model_key_effective": model_key_effective,
        "expected_memory_path": str(expected_memory),
        "resolved_memory_path": str(resolved_memory) if resolved_memory is not None else None,
        "memory_exists": bool(resolved_memory and resolved_memory.exists()),
        "expected_index_path": str(expected_index),
        "resolved_index_path": str(resolved_index) if resolved_index is not None else None,
        "index_exists": bool(resolved_index and resolved_index.exists()),
        "expected_calib_path": str(expected_calib),
        "resolved_calib_path": str(resolved_calib) if resolved_calib is not None else None,
        "calib_exists": bool(resolved_calib and resolved_calib.exists()),
    }
    if fallback_from and fallback_reason:
        payload["fallback_from"] = fallback_from
        payload["fallback_reason"] = fallback_reason
    return payload


def _attach_request_context(
    request: Request,
    *,
    request_id: str,
    recipe_id: str,
    role_id: str | None = None,
    roi_id: str | None = None,
    model_key: str | None = None,
) -> None:
    request.state.request_id = request_id
    request.state.recipe_id = recipe_id
    if role_id is not None:
        request.state.role_id = role_id
    if roi_id is not None:
        request.state.roi_id = roi_id
    if model_key is not None:
        request.state.model_key = model_key


def _resolve_request_context(request: Request, recipe_from_payload: str | None = None) -> tuple[str, str]:
    # Resolve request_id and recipe_id for routing/storage.
    # If recipe_id is invalid/reserved (e.g. 'last'), raise HTTPException(400).
    request_id = getattr(request.state, "request_id", None) or request.headers.get("X-Request-Id") or str(uuid.uuid4())

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
    request_id = getattr(request.state, "request_id", None) or request.headers.get("X-Request-Id") or str(uuid.uuid4())

    header_name = "X-Recipe-Id"
    recipe_from_header = request.headers.get(header_name)
    recipe_id = recipe_from_payload or recipe_from_header

    try:
        recipe_id_s = ModelStore._sanitize_recipe_id(recipe_id)
    except Exception:
        recipe_id_s = "default"

    return request_id, recipe_id_s

@app.middleware("http")
async def diag_http_middleware(request: Request, call_next):
    start = time.time()
    request_id = request.headers.get("X-Request-Id") or str(uuid.uuid4())
    request.state.request_id = request_id
    token = bind_request_id(request_id)
    try:
        response = await call_next(request)
    except Exception:
        elapsed_ms = int(1000 * (time.time() - start))
        recipe_id = getattr(request.state, "recipe_id", None)
        role_id = getattr(request.state, "role_id", None)
        roi_id = getattr(request.state, "roi_id", None)
        model_key = getattr(request.state, "model_key", None)
        if request_id is None or recipe_id is None:
            safe_request_id, safe_recipe_id = _resolve_request_context_safe(request)
            request_id = request_id or safe_request_id
            recipe_id = recipe_id or safe_recipe_id
        diag_event(
            "http",
            method=request.method,
            path=request.url.path,
            status_code=500,
            recipe_id=recipe_id,
            request_id=request_id,
            role_id=role_id,
            roi_id=roi_id,
            model_key=model_key,
            elapsed_ms=elapsed_ms,
        )
        reset_request_id(token)
        raise

    elapsed_ms = int(1000 * (time.time() - start))
    recipe_id = getattr(request.state, "recipe_id", None)
    role_id = getattr(request.state, "role_id", None)
    roi_id = getattr(request.state, "roi_id", None)
    model_key = getattr(request.state, "model_key", None)
    if request_id is None or recipe_id is None:
        safe_request_id, safe_recipe_id = _resolve_request_context_safe(request)
        request_id = request_id or safe_request_id
        recipe_id = recipe_id or safe_recipe_id

    diag_event(
        "http",
        method=request.method,
        path=request.url.path,
        status_code=response.status_code,
        recipe_id=recipe_id,
        request_id=request_id,
        role_id=role_id,
        roi_id=roi_id,
        model_key=model_key,
        elapsed_ms=elapsed_ms,
    )
    response.headers["X-Request-Id"] = request_id
    reset_request_id(token)
    return response

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
store: ModelStore = ModelStore(MODELS_DIR)

MM_PER_PX_EPS = 1e-6


@app.on_event("startup")
def _startup_diag():
    _init_diag_logger()
    resolved_log_dir = str(_DIAG_LOG_DIR) if _DIAG_LOG_DIR is not None else "disabled"
    resolved_log_file = (
        str((_DIAG_LOG_DIR / "backend_diagnostics.jsonl").resolve()) if _DIAG_LOG_DIR is not None else "disabled"
    )
    diag_event(
        "startup",
        cwd=os.getcwd(),
        resolved_log_dir=resolved_log_dir,
        resolved_log_file=resolved_log_file,
        models_dir=str(MODELS_DIR.resolve()),
        env_BDI_GUI_LOG_DIR=os.getenv("BDI_GUI_LOG_DIR"),
        env_LOCALAPPDATA=os.getenv("LOCALAPPDATA"),
        is_wsl=is_wsl(),
    )

# Manual validation checklist (WSL + override):
# 1) python app.py -> expect [diag] log dir: /mnt/c/Users/<user>/AppData/Local/BrakeDiscInspector/logs
# 2) ls -la "/mnt/c/Users/<user>/AppData/Local/BrakeDiscInspector/logs/backend_diagnostics.jsonl"
# 3) Call an endpoint (e.g. /datasets/list) and confirm JSONL grows.
# 4) export BDI_GUI_LOG_DIR="/tmp/bdi_logs_test" && python app.py -> verify log file creation.

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
_FAISS_GPU_RESOURCES: dict[int, Any] = {}


def _get_faiss_gpu_resources(device_id: int):
    import faiss  # type: ignore

    res = _FAISS_GPU_RESOURCES.get(device_id)
    if res is None:
        res = faiss.StandardGpuResources()
        _FAISS_GPU_RESOURCES[device_id] = res
    return res


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
    token_hw_mem = cast(Tuple[int, int], token_hw_mem)

    faiss_cfg = SETTINGS.get("faiss", {}) or {}
    prefer_gpu = _is_truthy(faiss_cfg.get("prefer_gpu", 1))
    gpu_device = int(faiss_cfg.get("gpu_device", 0))
    require_faiss = _is_truthy(faiss_cfg.get("require_faiss", 0))
    allow_sklearn_fallback = _is_truthy(faiss_cfg.get("allow_sklearn_fallback", 1))

    try:
        import faiss  # type: ignore
    except Exception as exc:
        if require_faiss or not allow_sklearn_fallback:
            raise RuntimeError(
                "FAISS is required but not installed; install faiss-gpu/faiss-cpu or enable sklearn fallback."
            ) from exc
        mem_obj = PatchCoreMemory(embeddings=emb_mem, index=None, coreset_rate=(metadata or {}).get("coreset_rate"))
    else:
        blob = store.load_index_blob(role_id, roi_id, recipe_id=recipe_id, model_key=model_key)
        if blob is not None:
            idx_cpu = faiss.deserialize_index(np.frombuffer(blob, dtype=np.uint8))
        else:
            idx_cpu = faiss.IndexFlatL2(int(emb_mem.shape[1]))
            idx_cpu.add(emb_mem.astype(np.float32, copy=False))

        idx = idx_cpu
        gpu_res = None
        if prefer_gpu and hasattr(faiss, "StandardGpuResources"):
            get_num_gpus = getattr(faiss, "get_num_gpus", None)
            ngpu = int(get_num_gpus()) if callable(get_num_gpus) else 0
            if ngpu > 0:
                gpu_res = _get_faiss_gpu_resources(gpu_device)
                idx = faiss.index_cpu_to_gpu(gpu_res, gpu_device, idx_cpu)
                diag_event(
                    "faiss.gpu.enabled",
                    recipe_id=recipe_id,
                    model_key=model_key,
                    role_id=role_id,
                    roi_id=roi_id,
                    device_id=gpu_device,
                )
        mem_obj = PatchCoreMemory(embeddings=emb_mem, index=idx, coreset_rate=(metadata or {}).get("coreset_rate"))
        if gpu_res is not None:
            mem_obj._faiss_gpu_res = gpu_res

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


def _read_image_file_with_len(file: UploadFile) -> tuple[np.ndarray, int]:
    data = file.file.read()
    data_len = len(data)
    img_array = np.frombuffer(data, dtype=np.uint8)
    img = cv2.imdecode(img_array, cv2.IMREAD_COLOR)
    if img is None:
        raise ValueError("No se pudo decodificar la imagen")
    return img, data_len


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


def _roi_index_guess(roi_id: str) -> Optional[int]:
    value = (roi_id or "").strip().lower()
    for prefix in ("inspection-", "inspection_", "inspection "):
        if value.startswith(prefix):
            tail = value[len(prefix):]
            if tail.isdigit():
                return int(tail)
    return None


def _dataset_summary(role_id: str, roi_id: str, *, recipe_id: str) -> dict[str, Any]:
    listing = store.list_dataset(role_id, roi_id, recipe_id=recipe_id)
    classes = listing.get("classes") or {}
    ok_files = classes.get("ok", {}).get("files", []) if isinstance(classes.get("ok"), dict) else []
    ng_files = classes.get("ng", {}).get("files", []) if isinstance(classes.get("ng"), dict) else []
    base = store.resolve_dataset_base_existing(role_id, roi_id, recipe_id=recipe_id)
    return {
        "dataset_base": str(base) if base is not None else None,
        "dataset_classes": list(classes.keys()),
        "dataset_ok_count": len(ok_files),
        "dataset_ng_count": len(ng_files),
    }


_FAISS_AVAILABLE: bool | None = None


def _faiss_available() -> bool:
    global _FAISS_AVAILABLE
    if _FAISS_AVAILABLE is None:
        try:
            import faiss  # type: ignore  # noqa: F401

            _FAISS_AVAILABLE = True
        except Exception:
            _FAISS_AVAILABLE = False
    return bool(_FAISS_AVAILABLE)


def _file_probe(path: Path | None) -> dict[str, Any]:
    if path is None:
        return {"exists": False, "size": None, "mtime_utc": None}
    try:
        stat = path.stat()
        mtime = datetime.datetime.utcfromtimestamp(stat.st_mtime).isoformat() + "Z"
        return {"exists": path.exists(), "size": int(stat.st_size), "mtime_utc": mtime}
    except Exception:
        return {"exists": False, "size": None, "mtime_utc": None}


def probe_artifacts(
    role_id: str,
    roi_id: str,
    recipe_id: str | None,
    model_key: str | None,
) -> dict[str, Any]:
    recipe_id_effective = store._sanitize_recipe_id(recipe_id)
    model_key_effective = model_key or roi_id
    expected_memory = store.expected_memory_path(
        role_id,
        roi_id,
        recipe_id=recipe_id_effective,
        model_key=model_key_effective,
        create=False,
    )
    expected_index = store.expected_index_path(
        role_id,
        roi_id,
        recipe_id=recipe_id_effective,
        model_key=model_key_effective,
        create=False,
    )
    expected_calib = store.expected_calib_path(
        role_id,
        roi_id,
        recipe_id=recipe_id_effective,
        model_key=model_key_effective,
        create=False,
    )
    resolved_memory = store.resolve_memory_path_existing(
        role_id,
        roi_id,
        recipe_id=recipe_id_effective,
        model_key=model_key_effective,
    )
    resolved_index = store.resolve_index_path_existing(
        role_id,
        roi_id,
        recipe_id=recipe_id_effective,
        model_key=model_key_effective,
    )
    resolved_calib = store.resolve_calib_path_existing(
        role_id,
        roi_id,
        recipe_id=recipe_id_effective,
        model_key=model_key_effective,
    )
    models_dir = store.resolve_models_dir(recipe_id_effective, model_key_effective, create=False)
    memory_probe = _file_probe(resolved_memory)
    index_probe = _file_probe(resolved_index)
    calib_probe = _file_probe(resolved_calib)
    return {
        "role_id": role_id,
        "roi_id": roi_id,
        "recipe_id_effective": recipe_id_effective,
        "model_key_effective": model_key_effective,
        "expected_memory_path": str(expected_memory),
        "resolved_memory_path": str(resolved_memory) if resolved_memory is not None else None,
        "memory_exists": bool(memory_probe["exists"]),
        "memory_size": memory_probe["size"],
        "memory_mtime_utc": memory_probe["mtime_utc"],
        "expected_index_path": str(expected_index),
        "resolved_index_path": str(resolved_index) if resolved_index is not None else None,
        "index_exists": bool(index_probe["exists"]),
        "index_size": index_probe["size"],
        "index_mtime_utc": index_probe["mtime_utc"],
        "expected_calib_path": str(expected_calib),
        "resolved_calib_path": str(resolved_calib) if resolved_calib is not None else None,
        "calib_exists": bool(calib_probe["exists"]),
        "calib_size": calib_probe["size"],
        "calib_mtime_utc": calib_probe["mtime_utc"],
        "models_dir": str(models_dir) if models_dir.exists() else None,
    }

@app.get("/health")
def health(request: Request):
    try:
        import torch

        cuda_available = torch.cuda.is_available()
    except Exception:
        cuda_available = False

    request_id, recipe_id = _resolve_request_context(request)
    _attach_request_context(request, request_id=request_id, recipe_id=recipe_id)
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
    t0: Optional[float] = None
    try:
        request_id, recipe_resolved = _resolve_request_context(request, recipe_id)
        model_key_effective = model_key or roi_id
        _attach_request_context(
            request,
            request_id=request_id,
            recipe_id=recipe_resolved,
            role_id=role_id,
            roi_id=roi_id,
            model_key=model_key_effective,
        )
        raw_recipe_id = recipe_id or request.headers.get("X-Recipe-Id")
        dataset_info = _dataset_summary(role_id, roi_id, recipe_id=recipe_resolved)
        expected_memory = store.expected_memory_path(
            role_id,
            roi_id,
            recipe_id=recipe_resolved,
            model_key=model_key_effective,
            create=False,
        )
        expected_index = store.expected_index_path(
            role_id,
            roi_id,
            recipe_id=recipe_resolved,
            model_key=model_key_effective,
            create=False,
        )
        expected_calib = store.expected_calib_path(
            role_id,
            roi_id,
            recipe_id=recipe_resolved,
            model_key=model_key_effective,
            create=False,
        )
        mm_per_px = _validate_mm_per_px(mm_per_px)
        training_settings = SETTINGS.get("training", {}) or {}
        min_ok_samples = int(training_settings.get("min_ok_samples", 10))
        train_dataset_only = _is_truthy(training_settings.get("dataset_only", "1"))
        diag_event(
            "fit_ok.request",
            request_id=request_id,
            recipe_id=recipe_resolved,
            role_id=role_id,
            roi_id=roi_id,
            model_key=model_key_effective,
            n_files=len(images or []),
            memory_fit=bool(memory_fit),
            use_dataset=bool(use_dataset),
            mm_per_px=float(mm_per_px),
            recipe_id_raw=raw_recipe_id,
            model_key_raw=model_key,
            dataset_base=dataset_info["dataset_base"],
            dataset_ok_count=dataset_info["dataset_ok_count"],
            dataset_ng_count=dataset_info["dataset_ng_count"],
            dataset_classes=dataset_info["dataset_classes"],
            expected_memory_path=str(expected_memory),
            expected_index_path=str(expected_index),
            expected_calib_path=str(expected_calib),
        )
        t0 = time.time()

        def _fit_ok_error(message: str, *, status_code: int = 400):
            diag_event(
                "fit_ok.error",
                request_id=request_id,
                recipe_id=recipe_resolved,
                role_id=role_id,
                roi_id=roi_id,
                model_key=model_key_effective,
                error_type="bad_request",
                error_message=message,
                elapsed_ms=int(1000 * (time.time() - t0)),
            )
            return JSONResponse(
                status_code=status_code,
                content={"error": message, "request_id": request_id, "recipe_id": recipe_resolved},
            )
        if train_dataset_only and not use_dataset:
            return _fit_ok_error("Training only supported from dataset (use_dataset=true) in this deployment.")
        if not use_dataset and not images:
            return _fit_ok_error("No images provided")

        all_emb: List[np.ndarray] = []
        token_hw: tuple[int, int] | None = None
        _ensure_recipe_mm_per_px(request_id, recipe_resolved, mm_per_px)

        if use_dataset:
            listing = store.list_dataset(role_id, roi_id, recipe_id=recipe_resolved)
            ok_files = listing.get("classes", {}).get("ok", {}).get("files", [])
            dataset_base = store.resolve_dataset_base_existing(role_id, roi_id, recipe_id=recipe_resolved)
            diag_event(
                "fit_ok.dataset",
                request_id=request_id,
                recipe_id=recipe_resolved,
                role_id=role_id,
                roi_id=roi_id,
                model_key=model_key_effective,
                dataset_base=str(dataset_base) if dataset_base is not None else None,
                ok_count=len(ok_files),
                ng_count=len(listing.get("classes", {}).get("ng", {}).get("files", []) or []),
                ok_files_sample=ok_files[:10],
                ok_files_sampled=len(ok_files[:10]),
            )
            if not ok_files:
                return _fit_ok_error("No OK dataset samples found")
            if len(ok_files) < min_ok_samples:
                return _fit_ok_error(
                    f"Insufficient OK samples: need at least {min_ok_samples}, found {len(ok_files)}"
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
                    return _fit_ok_error(f"Token grid mismatch: got {token_hw_local}, expected {token_hw}")
                token_hw = token_hw_local
                all_emb.append(emb)
        else:
            for uf in images or []:
                img = _read_image_file(uf)
                emb, token_hw_local = _extractor.extract(img)
                if token_hw is None:
                    token_hw = token_hw_local
                elif (int(token_hw_local[0]), int(token_hw_local[1])) != (int(token_hw[0]), int(token_hw[1])):
                    return _fit_ok_error(f"Token grid mismatch: got {token_hw_local}, expected {token_hw}")
                token_hw = token_hw_local
                all_emb.append(emb)

        if not all_emb:
            return _fit_ok_error("No valid images")

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

        memory_path_written = store.save_memory(
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
        index_path_written: str | None = None
        try:
            import faiss  # type: ignore
            if mem.index is not None:
                buf = faiss.serialize_index(mem.index)
                index_path_written = str(
                    store.save_index_blob(
                        role_id,
                        roi_id,
                        bytes(buf),
                        recipe_id=recipe_resolved,
                        model_key=model_key_effective,
                    )
                )
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
        memory_probe = _file_probe(Path(memory_path_written) if memory_path_written is not None else None)
        index_probe = _file_probe(Path(index_path_written) if index_path_written is not None else None)
        diag_event(
            "fit_ok.response",
            request_id=request_id,
            recipe_id=recipe_resolved,
            role_id=role_id,
            roi_id=roi_id,
            model_key=model_key_effective,
            elapsed_ms=int(1000 * (time.time() - t0)),
            n_embeddings=int(E.shape[0]),
            coreset_size=int(mem.emb.shape[0]),
            fitted_after=True,
            memory_path_written=str(memory_path_written) if memory_path_written is not None else None,
            index_path_written=index_path_written,
            memory_exists=bool(memory_probe["exists"]),
            memory_size=memory_probe["size"],
            memory_mtime_utc=memory_probe["mtime_utc"],
            index_exists=bool(index_probe["exists"]),
            index_size=index_probe["size"],
            index_mtime_utc=index_probe["mtime_utc"],
        )
        return response
    except HTTPException:
        raise
    except Exception as e:
        request_id2, recipe_id2 = _resolve_request_context_safe(request, recipe_id)
        diag_event(
            "fit_ok.error",
            request_id=request_id2,
            recipe_id=recipe_id2,
            error_type=type(e).__name__,
            error_message=str(e),
            elapsed_ms=int(1000 * (time.time() - t0)) if t0 is not None else None,
        )
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
        _attach_request_context(
            request,
            request_id=request_id,
            recipe_id=recipe_resolved,
            role_id=role_id,
            roi_id=roi_id,
            model_key=model_key,
        )
        mm_per_px = float(payload.get("mm_per_px", 0.2))
        ok_scores = _scores_1d_finite(payload.get("ok_scores"))
        ng_scores = _scores_1d_finite(payload.get("ng_scores")) if "ng_scores" in payload else None
        area_mm2_thr = float(payload.get("area_mm2_thr", SETTINGS.get("inference", {}).get("area_mm2_thr", 1.0)))
        p_score = int(payload.get("score_percentile", SETTINGS.get("inference", {}).get("score_percentile", 99)))

        diag_event(
            "calibrate_ng.request",
            role_id=role_id,
            roi_id=roi_id,
            recipe_id=recipe_resolved,
            model_key=model_key,
            request_id=request_id,
            ok_count=int(ok_scores.size),
            ng_count=int(ng_scores.size) if ng_scores is not None else 0,
            mm_per_px=float(mm_per_px),
            area_mm2_thr=float(area_mm2_thr),
            score_percentile=int(p_score),
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
        calib_path_written = store.save_calib(role_id, roi_id, calib, recipe_id=recipe_resolved, model_key=model_key)
        _invalidate_calib_cache(recipe_resolved, model_key, role_id, roi_id)
        calib_probe = _file_probe(Path(calib_path_written) if calib_path_written is not None else None)
        diag_event(
            "calibrate.saved",
            request_id=request_id,
            recipe_id=recipe_resolved,
            role_id=role_id,
            roi_id=roi_id,
            model_key=model_key,
            calib_path_written=str(calib_path_written),
            threshold=float(t),
            calib_exists=bool(calib_probe["exists"]),
            calib_size=calib_probe["size"],
            calib_mtime_utc=calib_probe["mtime_utc"],
        )

        diag_event(
            "calibrate_ng.response",
            role_id=role_id,
            roi_id=roi_id,
            recipe_id=recipe_resolved,
            model_key=model_key,
            request_id=request_id,
            elapsed_ms=int(1000 * (time.time() - t0)),
            threshold=float(t),
            ok_mean=ok_mean,
            ng_mean=ng_mean,
            p99_ok=p_ok,
            p5_ng=p_ng,
        )
        return calib
    except HTTPException:
        raise
    except (KeyError, ValueError) as e:
        request_id2, recipe_id2 = _resolve_request_context_safe(
            request,
            payload.get("recipe_id") if isinstance(payload, dict) else None,
        )
        diag_event("calibrate_ng.bad_request", request_id=request_id2, recipe_id=recipe_id2, error=str(e))
        return JSONResponse(status_code=400, content={"error": str(e), "request_id": request_id2, "recipe_id": recipe_id2})
    except Exception as e:
        request_id2, recipe_id2 = _resolve_request_context_safe(
            request,
            payload.get("recipe_id") if isinstance(payload, dict) else None,
        )
        diag_event("calibrate_ng.error", request_id=request_id2, recipe_id=recipe_id2, error=str(e), trace=traceback.format_exc())
        return JSONResponse(status_code=500, content={"error": str(e), "request_id": request_id2, "recipe_id": recipe_id2})


@app.post("/infer")
def infer(
    request: Request,
    role_id: str = Form(...),
    roi_id: str = Form(...),
    mm_per_px: float = Form(...),
    image: UploadFile = File(...),
    shape: Optional[str] = Form(None),
    include_heatmap: Optional[bool] = Form(None),
    recipe_id: Optional[str] = Form(None),
    model_key: Optional[str] = Form(None),
):
    t0: Optional[float] = None
    try:
        request_id, recipe_resolved = _resolve_request_context(request, recipe_id)
        model_key_effective = model_key or roi_id
        _attach_request_context(
            request,
            request_id=request_id,
            recipe_id=recipe_resolved,
            role_id=role_id,
            roi_id=roi_id,
            model_key=model_key_effective,
        )
        mm_per_px = _validate_mm_per_px(mm_per_px)
        _ensure_recipe_mm_per_px(request_id, recipe_resolved, mm_per_px)
        t0 = time.time()

        import base64, json

        # 1) Imagen ROI canónica
        img, image_len = _read_image_file_with_len(image)
        probe = probe_artifacts(role_id, roi_id, recipe_resolved, model_key_effective)
        calib = _get_calib_cached(role_id, roi_id, recipe_id=recipe_resolved, model_key=model_key_effective)
        thr = calib.get("threshold") if calib else None
        faiss_available = _faiss_available()
        has_fit_ok = bool(probe["memory_exists"] and (probe["index_exists"] or not faiss_available))
        diag_event(
            "infer.request",
            request_id=request_id,
            role_id=role_id,
            roi_id=roi_id,
            recipe_id=recipe_resolved,
            model_key=model_key_effective,
            image_shape=list(img.shape),
            image_bytes_len=int(image_len),
            mm_per_px=float(mm_per_px),
            threshold=(float(thr) if thr is not None else None),
            has_fit_ok=has_fit_ok,
            fit_ok_rule="memory_exists && (index_exists || !faiss_available)",
            faiss_available=faiss_available,
            shape_present=bool(shape),
        )
        diag_event(
            "infer.probe",
            request_id=request_id,
            **probe,
        )

        # 2) Cargar memoria/coreset (cacheado por worker)
        cached = _get_patchcore_memory_cached(role_id, roi_id, recipe_id=recipe_resolved, model_key=model_key_effective)
        if cached is None or not has_fit_ok:
            expected_path = Path(probe["expected_memory_path"])
            expected_dir = expected_path.parent
            dir_listing: list[str] = []
            list_error = None
            if expected_dir.exists():
                try:
                    dir_listing = sorted([p.name for p in expected_dir.iterdir()])[:20]
                except OSError as exc:
                    dir_listing = []
                    list_error = str(exc)
            dataset_info = _dataset_summary(role_id, roi_id, recipe_id=recipe_resolved)
            diag_payload = {
                "request_id": request_id,
                "recipe_id": recipe_resolved,
                "role_id": role_id,
                "roi_id": roi_id,
                "model_key": model_key_effective,
                "expected_memory_path": str(expected_path),
                "resolved_memory_path": probe.get("resolved_memory_path"),
                "memory_exists": bool(probe.get("memory_exists")),
                "index_exists": bool(probe.get("index_exists")),
                "faiss_available": faiss_available,
                "expected_dir": str(expected_dir),
                "dir_listing": dir_listing,
                "list_error": list_error,
                "roi_index_guess": _roi_index_guess(roi_id),
                "dataset_base": dataset_info["dataset_base"],
                "dataset_ok_count": dataset_info["dataset_ok_count"],
                "dataset_ng_count": dataset_info["dataset_ng_count"],
                "dataset_classes": dataset_info["dataset_classes"],
                "hint": "call fit_ok",
            }
            if dataset_info["dataset_ok_count"] < 10:
                diag_payload["reason"] = "insufficient_ok_samples"
            if not probe.get("memory_exists"):
                diag_payload["why_not_fitted"] = "memory_missing"
            elif faiss_available and not probe.get("index_exists"):
                diag_payload["why_not_fitted"] = "index_missing"
            diag_event("infer.not_fitted", **diag_payload)
            return JSONResponse(
                status_code=400,
                content={
                    "error": "Memoria no encontrada. Ejecuta /fit_ok antes de /infer.",
                    "request_id": request_id,
                    "recipe_id": recipe_resolved,
                },
            )
        mem, token_hw_mem, metadata = cached

        # 3) Calibración (obligatoria, también cacheada)
        calib = calib or _get_calib_cached(role_id, roi_id, recipe_id=recipe_resolved, model_key=model_key_effective)
        thr = calib.get("threshold") if calib else None
        area_mm2_thr = calib.get("area_mm2_thr", SETTINGS.get("inference", {}).get("area_mm2_thr", 1.0)) if calib else SETTINGS.get("inference", {}).get("area_mm2_thr", 1.0)
        p_score = calib.get("score_percentile", SETTINGS.get("inference", {}).get("score_percentile", 99)) if calib else SETTINGS.get("inference", {}).get("score_percentile", 99)
        if thr is None or float(thr) <= 0:
            diag_event(
                "infer.calibration_missing",
                request_id=request_id,
                role_id=role_id,
                roi_id=roi_id,
                recipe_id=recipe_resolved,
                model_key=model_key_effective,
            )
            return JSONResponse(
                status_code=400,
                content={
                    "error": "calibration_missing",
                    "request_id": request_id,
                    "recipe_id": recipe_resolved,
                },
            )

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

        decision = "ng" if float(score) >= float(thr) else "ok"
        should_include_heatmap = include_heatmap if include_heatmap is not None else decision == "ng"

        # 6) Heatmap -> PNG base64 (solo si se va a devolver)
        heatmap_png_b64 = None
        if should_include_heatmap and heat_u8 is not None:
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
            "decision": decision,
        }
        diag_event(
            "infer.response",
            request_id=request_id,
            role_id=role_id,
            roi_id=roi_id,
            recipe_id=recipe_resolved,
            model_key=model_key_effective,
            elapsed_ms=int(1000 * (time.time() - t0)),
            score=float(score),
            threshold=(float(thr) if thr is not None else None),
            decision=decision,
            timings_ms=res.get("timings_ms"),
        )
        return response

    except HTTPException:
        raise
    except Exception as e:
        request_id2, recipe_id2 = _resolve_request_context_safe(request, recipe_id)
        diag_event(
            "infer.error",
            request_id=request_id2,
            recipe_id=recipe_id2,
            error_type=type(e).__name__,
            error_message=str(e),
            elapsed_ms=int(1000 * (time.time() - t0)) if t0 is not None else None,
        )
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
        _attach_request_context(
            request,
            request_id=request_id,
            recipe_id=recipe_resolved,
            role_id=role_id,
            roi_id=roi_id,
            model_key=model_key,
        )
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
        if thr is None or float(thr) <= 0:
            raise HTTPException(status_code=400, detail="calibration_missing")

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
        _attach_request_context(
            request,
            request_id=request_id,
            recipe_id=recipe_resolved,
            role_id=role_id,
            roi_id=roi_id,
            model_key=model_key,
        )
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
    _attach_request_context(
        request,
        request_id=request_id,
        recipe_id=recipe_resolved,
        role_id=role_id,
        roi_id=roi_id,
    )
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
    _attach_request_context(
        request,
        request_id=request_id,
        recipe_id=recipe_resolved,
        role_id=role_id,
        roi_id=roi_id,
    )
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
    _attach_request_context(
        request,
        request_id=request_id,
        recipe_id=recipe_resolved,
        role_id=role_id,
        roi_id=roi_id,
    )
    diag_event(
        "datasets.list.request",
        request_id=request_id,
        role_id=role_id,
        roi_id=roi_id,
        recipe_id=recipe_resolved,
    )
    data = store.list_dataset(role_id, roi_id, recipe_id=recipe_resolved)
    class_counts = {
        name: len(meta.get("files", []) or [])
        for name, meta in (data.get("classes") or {}).items()
        if isinstance(meta, dict)
    }
    dataset_info = _dataset_summary(role_id, roi_id, recipe_id=recipe_resolved)
    response_payload = {
        "request_id": request_id,
        "role_id": role_id,
        "roi_id": roi_id,
        "recipe_id": recipe_resolved,
        "dataset_base": dataset_info["dataset_base"],
        "dataset_ok_count": dataset_info["dataset_ok_count"],
        "dataset_ng_count": dataset_info["dataset_ng_count"],
        "classes": dataset_info["dataset_classes"],
        "class_counts": class_counts,
    }
    if dataset_info["dataset_base"] is None:
        response_payload["dataset_base_reason"] = "dataset_base_missing"
    diag_event("datasets.list.response", **response_payload)
    data["request_id"] = request_id
    data["recipe_id"] = recipe_resolved
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
    _attach_request_context(
        request,
        request_id=request_id,
        recipe_id=recipe_resolved,
        role_id=role_id,
        roi_id=roi_id,
    )
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
    _attach_request_context(
        request,
        request_id=request_id,
        recipe_id=recipe_resolved,
        role_id=role_id,
        roi_id=roi_id,
    )
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
    _attach_request_context(
        request,
        request_id=request_id,
        recipe_id=recipe_resolved,
        role_id=role_id,
        roi_id=roi_id,
        model_key=model_key_effective,
    )
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
    _attach_request_context(
        request,
        request_id=request_id,
        recipe_id=recipe_resolved,
        role_id=role_id,
        roi_id=roi_id,
        model_key=model_key_effective,
    )
    diag_event(
        "state.request",
        request_id=request_id,
        role_id=role_id,
        roi_id=roi_id,
        recipe_id=recipe_resolved,
        model_key=model_key_effective,
    )
    probe = probe_artifacts(role_id, roi_id, recipe_resolved, model_key_effective)
    faiss_available = _faiss_available()
    has_fit_ok = bool(probe["memory_exists"] and (probe["index_exists"] or not faiss_available))
    mem_present = store.load_memory(role_id, roi_id, recipe_id=recipe_resolved, model_key=model_key_effective) is not None
    calib = store.load_calib(role_id, roi_id, default=None, recipe_id=recipe_resolved, model_key=model_key_effective)
    calib_present = calib is not None
    dataset_info = _dataset_summary(role_id, roi_id, recipe_id=recipe_resolved)
    diag_payload = {
        "request_id": request_id,
        "recipe_id": recipe_resolved,
        "role_id": role_id,
        "roi_id": roi_id,
        "model_key": model_key_effective,
        "fitted": bool(mem_present),
        "has_calib": bool(calib_present),
        "threshold": calib.get("threshold") if calib_present else None,
        "has_fit_ok": has_fit_ok,
        "fit_ok_rule": "memory_exists && (index_exists || !faiss_available)",
        "faiss_available": faiss_available,
        "probe_artifacts": probe,
        "roi_index_guess": _roi_index_guess(roi_id),
        "dataset_base": dataset_info["dataset_base"],
        "dataset_ok_count": dataset_info["dataset_ok_count"],
        "dataset_ng_count": dataset_info["dataset_ng_count"],
        "dataset_classes": dataset_info["dataset_classes"],
    }
    if dataset_info["dataset_ok_count"] < 10:
        diag_payload["reason"] = "insufficient_ok_samples"
    diag_event("state.response", **diag_payload)
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
    _attach_request_context(
        request,
        request_id=request_id,
        recipe_id=recipe_resolved,
        role_id=role_id,
        roi_id=roi_id,
    )
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
    _attach_request_context(
        request,
        request_id=request_id,
        recipe_id=recipe_resolved,
        role_id=role_id,
        roi_id=roi_id,
    )
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
