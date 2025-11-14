using System;
using System.Globalization;
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

        public static void Info(string message)
            => Write("INFO", message);

        // CODEX: accept FormattableString to keep structured logging with invariant culture.
        public static void Info(FormattableString message)
        {
            if (message == null)
            {
                return;
            }

            Info(message.ToString(CultureInfo.InvariantCulture));
        }

        public static void Warn(string message)
            => Write("WARN", message);

        // CODEX: FormattableString overload maintains parity with Info logging behavior.
        public static void Warn(FormattableString message)
        {
            if (message == null)
            {
                return;
            }

            Warn(message.ToString(CultureInfo.InvariantCulture));
        }

        public static void Error(string message)
            => Write("ERROR", message);

        // CODEX: unify error logging across severities using FormattableString inputs.
        public static void Error(FormattableString message)
        {
            if (message == null)
            {
                return;
            }

            Error(message.ToString(CultureInfo.InvariantCulture));
        }

        public static void Error(string message, Exception exception)
        {
            var baseMessage = message ?? string.Empty;
            var rendered = exception == null
                ? baseMessage
                : string.IsNullOrEmpty(baseMessage)
                    ? exception.ToString()
                    : $"{baseMessage} :: {exception}";

            Write("ERROR", rendered);
        }

        public static void Error(FormattableString message, Exception exception)
        {
            if (message == null)
            {
                Error((string?)null, exception);
                return;
            }

            Error(message.ToString(CultureInfo.InvariantCulture), exception);
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
