# WinPanX Manual

## What it does
- Detects apps currently producing audio.
- Tracks app window horizontal position.
- Pans each compatible app session left/right in real time.
- Restores original session channel volumes when disabled or exited.

## Tray menu
- `Routing enabled` / `Routing disabled`: enable or disable panning without exiting.
- `Run on startup`: toggle launch at sign-in.
- `Open manual`: open this file.
- `Open config`: open active `winpanx.json`.
- `Open log`: open `winpanx.log`.
- `Exit`: graceful shutdown with channel-volume restore.

## Config path
- Default: `%LocalAppData%\WinPanX\winpanx.json`
- Custom: pass config path as first argument

## Config keys
```json
{
  "PollIntervalMs": 500,
  "InactiveGraceSeconds": 3,
  "ExcludedProcesses": ["System", "svchost", "WinPanX.Agent"],
  "ActivityPeakThreshold": 0.001
}
```

## Log path
- Same folder as active config file.

## Notes
- No virtual audio endpoints are required.
- Some apps cannot be panned if their session does not expose channel-volume control.
- Browser tabs may sometimes map to parent-process windows rather than exact tab window context.
