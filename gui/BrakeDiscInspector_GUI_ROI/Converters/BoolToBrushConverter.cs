using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace BrakeDiscInspector_GUI_ROI.Converters
{
    public class BoolToBrushConverter : IValueConverter
    {
        public Brush OnBrush { get; set; } = Brushes.LimeGreen;

        public Brush OffBrush { get; set; } = Brushes.Gray;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool flag && flag)
            {
                return OnBrush;
            }

            return OffBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}
