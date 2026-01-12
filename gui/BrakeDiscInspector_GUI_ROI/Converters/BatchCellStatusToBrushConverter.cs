using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using BrakeDiscInspector_GUI_ROI.Workflow;

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

            if (value is BatchCellStatus statusValue)
            {
                return MapStatus(statusValue);
            }

            if (value is int statusInt && Enum.IsDefined(typeof(BatchCellStatus), statusInt))
            {
                return MapStatus((BatchCellStatus)statusInt);
            }

            if (value is string textValue)
            {
                return MapStatusText(textValue);
            }

            if (value is Enum)
            {
                return MapStatusText(value.ToString());
            }

            return Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return DependencyProperty.UnsetValue;
        }

        private static Brush MapStatus(BatchCellStatus status)
        {
            return status switch
            {
                BatchCellStatus.Ok => Brushes.LimeGreen,
                BatchCellStatus.Nok => Brushes.IndianRed,
                _ => Brushes.Gray
            };
        }

        private static Brush MapStatusText(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Trim() == "-")
            {
                return Brushes.Gray;
            }

            if (text.Equals("NOK", StringComparison.OrdinalIgnoreCase)
                || text.Equals("NG", StringComparison.OrdinalIgnoreCase)
                || text.Equals("FAIL", StringComparison.OrdinalIgnoreCase))
            {
                return Brushes.IndianRed;
            }

            if (text.Equals("OK", StringComparison.OrdinalIgnoreCase)
                || text.Equals("PASS", StringComparison.OrdinalIgnoreCase))
            {
                return Brushes.LimeGreen;
            }

            return Brushes.Gray;
        }
    }
}
