using BrakeDiscInspector_GUI_ROI;

namespace BrakeDiscInspector_GUI_ROI.LayoutManagement
{
    public interface ILayoutHost
    {
        MasterLayout? CurrentLayout { get; }
        void ApplyLayout(MasterLayout? layout, string sourceContext, string? layoutFilePath = null);
    }
}
