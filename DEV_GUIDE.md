# Guía de desarrollo — Octubre 2025

Este documento sintetiza las prácticas para colaborar en BrakeDiscInspector, alineadas con `agents.md`.

## 1. Prerrequisitos
- Windows 10/11 + Visual Studio 2022 (workload Desktop .NET) para GUI.
- Python 3.11/3.12 + Poetry o pip para backend.
- Git, PowerShell/Bash, acceso a GPU opcional.

## 2. Configuración inicial
1. Clonar repositorio.
2. Crear entorno virtual `python -m venv backend/.venv` y activar.
3. `pip install -r backend/requirements.txt`.
4. Abrir solución WPF (`gui/BrakeDiscInspector_GUI_ROI/BrakeDiscInspector_GUI_ROI.sln`).
5. Configurar variables `.env` (ver `docs/SETUP.md`).

## 3. Flujo de trabajo
- Trabajar en branches temáticos (`feature/`, `fix/`).
- Seguir convención de commits `tipo: mensaje` (`feat`, `fix`, `docs`, ...).
- Ejecutar tests backend (`pytest`) antes de PR.
- No modificar adorners ni contratos HTTP salvo acuerdo explícito.

## 4. Backend
- Ejecutar `uvicorn backend.app:app --reload` durante desarrollo.
- Tests unitarios en `backend/tests/`.
- Lint opcional con `ruff`.
- Configs en `backend/config.yaml` (device, coreset ratio).

## 5. GUI
- Patrón MVVM: actualizar `WorkflowViewModel`, `BackendClientService`.
- Llamadas HTTP asíncronas (`async Task`).
- No bloquear UI. Mostrar diálogos de error amigables.
- Mantener pipeline de ROI canónica (`TryGetRotatedCrop`).

## 6. Contrato frontend ↔ backend
- Referencia: `API_REFERENCE.md`.
- Toda llamada incluye `role_id`, `roi_id`, `mm_per_px` y `shape` (cuando aplica).
- Respuestas contienen `token_shape`, `heatmap_png_base64`, `regions[]`.
- Validar `model_version` en la GUI (mostrar advertencia si difiere).

## 7. Documentación
- Actualizar los `.md` relevantes cuando se cambien flujos.
- Usar tablas y ejemplos concretos.
- Añadir referencias cruzadas.

## 8. QA manual
- Scripts en `docs/curl_examples.md`.
- Checklist en `docs/PIPELINE_DETECCION.md`.
- Validar overlays y recalibraciones tras cambios.

## 9. Revisión de PR
- Adjuntar capturas si hay cambios en GUI (ver instrucciones en `docs/GUI.md`).
- Incluir logs relevantes (`logs/gui`, backend stdout).
- Verificar que CI pase.

## 10. Roadmap técnico
- Migrar a .NET 8 (en evaluación).
- Añadir caching incremental de heatmaps.
- Investigar scheduler multi-GPU.
