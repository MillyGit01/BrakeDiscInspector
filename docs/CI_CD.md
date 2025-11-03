# CI/CD — Octubre 2025

## 1. Objetivo
Garantizar calidad y despliegues consistentes tanto del backend como de la GUI.

## 2. Pipelines
- **GitHub Actions** (default):
  - Job `backend-tests`: instala deps, ejecuta `pytest`.
  - Job `lint` (opcional): `ruff` + `black --check`.
  - Artefactos: reports `pytest.xml`.
- **Build GUI**: pipeline separado (Azure DevOps/TeamCity) que compila binarios WPF y firma ejecutables.

## 3. Requisitos de PR
- Tests backend verdes.
- Documentación actualizada (`README.md`, etc.).
- Capturas si hay cambios visuales.

## 4. Deploy backend
- Git tag → build Docker (`docker/`).
- Publicación en registry privado (`brakedisc/backends`).
- Rollout mediante `docker compose pull && up -d`.

## 5. Deploy GUI
- Build MSIX/installer.
- Distribución vía servidor interno o Intune.
- Validar handshake `/health` tras actualización.

## 6. Observabilidad
- Logs agregados en ELK/Splunk.
- Métricas Prometheus + Grafana.
- Alertas en Slack/Teams cuando `score > threshold` consecutivo.

## 7. Checklist release
- [ ] Documentación actualizada (este repo).
- [ ] Changelog preparado.
- [ ] Binarios GUI firmados.
- [ ] Imagen Docker probada (`/health`, `/infer`).
- [ ] Backup datasets/models antes de despliegue.
