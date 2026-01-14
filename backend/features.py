# backend/features.py  (Option 2 robust: resize pos_embed manually + reset each call)
from __future__ import annotations

import io
import inspect
import logging
import threading
from contextlib import nullcontext
from typing import Any, Iterable, Optional, Tuple, Union, cast

import numpy as np
from PIL import Image

import torch
import torch.nn as nn
import torch.nn.functional as F
import timm

log = logging.getLogger(__name__)

# Pillow compatibility: Image.Resampling exists in newer versions.
_BICUBIC = getattr(getattr(Image, "Resampling", Image), "BICUBIC")


class DinoV2Features:
    """
    Extractor ViT/DINOv2 (timm) con:
      - LETTERBOX (mantener aspecto + padding) a input_size
      - Tamaño de entrada controlado (fijo o dinámico múltiplo de patch)
      - *Opción 2*: redimensionado del positional embedding **manual** (sin timm.resize_pos_embed)
      - *Reset* del pos_embed original en **cada** `extract()` para evitar acumulación

    extract(img) -> (embedding_numpy, (h_tokens, w_tokens))
      - pool="none" -> (HW, C)  (todos los tokens)  [recomendado para PatchCore/coreset]
      - pool="mean" -> (1,  C)  (media de tokens)
    """

    def __init__(
        self,
        model_name: str = "vit_small_patch14_dinov2.lvd142m",
        input_size: int = 1036,                 # múltiplo de 14 (por ser ViT/14)
        out_indices: Optional[Iterable[int]] = (9, 10, 11),
        device: Optional[Union[str, torch.device]] = None,   # "auto", "cpu", "cuda[:0]", "mps"
        half: bool = False,
        imagenet_norm: bool = True,
        pool: str = "none",                     # "none" | "mean"
        dynamic_input: bool = False,            # False => fuerza tamaño fijo; True => acepta HxW múltiplos de patch
        patch_size: Optional[int] = None,       # si se pasa, fuerza el valor de patch
        **_,
    ) -> None:
        self.model_name = model_name
        self.input_size = int(input_size)
        self.out_indices = list(out_indices) if out_indices else []
        self.dynamic_input = bool(dynamic_input)

        # --- resolver device (soporta "auto") ---
        if device is None:
            if torch.cuda.is_available():
                self.device = torch.device("cuda")
            elif getattr(torch.backends, "mps", None) and torch.backends.mps.is_available():
                self.device = torch.device("mps")
            else:
                self.device = torch.device("cpu")
        elif isinstance(device, str):
            dv = device.strip().lower()
            if dv in ("auto", ""):
                if torch.cuda.is_available():
                    self.device = torch.device("cuda")
                elif getattr(torch.backends, "mps", None) and torch.backends.mps.is_available():
                    self.device = torch.device("mps")
                else:
                    self.device = torch.device("cpu")
            else:
                self.device = torch.device(device)
        else:
            self.device = torch.device(device)

        # 'half' se interpreta como "habilitar AMP/autocast en CUDA" (no convertir pesos a FP16 permanente)
        self.use_amp = bool(half) and self.device.type == "cuda"
        self.half = self.use_amp
        self.imagenet_norm = bool(imagenet_norm)

        # validar pool
        pool = (pool or "none").lower()
        if pool not in ("none", "mean"):
            raise ValueError("pool debe ser 'none' o 'mean'")
        self.pool = pool

        # --- modelo ---
        self.model: nn.Module = timm.create_model(self.model_name, pretrained=True)
        self.model.eval().to(self.device)

        # patch size
        pe = getattr(self.model, "patch_embed", None)
        if patch_size is not None:
            self.patch = int(patch_size)
        else:
            ps = getattr(pe, "patch_size", 14)
            self.patch = int(ps[0]) if isinstance(ps, (tuple, list)) else int(ps)

        # normalización tipo ImageNet
        if self.imagenet_norm:
            self.mean = torch.tensor(
                [0.485, 0.456, 0.406],
                device=self.device,
                dtype=torch.float32,
            ).view(1, 3, 1, 1)
            self.std = torch.tensor(
                [0.229, 0.224, 0.225],
                device=self.device,
                dtype=torch.float32,
            ).view(1, 3, 1, 1)
        else:
            self.mean = torch.zeros((1, 3, 1, 1), device=self.device)
            self.std  = torch.ones((1, 3, 1, 1), device=self.device)

        # Guardar pos_embed "de fábrica" para poder resetearlo en cada extract()
        pe2 = getattr(self.model, "pos_embed", None)
        if isinstance(pe2, torch.Tensor):
            pe2_tensor = cast(torch.Tensor, pe2)
            self._pos_embed_base = pe2_tensor.detach().clone()
        else:
            self._pos_embed_base = None
        # Lock to make extract() thread-safe inside one uvicorn worker
        self._lock = threading.RLock()


    # ---------------- imagen / preprocesado ----------------
    @staticmethod
    def _to_pil(img) -> Image.Image:
        if isinstance(img, Image.Image):
            return img.convert("RGB")
        if isinstance(img, (bytes, io.BytesIO)):
            return Image.open(img).convert("RGB")
        if isinstance(img, str):
            return Image.open(img).convert("RGB")
        if isinstance(img, np.ndarray):
            arr = img
            if arr.ndim == 2:
                arr = np.stack([arr] * 3, axis=-1)
            if arr.shape[-1] == 3:
                # BGR (OpenCV) -> RGB
                arr = arr[..., ::-1].copy()
            return Image.fromarray(arr.astype(np.uint8), mode="RGB")
        raise TypeError(f"Tipo de imagen no soportado: {type(img)}")

    def _preprocess(self, img) -> torch.Tensor:
        """
        Preprocesa aplicando LETTERBOX (mantener aspecto + padding) a input_size x input_size
        si dynamic_input=False. Si dynamic_input=True se hace letterbox igualmente aquí para
        garantizar cuadrado; luego _prepare_input_size decidirá si mantener HxW.
        """
        pil = self._to_pil(img)

        # --- LETTERBOX (mantener aspecto + padding a cuadrado) ---
        if self.input_size and self.input_size > 0:
            target = int(self.input_size)
            w, h = pil.size
            scale = min(target / w, target / h)
            nw, nh = int(round(w * scale)), int(round(h * scale))
            pil_resized = pil.resize((nw, nh), _BICUBIC)
            canvas = Image.new("RGB", (target, target), (0, 0, 0))
            left = (target - nw) // 2
            top = (target - nh) // 2
            canvas.paste(pil_resized, (left, top))
            pil = canvas  # ahora es exactamente target x target

        arr = (np.asarray(pil).astype(np.float32) / 255.0)
        x = torch.from_numpy(arr).permute(2, 0, 1).unsqueeze(0).to(self.device)
        # Mantener preprocesado siempre en FP32 (más estable).
        x = x.float()
        x = (x - self.mean) / self.std
        return x

    # ---------------- tamaño de entrada ----------------
    def _prepare_input_size(self, model: nn.Module, x: torch.Tensor):
        """
        - dynamic_input=False -> fuerza siempre (input_size, input_size)
        - dynamic_input=True  -> acepta HxW actuales si son múltiplos de self.patch;
                                 si no, redimensiona a input_size.
        En ambos casos, sincroniza patch_embed/img_size del ViT y adapta pos_embed.
        """
        target_h = int(self.input_size)
        target_w = int(self.input_size)

        H, W = x.shape[-2:]
        if self.dynamic_input:
            # Aceptar tamaño actual solo si cuadra con el patch
            if (H % self.patch == 0) and (W % self.patch == 0):
                pass  # mantener H, W
            else:
                x = F.interpolate(x, size=(target_h, target_w), mode="bilinear", align_corners=False)
                H, W = target_h, target_w
        else:
            if (H, W) != (target_h, target_w):
                x = F.interpolate(x, size=(target_h, target_w), mode="bilinear", align_corners=False)
                H, W = target_h, target_w

        # Sincronizar ViT/timm
        pe = getattr(model, "patch_embed", None)
        if pe is not None:
            if hasattr(pe, "set_input_size"):
                pe.set_input_size((H, W))
            if hasattr(pe, "img_size"):
                pe.img_size = (H, W)
            # Mantener grid_size coherente si existe (evita reshape 37x37 de timm)
            if hasattr(pe, "grid_size"):
                pe.grid_size = (H // self.patch, W // self.patch)
        if hasattr(model, "img_size"):
            model.img_size = (H, W)
        if hasattr(model, "num_patches"):
            model.num_patches = (H // self.patch) * (W // self.patch)

        # Reset + resize manual del pos_embed al grid actual
        self._reset_and_resize_pos_embed(H // self.patch, W // self.patch)

        return x, ("dynamic" if self.dynamic_input else "resize")

    # ---- reset + adaptar pos_embed al grid actual (sin timm.resize_pos_embed) ----
    def _reset_and_resize_pos_embed(self, h_tokens: int, w_tokens: int):
        if self._pos_embed_base is None:
            return
        with torch.no_grad():
            # Reset al "de fábrica"
            base = self._pos_embed_base.to(self.model.pos_embed.device, dtype=self.model.pos_embed.dtype)
            self.model.pos_embed = nn.Parameter(base.clone(), requires_grad=False)

            pos = self.model.pos_embed  # (1, 1+HW, C)
            if not isinstance(pos, torch.Tensor):
                return
            cls, grid = pos[:, :1], pos[:, 1:]                   # (1,1,C) y (1,HW,C)

            # grid actual del pos_embed (g_old x g_old)
            g_old = int(grid.shape[1] ** 0.5)
            grid = grid[:, : g_old * g_old, :]                   # seguridad
            B, N, C = grid.shape

            # (1, HW, C) -> (1, C, g_old, g_old)
            grid_2d = grid.reshape(B, g_old, g_old, C).permute(0, 3, 1, 2).contiguous()

            # Interpolar en 2D al nuevo tamaño
            new_2d = F.interpolate(grid_2d, size=(h_tokens, w_tokens), mode='bicubic', align_corners=False)

            # Volver a (1, HW, C)
            new_grid = new_2d.permute(0, 2, 3, 1).reshape(B, h_tokens * w_tokens, C).contiguous()

            # Concatenar CLS
            new_pos = torch.cat([cls, new_grid], dim=1)
            self.model.pos_embed = nn.Parameter(new_pos, requires_grad=False)

    # ---------------- compat capas intermedias ----------------
    def _call_get_intermediate_layers_compat(
        self, model: nn.Module, x: torch.Tensor, out_indices: Iterable[int], want_cls: bool, want_reshape: bool
    ):
        fn = getattr(model, "get_intermediate_layers", None)
        if fn is None:
            return None

        sig = inspect.signature(fn)
        kwargs = {}
        if "return_class_token" in sig.parameters:
            kwargs["return_class_token"] = want_cls
        elif "return_cls_token" in sig.parameters:
            kwargs["return_cls_token"] = want_cls
        if "reshape" in sig.parameters:
            kwargs["reshape"] = want_reshape

        out_indices = list(out_indices)
        try:
            return fn(x, out_indices, **kwargs)
        except TypeError:
            pass
        try:
            n = len(out_indices) if out_indices else 1
            return fn(x, n, **kwargs)
        except TypeError:
            pass
        try:
            return fn(x, out_indices)
        except TypeError:
            n = len(out_indices) if out_indices else 1
            return fn(x, n)

    # ---------------- tokens ----------------
    def _forward_tokens(
        self,
        x: torch.Tensor,
        *,
        use_intermediate: bool | None = None,   # None => usa intermedias si self.out_indices está definido
        want_reshape: bool = False,             # << clave: evitar reshape interno de timm (37x37)
        remove_cls: bool = True,                # quitar CLS si viene
        combine: str = "concat",                # "concat" | "mean" | "stack"
    ) -> torch.Tensor:
        """
        Devuelve tokens como (N, C_out) con N = Htok*Wtok.
        """
        def _expected_grid(batched_x: torch.Tensor) -> tuple[int, int, int]:
            H, W = batched_x.shape[-2:]
            htok, wtok = H // self.patch, W // self.patch
            return htok, wtok, htok * wtok

        def _as_BxNC(t: torch.Tensor, expected_N: int) -> torch.Tensor:
            # Convierte (B,H,W,C)->(B,HW,C), quita CLS opcional y valida N
            if t.ndim == 4:  # (B,H,W,C)
                b, h, w, c = t.shape
                t = t.reshape(b, h * w, c)
            elif t.ndim != 3:  # (B,N,C) esperado
                raise RuntimeError(f"Forma inesperada en intermedia/feature: {t.shape}")

            # quitar CLS si procede
            if remove_cls and t.shape[1] == expected_N + 1:
                t = t[:, 1:, :]

            if t.shape[1] != expected_N:
                raise RuntimeError(
                    f"TokenShape mismatch: N={t.shape[1]} esperado={expected_N} "
                    f"(patch={self.patch})"
                )
            return t

        # 1) Asegurar tamaño de entrada (fijo / sincronizado) y adaptar pos_embed
        x, _ = self._prepare_input_size(self.model, x)
        htok, wtok, expected_N = _expected_grid(x)

        amp_ctx = (
            torch.autocast(device_type="cuda", dtype=torch.float16)
            if self.use_amp and self.device.type == "cuda"
            else nullcontext()
        )

        with amp_ctx:
            # 2) ¿Usamos intermedias?
            if use_intermediate is None:
                use_intermediate = bool(self.out_indices) and hasattr(self.model, "get_intermediate_layers")

            if use_intermediate:
                try:
                    layers = self._call_get_intermediate_layers_compat(
                        self.model,
                        x,
                        self.out_indices,
                        want_cls=False,
                        want_reshape=want_reshape,
                    )
                    if layers is not None:
                        # Normalizar todas a (B,N,C)
                        normed: list[torch.Tensor] = []
                        for li, layer in enumerate(layers):
                            if isinstance(layer, (tuple, list)) and len(layer) > 0 and torch.is_tensor(layer[0]):
                                layer = layer[0]
                            if not torch.is_tensor(layer):
                                raise RuntimeError(f"Tipo inesperado en capa {li}: {type(layer)}")
                            normed.append(_as_BxNC(layer, expected_N))

                        # Combinar capas
                        if combine == "concat":
                            out = torch.cat(normed, dim=-1)       # (B,N, sumC)
                        elif combine == "mean":
                            stk = torch.stack(normed, dim=0)      # (L,B,N,C)
                            out = stk.mean(dim=0)                 # (B,N,C)
                        elif combine == "stack":
                            stk = torch.stack(normed, dim=-1)     # (B,N,C,L)
                            b, n, c, l = stk.shape
                            out = stk.reshape(b, n, c * l)        # (B,N,C*L)
                        else:
                            raise ValueError("combine debe ser 'concat', 'mean' o 'stack'")

                        return out[0]  # (N, C_out)
                except Exception as ex:
                    # Fallback limpio a forward_features
                    log.debug("[features] fallback intermedias -> forward_features: %s", ex)

            # 3) forward_features (fallback o seleccionado)
            feats_any = self.model.forward_features(x)
            if isinstance(feats_any, dict):
                feats = cast(dict[str, Any], feats_any)
                tokens_any: Any = None
                # IMPORTANT: do NOT use `or` on torch.Tensor.
                # `bool(tensor)` raises: RuntimeError: Boolean value of Tensor with more than one value is ambiguous
                for k in ("x_norm_patchtokens", "x", "tokens"):
                    v = feats.get(k)
                    if torch.is_tensor(v):
                        tokens_any = v
                        break
                if tokens_any is None:
                    cands = [v for v in feats.values() if isinstance(v, torch.Tensor)]
                    if not cands:
                        raise RuntimeError("forward_features devolvió un dict sin tensores utilizables")
                    tokens_any = max(cands, key=lambda u: u.numel())
                t = cast(torch.Tensor, tokens_any)
            else:
                t = cast(torch.Tensor, feats_any)

        if t.ndim == 3:          # (B,N,C) posiblemente con CLS
            t = _as_BxNC(t, expected_N)
            return t[0]          # (N,C)
        elif t.ndim == 4:        # (B,C,H,W)
            b, c, h, w = t.shape
            return t.permute(0, 2, 3, 1).reshape(b, h * w, c)[0]  # (N,C)
        else:
            raise RuntimeError(f"Forma inesperada de features: {t.shape}")

    # ---------------- utilidades públicas ----------------
    def get_metadata(self) -> dict:
        return {
            "model_name": self.model_name,
            "device": str(self.device),
            "use_amp": bool(self.use_amp),
            "half": bool(self.use_amp),
            "imagenet_norm": bool(self.imagenet_norm),
            "patch_size": int(self.patch),
            "input_size": int(self.input_size),
            "dynamic_input": bool(self.dynamic_input),
            "out_indices": list(self.out_indices) if self.out_indices else [],
            "pool": self.pool,
        }

    def assert_token_shape(self, expected: Tuple[int, int], got: Tuple[int, int], ctx: str = ""):
        if expected and tuple(expected) != tuple(got):
            raise RuntimeError(
                f"TokenShape mismatch{(' @' + ctx) if ctx else ''}: {got} != {expected}. "
                f"Comprueba input_size/dynamic_input y reentrena si cambió."
            )

    # ---------------- API pública ----------------
    @torch.inference_mode()
    def extract(self, img):
        with self._lock:
            x = self._preprocess(img)
            x, how = self._prepare_input_size(self.model, x)
            model_param = next(self.model.parameters(), None)
            model_dtype = model_param.dtype if model_param is not None else None
            log.info(
                "[features] dtypes: x=%s model=%s use_amp=%s",
                x.dtype,
                model_dtype,
                self.use_amp,
            )
            H, W = x.shape[-2:]
            h_tokens, w_tokens = H // self.patch, W // self.patch

            # Debug útil
            pe_n = getattr(self.model, "pos_embed", None)
            pe_count = -1
            if isinstance(pe_n, torch.Tensor):
                pe_n_tensor = cast(torch.Tensor, pe_n)
                pe_count = int(pe_n_tensor.shape[1])
            log.debug(
                "[features] after-prep: %sx%s (%s), patch=%s, grid=%sx%s, tokens(N+CLS)=%s, pos_embed_N=%s",
                H,
                W,
                how,
                self.patch,
                h_tokens,
                w_tokens,
                h_tokens * w_tokens + 1,
                pe_count,
            )

            tokens = self._forward_tokens(x)  # (N, C)

            if self.pool == "mean":
                tokens = tokens.mean(dim=0, keepdim=True)  # (1, C)

            emb_np = tokens.float().detach().cpu().numpy()
            return emb_np, (int(h_tokens), int(w_tokens))
