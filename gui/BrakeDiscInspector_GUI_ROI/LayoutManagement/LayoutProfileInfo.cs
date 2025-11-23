using System;

namespace BrakeDiscInspector_GUI_ROI.LayoutManagement
{
    public sealed class LayoutProfileInfo
    {
        public string FilePath { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public DateTime CreatedUtc { get; init; }
        public bool IsLastLayout { get; init; }
    }
}
