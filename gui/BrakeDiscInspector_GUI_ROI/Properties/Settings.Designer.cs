using System.Configuration;

namespace BrakeDiscInspector_GUI_ROI.Properties
{
    [global::System.Runtime.CompilerServices.CompilerGeneratedAttribute]
    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("Manual", "1.0.0.0")]
    internal sealed partial class Settings : ApplicationSettingsBase
    {
        private static readonly Settings defaultInstance = (Settings)Synchronized(new Settings());

        public static Settings Default => defaultInstance;

        [UserScopedSetting]
        [DefaultSettingValue("")]
        public string LastDatasetPathROI1
        {
            get => (string)this[nameof(LastDatasetPathROI1)];
            set => this[nameof(LastDatasetPathROI1)] = value;
        }

        [UserScopedSetting]
        [DefaultSettingValue("")]
        public string LastDatasetPathROI2
        {
            get => (string)this[nameof(LastDatasetPathROI2)];
            set => this[nameof(LastDatasetPathROI2)] = value;
        }

        [UserScopedSetting]
        [DefaultSettingValue("")]
        public string LastDatasetPathROI3
        {
            get => (string)this[nameof(LastDatasetPathROI3)];
            set => this[nameof(LastDatasetPathROI3)] = value;
        }

        [UserScopedSetting]
        [DefaultSettingValue("")]
        public string LastDatasetPathROI4
        {
            get => (string)this[nameof(LastDatasetPathROI4)];
            set => this[nameof(LastDatasetPathROI4)] = value;
        }

        [UserScopedSetting]
        [DefaultSettingValue("")]
        public string LastModelDirROI1
        {
            get => (string)this[nameof(LastModelDirROI1)];
            set => this[nameof(LastModelDirROI1)] = value;
        }

        [UserScopedSetting]
        [DefaultSettingValue("")]
        public string LastModelDirROI2
        {
            get => (string)this[nameof(LastModelDirROI2)];
            set => this[nameof(LastModelDirROI2)] = value;
        }

        [UserScopedSetting]
        [DefaultSettingValue("")]
        public string LastModelDirROI3
        {
            get => (string)this[nameof(LastModelDirROI3)];
            set => this[nameof(LastModelDirROI3)] = value;
        }

        [UserScopedSetting]
        [DefaultSettingValue("")]
        public string LastModelDirROI4
        {
            get => (string)this[nameof(LastModelDirROI4)];
            set => this[nameof(LastModelDirROI4)] = value;
        }
    }
}
