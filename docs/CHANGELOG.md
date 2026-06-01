# Changelog (selected)

## Implemented
- Windows GUI CI now restores, builds, and runs the WPF test project on `windows-latest`.
- GUI backend auto-start can launch the configured WSL backend environment and wait for `/health`.
- Siemens S7-1200 communication scaffolding, simulation PLC, and camera provider abstractions are available in the GUI. `Folder` is implemented for no-hardware testing; `FlirBlackfly` and `Cognex` are explicit placeholders until vendor SDK wiring is selected.
- The camera `Source` field is active only for the `Folder` provider and is selected through a folder browser.
- The canvas image workflow now uses an image-folder thumbnail strip under the canvas. Selecting a thumbnail loads that image; collection loading shows a wait message.
- Heatmap opacity/scale controls and Object Visibility HUD are shown only when an image is loaded, with translucent backgrounds for readability.
- The Object Visibility HUD uses modern vertical toggles and black/dark-gray ROI visibility styling.
- Dark/light theme handling and Setup GUI color configuration were updated so buttons, list boxes, and common controls remain readable.
- Setup GUI color fields can be selected from an in-app color palette and persisted as custom GUI settings.
- ROI rotation affordances are shown through a corner rotate symbol.
- OK/NG status is displayed as an integrated canvas badge, and snackbar text is larger for operator visibility.
- Edit mode now shows a yellow canvas frame and `Edit Mode` label while ROI editing is active.
- Loading an image with a layout triggers master analysis and enabled ROI evaluation; loading an image without a layout shows `Please load a layout`.
- Inspection ROI datasets refresh after layout load so dataset counts/previews are restored.
- Inspection ROIs are locked after a dataset exists; users can add more dataset images, but cannot edit, resize, move, delete, recreate, change shape, or clear those inspection ROIs. Master ROIs remain editable.
- Master pattern matching now caches rotated/scaled template variants to reduce repeated work during batch alignment.
- Recipe-aware backend storage under `BDI_MODELS_DIR/recipes/<recipe_id>/...` with legacy fallback.
- Reserved `recipe_id` `last` rejected with HTTP 400.
- Dataset-only training gate via `BDI_TRAIN_DATASET_ONLY` and minimum OK samples via `BDI_MIN_OK_SAMPLES`.
- Backend diagnostics written as JSONL (`backend_diagnostics.jsonl`) in a resolved log directory.

## Planned / Spec
- FLIR Blackfly and Cognex providers still need final vendor SDK/protocol integration before real hardware acquisition can replace the placeholders.
- Heatmap overlay policy remains: **only show red overlay when result is NG and heatmap is the cause of NG**.
