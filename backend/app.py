from __future__ import annotations
import json
import logging
import os
import sys
import traceback
import time
from pathlib import Path
from typing import Any, Dict, List, Optional

import numpy as np
import cv2

try:
    from fastapi import FastAPI, UploadFile, File, Form
    from fastapi.responses import JSONResponse
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

app = FastAPI(title="Anomaly Backend (PatchCore + DINOv2)")


def slog(event: str, **kw):
    rec = {"ts": time.time(), "event": event}
    rec.update(kw)
    print(json.dumps(rec, ensure_ascii=False), flush=True)

# Carpeta para artefactos persistentes por (role_id, roi_id)
def _env_var(name: str, *, legacy: str | None = None, default: str | None = None) -> str | None:
    """Return environment variable prioritising the new BDI_* keys."""
    value = os.environ.get(name)
    if not value and legacy:
        value = os.environ.get(legacy)
    if value is None or value == "":
        return default
    return value


MODELS_DIR = Path(_env_var("BDI_MODELS_DIR", legacy="BRAKEDISC_MODELS_DIR", default="models"))

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

# Carga única del extractor (congelado)
_extractor = DinoV2Features(
    model_name="vit_small_patch14_dinov2.lvd142m",
    device="auto",
    input_size=448,   # múltiplo de 14; si envías 384, el extractor reescala internamente
    patch_size=14
)

def _read_image_file(file: UploadFile) -> np.ndarray:
    data = file.file.read()
    img_array = np.frombuffer(data, dtype=np.uint8)
    img = cv2.imdecode(img_array, cv2.IMREAD_COLOR)
    if img is None:
        raise ValueError("No se pudo decodificar la imagen")
    return img

@app.get("/health")
def health():
    import torch
    return {
        "status": "ok",
        "device": "cuda" if torch.cuda.is_available() else "cpu",
        "model": "vit_small_patch14_dinov2.lvd142m",
        "version": "0.1.0",
    }

@app.post("/fit_ok")
def fit_ok(
    role_id: str = Form(...),
    roi_id: str = Form(...),
    mm_per_px: float = Form(...),
    images: List[UploadFile] = File(...),
    memory_fit: bool = Form(False),
):
    """
    Acumula OKs para construir la memoria PatchCore (coreset + kNN).
    Guarda (role_id, roi_id): memoria (embeddings), token grid y, si hay FAISS, el índice.
    """
    try:
        slog("fit_ok.request", role_id=role_id, roi_id=roi_id, n_files=len(images), memory_fit=bool(memory_fit))
        t0 = time.time()
        if not images:
            return JSONResponse(status_code=400, content={"error": "No images provided"})

        all_emb: List[np.ndarray] = []
        token_hw: Optional[tuple[int, int]] = None

        for uf in images:
            img = _read_image_file(uf)
            emb, hw = _extractor.extract(img)
            if token_hw is None:
                token_hw = (int(hw[0]), int(hw[1]))
            else:
                if (int(hw[0]), int(hw[1])) != token_hw:
                    return JSONResponse(
                        status_code=400,
                        content={"error": f"Token grid mismatch: got {hw}, expected {token_hw}"},
                    )
            all_emb.append(emb)

        if not all_emb:
            return JSONResponse(status_code=400, content={"error": "No valid images"})

        E = np.concatenate(all_emb, axis=0)  # (N, D)

        # Coreset (puedes ajustar coreset_rate)
        coreset_rate = float(SETTINGS.get("inference", {}).get("coreset_rate", 0.02))
        if memory_fit:
            coreset_rate = 1.0
        mem = PatchCoreMemory.build(E, coreset_rate=coreset_rate, seed=0)

        # Persistir memoria + token grid
        applied_rate = float(mem.emb.shape[0]) / float(E.shape[0]) if E.shape[0] > 0 else 0.0
        store.save_memory(
            role_id,
            roi_id,
            mem.emb,
            token_hw,
            metadata={
                "coreset_rate": float(coreset_rate),
                "applied_rate": float(applied_rate),
            },
        )

        # Persistir índice FAISS si está disponible
        try:
            import faiss  # type: ignore
            if mem.index is not None:
                buf = faiss.serialize_index(mem.index)
                store.save_index_blob(role_id, roi_id, bytes(buf))
        except Exception:
            pass

        response = {
            "n_embeddings": int(E.shape[0]),
            "coreset_size": int(mem.emb.shape[0]),
            "token_shape": [int(token_hw[0]), int(token_hw[1])],
            "coreset_rate_requested": float(coreset_rate),
            "coreset_rate_applied": float(applied_rate),
        }
        slog(
            "fit_ok.response",
            role_id=role_id,
            roi_id=roi_id,
            elapsed_ms=int(1000 * (time.time() - t0)),
            n_embeddings=int(E.shape[0]),
            coreset_size=int(mem.emb.shape[0]),
        )
        return response
    except Exception as e:
        slog("fit_ok.error", role_id=role_id, roi_id=roi_id, error=str(e))
        return JSONResponse(status_code=500, content={"error": str(e), "trace": traceback.format_exc()})

