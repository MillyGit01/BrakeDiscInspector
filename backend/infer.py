from __future__ import annotations
import time
import numpy as np
import cv2
from typing import Tuple, Optional, Dict, Any, List

from .features import DinoV2Features
from .patchcore import PatchCoreMemory
from .roi_mask import build_mask
from .utils import percentile, mm2_to_px2, px2_to_mm2


class InferenceEngine:
    """
    Ejecuta el pipeline de inferencia:
      ROI (BGR uint8) -> embeddings (DINOv2) -> kNN (PatchCore) -> heatmap + score
      + posproceso opcional (blur, máscara ROI, umbral, eliminación de islas, contornos).
    """
    def __init__(self,
                 extractor: DinoV2Features,
                 memory: PatchCoreMemory,
                 token_hw: Tuple[int, int],
                 mm_per_px: float,
                 k: int = 1,
                 score_percentile: int = 99,
                 memory_metadata: Optional[Dict[str, Any]] = None):
        self.extractor = extractor
        self.memory = memory
        self.token_hw = tuple(token_hw)  # (Ht, Wt) con el que se construyó la memoria
        self.mm_per_px = float(mm_per_px)
        self.k = int(k)
        self.score_p = int(score_percentile)
        self.memory_metadata = memory_metadata or {}

    def run(self,
            img_bgr: np.ndarray,
            *,
            token_shape_expected: Optional[Tuple[int, int]] = None,
            shape: Optional[Dict[str, Any]] = None,
            blur_sigma: float = 1.0,
            area_mm2_thr: float = 1.0,
            threshold: Optional[float] = None,
            score_percentile: Optional[int] = None) -> Dict[str, Any]:
        """
        Ejecuta una pasada de inferencia.

        Args:
            img_bgr: imagen ROI en BGR (uint8).
            token_shape_expected: (Ht,Wt) esperado por la memoria. Si no coincide, se lanza ValueError.
            shape: definición de máscara ROI (rect/circle/annulus...).
            blur_sigma: sigma del GaussianBlur para suavizado del heatmap reescalado.
            area_mm2_thr: área mínima de defectos en mm² para eliminar islas pequeñas.
            threshold: si se pasa, se segmenta el heatmap y se devuelven regiones.
            score_percentile: si se pasa, sobrescribe el percentil usado para el score global.

        Returns:
            dict con:
              - score: float
              - threshold: Optional[float]
              - heatmap_u8: np.uint8[H,W] (0..255, ya enmascarado)
              - regions: lista de regiones (si threshold no es None)
              - token_shape: [Ht, Wt]
              - params: metadatos de ejecución
        """
        t0 = time.perf_counter()
        # 1) Embeddings del ROI canónico
        emb, (Ht, Wt) = self.extractor.extract(img_bgr)
        t1 = time.perf_counter()

        # Validación de grid si se solicita
        if token_shape_expected is not None:
            exp = tuple(int(x) for x in token_shape_expected)
            got = (int(Ht), int(Wt))
            if got != exp:
                raise ValueError(f"Token grid mismatch: got {got}, expected {exp}")

        # 2) Distancias kNN por parche (min-dist al coreset)
        d = self.memory.knn_min_dist(emb)  # (N,)
        t2 = time.perf_counter()
        heat = d.reshape(Ht, Wt).astype(np.float32)

        # 3) Reescalar a tamaño del ROI (para overlay)
        H, W = img_bgr.shape[:2]
        heat_up = cv2.resize(heat, (W, H), interpolation=cv2.INTER_LINEAR).astype(np.float32)

        # 4) Suavizado opcional
        if blur_sigma and blur_sigma > 0:
            ksize = int(max(3, round(blur_sigma * 3) * 2 + 1))
            heat_proc = cv2.GaussianBlur(heat_up, (ksize, ksize), blur_sigma)
        else:
            heat_proc = heat_up

        # 5) Máscara del ROI (rect/circle/annulus) si viene descrita
        mask = build_mask(H, W, shape)
        mask_bool = mask > 0

        # 6) Score global sobre el heatmap suavizado y enmascarado
        p_use = int(score_percentile) if score_percentile is not None else self.score_p
        valid = heat_proc[mask_bool]
        sc = percentile(valid, p_use) if valid.size else 0.0

        # 7) Generar heatmap 0..255 para visualización
        heat_vis = np.zeros_like(heat_proc, dtype=np.float32)
        if valid.size:
            mn, mx = np.percentile(valid, [1, 99])
            if mx > mn:
                heat_vis[mask_bool] = np.clip((heat_proc[mask_bool] - mn) / (mx - mn), 0.0, 1.0)
            else:
                heat_vis[mask_bool] = 0.0
        heat_u8 = (heat_vis * 255.0 + 0.5).astype(np.uint8)
        heat_u8_masked = cv2.bitwise_and(heat_u8, heat_u8, mask=mask)

        # 8) Umbral + eliminación de islas pequeñas + contornos
        regions: List[Dict[str, Any]] = []
        thr_value = float(threshold) if threshold is not None else None
        if thr_value is not None:
            bin_img = np.zeros((H, W), dtype=np.uint8)
            bin_img[mask_bool & (heat_proc >= thr_value)] = 255
            # Elimina regiones con área < área mínima (en mm² → px²)
            px_thr = mm2_to_px2(area_mm2_thr, self.mm_per_px)
            cnts, _ = cv2.findContours(bin_img, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
            for c in cnts:
                area_px = cv2.contourArea(c)
                if area_px < px_thr:
                    cv2.drawContours(bin_img, [c], -1, 0, thickness=-1)
            cnts, _ = cv2.findContours(bin_img, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
            for c in cnts:
                x, y, w, h = cv2.boundingRect(c)
                area_px = cv2.contourArea(c)
                regions.append({
                    "bbox": [int(x), int(y), int(w), int(h)],
                    "area_px": float(area_px),
                    "area_mm2": float(px2_to_mm2(area_px, self.mm_per_px)),
                    "contour": contour_to_list(c),
                })
            regions.sort(key=lambda r: r["area_px"], reverse=True)

        return {
            "score": float(sc),
            "threshold": float(thr_value) if thr_value is not None else None,
            "heatmap_u8": heat_u8_masked,   # la API lo convertirá a PNG base64
            "regions": regions,
            "token_shape": [int(Ht), int(Wt)],
            "timings_ms": {
                "preprocess": 0,
                "encode": int((t1 - t0) * 1000),
                "search": int((t2 - t1) * 1000),
                "post": int((time.perf_counter() - t2) * 1000),
            },
            "params": {
                "extractor": self.extractor.model_name,
                "input_size": int(self.extractor.input_size),
                "patch_size": int(self.extractor.patch),
                "coreset_rate": float(self.memory.coreset_rate) if self.memory.coreset_rate is not None else None,
                "coreset_rate_applied": self.memory_metadata.get("applied_rate"),
                "k": int(self.k),
                "score_percentile": int(p_use),
                "blur_sigma": float(blur_sigma),
                "mm_per_px": float(self.mm_per_px),
            },
        }


def contour_to_list(contour: np.ndarray) -> List[List[int]]:
    pts = contour.reshape(-1, 2)
    return [[int(x), int(y)] for x, y in pts]
