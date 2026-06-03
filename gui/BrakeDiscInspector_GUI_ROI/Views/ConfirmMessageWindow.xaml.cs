using System.Windows;

namespace BrakeDiscInspector_GUI_ROI.Views
{
    public partial class ConfirmMessageWindow : Window
    {
        public ConfirmMessageWindow(string title, string message, string confirmText = "Confirm")
        {
            InitializeComponent();
            Title = title;
            TitleText.Text = title;
            MessageText.Text = message;
            ConfirmButton.Content = confirmText;
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}
