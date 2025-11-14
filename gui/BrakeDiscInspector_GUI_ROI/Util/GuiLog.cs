using System;
using System.IO;

namespace BrakeDiscInspector_GUI_ROI.Util
{
    internal static class GuiLog
    {
        private static readonly object Sync = new();
        private static readonly string LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BrakeDiscInspector",
            "logs");

        private static readonly string LogFilePath = Path.Combine(LogDirectory, "gui.log");

        // CODEX: accept FormattableString to keep structured logging with invariant culture.
        public static void Info(FormattableString message)
            => Write("INFO", FormattableString.Invariant(message));

        // CODEX: FormattableString overload maintains parity with Info logging behavior.
        public static void Warn(FormattableString message)
            => Write("WARN", FormattableString.Invariant(message));

        // CODEX: unify error logging across severities using FormattableString inputs.
        public static void Error(FormattableString message)
            => Write("ERROR", FormattableString.Invariant(message));

        public static void Error(FormattableString message, Exception exception)
        {
            // CODEX: render interpolated error once and append exception safely.
            var rendered = FormattableString.Invariant(message);
            Write("ERROR", $"{rendered} :: {exception}");
        }

        private static void Write(string level, string message)
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
                    var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
                    File.AppendAllText(LogFilePath, line);
                }
            }
            catch
            {
                // Intentionally ignore logging failures
            }
        }
    }
}
