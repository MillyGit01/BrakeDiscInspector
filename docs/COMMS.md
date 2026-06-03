# Communications

The GUI owns the production-cell communication layer. It can run without hardware by using simulation modes, and it is prepared for a Siemens S7-1200 PLC plus one industrial camera provider.

The PLC contract follows the `Mesa_Giratoria` project used as reference:

- `DB150` / `PC_PLC`: commands and state written by the GUI to the PLC.
- `DB151` / `PLC_PC`: state, handshake, and camera result bits read by the GUI from the PLC.
- `DB8` / `DB_Diag_AutoCam1_360`: diagnostic DB reserved for AutoCam1 360 troubleshooting.

The FLIR Blackfly S `BFS-PGE-120S4M-CS` is a mono GigE Vision / GenICam camera. It is not treated as a native PROFINET device by the GUI. The intended production topology is:

```text
BrakeDiscInspector <-> Siemens PLC: S7 over Ethernet / PROFINET network
BrakeDiscInspector <-> FLIR camera: Spinnaker / GigE Vision
PLC -> camera trigger: PLC output or PROFINET remote I/O wired to the camera trigger input
```

## PLC

Supported modes:
- `Simulation`: in-memory PLC for development without wiring.
- `S7`: Siemens S7-1200 via S7.NetPlus.

Default PLC settings live in `gui/BrakeDiscInspector_GUI_ROI/appsettings.json` and `config/appsettings.json`:

```json
"Comms": {
  "Plc": {
    "Mode": "Simulation",
    "IpAddress": "192.168.0.1",
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

The communications panel can edit these PLC fields directly. Use `Save comms` to persist the complete communications settings to:

```text
%LOCALAPPDATA%\BrakeDiscInspector\appsettings.user.json
```

That user config is loaded after the packaged `appsettings.json`, so saved IP/DB/camera changes survive Visual Studio rebuilds.

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

The `PLC trigger` button in the communications panel manually pulses `DB150.DBX1.0` (`Trigger 1`) for 150 ms. The PLC must translate that bit into the camera trigger action.

## Camera

Supported providers:
- `Disabled`: no acquisition.
- `Folder`: development provider that copies the next image from a folder into the capture output directory.
- `FlirBlackfly`: FLIR/Teledyne Blackfly S acquisition through Spinnaker software trigger.
- `Cognex`: placeholder for the selected Cognex SDK/protocol wiring.

`Folder` is the safe no-hardware path for integration testing. The GUI enables the camera `Source` selector for `Folder` and `FlirBlackfly`:

- For `Folder`, use the `Browse` button and point `Source` to a folder of sample images.
- For `FlirBlackfly`, `Source` is optional. Leave it empty to use the first detected Blackfly, or set it to the camera serial/IP. The current validated camera is `BFS-PGE-120S4M-CS`, serial `25103916`, IP `192.168.1.15`.

```json
"Camera": {
  "Provider": "Folder",
  "Source": "C:\\path\\to\\sample-images",
  "OutputDirectory": "C:\\path\\to\\captures"
}
```

For the current Blackfly software-trigger path:

```json
"Camera": {
  "Provider": "FlirBlackfly",
  "Source": "25103916",
  "OutputDirectory": "C:\\path\\to\\captures"
}
```

Requirements and behavior:

- Install Teledyne Spinnaker 4.3.x. The GUI loads `SpinnakerNET_v140.dll` dynamically from `C:\Program Files\Teledyne\Spinnaker\bin64\vs2015\` or from `BDI_SPINNAKER_NET_DLL`.
- Close SpinView before connecting from BDI; the camera can be locked by another Spinnaker client while it is acquiring.
- The client configures `AcquisitionMode=SingleFrame`, `TriggerSource=Software`, `TriggerSelector=FrameStart`, and `TriggerMode=On`. Each `Acquire` starts acquisition, executes one software trigger, reads one image, ends acquisition, converts the frame to `Mono8`, and saves it as a PNG file in `OutputDirectory`.
- PLC-driven hardware trigger is intentionally left for the later wiring phase.

## Cycle

When `AutoRunInspectionOnTrigger` is true, the cycle is:

1. PLC rising edge: `Start inspection / TriggerAck Cam1` or legacy `Captura1`.
2. GUI writes `Busy=true`, clears result bits.
3. Camera acquires an image.
4. GUI loads the image, analyzes masters, and evaluates enabled inspection ROIs.
5. GUI writes `Busy=false`, `BDI inspection complete=true`, plus `BDI result OK` or `BDI result NG`.
6. On failure, GUI writes `Error=true`.

Real Cognex acquisition requires the selected Cognex SDK/protocol adapter before that provider can replace its placeholder.

## UI behavior
- The communications panel can run against the simulated PLC and `Folder` camera without hardware.
- `AutoConnectOnStartup` controls whether the GUI connects automatically.
- `AutoRunInspectionOnTrigger` controls whether a PLC start edge loads the acquired image and evaluates enabled inspection ROIs.
- `RequirePartPresent` requires the PLC `Part present` signal before accepting a start edge.
- Provider changes rebuild the camera client; the `Source` field is meaningful for `Folder` and optional for `FlirBlackfly`.
