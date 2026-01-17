# Logging guide (source of truth)

This document describes **where logs are written** and **what they contain** in the current codebase.

## GUI logs (WPF)
**Directory:** `%LOCALAPPDATA%\BrakeDiscInspector\logs\` (created on startup). The GUI clears and recreates core log files at startup. (See `gui/BrakeDiscInspector_GUI_ROI/App.xaml.cs`.)

**Files created by the GUI:**
- `gui.log` — main GUI log (`GuiLog.Info/Warn/Error`).
- `gui_heatmap.log` — heatmap overlay placement/debug.
- `roi_load_coords.log` — ROI load/save diagnostics.
- `roi_analyze_master.log` — master detection diagnostics.
- `gui_setup.log` — GUI setup persistence (`GuiSetupSettingsService`).

**Format:**
```
YYYY-MM-DD HH:mm:ss.fff [LEVEL] message
```
Example:
```
2025-01-05 12:34:56.789 [INFO] [infer] ROI=... score=... threshold=...
```

**Notes:**
- GUI logs are **plain text** and do not include a `request_id` by default.
- If a file disappears during the session, the GUI will recreate it on the next write.

## Backend diagnostics logs (FastAPI)
**Format:** JSON Lines (`.jsonl`) written by `backend/diagnostics.py` via `diag_event(...)`.

**Default filename:** `backend_diagnostics.jsonl`.

**Directory resolution (in order):**
1. `BDI_GUI_LOG_DIR` (if set).
2. `%LOCALAPPDATA%\BrakeDiscInspector\logs\` when running on Windows.
3. On WSL, the backend attempts to translate Windows `%LOCALAPPDATA%` to `/mnt/<drive>/...`.
4. Fallback: `backend/logs/` inside the repository.

If none of the above paths are writable, diagnostics logging is disabled and a warning is emitted to the backend logger.

**Common fields:**
- `ts` (epoch seconds)
- `event` (e.g., `startup`, `http`, `fit_ok.request`, `infer.response`)
- `request_id` (if present in request context)
- `recipe_id`, `role_id`, `roi_id`, `model_key` (when relevant)

**Example line:**
```json
{"ts": 1720000000.123, "event": "infer.response", "request_id": "...", "recipe_id": "default", "score": 0.42, "threshold": 0.9, "elapsed_ms": 123}
```

## Correlation tips
- Backend responses always include `request_id` and `recipe_id` in the JSON body.
- The backend also sets the `X-Request-Id` response header for correlation.
- Use time proximity between GUI log timestamps and backend JSONL entries when no request id is available in the GUI logs.

## Related docs
- `docs/BACKEND.md` — backend configuration and storage details.
- `docs/TROUBLESHOOTING.md` — common failure modes and log pointers.
