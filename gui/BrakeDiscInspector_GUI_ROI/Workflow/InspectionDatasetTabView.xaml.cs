using System;
using System.Windows;
using System.Windows.Controls;
using BrakeDiscInspector_GUI_ROI.Models;
using BrakeDiscInspector_GUI_ROI.Util;

namespace BrakeDiscInspector_GUI_ROI.Workflow
{
    public partial class InspectionDatasetTabView : UserControl
    {
        public event EventHandler<LoadModelRequestedEventArgs>? LoadModelRequested;
        public event EventHandler<ToggleEditRequestedEventArgs>? ToggleEditRequested;

        public InspectionDatasetTabView()
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

    }
}
