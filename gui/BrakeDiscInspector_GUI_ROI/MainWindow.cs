using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using BrakeDiscInspector_GUI_ROI.Util;


namespace BrakeDiscInspector_GUI_ROI
{
    public partial class MainWindow
    {
        public async Task AnalyzeMastersViaBackend()
        {
            if (_layout?.Master1Pattern == null || _layout?.Master2Pattern == null)
            { Snack($"Faltan ROIs de patrón para Master 1/2"); return; } // CODEX: FormattableString compatibility.
            if (string.IsNullOrWhiteSpace(_currentImagePathWin) || !File.Exists(_currentImagePathWin))
            { Snack($"No hay imagen cargada"); return; } // CODEX: FormattableString compatibility.

            if (ViewModel != null)
            {
                VisConfLog.AnalyzeMaster(FormattableStringFactory.Create(
                    "[VISCONF][ANALYZE_MASTER][LOCAL] image='{0}'",
                    _currentImagePathWin));
                await AnalyzeMastersAsync(showFailureDialog: true);
            }

            await Dispatcher.InvokeAsync(() => RedrawOverlay(), DispatcherPriority.Render);
        }


        private (System.Windows.Point c1Canvas, System.Windows.Point c2Canvas, System.Windows.Point midCanvas) ConvertMasterPointsToCanvas(
            System.Windows.Point c1,
            System.Windows.Point c2,
            System.Windows.Point mid)
        {
            var c1Canvas = ImagePxToCanvasPt(c1.X, c1.Y);
            var c2Canvas = ImagePxToCanvasPt(c2.X, c2.Y);
            var midCanvas = ImagePxToCanvasPt(mid.X, mid.Y);

            return (c1Canvas, c2Canvas, midCanvas);
        }


        public async Task AnalyzeInspectionViaBackend()
        {
            if (_layout?.Inspection == null) { Snack($"Falta ROI de Inspección"); return; } // CODEX: FormattableString compatibility.
            if (string.IsNullOrWhiteSpace(_currentImagePathWin) || !File.Exists(_currentImagePathWin))
            { Snack($"No hay imagen cargada"); return; } // CODEX: FormattableString compatibility.

            var resp = await BackendAPI.InferAsync(_currentImagePathWin, _layout.Inspection, _preset, AppendLog);
            if (!resp.ok || resp.result == null)
            {
                Snack($"Analyze backend: {resp.error ?? "error desconocido"}"); // CODEX: FormattableString compatibility.
                return;
            }

            var result = resp.result;
            bool pass = !result.threshold.HasValue || result.score <= result.threshold.Value;
            string thrText = result.threshold.HasValue ? result.threshold.Value.ToString("0.###") : "n/a";
            AppendLog($"[infer] Inspection score={result.score:0.###} thr={thrText} regions={(result.regions?.Length ?? 0)} status={(pass ? "OK" : "NG")}");
            string msg = pass
                ? $"Resultado OK (score={result.score:0.###} / thr={thrText})"
                : $"Resultado NG (score={result.score:0.###} / thr={thrText})";
            Snack($"{msg}"); // CODEX: FormattableString compatibility.
        }
    }
}
