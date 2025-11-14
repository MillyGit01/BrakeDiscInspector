using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;


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

            var inferM1 = await BackendAPI.InferAsync(_currentImagePathWin, _layout.Master1Pattern, _preset, AppendLog);
            if (!inferM1.ok || inferM1.result == null)
            {
                Snack($"Backend Master 1: {inferM1.error ?? "error desconocido"}"); // CODEX: FormattableString compatibility.
                return;
            }

            var inferM2 = await BackendAPI.InferAsync(_currentImagePathWin, _layout.Master2Pattern, _preset, AppendLog);
            if (!inferM2.ok || inferM2.result == null)
            {
                Snack($"Backend Master 2: {inferM2.error ?? "error desconocido"}"); // CODEX: FormattableString compatibility.
                return;
            }

            var res1 = inferM1.result;
            var res2 = inferM2.result;
            bool pass1 = !res1.threshold.HasValue || res1.score <= res1.threshold.Value;
            bool pass2 = !res2.threshold.HasValue || res2.score <= res2.threshold.Value;
            string thr1 = res1.threshold.HasValue ? res1.threshold.Value.ToString("0.###") : "n/a";
            string thr2 = res2.threshold.HasValue ? res2.threshold.Value.ToString("0.###") : "n/a";

            var (m1x, m1y) = _layout.Master1Pattern.GetCenter();
            var (m2x, m2y) = _layout.Master2Pattern.GetCenter();
            var c1 = new System.Windows.Point(m1x, m1y);
            var c2 = new System.Windows.Point(m2x, m2y);
            var mid = new System.Windows.Point((c1.X + c2.X) / 2.0, (c1.Y + c2.Y) / 2.0);
            var (c1Canvas, c2Canvas, midCanvas) = ConvertMasterPointsToCanvas(c1, c2, mid);

            AppendLog($"[infer] Master1 score={res1.score:0.###} thr={thr1} regions={(res1.regions?.Length ?? 0)} status={(pass1 ? "OK" : "NG")}");
            AppendLog($"[infer] Master2 score={res2.score:0.###} thr={thr2} regions={(res2.regions?.Length ?? 0)} status={(pass2 ? "OK" : "NG")}");

            if (!pass1 || !pass2)
            {
                string msg = $"Master1 {(pass1 ? "OK" : "NG")} (score={res1.score:0.###}, thr={thr1}) | " +
                             $"Master2 {(pass2 ? "OK" : "NG")} (score={res2.score:0.###}, thr={thr2})";
                Snack($"{msg}"); // CODEX: FormattableString compatibility.
            }

            if (_layout.Inspection == null) { Snack($"Falta ROI de Inspección"); return; } // CODEX: FormattableString compatibility.
            MoveInspectionTo(_layout.Inspection, c1, c2);
            ClipInspectionROI(_layout.Inspection, _imgW, _imgH);

            RedrawOverlay();
            DrawCross(c1Canvas.X, c1Canvas.Y, 20, Brushes.LimeGreen, 2);
            DrawCross(c2Canvas.X, c2Canvas.Y, 20, Brushes.Orange, 2);
            DrawCross(midCanvas.X, midCanvas.Y, 24, Brushes.Red, 2);
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