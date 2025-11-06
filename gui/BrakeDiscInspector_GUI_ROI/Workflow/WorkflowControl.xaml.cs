using System;
using System.Windows;
using System.Windows.Controls;
using BrakeDiscInspector_GUI_ROI.Models;

namespace BrakeDiscInspector_GUI_ROI.Workflow
{
    public partial class WorkflowControl : UserControl
    {
        public event EventHandler<LoadModelRequestedEventArgs>? LoadModelRequested;

        public WorkflowControl()
        {
            InitializeComponent();
        }

        private void LoadModelButton_Click(object sender, RoutedEventArgs e)
        {
            var roiIndex = 0;

            if (sender is FrameworkElement element)
            {
                if (element.Tag is int intTag)
                {
                    roiIndex = intTag;
                }
                else if (element.Tag is string strTag && int.TryParse(strTag, out var parsed))
                {
                    roiIndex = parsed;
                }
                else if (element.DataContext is InspectionRoiConfig roi)
                {
                    roiIndex = roi.Index;
                }
            }

            if (roiIndex <= 0 && sender is FrameworkElement { DataContext: InspectionRoiConfig ctx })
            {
                roiIndex = ctx.Index;
            }

            if (roiIndex <= 0)
            {
                return;
            }

            LoadModelRequested?.Invoke(this, new LoadModelRequestedEventArgs(roiIndex));
        }
    }

    public sealed class LoadModelRequestedEventArgs : EventArgs
    {
        public LoadModelRequestedEventArgs(int index)
        {
            Index = index;
        }

        public int Index { get; }
    }
}
