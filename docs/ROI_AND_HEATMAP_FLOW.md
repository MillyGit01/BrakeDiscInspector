# ROI and heatmap workflow

This document focuses on ROI identifiers, canonical ROI export, and heatmap visualization for manual and batch flows.

## ROI identifiers and terminology
- **role_id**: logical role (`Master1`, `Master2`, `Inspection`, etc.) sent to the backend.
- **roi_id**: logical ROI identifier, often `inspection-<n>` for inspection slots.
- **model_key**: backend model slot, defaults to `roi_id` if not provided.

The GUI uses inspection defaults like `inspection-1..4` (see `InspectionRoiConfig.ModelKey`). Local dataset folders (legacy) map these to `Inspection_1..4` under `<exe>/Recipes/<LayoutName>/Dataset/`.

## Canonical ROI export
1. The GUI exports the ROI **after rotation**, producing a canonical crop (PNG) and a `shape` JSON mask.
2. The `shape` JSON is expressed in **canonical ROI coordinates** (i.e., the crop’s pixel space).
3. The backend treats the image and `shape` as authoritative and **does not** crop or rotate the source image.

This ensures heatmaps align with the GUI overlay when rendered back in the same canonical space.

## Shape JSON conventions
- Rectangle:
  ```json
  {"kind":"rect","x":0,"y":0,"w":W,"h":H}
  ```
- Circle:
  ```json
  {"kind":"circle","cx":CX,"cy":CY,"r":R}
  ```
- Annulus:
  ```json
  {"kind":"annulus","cx":CX,"cy":CY,"r":R,"r_inner":R_INNER}
  ```

**Important:** the coordinates must match the crop size that was sent to `/infer`.

## Master anchors and inspection alignment (batch)
- Master 1/2 anchors are used to reposition inspection ROIs per batch image.
- The GUI applies translation/rotation/scale based on the anchor vector and each ROI’s selected anchor (Master1/Master2/Mid).
- If anchors are missing or invalid, the GUI leaves ROIs at their saved positions.

## Heatmap visualization (spec)
> This section is a UI spec. If the current implementation deviates, treat it as TODO.

- Show **red** heatmap areas **only** when the final decision is **NG** and the heatmap is the NG cause.
- If the final decision is **OK**, do **not** render a heatmap overlay.
- Apply the same rule in **manual** and **batch** views.

## Batch known issue (ROI2)
**Observed issue:** ROI2 heatmap may not appear after the first batch image.

**Plan:**
1. Verify placement logs (`gui.log` / `gui_heatmap.log`).
2. Ensure ROI2 placement does not reuse ROI1 geometry.
3. Add a regression checklist once fixed.

## Related docs
- `docs/FRONTEND.md` — UI controls and toggle behavior.
- `docs/API_CONTRACTS.md` — backend `shape` schema and contracts.
- `LOGGING.md` — log locations and fields.
