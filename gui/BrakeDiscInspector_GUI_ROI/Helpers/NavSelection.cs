using System.Windows;

namespace BrakeDiscInspector_GUI_ROI
{
    public static class NavSelection
    {
        public static readonly DependencyProperty KeyProperty =
            DependencyProperty.RegisterAttached(
                "Key",
                typeof(string),
                typeof(NavSelection),
                new PropertyMetadata(default(string)));

        public static void SetKey(DependencyObject element, string value) => element.SetValue(KeyProperty, value);

        public static string GetKey(DependencyObject element) => (string)element.GetValue(KeyProperty);
    }
}
