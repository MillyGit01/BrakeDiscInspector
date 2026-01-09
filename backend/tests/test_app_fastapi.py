import io
import sys
import types
from types import SimpleNamespace

import numpy as np
from fastapi.testclient import TestClient
from PIL import Image


# --- Stubs to keep CI light -------------------------------------------------
# Avoid libGL dependency in CI (opencv-python-headless may not be installed)
if "cv2" not in sys.modules:  # pragma: no cover
    cv2_stub = types.ModuleType("cv2")

    def _imdecode(buf: np.ndarray, flags: int):  # type: ignore[override]
        pil = Image.open(io.BytesIO(buf.tobytes())).convert("RGB")
        return np.asarray(pil)[:, :, ::-1]

    def _imencode(ext: str, img: np.ndarray):  # type: ignore[override]
        out = io.BytesIO()
        Image.fromarray(img).save(out, format="PNG")
        return True, np.frombuffer(out.getvalue(), dtype=np.uint8)

    cv2_stub.IMREAD_COLOR = 1
    cv2_stub.imdecode = _imdecode
    cv2_stub.imencode = _imencode
    sys.modules["cv2"] = cv2_stub

# Lightweight stub for DinoV2 (avoid torch/timm in unit tests)
if "backend.features" not in sys.modules:  # pragma: no cover
    features_stub = types.ModuleType("backend.features")

    class _StubFeatures:
        def __init__(self, *_, **__):
            self.device = "cpu"
            self.model_name = "stub"
            self.input_size = 448
            self.patch = 14

        def extract(self, image):  # type: ignore[no-untyped-def]
            emb = np.ones((3, 4), dtype=np.float32)
            return emb, (2, 2)

        def get_metadata(self):
            return {"model_name": "stub"}

    features_stub.DinoV2Features = _StubFeatures
    sys.modules["backend.features"] = features_stub

from backend import app as app_mod


def _png_bytes(color=(120, 80, 200)) -> bytes:
    buf = io.BytesIO()
    Image.new("RGB", (32, 24), color=color).save(buf, format="PNG")
    return buf.getvalue()


def _reset_backend_state(tmp_path, monkeypatch):
    # Ensure store points to temp dir and caches are clean between tests
    monkeypatch.setattr(app_mod, "MODELS_DIR", tmp_path)
    monkeypatch.setattr(app_mod, "store", app_mod.ModelStore(tmp_path))
    if hasattr(app_mod, "_MEM_CACHE"):
        app_mod._MEM_CACHE.clear()
    if hasattr(app_mod, "_CALIB_CACHE"):
        app_mod._CALIB_CACHE.clear()


def test_fit_ok_persists_memory(tmp_path, monkeypatch):
    client = TestClient(app_mod.app)

    class DummyExtractor:
        def extract(self, image):
            emb = np.ones((3, 4), dtype=np.float32)
            return emb, (2, 2)

    monkeypatch.setattr(app_mod, "_extractor", DummyExtractor())

    def fake_build(embeddings, coreset_rate=0.02, seed=0):
        return SimpleNamespace(emb=np.ones((2, embeddings.shape[1]), dtype=np.float32), index=None)

    monkeypatch.setattr(app_mod.PatchCoreMemory, "build", staticmethod(fake_build))

    _reset_backend_state(tmp_path, monkeypatch)

    files = [("images", ("roi.png", _png_bytes(), "image/png"))]
    data = {"role_id": "Master", "roi_id": "Pattern", "mm_per_px": "0.25", "memory_fit": "false"}

    resp = client.post("/fit_ok", data=data, files=files)
    assert resp.status_code == 200, resp.text
    payload = resp.json()
    assert payload["n_embeddings"] == 3
    assert payload["coreset_size"] == 2
    assert payload["token_shape"] == [2, 2]

    mem_path = app_mod.store.resolve_memory_path_existing("Master", "Pattern", recipe_id="default", model_key="Pattern")
    assert mem_path is not None and mem_path.exists(), "memory file should be saved"


def test_calibrate_ng_persists_file(tmp_path, monkeypatch):
    client = TestClient(app_mod.app)
    _reset_backend_state(tmp_path, monkeypatch)

    payload = {
        "role_id": "Master",
        "roi_id": "Pattern",
        "mm_per_px": 0.2,
        "ok_scores": [10.0, 11.0, 13.0],
        "ng_scores": [22.0],
        "score_percentile": 99,
        "area_mm2_thr": 1.0,
    }

    resp = client.post("/calibrate_ng", json=payload)
    assert resp.status_code == 200, resp.text
    body = resp.json()
    assert "threshold" in body and isinstance(body["threshold"], float)

    calib_path = app_mod.store.resolve_calib_path_existing("Master", "Pattern", recipe_id="default", model_key="Pattern")
    assert calib_path is not None and calib_path.exists(), "calibration file should be created"


def test_calibrate_ng_accepts_null_ng_scores(tmp_path, monkeypatch):
    client = TestClient(app_mod.app)
    _reset_backend_state(tmp_path, monkeypatch)

    payload = {
        "role_id": "Master",
        "roi_id": "Pattern",
        "mm_per_px": 0.2,
        "ok_scores": [10.0, 11.0, 13.0],
        "ng_scores": None,  # <-- regression: GUI can send null
        "score_percentile": 99,
        "area_mm2_thr": 1.0,
    }

    resp = client.post("/calibrate_ng", json=payload)
    assert resp.status_code == 200, resp.text
    body = resp.json()
    assert "threshold" in body and isinstance(body["threshold"], float)
