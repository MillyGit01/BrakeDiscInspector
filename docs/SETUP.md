# Setup — Octubre 2025

## 1. Requisitos
- Windows 10/11 con .NET 6/7 (GUI).
- Python 3.11/3.12 (backend).
- GPU NVIDIA opcional (CUDA 12.x).
- Git, Visual Studio 2022, PowerShell/Bash.

## 2. Preparar backend
```bash
cd backend
python -m venv .venv
source .venv/bin/activate  # Windows: .venv\Scripts\activate
pip install -r requirements.txt
uvicorn backend.app:app --reload
```
- Variables: `BACKEND_DEVICE=cuda:0`, `BACKEND_DATA_ROOT=../datasets`.

## 3. Preparar GUI
1. Abrir `gui/BrakeDiscInspector_GUI_ROI.sln` en Visual Studio.
2. Restaurar paquetes NuGet.
3. Configurar `appsettings.json`/`settings.json` si aplica (backend URL, idioma).
4. Ejecutar en modo depuración.

## 4. Datasets demo
- `datasets_demo/` (si disponible) contiene ROI canónicas con manifests.
- Copiar a `datasets/` y ajustar `role_id`/`roi_id`.

## 5. Pruebas rápidas
1. Iniciar backend (`uvicorn`).
2. Desde GUI: cargar imagen demo, dibujar ROI, `Add to OK` (x5).
3. Ejecutar `Train memory fit` → comprobar respuesta.
4. Ejecutar `Calibrate threshold` → threshold > 0.
5. Ejecutar `Evaluate` → revisar heatmap.

## 6. Dependencias opcionales
- `cuda-toolkit` para GPU.
- `faiss-gpu` (incluido en requirements si GPU detectada).
- `jq`, `curl` para scripts.

## 7. Troubleshooting
- Backend no arranca → revisar versión Python.
- GUI no compila → reinstalar workload Desktop en Visual Studio.
- Sin GPU → configurar `BACKEND_DEVICE=cpu` y ajustar tiempo de inferencia.

## 8. Referencias
- `README.md`, `docs/GUI.md`, `docs/BACKEND.md`.
- `docs/curl_examples.md` para pruebas manuales.