@app.post("/calibrate_ng")
async def calibrate_ng(payload: Dict[str, Any]):
    """
    Fija umbral por ROI/rol con 0–3 NG.
    Si hay NG: umbral entre p99(OK) y p5(NG). Si no: p99(OK).
    Devuelve siempre 'threshold' como float (nunca null).
    """
    try:
        role_id = payload["role_id"]
        roi_id = payload["roi_id"]
        mm_per_px = float(payload.get("mm_per_px", 0.2))
        ok_scores = np.asarray(payload.get("ok_scores", []), dtype=float)
        ng_scores = np.asarray(payload.get("ng_scores", []), dtype=float) if "ng_scores" in payload else None
        area_mm2_thr = float(payload.get("area_mm2_thr", SETTINGS.get("inference", {}).get("area_mm2_thr", 1.0)))
        p_score = int(payload.get("score_percentile", SETTINGS.get("inference", {}).get("score_percentile", 99)))

        slog(
            "calibrate_ng.request",
            role_id=role_id,
            roi_id=roi_id,
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
        }
        store.save_calib(role_id, roi_id, calib)
        slog(
            "calibrate_ng.response",
            role_id=role_id,
            roi_id=roi_id,
            elapsed_ms=int(1000 * (time.time() - t0)),
            threshold=float(t),
        )
        return calib
    except Exception as e:
        slog("calibrate_ng.error", error=str(e), payload_keys=list(payload.keys()))
        return JSONResponse(status_code=500, content={"error": str(e), "trace": traceback.format_exc()})


