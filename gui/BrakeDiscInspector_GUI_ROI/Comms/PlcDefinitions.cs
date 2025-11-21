using System.Collections.Generic;

namespace BrakeDiscInspector_GUI_ROI.Comms
{
    public enum PlcSignalDirection
    {
        Input,
        Output
    }

    public enum PlcSignalId
    {
        StartInspection,
        StopInspection,
        PartPresent,
        ResetError,
        SystemReady,
        Busy,
        ResultOk,
        ResultNg,
        Error
    }

    public sealed class PlcSignalDefinition
    {
        public PlcSignalDefinition(PlcSignalId id, string displayName, PlcSignalDirection direction, int byteOffset, int bitOffset)
        {
            Id = id;
            DisplayName = displayName;
            Direction = direction;
            ByteOffset = byteOffset;
            BitOffset = bitOffset;
        }

        public PlcSignalId Id { get; }

        public string DisplayName { get; }

        public PlcSignalDirection Direction { get; }

        public int ByteOffset { get; }

        public int BitOffset { get; }
    }

    public static class PlcSignals
    {
        public const int DbNumber = 1;

        public static readonly IReadOnlyList<PlcSignalDefinition> Definitions = new[]
        {
            new PlcSignalDefinition(PlcSignalId.StartInspection, "Start inspection", PlcSignalDirection.Input, 0, 0),
            new PlcSignalDefinition(PlcSignalId.StopInspection, "Stop inspection", PlcSignalDirection.Input, 0, 1),
            new PlcSignalDefinition(PlcSignalId.PartPresent, "Part present", PlcSignalDirection.Input, 0, 2),
            new PlcSignalDefinition(PlcSignalId.ResetError, "Reset error", PlcSignalDirection.Input, 0, 3),

            new PlcSignalDefinition(PlcSignalId.SystemReady, "System ready", PlcSignalDirection.Output, 2, 0),
            new PlcSignalDefinition(PlcSignalId.Busy, "Busy", PlcSignalDirection.Output, 2, 1),
            new PlcSignalDefinition(PlcSignalId.ResultOk, "Result OK", PlcSignalDirection.Output, 2, 2),
            new PlcSignalDefinition(PlcSignalId.ResultNg, "Result NG", PlcSignalDirection.Output, 2, 3),
            new PlcSignalDefinition(PlcSignalId.Error, "Error", PlcSignalDirection.Output, 2, 4)
        };
    }
}
