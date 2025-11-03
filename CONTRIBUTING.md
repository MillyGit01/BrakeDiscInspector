# Guía de contribución — Octubre 2025

Gracias por interesarte en BrakeDiscInspector. Este documento establece el proceso para colaborar garantizando coherencia con el contrato frontend ↔ backend.

## 1. Código de conducta
- Respeta el flujo de trabajo del equipo y las restricciones de `agents.md`.
- Mantén comunicación clara en issues/PRs.

## 2. Antes de empezar
- Lee `README.md`, `ARCHITECTURE.md`, `API_REFERENCE.md` y `docs/GUI.md`.
- Configura tu entorno siguiendo `docs/SETUP.md`.

## 3. Reportar issues
- Incluye: pasos para reproducir, logs relevantes (`logs/gui`, backend stdout), capturas si aplica.
- Etiqueta: `bug`, `enhancement`, `question`.

## 4. Flujo de Pull Request
1. Crea rama desde `main` (`git checkout -b feature/nombre`).
2. Realiza cambios siguiendo estándares (C#, Python, docs).
3. Ejecuta tests backend (`pytest`) y revisa que la GUI funcione en modo demo.
4. Actualiza documentación (`*.md`) relacionada.
5. Redacta PR con:
   - Descripción clara del cambio.
   - Checklist de endpoints tocados.
   - Capturas/heatmaps si hay cambios visuales.
   - Resultados de tests.
6. Espera revisión (al menos 1 aprobación).

## 5. Estándares de código
- **GUI**: MVVM, `async/await`, no bloquear UI.
- **Backend**: PEP8, type hints, manejar excepciones con `HTTPException`.
- **Docs**: Markdown con tablas, ejemplos y referencias cruzadas.

## 6. Contrato estable
- No cambiar nombres de endpoints ni parámetros sin RFC previo.
- Mantener compatibilidad de manifests y datasets.
- Cualquier cambio en `shape` o ROI debe acompañarse de reentrenamiento y actualización documental.

## 7. Tests
- Backend: `pytest` + fixtures (datasets sintéticos en `backend/tests/data`).
- GUI: pruebas manuales guiadas (checklist en `docs/GUI.md`).
- QA integrado: scripts `docs/curl_examples.md`.

## 8. Lanzamientos
- Versionado semántico: `YYYY.MM.patch`.
- Tag + changelog (`CHANGELOG.md` pendiente) + binarios GUI firmados.

## 9. Contacto
- Para dudas críticas sobre adorners o pipeline ROI, consultar al responsable indicado en `agents.md`.
