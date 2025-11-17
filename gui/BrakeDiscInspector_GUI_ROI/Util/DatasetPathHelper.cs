using System;
using System.IO;
using System.Text;

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

            var normalized = CollapseSeparatorsPreservingPrefixes(rawPath.Trim());

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

        private static string CollapseSeparatorsPreservingPrefixes(string path)
        {
            var isExtendedLength = path.StartsWith(@"\\?\", StringComparison.Ordinal);
            var isUnc = path.StartsWith(@"\\", StringComparison.Ordinal) && !isExtendedLength;

            var startIndex = isExtendedLength ? 4 : isUnc ? 2 : 0;
            var builder = new StringBuilder();
            builder.Append(path, 0, startIndex);

            var previousWasSeparator = false;

            for (var i = startIndex; i < path.Length; i++)
            {
                var current = path[i];

                if (current == '/' || current == '\\')
                {
                    if (!previousWasSeparator)
                    {
                        builder.Append('\\');
                        previousWasSeparator = true;
                    }
                }
                else
                {
                    builder.Append(current);
                    previousWasSeparator = false;
                }
            }

            return builder.ToString();
        }
    }
}
