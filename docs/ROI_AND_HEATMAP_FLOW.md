# ROI and Heatmap Workflow

This document focuses on ROI identifiers, canonical ROI export, edit locking, master alignment, and heatmap visualization for manual and batch flows.

## ROI Identifiers and Terminology
- **role_id:** logical role (`Master1`, `Master2`, `Inspection`, etc.) sent to the backend.
- **roi_id:** logical ROI identifier, often `inspection-<n>` for inspection slots.
- **model_key:** backend model slot, defaults to `roi_id` if not provided.

The GUI uses inspection defaults like `inspection-1..4` (see `InspectionRoiConfig.ModelKey`). Local dataset folders (legacy) map these to `Inspection_1..4` under `<exe>/Recipes/<LayoutName>/Dataset/`.

## Canonical ROI Export
1. The GUI exports the ROI after rotation, producing a canonical crop (PNG) and a `shape` JSON mask.
2. The `shape` JSON is expressed in canonical ROI coordinates, i.e. the crop pixel space.
3. The backend treats the image and `shape` as authoritative and does not crop or rotate the source image.

This keeps backend scores, heatmaps, and GUI overlays aligned in the same canonical space.

## Shape JSON Conventions
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

The coordinates must match the crop size sent to `/infer`.

## Master Anchors and Inspection Alignment
- Master 1/2 anchors are used to reposition inspection ROIs per image.
- The GUI applies translation/rotation/scale based on the anchor vector and each ROI's selected anchor (Master1/Master2/Mid).
- If anchors are missing or invalid, the GUI leaves ROIs at their saved positions.
- Master pattern matching caches rotated/scaled template variants to reduce repeated template generation during batch runs.

## Inspection ROI Dataset Lock
- Inspection ROI geometry can be edited before a dataset exists.
- Once the backend/local dataset for an inspection ROI has samples, that inspection ROI is locked against edit, move, resize, shape change, delete/recreate, and canvas clear operations.
- Additional OK/NG images can still be added to the existing dataset.
- Master ROIs remain editable because they only drive alignment.
- The warning shown to the operator is: `Inspection ROIs can't be modified once Dataset is created`.

## Heatmap Visualization
- Show red heatmap areas only when the final decision is NG and the heatmap is the NG cause.
- If the final decision is OK, do not render a heatmap overlay.
- Apply the same rule in manual and batch views.
- Batch heatmaps are stored per ROI index, and the UI selection chooses which ROI heatmap is shown.
- Heatmap scale/opacity controls are hidden until an image is loaded.

## Batch Heatmap Regression Checklist
If a ROI heatmap is missing or assigned to the wrong ROI:
1. Verify placement logs (`gui.log` / `gui_heatmap.log`).
2. Confirm the ROI index stored by the batch result.
3. Confirm the UI selected ROI index matches the displayed heatmap.
4. Check for placement guard messages that could suppress overlay placement.

## Related Docs
- `docs/FRONTEND.md` - UI controls and toggle behavior.
- `docs/API_CONTRACTS.md` - backend `shape` schema and contracts.
- `LOGGING.md` - log locations and fields.
