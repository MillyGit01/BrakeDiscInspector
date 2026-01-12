using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using System.Windows.Input;
using BrakeDiscInspector_GUI_ROI.Util;

namespace BrakeDiscInspector_GUI_ROI.Workflow
{
    public partial class DatasetPreviewItemView : UserControl
    {
        public DatasetPreviewItemView()
        {
            InitializeComponent();
        }

        private void DatasetImage_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Image image && image.DataContext is DatasetPreviewItem item && File.Exists(item.Path))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    GuiLog.Warn($"[dataset] Failed to open '{item.Path}': {ex.Message}");
                }
            }
        }
    }
}
