using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BrakeDiscInspector_GUI_ROI.Models;
using BrakeDiscInspector_GUI_ROI.Util;

namespace BrakeDiscInspector_GUI_ROI.Workflow
{
    public partial class WorkflowControl : UserControl
    {
        public event EventHandler<LoadModelRequestedEventArgs>? LoadModelRequested;
        public event EventHandler<ToggleEditRequestedEventArgs>? ToggleEditRequested;

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

        private void BtnToggleEdit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is InspectionRoiConfig cfg)
            {
                GuiLog.Info($"[workflow-edit] ToggleEditRequested roi='{cfg.Id}' index={cfg.Index} enabled={cfg.Enabled} isEditable={cfg.IsEditable}");

                ToggleEditRequested?.Invoke(this, new ToggleEditRequestedEventArgs(cfg.Id, cfg.Index));
            }
            else
            {
                GuiLog.Warn("[workflow-edit] BtnToggleEdit_Click ignored: invalid sender/DataContext");
            }
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
