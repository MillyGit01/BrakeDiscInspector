# Changelog (selected)

## Implemented
- Recipe-aware backend storage under `BDI_MODELS_DIR/recipes/<recipe_id>/...` with legacy fallback.
- Reserved `recipe_id` `last` rejected with HTTP 400.
- Dataset-only training gate via `BDI_TRAIN_DATASET_ONLY` and minimum OK samples via `BDI_MIN_OK_SAMPLES`.
- Backend diagnostics written as JSONL (`backend_diagnostics.jsonl`) in a resolved log directory.

## Planned / Spec
- Heatmap overlay rule: **only show red overlay when result is NG and heatmap is the cause of NG**.
- OK/NG badge spec: square badge with bold white `OK`/`NG` on green/red background.
- Batch issue: ROI2 heatmap missing after the first image — investigate and add regression guard.
