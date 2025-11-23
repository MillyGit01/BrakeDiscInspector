using System.Collections.Generic;
using BrakeDiscInspector_GUI_ROI;

namespace BrakeDiscInspector_GUI_ROI.LayoutManagement
{
    public interface ILayoutProfileStore
    {
        IReadOnlyList<LayoutProfileInfo> GetProfiles();
        MasterLayout? LoadLayout(LayoutProfileInfo profile);
        LayoutProfileInfo SaveNewLayout(MasterLayout layout);
        void DeleteLayout(LayoutProfileInfo profile);
    }
}
