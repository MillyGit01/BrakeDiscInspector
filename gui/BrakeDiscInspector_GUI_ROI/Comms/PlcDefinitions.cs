using System.Collections.Generic;
using System.Linq;

namespace BrakeDiscInspector_GUI_ROI.Comms
{
    public enum PlcDbRole
    {
        PcToPlc,
        PlcToPc
    }

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
        Error,
        PlcAlarm,
        Rotating,
        GripperOpen,
        GripperClosed,
        LegacyCapture1,
        InspectionCompleteCam1,
        PlcReportedOkCam1,
        PlcReportedNgCam1,
        DataSaved,
        ManualAuto,
        AutoCam1Active,
        AutoCam1Error,
        OpenGripper,
        CloseGripper,
        NewPosition,
        ResetCommand,
        AbortCommand,
        Trigger1,
        InspectionComplete,
        RotateCommand,
        ManualAutoCommand,
        AutoCam1Start
    }

    public sealed class PlcSignalDefinition
    {
        public PlcSignalDefinition(
            PlcSignalId id,
            string displayName,
            PlcSignalDirection direction,
            PlcDbRole dbRole,
            int byteOffset,
            int bitOffset)
        {
            Id = id;
            DisplayName = displayName;
            Direction = direction;
            DbRole = dbRole;
            ByteOffset = byteOffset;
            BitOffset = bitOffset;
        }

        public PlcSignalId Id { get; }

        public string DisplayName { get; }

        public PlcSignalDirection Direction { get; }

        public PlcDbRole DbRole { get; }

        public int ByteOffset { get; }

        public int BitOffset { get; }
    }

    public static class PlcSignals
    {
        public const int DefaultPcToPlcDbNumber = 150;
        public const int DefaultPlcToPcDbNumber = 151;
        public const int DefaultDiagnosticDbNumber = 8;
        public const int PlcToPcReadLength = 9;
        public const int PcToPlcBoolWriteLength = 2;

        public const int DefaultDbNumber = DefaultPcToPlcDbNumber;

        public static readonly IReadOnlyList<PlcSignalDefinition> Definitions = new[]
        {
            new PlcSignalDefinition(PlcSignalId.PartPresent, "PLC ready / part present", PlcSignalDirection.Input, PlcDbRole.PlcToPc, 0, 0),
            new PlcSignalDefinition(PlcSignalId.PlcAlarm, "PLC alarm", PlcSignalDirection.Input, PlcDbRole.PlcToPc, 0, 1),
            new PlcSignalDefinition(PlcSignalId.Rotating, "Rotating", PlcSignalDirection.Input, PlcDbRole.PlcToPc, 0, 2),
            new PlcSignalDefinition(PlcSignalId.GripperOpen, "Gripper open", PlcSignalDirection.Input, PlcDbRole.PlcToPc, 0, 3),
            new PlcSignalDefinition(PlcSignalId.GripperClosed, "Gripper closed", PlcSignalDirection.Input, PlcDbRole.PlcToPc, 0, 4),
            new PlcSignalDefinition(PlcSignalId.ResetError, "Reset from PLC", PlcSignalDirection.Input, PlcDbRole.PlcToPc, 0, 5),
            new PlcSignalDefinition(PlcSignalId.StopInspection, "Abort from PLC", PlcSignalDirection.Input, PlcDbRole.PlcToPc, 0, 6),
            new PlcSignalDefinition(PlcSignalId.LegacyCapture1, "Legacy capture 1", PlcSignalDirection.Input, PlcDbRole.PlcToPc, 0, 7),

            new PlcSignalDefinition(PlcSignalId.StartInspection, "Start inspection / TriggerAck Cam1", PlcSignalDirection.Input, PlcDbRole.PlcToPc, 1, 0),
            new PlcSignalDefinition(PlcSignalId.InspectionCompleteCam1, "PLC inspection complete Cam1", PlcSignalDirection.Input, PlcDbRole.PlcToPc, 1, 1),
            new PlcSignalDefinition(PlcSignalId.PlcReportedOkCam1, "PLC OK Cam1", PlcSignalDirection.Input, PlcDbRole.PlcToPc, 1, 2),
            new PlcSignalDefinition(PlcSignalId.PlcReportedNgCam1, "PLC NG Cam1", PlcSignalDirection.Input, PlcDbRole.PlcToPc, 1, 3),
            new PlcSignalDefinition(PlcSignalId.DataSaved, "Data saved", PlcSignalDirection.Input, PlcDbRole.PlcToPc, 1, 4),
            new PlcSignalDefinition(PlcSignalId.ManualAuto, "Manual / auto", PlcSignalDirection.Input, PlcDbRole.PlcToPc, 1, 5),
            new PlcSignalDefinition(PlcSignalId.AutoCam1Active, "AutoCam1 active", PlcSignalDirection.Input, PlcDbRole.PlcToPc, 1, 6),
            new PlcSignalDefinition(PlcSignalId.AutoCam1Error, "AutoCam1 error", PlcSignalDirection.Input, PlcDbRole.PlcToPc, 1, 7),

            new PlcSignalDefinition(PlcSignalId.SystemReady, "App ready (EstadoAPP)", PlcSignalDirection.Output, PlcDbRole.PcToPlc, 0, 0),
            new PlcSignalDefinition(PlcSignalId.Error, "App alarm", PlcSignalDirection.Output, PlcDbRole.PcToPlc, 0, 1),
            new PlcSignalDefinition(PlcSignalId.Busy, "App busy / Estado", PlcSignalDirection.Output, PlcDbRole.PcToPlc, 0, 2),
            new PlcSignalDefinition(PlcSignalId.OpenGripper, "Open gripper", PlcSignalDirection.Output, PlcDbRole.PcToPlc, 0, 3),
            new PlcSignalDefinition(PlcSignalId.CloseGripper, "Close gripper", PlcSignalDirection.Output, PlcDbRole.PcToPlc, 0, 4),
            new PlcSignalDefinition(PlcSignalId.NewPosition, "New position", PlcSignalDirection.Output, PlcDbRole.PcToPlc, 0, 5),
            new PlcSignalDefinition(PlcSignalId.ResetCommand, "Reset command", PlcSignalDirection.Output, PlcDbRole.PcToPlc, 0, 6),
            new PlcSignalDefinition(PlcSignalId.AbortCommand, "Abort command", PlcSignalDirection.Output, PlcDbRole.PcToPlc, 0, 7),

            new PlcSignalDefinition(PlcSignalId.Trigger1, "Trigger 1", PlcSignalDirection.Output, PlcDbRole.PcToPlc, 1, 0),
            new PlcSignalDefinition(PlcSignalId.InspectionComplete, "BDI inspection complete (Trigger2)", PlcSignalDirection.Output, PlcDbRole.PcToPlc, 1, 1),
            new PlcSignalDefinition(PlcSignalId.ResultOk, "BDI result OK (Trigger3)", PlcSignalDirection.Output, PlcDbRole.PcToPlc, 1, 2),
            new PlcSignalDefinition(PlcSignalId.RotateCommand, "Rotate command", PlcSignalDirection.Output, PlcDbRole.PcToPlc, 1, 3),
            new PlcSignalDefinition(PlcSignalId.ManualAutoCommand, "Manual / auto command", PlcSignalDirection.Output, PlcDbRole.PcToPlc, 1, 4),
            new PlcSignalDefinition(PlcSignalId.AutoCam1Start, "AutoCam1 360 start", PlcSignalDirection.Output, PlcDbRole.PcToPlc, 1, 5),
            new PlcSignalDefinition(PlcSignalId.ResultNg, "BDI result NG (Reserva3)", PlcSignalDirection.Output, PlcDbRole.PcToPlc, 1, 6)
        };

        public static IDictionary<PlcSignalId, bool> Decode(
            IReadOnlyList<byte> plcToPcBytes,
            IReadOnlyList<byte>? pcToPlcBytes = null)
        {
            var result = new Dictionary<PlcSignalId, bool>();

            foreach (var definition in Definitions)
            {
                IReadOnlyList<byte>? source = definition.DbRole == PlcDbRole.PlcToPc
                    ? plcToPcBytes
                    : definition.DbRole == PlcDbRole.PcToPlc
                        ? pcToPlcBytes
                        : null;

                if (source == null)
                {
                    continue;
                }

                result[definition.Id] = GetBit(source, definition.ByteOffset, definition.BitOffset);
            }

            return result;
        }

        public static void EncodeOutputs(IList<byte> pcToPlcBytes, IDictionary<PlcSignalId, bool> outputs)
        {
            if (outputs == null || outputs.Count == 0)
            {
                return;
            }

            foreach (var kvp in outputs)
            {
                var definition = Definitions.FirstOrDefault(d => d.Id == kvp.Key && d.Direction == PlcSignalDirection.Output);
                if (definition == null)
                {
                    continue;
                }

                SetBit(pcToPlcBytes, definition.ByteOffset, definition.BitOffset, kvp.Value);
            }
        }

        public static bool GetBit(IReadOnlyList<byte> buffer, int byteIndex, int bitIndex)
        {
            if (byteIndex < 0 || bitIndex < 0 || bitIndex > 7 || byteIndex >= buffer.Count)
            {
                return false;
            }

            return (buffer[byteIndex] & (1 << bitIndex)) != 0;
        }

        public static void SetBit(IList<byte> buffer, int byteIndex, int bitIndex, bool value)
        {
            if (byteIndex < 0 || bitIndex < 0 || bitIndex > 7 || byteIndex >= buffer.Count)
            {
                return;
            }

            if (value)
            {
                buffer[byteIndex] = (byte)(buffer[byteIndex] | (1 << bitIndex));
            }
            else
            {
                buffer[byteIndex] = (byte)(buffer[byteIndex] & ~(1 << bitIndex));
            }
        }
    }
}
