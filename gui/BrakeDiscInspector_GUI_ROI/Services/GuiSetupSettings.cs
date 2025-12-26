using System.Collections.Generic;

namespace BrakeDiscInspector_GUI_ROI.Services
{
    public sealed class GuiSetupSettings
    {
        public string? Theme { get; set; }
        public Dictionary<string, string>? FontFamilies { get; set; }
        public Dictionary<string, double>? FontSizes { get; set; }
        public Dictionary<string, string>? Brushes { get; set; }
        public string? Accent { get; set; }
    }
}
