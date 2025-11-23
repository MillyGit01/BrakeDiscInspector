using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BrakeDiscInspector_GUI_ROI;

namespace BrakeDiscInspector_GUI_ROI.LayoutManagement
{
    public sealed class FileLayoutProfileStore : ILayoutProfileStore
    {
        private readonly Preset _preset;

        public FileLayoutProfileStore(Preset preset)
        {
            _preset = preset ?? throw new ArgumentNullException(nameof(preset));
        }

        public IReadOnlyList<LayoutProfileInfo> GetProfiles()
        {
            var profiles = new List<LayoutProfileInfo>();
            var layoutsDir = MasterLayoutManager.GetLayoutsFolder(_preset);

            if (!Directory.Exists(layoutsDir))
            {
                return profiles;
            }

            var lastPath = MasterLayoutManager.GetDefaultPath(_preset);
            if (File.Exists(lastPath))
            {
                profiles.Add(CreateProfileInfo(lastPath, isLastLayout: true));
            }

            foreach (var file in Directory.EnumerateFiles(layoutsDir, "*.layout.json"))
            {
                if (string.Equals(file, lastPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                profiles.Add(CreateProfileInfo(file, isLastLayout: false));
            }

            return profiles
                .OrderByDescending(p => p.CreatedUtc)
                .ThenBy(p => p.DisplayName)
                .ToList();
        }

        public MasterLayout? LoadLayout(LayoutProfileInfo profile)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.FilePath))
            {
                return null;
            }

            return MasterLayoutManager.LoadFromFile(profile.FilePath);
        }

        public LayoutProfileInfo SaveNewLayout(MasterLayout layout)
        {
            if (layout == null)
            {
                throw new ArgumentNullException(nameof(layout));
            }

            MasterLayoutManager.Save(_preset, layout);

            var profiles = GetProfiles();
            var latestProfile = profiles.FirstOrDefault(p => !p.IsLastLayout)
                ?? profiles.FirstOrDefault();
            if (latestProfile != null)
            {
                return latestProfile;
            }

            var defaultPath = MasterLayoutManager.GetDefaultPath(_preset);
            return new LayoutProfileInfo
            {
                FilePath = defaultPath,
                FileName = Path.GetFileName(defaultPath),
                DisplayName = "Last layout",
                CreatedUtc = DateTime.UtcNow,
                IsLastLayout = true
            };
        }

        public void DeleteLayout(LayoutProfileInfo profile)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.FilePath))
            {
                return;
            }

            if (profile.IsLastLayout)
            {
                return;
            }

            if (File.Exists(profile.FilePath))
            {
                File.Delete(profile.FilePath);
            }
        }

        private static LayoutProfileInfo CreateProfileInfo(string path, bool isLastLayout)
        {
            var fileName = Path.GetFileName(path);
            var displayName = isLastLayout
                ? "Last layout"
                : Path.GetFileNameWithoutExtension(path);

            return new LayoutProfileInfo
            {
                FilePath = path,
                FileName = fileName,
                DisplayName = displayName,
                CreatedUtc = File.GetCreationTimeUtc(path),
                IsLastLayout = isLastLayout
            };
        }
    }
}
