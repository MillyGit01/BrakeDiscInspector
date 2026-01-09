from __future__ import annotations

from typing import Optional

import numpy as np


def _as_1d_finite(arr) -> np.ndarray:
    """Coerce scores input into a 1D float array and drop NaN/Inf."""
    if arr is None:
        return np.asarray([], dtype=float)
    x = np.asarray(arr, dtype=float).reshape(-1)
    if x.size == 0:
        return x
    return x[np.isfinite(x)]


def choose_threshold(ok_scores: np.ndarray, ng_scores: Optional[np.ndarray] = None, percentile: int = 99) -> float:
    """
    Devuelve un umbral sugerido a partir de:
      - p{percentile} de los OK (por defecto p99),
      - y opcionalmente p5 de los NG si existen (para separar).

    Robustez:
      - `ng_scores` puede venir como `null` desde el cliente => se trata como vacío.
      - se filtran NaN/Inf.
    """
    ok = _as_1d_finite(ok_scores)
    if ok.size == 0:
        raise ValueError("Se requiere al menos 1 score OK para calibrar")

    p_ok = float(np.percentile(ok, percentile))

    ng = _as_1d_finite(ng_scores)
    if ng.size == 0:
        return p_ok

    p_ng = float(np.percentile(ng, 5))

    if p_ng <= p_ok:
        return p_ok * 1.02  # pequeño margen

    return (p_ok + p_ng) * 0.5
