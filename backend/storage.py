from __future__ import annotations

import base64
import json
import re
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple
from datetime import datetime

import numpy as np

from .utils import ensure_dir, load_json, save_json

class ModelStore:
    def __init__(self, root: Path):
        self.root = Path(root)
        ensure_dir(self.root)

    # --- Path helpers -------------------------------------------------

    @staticmethod
    def _sanitize_recipe_id(recipe_id: Optional[str]) -> str:
        if not recipe_id:
            return "default"
        cleaned = re.sub(r"[^A-Za-z0-9._-]", "_", recipe_id).strip()
        cleaned = cleaned[:80]
        return cleaned or "default"

    @staticmethod
    def _sanitize_model_key(model_key: Optional[str]) -> str:
        if not model_key:
            return "default"
        cleaned = re.sub(r"[^A-Za-z0-9._-]", "_", model_key).strip()
        cleaned = cleaned[:80]
        return cleaned or "default"

    def _sanitize(self, value: str) -> str:
        cleaned = "".join(c if c.isalnum() or c in ("-", "_") else "_" for c in value.strip())
        return cleaned or "default"

    def _encode_component(self, value: str) -> str:
        """Return a filesystem-safe, injective encoding for role/ROI identifiers."""
        if not value:
            return "default"
        encoded = base64.urlsafe_b64encode(value.encode("utf-8")).decode("ascii").rstrip("=")
        return encoded or "default"

    def _base_name(self, role_id: str, roi_id: str) -> str:
        return f"{self._encode_component(role_id)}__{self._encode_component(roi_id)}"

    def _legacy_flat_base_name(self, role_id: str, roi_id: str) -> str:
        """Return the sanitized basename used by pre-encoding releases."""
        return f"{self._sanitize(role_id)}_{self._sanitize(roi_id)}"

    def _legacy_dir(self, role_id: str, roi_id: str) -> Path:
        return self.root / self._sanitize(role_id) / self._sanitize(roi_id)

    def resolve_models_dir(self, recipe_id: Optional[str], model_key: Optional[str]) -> Path:
        recipe_safe = self._sanitize_recipe_id(recipe_id)
        key_safe = self._sanitize_model_key(model_key)
        base = self.root / "recipes" / recipe_safe / key_safe
        ensure_dir(base)
        return base

    def _memory_path(self, role_id: str, roi_id: str, recipe_id: Optional[str], model_key: Optional[str]) -> Path:
        return self.resolve_models_dir(recipe_id, model_key) / f"{self._base_name(role_id, roi_id)}.npz"

    def _index_path(self, role_id: str, roi_id: str, recipe_id: Optional[str], model_key: Optional[str]) -> Path:
        return self.resolve_models_dir(recipe_id, model_key) / f"{self._base_name(role_id, roi_id)}_index.faiss"

    def _calib_path(self, role_id: str, roi_id: str, recipe_id: Optional[str], model_key: Optional[str]) -> Path:
        return self.resolve_models_dir(recipe_id, model_key) / f"{self._base_name(role_id, roi_id)}_calib.json"

    def _load_memory_from_path(self, path: Path):
        with np.load(path, allow_pickle=False) as z:
            emb = z["emb"].astype(np.float32)
            H = int(z["token_h"])
            W = int(z["token_w"])
            metadata = {}
            if "metadata" in z.files:
                meta_raw = z["metadata"]
                if np.ndim(meta_raw) == 0:
                    meta_str = str(meta_raw.item())
                else:
                    meta_str = str(meta_raw)
                try:
                    metadata = json.loads(meta_str)
                except Exception:
                    metadata = {}
        return emb, (H, W), metadata

    def save_memory(
        self,
        role_id: str,
        roi_id: str,
        embeddings: np.ndarray,
        token_hw: Tuple[int, int],
        metadata: Optional[Dict[str, Any]] = None,
        *,
        recipe_id: Optional[str] = None,
        model_key: Optional[str] = None,
    ):
        """
        Guarda la memoria (embeddings coreset L2-normalizados) y la forma del grid de tokens.
        """
        ensure_dir(self.root)
        payload = {
            "emb": embeddings.astype(np.float32),
            "token_h": int(token_hw[0]),
            "token_w": int(token_hw[1]),
        }
        if metadata:
            payload["metadata"] = json.dumps(metadata)
        np.savez_compressed(self._memory_path(role_id, roi_id, recipe_id, model_key or roi_id), **payload)

    def load_memory(self, role_id: str, roi_id: str, *, recipe_id: Optional[str] = None, model_key: Optional[str] = None):
        """
        Carga (embeddings, (Ht, Wt)) o None si no existe.
        """
        new_path = self._memory_path(role_id, roi_id, recipe_id, model_key or roi_id)
        if new_path.exists():
            return self._load_memory_from_path(new_path)

        # fallback: default recipe path
        if recipe_id and self._sanitize_recipe_id(recipe_id) != "default":
            default_path = self._memory_path(role_id, roi_id, "default", model_key or roi_id)
            if default_path.exists():
                return self._load_memory_from_path(default_path)

        flat_legacy_path = self.root / f"{self._legacy_flat_base_name(role_id, roi_id)}.npz"
        if flat_legacy_path.exists():
            return self._load_memory_from_path(flat_legacy_path)

        legacy_path = self._legacy_dir(role_id, roi_id) / "memory.npz"
        if legacy_path.exists():
            return self._load_memory_from_path(legacy_path)

        return None

    def save_index_blob(self, role_id: str, roi_id: str, blob: bytes, *, recipe_id: Optional[str] = None, model_key: Optional[str] = None):
        ensure_dir(self.root)
        self._index_path(role_id, roi_id, recipe_id, model_key or roi_id).write_bytes(blob)

    def load_index_blob(self, role_id: str, roi_id: str, *, recipe_id: Optional[str] = None, model_key: Optional[str] = None) -> Optional[bytes]:
        new_path = self._index_path(role_id, roi_id, recipe_id, model_key or roi_id)
        if new_path.exists():
            return new_path.read_bytes()

        if recipe_id and self._sanitize_recipe_id(recipe_id) != "default":
            default_path = self._index_path(role_id, roi_id, "default", model_key or roi_id)
            if default_path.exists():
                return default_path.read_bytes()

        flat_legacy_path = self.root / f"{self._legacy_flat_base_name(role_id, roi_id)}_index.faiss"
        if flat_legacy_path.exists():
            return flat_legacy_path.read_bytes()

        legacy_path = self._legacy_dir(role_id, roi_id) / "index.faiss"
        if legacy_path.exists():
            return legacy_path.read_bytes()
        return None

    def save_calib(self, role_id: str, roi_id: str, data: dict, *, recipe_id: Optional[str] = None, model_key: Optional[str] = None):
        save_json(self._calib_path(role_id, roi_id, recipe_id, model_key or roi_id), data)

    def load_calib(self, role_id: str, roi_id: str, default=None, *, recipe_id: Optional[str] = None, model_key: Optional[str] = None):
        new_path = self._calib_path(role_id, roi_id, recipe_id, model_key or roi_id)
        if new_path.exists():
            return load_json(new_path, default=default)

        if recipe_id and self._sanitize_recipe_id(recipe_id) != "default":
            default_path = self._calib_path(role_id, roi_id, "default", model_key or roi_id)
            if default_path.exists():
                return load_json(default_path, default=default)

        flat_legacy_path = self.root / f"{self._legacy_flat_base_name(role_id, roi_id)}_calib.json"
        if flat_legacy_path.exists():
            return load_json(flat_legacy_path, default=default)

        legacy_path = self._legacy_dir(role_id, roi_id) / "calib.json"
        return load_json(legacy_path, default=default)

    def _ds_dir(self, role_id: str, roi_id: str, label: str) -> Path:
        p = self.root / "datasets" / role_id / roi_id / label
        p.mkdir(parents=True, exist_ok=True)
        return p

    def save_dataset_image(self, role_id: str, roi_id: str, label: str, data: bytes, ext: str = ".png") -> Path:
        ts = datetime.now().strftime("%Y%m%d-%H%M%S-%f")
        ext = ext if ext.startswith(".") else "." + ext
        path = self._ds_dir(role_id, roi_id, label) / f"{ts}{ext.lower()}"
        path.write_bytes(data)
        return path

    def list_dataset(self, role_id: str, roi_id: str) -> Dict[str, Any]:
        base = self.root / "datasets" / role_id / roi_id
        out: Dict[str, Any] = {"role_id": role_id, "roi_id": roi_id, "classes": {}}
        if not base.exists():
            return out
        for cls in ["ok", "ng"]:
            d = base / cls
            if d.exists():
                files = sorted([f.name for f in d.iterdir() if f.is_file()])
                out["classes"][cls] = {"count": len(files), "files": files}
        return out

    def delete_dataset_file(self, role_id: str, roi_id: str, label: str, filename: str) -> bool:
        fn = Path(filename).name
        p = self._ds_dir(role_id, roi_id, label) / fn
        if p.exists() and p.is_file():
            p.unlink()
            return True
        return False

    def clear_dataset_class(self, role_id: str, roi_id: str, label: str) -> int:
        d = self._ds_dir(role_id, roi_id, label)
        count = 0
        for f in list(d.glob("*")):
            if f.is_file():
                f.unlink()
                count += 1
        return count

    def manifest(self, role_id: str, roi_id: str) -> Dict[str, Any]:
        return {
            "role_id": role_id,
            "roi_id": roi_id,
            "memory": self.load_memory(role_id, roi_id) is not None,
            "calib": self.load_calib(role_id, roi_id, default=None),
            "datasets": self.list_dataset(role_id, roi_id),
        }
