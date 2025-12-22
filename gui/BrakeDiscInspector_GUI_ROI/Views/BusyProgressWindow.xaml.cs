using System;
using System.Windows;

namespace BrakeDiscInspector_GUI_ROI.Views
{
    public partial class BusyProgressWindow : Window
    {
        public BusyProgressWindow()
        {
            InitializeComponent();
        }

        public void SetTitle(string title)
        {
            if (title == null)
            {
                return;
            }

            SetState(title, detail: DetailText.Text, percent: Progress.IsIndeterminate ? null : Progress.Value);
        }

        public void SetDetail(string? detail)
        {
            SetState(Title, detail, percent: Progress.IsIndeterminate ? null : Progress.Value);
        }

        public void SetProgress(double? percent)
        {
            SetState(Title, DetailText.Text, percent);
        }

        public void SetState(string title, string? detail, double? percent)
        {
            Title = title;
            TitleText.Text = title;

            if (percent is null)
            {
                Progress.IsIndeterminate = true;
            }
            else
            {
                Progress.IsIndeterminate = false;
                var value = Math.Max(0, Math.Min(100, percent.Value));
                Progress.Value = value;

                if (string.IsNullOrWhiteSpace(detail))
                {
                    detail = $"{value:0}%";
                }
            }

            DetailText.Text = detail ?? string.Empty;
        }
    }
}
