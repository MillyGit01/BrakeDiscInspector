using System;
using System.IO;
using System.Windows;

namespace BrakeDiscInspector_GUI_ROI
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            ResetLogsOnStartup();
            base.OnStartup(e);
        }

        private static void ResetLogsOnStartup()
        {
            try
            {
                var logsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BrakeDiscInspector",
                    "logs");

                if (Directory.Exists(logsDir))
                {
                    foreach (var logFile in Directory.EnumerateFiles(logsDir, "*.log"))
                    {
                        try
                        {
                            File.Delete(logFile);
                        }
                        catch
                        {
                            // Swallow individual deletion errors to avoid breaking startup.
                        }
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

