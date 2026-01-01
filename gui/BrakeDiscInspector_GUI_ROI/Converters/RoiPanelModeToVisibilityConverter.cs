using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BrakeDiscInspector_GUI_ROI.Converters
{
    public sealed class RoiPanelModeToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not Enum)
            {
                return Visibility.Collapsed;
            }

            if (parameter is null)
            {
                return Visibility.Collapsed;
            }

            var parameterText = parameter.ToString();
            if (string.IsNullOrWhiteSpace(parameterText))
            {
                return Visibility.Collapsed;
            }

            if (!Enum.TryParse(value.GetType(), parameterText, out var parameterValue))
            {
                return Visibility.Collapsed;
            }

            return Equals(value, parameterValue) ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
