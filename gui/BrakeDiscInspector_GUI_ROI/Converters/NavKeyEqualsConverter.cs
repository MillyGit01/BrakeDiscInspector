using System;
using System.Globalization;
using System.Windows.Data;

namespace BrakeDiscInspector_GUI_ROI.Converters
{
    public sealed class NavKeyEqualsConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var selected = values != null && values.Length > 0 ? values[0] as string : null;
            var key = values != null && values.Length > 1 ? values[1] as string : null;

            if (string.IsNullOrWhiteSpace(selected) || string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            return string.Equals(selected, key, StringComparison.Ordinal);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
