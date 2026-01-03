using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using BrakeDiscInspector_GUI_ROI.Models;
using BrakeDiscInspector_GUI_ROI.Util;

namespace BrakeDiscInspector_GUI_ROI.Workflow
{
    public partial class WorkflowControl : UserControl
    {
        public event EventHandler<LoadModelRequestedEventArgs>? LoadModelRequested;
        public event EventHandler<ToggleEditRequestedEventArgs>? ToggleEditRequested;
        public event EventHandler? ClearCanvasRequested;

        public WorkflowControl()
        {
            InitializeComponent();
            if (InspectionDatasetTabs != null)
            {
                InspectionDatasetTabs.LoadModelRequested += InspectionDatasetTabsOnLoadModelRequested;
                InspectionDatasetTabs.ToggleEditRequested += InspectionDatasetTabsOnToggleEditRequested;
            }
        }

        private void InspectionDatasetTabsOnLoadModelRequested(object? sender, LoadModelRequestedEventArgs e)
        {
            LoadModelRequested?.Invoke(this, e);
        }

        private void InspectionDatasetTabsOnToggleEditRequested(object? sender, ToggleEditRequestedEventArgs e)
        {
            ToggleEditRequested?.Invoke(this, e);
        }

        public void ToggleInspectionEditFromExternal(int index)
        {
            if (DataContext is not WorkflowViewModel vm)
            {
                GuiLog.Warn($"[workflow-edit] ToggleEditRequested (external) ignored: DataContext not WorkflowViewModel index={index}");
                return;
            }

            if (vm.InspectionRois == null || vm.InspectionRois.Count == 0)
            {
                GuiLog.Warn($"[workflow-edit] ToggleEditRequested (external) ignored: InspectionRois empty index={index}");
                return;
            }

            var cfg = vm.InspectionRois.FirstOrDefault(r => r.Index == index);
            if (cfg == null)
            {
                GuiLog.Warn($"[workflow-edit] ToggleEditRequested (external) unknown ROI index={index}");
                return;
            }

            GuiLog.Info($"[workflow-edit] ToggleEditRequested (external) roi='{cfg.Id}' index={cfg.Index} enabled={cfg.Enabled} isEditable={cfg.IsEditable}");

            ToggleEditRequested?.Invoke(this, new ToggleEditRequestedEventArgs(cfg.Id, cfg.Index));
        }

        private void ClearCanvasButton_Click(object sender, RoutedEventArgs e)
        {
            ClearCanvasRequested?.Invoke(this, EventArgs.Empty);
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

    public sealed class ToggleEditRequestedEventArgs : EventArgs
    {
        public ToggleEditRequestedEventArgs(string roiId, int index)
        {
            RoiId = roiId;
            Index = index;
        }

        public string RoiId { get; }
        public int Index { get; }
    }
}
