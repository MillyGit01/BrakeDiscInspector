using System;

namespace BrakeDiscInspector_GUI_ROI.Workflow
{
    public class BackendBadRequestException : Exception
    {
        public BackendBadRequestException(string message, string? detail = null)
            : base(string.IsNullOrWhiteSpace(detail) ? message : $"{message}: {detail}")
        {
            Detail = string.IsNullOrWhiteSpace(detail) ? null : detail;
        }

        public string? Detail { get; }
    }

    public sealed class BackendMemoryNotFittedException : BackendBadRequestException
    {
        public BackendMemoryNotFittedException(string? detail = null)
            : base("Backend memory not fitted", detail)
        {
        }
    }

    public sealed class BackendCalibrationMissingException : BackendBadRequestException
    {
        public BackendCalibrationMissingException(string? detail = null)
            : base("Backend calibration missing", detail)
        {
        }
    }
}
