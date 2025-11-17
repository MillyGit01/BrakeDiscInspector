using System;
using System.IO;

namespace BrakeDiscInspector_GUI_ROI.Util
{
    public static class DatasetPathHelper
    {
        public static string? NormalizeDatasetPath(string? rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                return null;
            }

            var normalized = rawPath.Trim();
            normalized = normalized.Replace("\\\\", "\\");

            try
            {
                normalized = Path.GetFullPath(normalized);
            }
            catch (Exception)
            {
                // If the path cannot be resolved we still return the trimmed version
                // so callers can surface a meaningful validation error later.
            }

            return normalized;
        }
    }
}
