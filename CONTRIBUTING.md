# Contributing guide

Thank you for helping improve BrakeDiscInspector. Follow these rules to stay aligned with the current codebase and the constraints listed in `agents.md`.

## 1. Before you start
- Read `README.md`, `docs/ARCHITECTURE.md`, `docs/FRONTEND.md`, `docs/BACKEND.md` and `docs/API_CONTRACTS.md` to understand the active workflows.
- Ensure you can run the backend locally (`uvicorn backend.app:app`) and build the GUI (Visual Studio 2022, .NET 8).

## 2. Branching and coding standards
- Create topic branches from `main` (`git checkout -b feature/<name>`).
- GUI: keep network calls async (`async/await`), do not touch adorners or ROI transforms without approval, and respect the canonical ROI export pipeline.
- Backend: follow PEP8 + type hints, keep endpoint names and payloads stable.
- Documentation: update the relevant `.md` file when you change a workflow.

## 3. Pull requests
Every PR must include:
1. A description of the change plus affected ROI slots/endpoints if applicable.
2. Evidence of testing (backend `pytest`, GUI screenshots or log excerpts for manual/batch flows).
3. Updates to docs/logging instructions when necessary (e.g. if you add a new dataset field).

## 4. Tests
- Backend: run `pytest` before opening a PR.
- GUI: manual checklist (load image, edit ROI, run `/fit_ok`, `/calibrate_ng`, `/infer`, batch folder). Capture `gui.log` snippets for regressions.

## 5. Contracts
- Do not rename `/health`, `/fit_ok`, `/calibrate_ng`, `/infer` or their fields without updating `docs/API_CONTRACTS.md` and the GUI client.
- Dataset folders must keep the `Inspection_<n>/ok|ng` structure unless `WorkflowViewModel` is updated accordingly.

## 6. Communication
- Report issues with steps to reproduce, relevant log excerpts (`gui.log`, backend stdout) and screenshots if heatmaps are involved.
- When in doubt about ROI geometry or backend payloads, consult the owner listed in `agents.md`.
