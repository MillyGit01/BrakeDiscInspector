# Communications

The GUI owns the production-cell communication layer. It can run without hardware by using simulation modes, and it is prepared for a Siemens S7-1200 PLC plus one industrial camera provider.

The PLC contract follows the `Mesa_Giratoria` project used as reference:

- `DB150` / `PC_PLC`: commands and state written by the GUI to the PLC.
- `DB151` / `PLC_PC`: state, handshake, and camera result bits read by the GUI from the PLC.
- `DB8` / `DB_Diag_AutoCam1_360`: diagnostic DB reserved for AutoCam1 360 troubleshooting.

## PLC

Supported modes:
- `Simulation`: in-memory PLC for development without wiring.
- `S7`: Siemens S7-1200 via S7.NetPlus.

Default PLC settings live in `gui/BrakeDiscInspector_GUI_ROI/appsettings.json` and `config/appsettings.json`:

```json
"Comms": {
  "Plc": {
    "Mode": "Simulation",
    "IpAddress": "192.168.0.10",
    "Rack": 0,
    "Slot": 1,
    "DbNumber": 150,
    "PlcToPcDbNumber": 151,
    "DiagnosticDbNumber": 8,
    "PollIntervalMs": 100
  }
}
```

`DbNumber` is kept for backward compatibility and means the PC-to-PLC DB. In the Mesa Giratoria layout that is `DB150`.

PLC-to-GUI status map (`DB151`):

| Signal | DB byte.bit | Notes |
|---|---|---|
| PLC ready / part present | DBX0.0 | Used by `RequirePartPresent`. |
| PLC alarm | DBX0.1 | PLC alarm state. |
| Rotating | DBX0.2 | Rotary axis in motion. |
| Gripper open | DBX0.3 | Gripper open feedback. |
| Gripper closed | DBX0.4 | Gripper closed feedback. |
| Reset from PLC | DBX0.5 | Clears GUI alarm output. |
| Abort from PLC | DBX0.6 | Cancels the current acquisition cycle. |
| Legacy capture 1 | DBX0.7 | Also accepted as a start edge for compatibility. |
| Start inspection / TriggerAck Cam1 | DBX1.0 | Main rising edge used to acquire an image. |
| PLC inspection complete Cam1 | DBX1.1 | Mesa Giratoria camera completion feedback. |
| PLC OK Cam1 | DBX1.2 | PLC-side camera result. |
| PLC NG Cam1 | DBX1.3 | PLC-side camera result. |
| Data saved | DBX1.4 | Mesa Giratoria saved-data flag. |
| Manual / auto | DBX1.5 | PLC mode feedback. |
| AutoCam1 active | DBX1.6 | AutoCam1 360 sequence active. |
| AutoCam1 error | DBX1.7 | AutoCam1 360 sequence error. |

GUI-to-PLC command map (`DB150`):

| Signal | DB byte.bit | Notes |
|---|---|---|
| App ready (`EstadoAPP`) | DBX0.0 | Set when the GUI connects. |
| App alarm | DBX0.1 | Set when the GUI cycle fails. |
| App busy / `Estado` | DBX0.2 | Set while acquisition or inspection is running. |
| Open gripper | DBX0.3 | Manual gripper command. |
| Close gripper | DBX0.4 | Manual gripper command. |
| New position | DBX0.5 | Manual position command. |
| Reset command | DBX0.6 | Reset command to PLC. |
| Abort command | DBX0.7 | Abort command to PLC. |
| Trigger 1 | DBX1.0 | Mesa Giratoria trigger bit. |
| BDI inspection complete (`Trigger2`) | DBX1.1 | Written when BDI finishes evaluating the image. |
| BDI result OK (`Trigger3`) | DBX1.2 | Written when BDI result is OK. |
| Rotate command | DBX1.3 | Mesa Giratoria rotate command. |
| Manual / auto command | DBX1.4 | Mesa Giratoria mode command. |
| AutoCam1 360 start (`RESERVA2`) | DBX1.5 | Pulsed for 150 ms by the AutoCam1 button. |
| BDI result NG (`RESERVA3`) | DBX1.6 | Written when BDI result is NG. |

The GUI detects a rising edge on `Start inspection / TriggerAck Cam1`, with `Legacy capture 1` accepted as a compatibility start edge. If `RequirePartPresent` is enabled, `PLC ready / part present` must also be true.

The S7 client writes only the first two boolean bytes of `DB150` for the command map. It does not write the Mesa Giratoria `GiroPinza` DInt at byte 4 or `Velocidad` byte at 8, so motion setpoints remain owned by the PLC/HMI workflow until they are explicitly wired in BDI.

## Camera

Supported providers:
- `Disabled`: no acquisition.
- `Folder`: development provider that copies the next image from a folder into the capture output directory.
- `FlirBlackfly`: placeholder for FLIR Spinnaker SDK wiring.
- `Cognex`: placeholder for the selected Cognex SDK/protocol wiring.

`Folder` is the safe no-hardware path for integration testing. The GUI only enables the camera `Source` selector and browse button when this provider is active; for FLIR/Cognex that field is not used. Set:

```json
"Camera": {
  "Provider": "Folder",
  "Source": "C:\\path\\to\\sample-images",
  "OutputDirectory": "C:\\path\\to\\captures"
}
```

## Cycle

When `AutoRunInspectionOnTrigger` is true, the cycle is:

1. PLC rising edge: `Start inspection / TriggerAck Cam1` or legacy `Captura1`.
2. GUI writes `Busy=true`, clears result bits.
3. Camera acquires an image.
4. GUI loads the image, analyzes masters, and evaluates enabled inspection ROIs.
5. GUI writes `Busy=false`, `BDI inspection complete=true`, plus `BDI result OK` or `BDI result NG`.
6. On failure, GUI writes `Error=true`.

Real FLIR/Cognex acquisition requires the vendor SDK choice and installed native/.NET assemblies before those providers can replace their placeholders.

## UI behavior
- The communications panel can run against the simulated PLC and `Folder` camera without hardware.
- `AutoConnectOnStartup` controls whether the GUI connects automatically.
- `AutoRunInspectionOnTrigger` controls whether a PLC start edge loads the acquired image and evaluates enabled inspection ROIs.
- `RequirePartPresent` requires the PLC `Part present` signal before accepting a start edge.
- Provider changes rebuild the camera client; the `Source` path is only meaningful for `Folder`.