@app.post("/infer")
def infer(
    role_id: str = Form(...),
    roi_id: str = Form(...),
    mm_per_px: float = Form(...),
    image: UploadFile = File(...),
    shape: Optional[str] = Form(None),
):
    try:
        slog("infer.request", role_id=role_id, roi_id=roi_id)
        t0 = time.time()
        import json, base64, numpy as np
        try:
            import cv2  # opcional para PNG rápido
            _has_cv2 = True
        except Exception:
            _has_cv2 = False
            from PIL import Image
            import io

        # 1) Imagen y features (sólo para verificar grid)
        img = _read_image_file(image)
        emb, token_hw = _extractor.extract(img)

        # 2) Cargar memoria/coreset
        loaded = store.load_memory(role_id, roi_id)
        if loaded is None:
            return JSONResponse(status_code=400, content={"error": "Memoria no encontrada. Ejecuta /fit_ok primero."})
        emb_mem, token_hw_mem, metadata = loaded

        # 3) Validación de grid aquí (clara al usuario)
        if tuple(map(int, token_hw)) != tuple(map(int, token_hw_mem)):
            return JSONResponse(
                status_code=400,
                content={"error": f"Token grid mismatch: got {tuple(map(int,token_hw))}, expected {tuple(map(int,token_hw_mem))}"},
            )

        # 4) Reconstruir memoria (+FAISS si existe)
        mem = PatchCoreMemory(embeddings=emb_mem, index=None, coreset_rate=metadata.get("coreset_rate"))
        try:
            import faiss  # type: ignore
            blob = store.load_index_blob(role_id, roi_id)
            if blob is not None:
                idx = faiss.deserialize_index(np.frombuffer(blob, dtype=np.uint8))
                mem.index = idx
                mem.nn = None
        except Exception:
            pass

        # 5) Calibración (puede faltar)
        calib = store.load_calib(role_id, roi_id, default=None)
        thr = calib.get("threshold") if calib else None
        area_mm2_thr = calib.get("area_mm2_thr", SETTINGS.get("inference", {}).get("area_mm2_thr", 1.0)) if calib else SETTINGS.get("inference", {}).get("area_mm2_thr", 1.0)
        p_score = calib.get("score_percentile", SETTINGS.get("inference", {}).get("score_percentile", 99)) if calib else SETTINGS.get("inference", {}).get("score_percentile", 99)

        # 6) Shape (máscara) opcional
        shape_obj = json.loads(shape) if shape else None

        # 7) Crear engine con lo que tu __init__ soporte
        try:
            engine = InferenceEngine(_extractor, mem, token_hw_mem, mm_per_px=float(mm_per_px))
        except TypeError:
            # Si tu __init__ no acepta mm_per_px
            engine = InferenceEngine(_extractor, mem, token_hw_mem)

        # 8) Ejecutar run() (probar con token_shape_expected y si no reintentar sin él)
        try:
            res = engine.run(
                img,
                token_shape_expected=tuple(map(int, token_hw_mem)),
                shape=shape_obj,
                threshold=thr,
                area_mm2_thr=float(area_mm2_thr),
                score_percentile=int(p_score),
            )
        except TypeError:
            res = engine.run(
                img,
                shape=shape_obj,
                threshold=thr,
                area_mm2_thr=float(area_mm2_thr),
                score_percentile=int(p_score),
            )

        # 9) Normalizar salida (dict nuevo o tupla antigua)
        score: float
        regions = []
        heatmap_png_b64 = None
        token_shape_out = [int(token_hw_mem[0]), int(token_hw_mem[1])]

        if isinstance(res, dict):
            score = float(res.get("score", 0.0))
            regions = res.get("regions") or []
            token_shape_out = list(res.get("token_shape") or token_shape_out)
            # heatmap puede venir como uint8 ("heatmap_u8") o como float32 ("heatmap")
            hm_u8 = res.get("heatmap_u8")
            if hm_u8 is None:
                hm = res.get("heatmap")
                if hm is not None:
                    hm_u8 = np.clip(np.asarray(hm, dtype=np.float32) * 255.0, 0, 255).astype(np.uint8)
            if hm_u8 is not None:
                if _has_cv2:
                    ok, png = cv2.imencode(".png", np.asarray(hm_u8, dtype=np.uint8))
                    if ok:
                        heatmap_png_b64 = base64.b64encode(png.tobytes()).decode("ascii")
                else:
                    pil = Image.fromarray(np.asarray(hm_u8, dtype=np.uint8), mode="L")
                    buf = io.BytesIO()
                    pil.save(buf, format="PNG")
                    heatmap_png_b64 = base64.b64encode(buf.getvalue()).decode("ascii")
        else:
            # Compat tupla antigua: (score, heatmap_float, regions)
            score, heatmap_f32, regions = res
            hm_u8 = np.clip(np.asarray(heatmap_f32, dtype=np.float32) * 255.0, 0, 255).astype(np.uint8)
            if _has_cv2:
                ok, png = cv2.imencode(".png", hm_u8)
                if ok:
                    heatmap_png_b64 = base64.b64encode(png.tobytes()).decode("ascii")
            else:
                pil = Image.fromarray(hm_u8, mode="L")
                buf = io.BytesIO()
                pil.save(buf, format="PNG")
                heatmap_png_b64 = base64.b64encode(buf.getvalue()).decode("ascii")

        # 10) Respuesta (threshold puede ser None → se serializa como null)
        response = {
            "score": float(score),
            "threshold": (float(thr) if thr is not None else None),
            "token_shape": [int(token_shape_out[0]), int(token_shape_out[1])],
            "heatmap_png_base64": heatmap_png_b64,
            "regions": regions or [],
        }
        slog(
            "infer.response",
            role_id=role_id,
            roi_id=roi_id,
            elapsed_ms=int(1000 * (time.time() - t0)),
            score=float(score),
            threshold=(float(thr) if thr is not None else None),
        )
        return response

    except Exception as e:
        slog("infer.error", role_id=role_id, roi_id=roi_id, error=str(e))
        return JSONResponse(status_code=500, content={"error": str(e), "trace": traceback.format_exc()})



