# Contributing guide

Thank you for helping improve BrakeDiscInspector. Follow these rules to stay aligned with the current codebase and the constraints listed in `agents.md`.

## 1. Before you start
- Read `docs/INDEX.md` for the documentation map.
- Use `LOGGING.md` as the source of truth for logs.
- Ensure you can run the backend locally and build the GUI.

## 2. Branching and coding standards
- Create topic branches from `main` (`git checkout -b feature/<name>`).
- GUI: keep network calls async (`async/await`), do not touch adorners or ROI transforms without approval, and reuse the canonical ROI export pipeline.
- Backend: keep endpoint names and payloads stable.
- Documentation: update or add `.md` files for workflow changes and mark unverified statements as **TODO**.

## 3. Pull requests
Every PR must include:
1. A description of changes and affected ROI slots/endpoints.
2. Evidence of testing (backend `pytest`, GUI screenshots or log excerpts for manual/batch flows).
3. Documentation updates (especially `docs/API_CONTRACTS.md`, `docs/BACKEND.md`, `docs/FRONTEND.md`).

## 4. Tests
- Backend: run `pytest` if you change backend logic.
- GUI: manual checklist (load image, edit ROI, run `fit_ok`, `calibrate`, `infer`, batch folder).

## 5. Contracts
- Do not rename `/health`, `/fit_ok`, `/calibrate_ng`, `/infer` or their fields without updating `docs/API_CONTRACTS.md` and the GUI client.
- The backend is the source of truth for datasets and models.

## 6. Communication
- Report issues with steps to reproduce and relevant log excerpts.
- For ROI geometry questions, consult `docs/ROI_AND_HEATMAP_FLOW.md` and `agents.md`.
