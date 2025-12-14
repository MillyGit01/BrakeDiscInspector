using System;
using System.IO;

namespace BrakeDiscInspector_GUI_ROI.Util
{
    internal static class VisConfLog
    {
        private static readonly object Sync = new();
        private static readonly string LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BrakeDiscInspector",
            "logs");

        private static readonly string RoiCoordsPath = Path.Combine(LogDirectory, "roi_load_coords.log");
        private static readonly string AnalyzeMasterPath = Path.Combine(LogDirectory, "roi_analyze_master.log");

        public static string FileNameOrEmpty(string? path)
            => string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFileName(path);

        public static void Gui(string message) => GuiLog.Info(message);

        public static void GuiAndRoi(string message)
        {
            Gui(message);
            Roi(message);
        }

        public static void Roi(string message) => WriteLine(RoiCoordsPath, message);

        public static void AnalyzeMaster(string message) => WriteLine(AnalyzeMasterPath, message);

        private static void WriteLine(string path, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            try
            {
                lock (Sync)
                {
                    Directory.CreateDirectory(LogDirectory);
                    var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";
                    File.AppendAllText(path, line);
                }
            }
            catch
            {
                // Never throw from logging utilities.
            }
        }
    }
}
