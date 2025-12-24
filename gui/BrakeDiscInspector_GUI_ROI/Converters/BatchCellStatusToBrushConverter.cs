using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace BrakeDiscInspector_GUI_ROI.Converters
{
    public sealed class BatchCellStatusToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (targetType != typeof(Brush) && !targetType.IsAssignableFrom(typeof(Brush)))
            {
                return DependencyProperty.UnsetValue;
            }

            if (value == null || value == DependencyProperty.UnsetValue)
            {
                return Brushes.Gray;
            }

            if (value is bool boolValue)
            {
                return boolValue ? Brushes.LimeGreen : Brushes.IndianRed;
            }

            if (value is string textValue)
            {
                return MapStatusText(textValue);
            }

            if (value is Enum || value is int)
            {
                return MapStatusText(value.ToString());
            }

            return Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return DependencyProperty.UnsetValue;
        }

        private static Brush MapStatusText(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Trim() == "-")
            {
                return Brushes.Gray;
            }

            if (text.IndexOf("OK", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("PASS", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return Brushes.LimeGreen;
            }

            if (text.IndexOf("NG", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("FAIL", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return Brushes.IndianRed;
            }

            return Brushes.Gray;
        }
    }
}
