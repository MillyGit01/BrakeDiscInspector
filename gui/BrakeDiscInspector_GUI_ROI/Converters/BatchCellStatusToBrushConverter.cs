using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using BrakeDiscInspector_GUI_ROI.Workflow;

namespace BrakeDiscInspector_GUI_ROI.Converters
{
    public sealed class BatchCellStatusToBrushConverter : IValueConverter
    {
        // Ajusta aquí si quieres otros colores
        private static readonly Brush OkBrush = Brushes.LimeGreen;
        private static readonly Brush NokBrush = Brushes.Tomato;
        private static readonly Brush UnknownBrush = Brushes.Gray;
        private static readonly Brush AnchorFailBrush = Brushes.Orange;
        private static readonly Brush SkippedBrush = Brushes.SlateGray;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is null)
                return UnknownBrush;

            // 1) Caso ideal: enum BatchCellStatus
            if (value is BatchCellStatus status)
                return MapStatus(status);

            // 2) Legacy: bool (si tu UI estaba enlazando a bool)
            if (value is bool b)
                return b ? OkBrush : NokBrush;

            // 3) Legacy: string ("OK"/"NOK"/"NG"/etc). SIN CONTAINS.
            if (value is string s)
                return MapStatusText(s);

            // 4) Fallback: intenta ToString()
            return MapStatusText(value.ToString());
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;

        private static Brush MapStatus(BatchCellStatus status)
        {
            return status switch
            {
                BatchCellStatus.Ok => OkBrush,
                BatchCellStatus.Nok => NokBrush,
                BatchCellStatus.AnchorFail => AnchorFailBrush,
                BatchCellStatus.Skipped => SkippedBrush,
                BatchCellStatus.Unknown => UnknownBrush,
                _ => UnknownBrush
            };
        }

        private static Brush MapStatusText(string statusText)
        {
            if (string.IsNullOrWhiteSpace(statusText))
                return UnknownBrush;

            var t = statusText.Trim();

            // Comparación EXACTA (sin Contains)
            if (string.Equals(t, "OK", StringComparison.OrdinalIgnoreCase))
                return OkBrush;

            if (string.Equals(t, "NOK", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, "NG", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, "FAIL", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, "KO", StringComparison.OrdinalIgnoreCase))
                return NokBrush;

            if (string.Equals(t, "ANCHORFAIL", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, "ANCHOR_FAIL", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, "ANCHOR FAIL", StringComparison.OrdinalIgnoreCase))
                return AnchorFailBrush;

            if (string.Equals(t, "SKIPPED", StringComparison.OrdinalIgnoreCase))
                return SkippedBrush;

            if (string.Equals(t, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
                return UnknownBrush;

            return UnknownBrush;
        }
    }
}
