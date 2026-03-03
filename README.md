# WinPan X

WinPan X is a Windows 11 tray app that pans each app's audio in stereo based on that app window's horizontal desktop position.

## Most users: install and use
If you just want to use WinPan X, do this:

1. Download the latest installer from [Releases](https://github.com/nexxyz/WinPanX/releases/).
2. Run `WinPanX-Setup-FD.exe` (smaller, requires .NET Desktop Runtime 10 x64) or `WinPanX-Setup-SC.exe` (larger, runtime included).
3. Launch WinPan X from Start Menu.

After install, WinPan X runs in the system tray.

Tray menu:
- `Routing enabled` / `Routing disabled`: enable or disable panning without exiting.
- `Run on startup`: toggle startup at user sign-in.
- `Open manual`: open user manual.
- `Open config`: open active config (`%LocalAppData%\WinPanX\winpanx.json` by default).
- `Open log`: open `winpanx.log`.
- `Exit`: graceful shutdown with session volume restore.

User documentation:
- [MANUAL.md](src/WinPanX.Agent/MANUAL.md)

## Developers: build from source
Use this section only if you want to build, modify, or package WinPan X yourself.

Requirements:
- Windows 11
- .NET 10 SDK
- Inno Setup 6 (only for installer builds)

Build:
```powershell
dotnet restore .\WinPanX.sln
dotnet build .\WinPanX.sln -c Debug
```

Run from source:
```powershell
dotnet run --project .\src\WinPanX.Agent\WinPanX.Agent.csproj
```

Optional custom config path:
```powershell
dotnet run --project .\src\WinPanX.Agent\WinPanX.Agent.csproj -- C:\path\to\winpanx.json
```

List active render devices:
```powershell
dotnet run --project .\src\WinPanX.Agent\WinPanX.Agent.csproj -- --list-render-devices
```

Build installers:
```powershell
powershell -ExecutionPolicy Bypass -File .\installer\Build-Installer.ps1
powershell -ExecutionPolicy Bypass -File .\installer\Build-Installer.ps1 -SelfContained
```

Outputs:
- `artifacts\installer\WinPanX-Setup-FD.exe`
- `artifacts\installer\WinPanX-Setup-SC.exe`

## Configuration
Default config path: `%LocalAppData%\WinPanX\winpanx.json`

```json
{
  "PollIntervalMs": 500,
  "InactiveGraceSeconds": 3,
  "LogRetentionDays": 7,
  "ExcludedProcesses": ["System", "svchost", "WinPanX.Agent"],
  "ActivityPeakThreshold": 0.001
}
```

`LogRetentionDays` controls startup log cleanup:
- `7` means keep the last 7 days (default).
- `0` disables log pruning.

Unknown keys are ignored for backward compatibility.

## How it works
- Detects active render audio sessions.
- Resolves one tracked window per active app runtime (PID + start time).
- Converts window X-position to pan in `[-1..+1]`.
- Applies per-session channel balance through `IChannelAudioVolume`.
- Restores original session channel volumes when disabled or on exit.

Center pan is unity gain (no attenuation).

## Known limitations
- Some apps/sessions do not expose `IChannelAudioVolume`; those sessions cannot be panned.
- Chromium-family apps may emit audio from child processes without windows; WinPan X uses parent-window fallback, which can still fail for some tab/process layouts.
- Only horizontal stereo panning is implemented.

## Project docs
- [ARCHITECTURE.md](ARCHITECTURE.md)
- [CHANGELOG.md](CHANGELOG.md)
- [RELEASE_CHECKLIST.md](RELEASE_CHECKLIST.md)
- [CONTRIBUTING.md](CONTRIBUTING.md)
- [SECURITY.md](SECURITY.md)
- [LICENSE](LICENSE)
