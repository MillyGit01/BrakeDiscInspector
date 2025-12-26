using System;
using System.IO;
using System.Linq;
using System.Windows;
using BrakeDiscInspector_GUI_ROI.Services;
using BrakeDiscInspector_GUI_ROI.Properties;

namespace BrakeDiscInspector_GUI_ROI
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public static GuiSetupSettings CurrentGuiSetup { get; set; } = new();

        protected override void OnStartup(StartupEventArgs e)
        {
            ResetLogsOnStartup();
            var guiSettings = GuiSetupSettingsService.LoadOrDefault();
            CurrentGuiSetup = guiSettings;
            GuiSetupSettingsService.Apply(guiSettings);
            ApplyThemePreference(guiSettings.Theme);
            GuiSetupSettingsService.Apply(CurrentGuiSetup);
            base.OnStartup(e);
        }

        private static void ApplyThemePreference(string? preference)
        {
            if (string.IsNullOrWhiteSpace(preference))
            {
                return;
            }

            var themeValue = preference.Equals("Auto", StringComparison.OrdinalIgnoreCase)
                ? "Light"
                : preference;

            try
            {
                var themeDictionary = Application.Current.Resources.MergedDictionaries
                    .FirstOrDefault(dictionary => dictionary.GetType().Name == "ThemesDictionary");
                if (themeDictionary == null)
                {
                    return;
                }

                var themeProperty = themeDictionary.GetType().GetProperty("Theme");
                if (themeProperty == null || !themeProperty.CanWrite)
                {
                    return;
                }

                var targetType = themeProperty.PropertyType;
                object? resolvedValue = themeValue;

                if (targetType.IsEnum)
                {
                    if (Enum.TryParse(targetType, themeValue, true, out var parsed))
                    {
                        resolvedValue = parsed;
                    }
                    else
                    {
                        resolvedValue = null;
                    }
                }
                else if (targetType != typeof(string) && targetType != typeof(object))
                {
                    resolvedValue = null;
                }

                if (resolvedValue != null)
                {
                    themeProperty.SetValue(themeDictionary, resolvedValue);
                }

                Settings.Default.ThemePreference = preference;
                Settings.Default.Save();
            }
            catch
            {
                // Swallow theme initialization errors to avoid startup failure.
            }
        }

        private static void ResetLogsOnStartup()
        {
            try
            {
                var logsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BrakeDiscInspector",
                    "logs");

                Directory.CreateDirectory(logsDir);

                var logFiles = new[]
                {
                    Path.Combine(logsDir, "gui.log"),
                    Path.Combine(logsDir, "roi_load_coords.log"),
                    Path.Combine(logsDir, "gui_heatmap.log"),
                    Path.Combine(logsDir, "roi_analyze_master.log"),
                };

                foreach (var logFile in logFiles)
                {
                    try
                    {
                        if (File.Exists(logFile))
                        {
                            File.Delete(logFile);
                        }

                        // Recreate empty file so logging starts cleanly for the session.
                        using (File.Create(logFile))
                        {
                        }
                    }
                    catch
                    {
                        // Swallow individual deletion errors to avoid breaking startup.
                    }
                }

                var miscLogPaths = new[]
                {
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        "brakedisc_localmatcher.log")
                };

                foreach (var path in miscLogPaths)
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            File.Delete(path);
                        }
                    }
                    catch
                    {
                        // Best-effort cleanup; ignore failures.
                    }
                }
            }
            catch
            {
                // Never break startup because of log cleanup issues.
            }
        }
    }
}
