from __future__ import annotations

import json
import logging
import os
import threading
import time
from contextvars import ContextVar
from pathlib import Path
from typing import Any

REQUEST_ID_CTX: ContextVar[str | None] = ContextVar("request_id", default=None)

_DIAG_LOGGER: logging.Logger | None = None
_DIAG_LOG_PATH: Path | None = None


def init_diagnostics_logger(log_dir: Path, *, filename: str = "backend_diagnostics.jsonl") -> Path | None:
    global _DIAG_LOGGER, _DIAG_LOG_PATH
    try:
        log_dir.mkdir(parents=True, exist_ok=True)
        log_path = log_dir / filename
        handler = logging.FileHandler(log_path, encoding="utf-8")
        handler.setFormatter(logging.Formatter("%(message)s"))
        logger = logging.getLogger("diagnostics")
        logger.setLevel(logging.INFO)
        if logger.handlers:
            logger.handlers.clear()
        logger.addHandler(handler)
        logger.propagate = False
        _DIAG_LOGGER = logger
        _DIAG_LOG_PATH = log_path
        return log_dir
    except Exception:
        _DIAG_LOGGER = None
        _DIAG_LOG_PATH = None
        return None


def diagnostics_log_path() -> Path | None:
    return _DIAG_LOG_PATH


def bind_request_id(request_id: str) -> Any:
    return REQUEST_ID_CTX.set(request_id)


def reset_request_id(token: Any) -> None:
    REQUEST_ID_CTX.reset(token)


def diag_event(event: str, **fields: Any) -> None:
    if _DIAG_LOGGER is None:
        return
    payload = {
        "ts": time.time(),
        "event": event,
        "pid": os.getpid(),
        "thread": threading.current_thread().name,
    }
    request_id = REQUEST_ID_CTX.get()
    if request_id and "request_id" not in fields:
        payload["request_id"] = request_id
    payload.update(fields)
    try:
        _DIAG_LOGGER.info(json.dumps(payload, ensure_ascii=False))
    except Exception:
        return
