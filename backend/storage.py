from __future__ import annotations

import base64
import json
import re
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple
from datetime import datetime

import numpy as np

from .utils import ensure_dir, load_json, save_json

_RECIPE_ID_RE = re.compile(r"^[a-z0-9][a-z0-9_-]{0,63}$")

class ModelStore:
    # IDs reservados a nivel de API. No pueden ser usados por clientes como recipes válidos.
    # "last" se usa en la GUI como layout efímero (p.ej. last.layout.json) y nunca debe mapear a un recipe real.
    _RESERVED_RECIPE_IDS = {"last"}
    def __init__(self, root: Path):
        self.root = Path(root)
        ensure_dir(self.root)

    # --- Path helpers -------------------------------------------------

    @staticmethod
    def _sanitize_recipe_id(recipe_id: str | None) -> str:
        if recipe_id is None:
            return "default"

        s = recipe_id.strip().lower()
        if not s:
            return "default"

        # RECHAZAR IDs reservados (no hacer fallback silencioso)
        if s in ModelStore._RESERVED_RECIPE_IDS:
            raise ValueError(
                f"Invalid recipe_id '{recipe_id}': value is reserved. "
                f"Use an explicit recipe name (e.g. 'default' or a real recipe id)."
            )

        if not _RECIPE_ID_RE.match(s):
            raise ValueError(
                f"Invalid recipe_id '{recipe_id}'. Allowed: [a-z0-9][a-z0-9_-]{{0,63}}."
            )
        return s

    @staticmethod
    def _sanitize_model_key(model_key: Optional[str]) -> str:
        if not model_key:
            return "default"
        cleaned = re.sub(r"[^A-Za-z0-9._-]", "_", model_key).strip()
        cleaned = cleaned[:80]
        return cleaned or "default"

    @staticmethod
    def _sanitize_label(label: str) -> str:
        v = (label or "").strip().lower()
        if v not in ("ok", "ng"):
            raise ValueError("label debe ser 'ok' o 'ng'")
        return v

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

    def resolve_models_dir(self, recipe_id: Optional[str], model_key: Optional[str], *, create: bool = True) -> Path:
        recipe_safe = self._sanitize_recipe_id(recipe_id)
        key_safe = self._sanitize_model_key(model_key)
        base = self.root / "recipes" / recipe_safe / key_safe
        if create:
            ensure_dir(base)
        return base

    def _find_recipe_dir_case_insensitive(self, recipe_safe: str) -> Optional[str]:
        """Return existing recipe directory name on disk matching recipe_safe ignoring case."""
        recipes_root = self.root / "recipes"
        if not recipes_root.exists():
            return None
        try:
            for d in recipes_root.iterdir():
                if d.is_dir() and d.name.lower() == recipe_safe:
                    return d.name
        except OSError:
            return None
        return None

    def _memory_path(
        self,
        role_id: str,
        roi_id: str,
        recipe_id: Optional[str],
        model_key: Optional[str],
        *,
        create: bool = True,
    ) -> Path:
        return self.resolve_models_dir(recipe_id, model_key, create=create) / f"{self._base_name(role_id, roi_id)}.npz"

    def _index_path(
        self,
        role_id: str,
        roi_id: str,
        recipe_id: Optional[str],
        model_key: Optional[str],
        *,
        create: bool = True,
    ) -> Path:
        return self.resolve_models_dir(recipe_id, model_key, create=create) / f"{self._base_name(role_id, roi_id)}_index.faiss"

    def _calib_path(
        self,
        role_id: str,
        roi_id: str,
        recipe_id: Optional[str],
        model_key: Optional[str],
        *,
        create: bool = True,
    ) -> Path:
        return self.resolve_models_dir(recipe_id, model_key, create=create) / f"{self._base_name(role_id, roi_id)}_calib.json"
    # --- Resolve existing artifact paths (recipe-aware with fallback) ---

    def resolve_memory_path_existing(
        self,
        role_id: str,
        roi_id: str,
        *,
        recipe_id: Optional[str] = None,
        model_key: Optional[str] = None,
    ) -> Optional[Path]:
        model_key_effective = model_key or roi_id
        new_path = self._memory_path(role_id, roi_id, recipe_id, model_key_effective, create=False)
        if new_path.exists():
            return new_path

        # Backwards-compat: if recipe folder exists with different casing, try it before fallback.
        if recipe_id:
            recipe_safe = self._sanitize_recipe_id(recipe_id)
            if recipe_safe != "default":
                alt_recipe_dir = self._find_recipe_dir_case_insensitive(recipe_safe)
                if alt_recipe_dir and alt_recipe_dir != recipe_safe:
                    alt_base = self.root / "recipes" / alt_recipe_dir / self._sanitize_model_key(model_key_effective)
                    alt_path = alt_base / f"{self._base_name(role_id, roi_id)}.npz"
                    if alt_path.exists():
                        return alt_path

        # fallback: default recipe path
        if recipe_id and self._sanitize_recipe_id(recipe_id) != "default":
            default_path = self._memory_path(role_id, roi_id, "default", model_key_effective, create=False)
            if default_path.exists():
                return default_path

        flat_legacy_path = self.root / f"{self._legacy_flat_base_name(role_id, roi_id)}.npz"
        if flat_legacy_path.exists():
            return flat_legacy_path

        legacy_path = self._legacy_dir(role_id, roi_id) / "memory.npz"
        if legacy_path.exists():
            return legacy_path

        return None

    def resolve_index_path_existing(
        self,
        role_id: str,
        roi_id: str,
        *,
        recipe_id: Optional[str] = None,
        model_key: Optional[str] = None,
    ) -> Optional[Path]:
        model_key_effective = model_key or roi_id
        new_path = self._index_path(role_id, roi_id, recipe_id, model_key_effective, create=False)
        if new_path.exists():
            return new_path

        if recipe_id:
            recipe_safe = self._sanitize_recipe_id(recipe_id)
            if recipe_safe != "default":
                alt_recipe_dir = self._find_recipe_dir_case_insensitive(recipe_safe)
                if alt_recipe_dir and alt_recipe_dir != recipe_safe:
                    alt_base = self.root / "recipes" / alt_recipe_dir / self._sanitize_model_key(model_key_effective)
                    alt_path = alt_base / f"{self._base_name(role_id, roi_id)}_index.faiss"
                    if alt_path.exists():
                        return alt_path

        if recipe_id and self._sanitize_recipe_id(recipe_id) != "default":
            default_path = self._index_path(role_id, roi_id, "default", model_key_effective, create=False)
            if default_path.exists():
                return default_path

        flat_legacy_path = self.root / f"{self._legacy_flat_base_name(role_id, roi_id)}_index.faiss"
        if flat_legacy_path.exists():
            return flat_legacy_path

        legacy_path = self._legacy_dir(role_id, roi_id) / "index.faiss"
        if legacy_path.exists():
            return legacy_path

        return None

    def resolve_calib_path_existing(
        self,
        role_id: str,
        roi_id: str,
        *,
        recipe_id: Optional[str] = None,
        model_key: Optional[str] = None,
    ) -> Optional[Path]:
        model_key_effective = model_key or roi_id
        new_path = self._calib_path(role_id, roi_id, recipe_id, model_key_effective, create=False)
        if new_path.exists():
            return new_path

        if recipe_id:
            recipe_safe = self._sanitize_recipe_id(recipe_id)
            if recipe_safe != "default":
                alt_recipe_dir = self._find_recipe_dir_case_insensitive(recipe_safe)
                if alt_recipe_dir and alt_recipe_dir != recipe_safe:
                    alt_base = self.root / "recipes" / alt_recipe_dir / self._sanitize_model_key(model_key_effective)
                    alt_path = alt_base / f"{self._base_name(role_id, roi_id)}_calib.json"
                    if alt_path.exists():
                        return alt_path

        if recipe_id and self._sanitize_recipe_id(recipe_id) != "default":
            default_path = self._calib_path(role_id, roi_id, "default", model_key_effective, create=False)
            if default_path.exists():
                return default_path

        flat_legacy_path = self.root / f"{self._legacy_flat_base_name(role_id, roi_id)}_calib.json"
        if flat_legacy_path.exists():
            return flat_legacy_path

        legacy_path = self._legacy_dir(role_id, roi_id) / "calib.json"
        if legacy_path.exists():
            return legacy_path

        return None



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
        Carga (embeddings, (Ht, Wt), metadata) o None si no existe.

        Nota: la resolución del fichero es recipe-aware con fallback a:
          1) recipe actual,
          2) recipe "default" (si recipe != default),
          3) layout legacy (flat),
          4) layout legacy por carpetas.
        """
        path = self.resolve_memory_path_existing(role_id, roi_id, recipe_id=recipe_id, model_key=model_key)
        if path is None:
            return None
        return self._load_memory_from_path(path)

    def save_index_blob(self, role_id: str, roi_id: str, blob: bytes, *, recipe_id: Optional[str] = None, model_key: Optional[str] = None):
        ensure_dir(self.root)
        self._index_path(role_id, roi_id, recipe_id, model_key or roi_id).write_bytes(blob)

    def load_index_blob(self, role_id: str, roi_id: str, *, recipe_id: Optional[str] = None, model_key: Optional[str] = None) -> Optional[bytes]:
        path = self.resolve_index_path_existing(role_id, roi_id, recipe_id=recipe_id, model_key=model_key)
        if path is None:
            return None
        return path.read_bytes()

    def save_calib(self, role_id: str, roi_id: str, data: dict, *, recipe_id: Optional[str] = None, model_key: Optional[str] = None):
        save_json(self._calib_path(role_id, roi_id, recipe_id, model_key or roi_id), data)

    def load_calib(self, role_id: str, roi_id: str, default=None, *, recipe_id: Optional[str] = None, model_key: Optional[str] = None):
        path = self.resolve_calib_path_existing(role_id, roi_id, recipe_id=recipe_id, model_key=model_key)
        if path is None:
            return default
        return load_json(path, default=default)

    # --- Dataset helpers (recipe-aware) -------------------------------------

    def _datasets_root(self, recipe_id: Optional[str], *, create: bool) -> Path:
        recipe_safe = self._sanitize_recipe_id(recipe_id)
        base = self.root / "recipes" / recipe_safe / "datasets"
        if create:
            ensure_dir(base)
        return base

    def _ds_base_dir_new(self, role_id: str, roi_id: str, recipe_id: Optional[str], *, create: bool) -> Path:
        base = self._datasets_root(recipe_id, create=create)
        p = base / self._base_name(role_id, roi_id)
        if create:
            ensure_dir(p)
        return p

    def _ds_base_dir_legacy(self, role_id: str, roi_id: str) -> Path:
        # Legacy layout (pre-recipe): models/datasets/<role>/<roi>/<ok|ng>/*
        return self.root / "datasets" / role_id / roi_id

    def resolve_dataset_base_existing(self, role_id: str, roi_id: str, *, recipe_id: Optional[str] = None) -> Optional[Path]:
        """
        Devuelve el directorio base de dataset existente para (role, roi) siguiendo este orden:
          1) recipe actual (nuevo layout),
          2) recipe "default" (si recipe != default),
          3) legacy layout (models/datasets/<role>/<roi>).

        No crea carpetas.
        """
        base_name = self._base_name(role_id, roi_id)

        p = self._datasets_root(recipe_id, create=False) / base_name
        if p.exists():
            return p

        # Backwards-compat: locate mixed-case recipe directory on disk (datasets)
        if recipe_id:
            recipe_safe = self._sanitize_recipe_id(recipe_id)
            if recipe_safe != "default":
                alt_recipe_dir = self._find_recipe_dir_case_insensitive(recipe_safe)
                if alt_recipe_dir and alt_recipe_dir != recipe_safe:
                    p_alt = self.root / "recipes" / alt_recipe_dir / "datasets" / base_name
                    if p_alt.exists():
                        return p_alt

        if recipe_id and self._sanitize_recipe_id(recipe_id) != "default":
            p2 = self._datasets_root("default", create=False) / base_name
            if p2.exists():
                return p2

        p3 = self._ds_base_dir_legacy(role_id, roi_id)
        if p3.exists():
            return p3

        return None

    def _ds_dir(self, role_id: str, roi_id: str, label: str, *, recipe_id: Optional[str] = None, create: bool = True) -> Path:
        """Devuelve el directorio de clase (ok|ng) para guardar. Crea carpetas por defecto."""
        lbl = self._sanitize_label(label)
        base = self._ds_base_dir_new(role_id, roi_id, recipe_id, create=create)
        p = base / lbl
        if create:
            ensure_dir(p)
        return p

    def save_dataset_image(
        self,
        role_id: str,
        roi_id: str,
        label: str,
        data: bytes,
        ext: str = ".png",
        *,
        recipe_id: Optional[str] = None,
    ) -> Path:
        ts = datetime.now().strftime("%Y%m%d-%H%M%S-%f")
        ext = ext if ext.startswith(".") else "." + ext
        path = self._ds_dir(role_id, roi_id, label, recipe_id=recipe_id, create=True) / f"{ts}{ext.lower()}"
        path.write_bytes(data)
        return path

    def list_dataset(self, role_id: str, roi_id: str, *, recipe_id: Optional[str] = None) -> Dict[str, Any]:
        base = self.resolve_dataset_base_existing(role_id, roi_id, recipe_id=recipe_id)
        out: Dict[str, Any] = {"role_id": role_id, "roi_id": roi_id, "classes": {}}
        if base is None or not base.exists():
            return out
        for cls in ["ok", "ng"]:
            d = base / cls
            if d.exists():
                files = sorted([f.name for f in d.iterdir() if f.is_file()])
                out["classes"][cls] = {"count": len(files), "files": files}
        return out

    def delete_dataset_file(self, role_id: str, roi_id: str, label: str, filename: str, *, recipe_id: Optional[str] = None) -> bool:
        base = self.resolve_dataset_base_existing(role_id, roi_id, recipe_id=recipe_id)
        if base is None:
            return False
        lbl = self._sanitize_label(label)
        fn = Path(filename).name
        p = base / lbl / fn
        if p.exists() and p.is_file():
            p.unlink()
            return True
        return False

    def clear_dataset_class(self, role_id: str, roi_id: str, label: str, *, recipe_id: Optional[str] = None) -> int:
        base = self.resolve_dataset_base_existing(role_id, roi_id, recipe_id=recipe_id)
        if base is None:
            return 0
        lbl = self._sanitize_label(label)
        d = base / lbl
        if not d.exists():
            return 0
        count = 0
        for f in list(d.glob("*")):
            if f.is_file():
                f.unlink()
                count += 1
        return count

    def manifest(
        self,
        role_id: str,
        roi_id: str,
        *,
        recipe_id: Optional[str] = None,
        model_key: Optional[str] = None,
    ) -> Dict[str, Any]:
        model_key_effective = model_key or roi_id
        return {
            "role_id": role_id,
            "roi_id": roi_id,
            "recipe_id": self._sanitize_recipe_id(recipe_id),
            "model_key": self._sanitize_model_key(model_key_effective),
            "memory": self.load_memory(role_id, roi_id, recipe_id=recipe_id, model_key=model_key_effective) is not None,
            "calib": self.load_calib(role_id, roi_id, default=None, recipe_id=recipe_id, model_key=model_key_effective),
            "datasets": self.list_dataset(role_id, roi_id, recipe_id=recipe_id),
        }