@app.post("/datasets/ok/upload")
def datasets_ok_upload(
    role_id: str = Form(...),
    roi_id: str = Form(...),
    images: List[UploadFile] = File(...)
):
    slog("datasets.upload.request", label="ok", role_id=role_id, roi_id=roi_id, n_files=len(images))
    t0 = time.time()
    saved = []
    for up in images:
        data = up.file.read()
        ext = Path(up.filename).suffix or ".png"
        path = store.save_dataset_image(role_id, roi_id, "ok", data, ext)
        saved.append(Path(path).name)
    slog(
        "datasets.upload.response",
        label="ok",
        role_id=role_id,
        roi_id=roi_id,
        elapsed_ms=int(1000 * (time.time() - t0)),
        saved=len(saved),
    )
    return {"status": "ok", "saved": saved}


@app.post("/datasets/ng/upload")
def datasets_ng_upload(
    role_id: str = Form(...),
    roi_id: str = Form(...),
    images: List[UploadFile] = File(...)
):
    slog("datasets.upload.request", label="ng", role_id=role_id, roi_id=roi_id, n_files=len(images))
    t0 = time.time()
    saved = []
    for up in images:
        data = up.file.read()
        ext = Path(up.filename).suffix or ".png"
        path = store.save_dataset_image(role_id, roi_id, "ng", data, ext)
        saved.append(Path(path).name)
    slog(
        "datasets.upload.response",
        label="ng",
        role_id=role_id,
        roi_id=roi_id,
        elapsed_ms=int(1000 * (time.time() - t0)),
        saved=len(saved),
    )
    return {"status": "ok", "saved": saved}


@app.get("/datasets/list")
def datasets_list(role_id: str, roi_id: str):
    slog("datasets.list.request", role_id=role_id, roi_id=roi_id)
    data = store.list_dataset(role_id, roi_id)
    slog(
        "datasets.list.response",
        role_id=role_id,
        roi_id=roi_id,
        classes=list((data.get("classes") or {}).keys()),
    )
    return data


@app.get("/manifest")
def manifest(role_id: str, roi_id: str):
    slog("manifest.request", role_id=role_id, roi_id=roi_id)
    data = store.manifest(role_id, roi_id)
    slog(
        "manifest.response",
        role_id=role_id,
        roi_id=roi_id,
        has_memory=bool(data.get("memory")),
        datasets=list((data.get("datasets", {}).get("classes", {}) or {}).keys()) if isinstance(data.get("datasets"), dict) else [],
    )
    return data


@app.delete("/datasets/file")
def datasets_delete_file(role_id: str, roi_id: str, label: str, filename: str):
    ok = store.delete_dataset_file(role_id, roi_id, label, filename)
    slog(
        "datasets.delete",
        role_id=role_id,
        roi_id=roi_id,
        label=label,
        filename=filename,
        deleted=bool(ok),
    )
    return {"deleted": ok, "filename": filename}


@app.delete("/datasets/clear")
def datasets_clear_class(role_id: str, roi_id: str, label: str):
    n = store.clear_dataset_class(role_id, roi_id, label)
    slog(
        "datasets.clear",
        role_id=role_id,
        roi_id=roi_id,
        label=label,
        cleared=int(n),
    )
    return {"cleared": n, "label": label}


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
