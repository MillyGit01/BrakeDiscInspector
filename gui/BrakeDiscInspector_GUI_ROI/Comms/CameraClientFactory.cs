using System;

namespace BrakeDiscInspector_GUI_ROI.Comms
{
    public static class CameraClientFactory
    {
        public static ICameraClient Create(CameraConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            return config.Provider.Trim() switch
            {
                CameraProviders.Folder => new FolderCameraClient(config),
                CameraProviders.FlirBlackfly => new FlirBlackflyCameraClient(config),
                CameraProviders.Cognex => new UnavailableCameraClient(
                    config,
                    "Cognex requires the selected Cognex SDK/protocol adapter to be installed and wired before this provider can acquire images."),
                _ => new UnavailableCameraClient(config, "Camera provider is disabled.")
            };
        }
    }
}
