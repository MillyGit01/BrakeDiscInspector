using System;
using System.Windows;
using BrakeDiscInspector_GUI_ROI.Comms;

namespace BrakeDiscInspector_GUI_ROI
{
    public partial class MainWindow : Window
    {
        private CommsViewModel? _commsVm;

        private void InitComms()
        {
            try
            {
                var config = new PlcConfig("192.168.0.10", 0, 1);
                _commsVm = new CommsViewModel(config, cfg => new S7PlcClient(cfg));

                CommsRoot.DataContext = _commsVm;
            }
            catch (Exception ex)
            {
                Util.GuiLog.Error("[plc] InitComms failed", ex);
            }
        }
    }
}
