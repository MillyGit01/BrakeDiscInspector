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

            if (value is BatchCellStatus status)
            {
                return MapStatus(status);
            }

            if (value is int intValue && Enum.IsDefined(typeof(BatchCellStatus), intValue))
            {
                return MapStatus((BatchCellStatus)intValue);
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

            if (text.Trim().Equals("NOK", StringComparison.OrdinalIgnoreCase)
                || text.Trim().Equals("NG", StringComparison.OrdinalIgnoreCase)
                || text.Trim().Equals("FAIL", StringComparison.OrdinalIgnoreCase))
            {
                return Brushes.IndianRed;
            }

            if (text.Trim().Equals("OK", StringComparison.OrdinalIgnoreCase)
                || text.Trim().Equals("PASS", StringComparison.OrdinalIgnoreCase))
            {
                return Brushes.LimeGreen;
            }

            return Brushes.Gray;
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
    }
}
