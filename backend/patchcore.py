from __future__ import annotations
import numpy as np

try:
    import faiss  # type: ignore
    _HAS_FAISS = True
except Exception:
    _HAS_FAISS = False

from sklearn.neighbors import NearestNeighbors

def l2_normalize(x: np.ndarray, eps: float = 1e-8) -> np.ndarray:
    n = np.linalg.norm(x, axis=1, keepdims=True) + eps
    return x / n

def kcenter_greedy(E: np.ndarray, m: int, seed: int = 0) -> np.ndarray:
    """Coreset k-center greedy sobre embeddings ya normalizados."""
    rng = np.random.default_rng(seed)
    n = E.shape[0]
    if m >= n:
        return np.arange(n, dtype=np.int64)
    c0 = int(rng.integers(0, n))
    centers = [c0]
    d = np.linalg.norm(E - E[c0], axis=1)
    for _ in range(1, m):
        i = int(np.argmax(d))
        centers.append(i)
        di = np.linalg.norm(E - E[i], axis=1)
        d = np.minimum(d, di)
    return np.array(centers, dtype=np.int64)

class PatchCoreMemory:
    def __init__(self, embeddings: np.ndarray, index=None, coreset_rate: float | None = None):
        self.emb = embeddings.astype(np.float32, copy=False)
        self.index = index
        self.nn = None
        self.coreset_rate = coreset_rate
        if index is None:
            self.nn = NearestNeighbors(n_neighbors=1, algorithm="auto", metric="euclidean")
            self.nn.fit(self.emb)

    @staticmethod
    def build(embeddings: np.ndarray, coreset_rate: float = 0.02, seed: int = 0) -> "PatchCoreMemory":
        E = l2_normalize(embeddings.astype(np.float32, copy=False))
        n = E.shape[0]
        m = max(1, int(np.ceil(n * coreset_rate)))
        idx = kcenter_greedy(E, m, seed=seed)
        C = E[idx]
        if _HAS_FAISS:
            import faiss  # type: ignore
            index = faiss.IndexFlatL2(C.shape[1])
            index.add(C)
            return PatchCoreMemory(C, index=index, coreset_rate=coreset_rate)
        else:
            return PatchCoreMemory(C, index=None, coreset_rate=coreset_rate)

    def knn_min_dist(self, query: np.ndarray) -> np.ndarray:
        Q = l2_normalize(query.astype(np.float32, copy=False))
        if self.index is None and self.nn is None:
            raise RuntimeError("PatchCoreMemory not fitted: missing kNN index (FAISS/sklearn).")
        if self.index is not None:
            import faiss  # type: ignore
            D, I = self.index.search(Q, 1)
            return np.sqrt(np.maximum(D[:, 0], 0.0))
        else:
            assert self.nn is not None
            d, i = self.nn.kneighbors(Q, n_neighbors=1, return_distance=True)
            return d[:, 0]
