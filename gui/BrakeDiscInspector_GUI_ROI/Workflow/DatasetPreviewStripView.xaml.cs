using System.Collections;
using System.Windows;
using System.Windows.Controls;

namespace BrakeDiscInspector_GUI_ROI.Workflow
{
    public partial class DatasetPreviewStripView : UserControl
    {
        public static readonly DependencyProperty ItemsSourceProperty =
            DependencyProperty.Register(
                nameof(ItemsSource),
                typeof(IEnumerable),
                typeof(DatasetPreviewStripView),
                new PropertyMetadata(null));

        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(
                nameof(Title),
                typeof(string),
                typeof(DatasetPreviewStripView),
                new PropertyMetadata(string.Empty));

        public DatasetPreviewStripView()
        {
            InitializeComponent();
        }

        public IEnumerable? ItemsSource
        {
            get => (IEnumerable?)GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        public string? Title
        {
            get => (string?)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }
    }
}
