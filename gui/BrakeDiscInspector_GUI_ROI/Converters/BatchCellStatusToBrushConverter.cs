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
        public Brush OkBrush { get; set; } = Brushes.LimeGreen;
        public Brush NokBrush { get; set; } = Brushes.Red;
        public Brush UnknownBrush { get; set; } = Brushes.Gray;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (targetType != typeof(Brush) && !targetType.IsAssignableFrom(typeof(Brush)))
            {
                return DependencyProperty.UnsetValue;
            }

            if (value is not BatchCellStatus status)
            {
                return UnknownBrush;
            }

            return status switch
            {
                BatchCellStatus.Ok => OkBrush,
                BatchCellStatus.Nok => NokBrush,
                _ => UnknownBrush,
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
