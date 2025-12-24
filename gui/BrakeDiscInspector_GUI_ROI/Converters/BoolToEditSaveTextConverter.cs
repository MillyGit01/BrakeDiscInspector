using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BrakeDiscInspector_GUI_ROI.Converters
{
    public class BoolToEditSaveTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? "Save" : "Edit";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value switch
            {
                string text when string.Equals(text, "Save", StringComparison.OrdinalIgnoreCase) => true,
                string text when string.Equals(text, "Edit", StringComparison.OrdinalIgnoreCase) => false,
                _ => DependencyProperty.UnsetValue,
            };
        }
    }
}
