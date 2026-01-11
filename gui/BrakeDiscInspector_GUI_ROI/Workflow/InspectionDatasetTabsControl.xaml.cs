using System;
using System.Windows;
using System.Windows.Controls;

namespace BrakeDiscInspector_GUI_ROI.Workflow
{
    public partial class InspectionDatasetTabsControl : UserControl
    {
        public event EventHandler<LoadModelRequestedEventArgs>? LoadModelRequested;
        public event EventHandler<ToggleEditRequestedEventArgs>? ToggleEditRequested;

        public InspectionDatasetTabsControl()
        {
            InitializeComponent();
        }

        private void InspectionTab_LoadModelRequested(object sender, LoadModelRequestedEventArgs e)
        {
            LoadModelRequested?.Invoke(this, e);
        }

        private void InspectionTab_ToggleEditRequested(object sender, ToggleEditRequestedEventArgs e)
        {
            ToggleEditRequested?.Invoke(this, e);
        }
    }
}
