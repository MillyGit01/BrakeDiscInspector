# Communications

The GUI owns the production-cell communication layer. It can run without hardware by using simulation modes, and it is prepared for a Siemens S7-1200 PLC plus one industrial camera provider.

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
    "DbNumber": 1,
    "PollIntervalMs": 100
  }
}
```

PLC DB bit map:

| Signal | Direction | DB byte.bit |
|---|---|---|
| Start inspection | PLC to GUI | DBX0.0 |
| Stop inspection | PLC to GUI | DBX0.1 |
| Part present | PLC to GUI | DBX0.2 |
| Reset error | PLC to GUI | DBX0.3 |
| System ready | GUI to PLC | DBX2.0 |
| Busy | GUI to PLC | DBX2.1 |
| Result OK | GUI to PLC | DBX2.2 |
| Result NG | GUI to PLC | DBX2.3 |
| Error | GUI to PLC | DBX2.4 |

The GUI detects a rising edge on `Start inspection`. If `RequirePartPresent` is enabled, `Part present` must also be true.

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

1. PLC rising edge: `Start inspection`.
2. GUI writes `Busy=true`, clears result bits.
3. Camera acquires an image.
4. GUI loads the image, analyzes masters, and evaluates enabled inspection ROIs.
5. GUI writes `Busy=false` plus `Result OK` or `Result NG`.
6. On failure, GUI writes `Error=true`.

Real FLIR/Cognex acquisition requires the vendor SDK choice and installed native/.NET assemblies before those providers can replace their placeholders.

## UI behavior
- The communications panel can run against the simulated PLC and `Folder` camera without hardware.
- `AutoConnectOnStartup` controls whether the GUI connects automatically.
- `AutoRunInspectionOnTrigger` controls whether a PLC start edge loads the acquired image and evaluates enabled inspection ROIs.
- `RequirePartPresent` requires the PLC `Part present` signal before accepting a start edge.
- Provider changes rebuild the camera client; the `Source` path is only meaningful for `Folder`.
