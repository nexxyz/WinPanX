# WinPan X

WinPan X is a Windows 11 tray agent that pans each app's audio in stereo based on that app window's horizontal desktop position.

## How it works
- Detects active render audio sessions.
- Resolves one tracked window per active app runtime (PID + start time).
- Converts window X-position to pan in `[-1..+1]`.
- Applies per-session channel balance through `IChannelAudioVolume`.
- Restores original session channel volumes when disabled or on exit.

Center pan is unity gain (no attenuation).

## Requirements
- Windows 11
- .NET 10 SDK for local build
- .NET Desktop Runtime 10 x64 for framework-dependent installer builds

No virtual audio endpoints are required.

## Build
```powershell
dotnet restore .\WinPanX.sln
dotnet build .\WinPanX.sln -c Debug
```

## Run
```powershell
dotnet run --project .\src\WinPanX.Agent\WinPanX.Agent.csproj
```

Optional config path:
```powershell
dotnet run --project .\src\WinPanX.Agent\WinPanX.Agent.csproj -- C:\path\to\winpanx.json
```

List active render devices:
```powershell
dotnet run --project .\src\WinPanX.Agent\WinPanX.Agent.csproj -- --list-render-devices
```

## Configuration
Default path: `%LocalAppData%\WinPanX\winpanx.json`

```json
{
  "PollIntervalMs": 500,
  "InactiveGraceSeconds": 3,
  "ExcludedProcesses": ["System", "svchost", "WinPanX.Agent"],
  "ActivityPeakThreshold": 0.001
}
```

Unknown keys are ignored, so older config files still load.
## Tray menu
- `Routing enabled` / `Routing disabled`: enable or disable panning without exiting.
- `Run on startup`: toggle startup at user sign-in.
- `Open manual`: open `MANUAL.md`.
- `Open config`: open active config (`%LocalAppData%\WinPanX\winpanx.json` by default).
- `Open log`: open `winpanx.log`.
- `Exit`: graceful shutdown with session volume restore.

## Installer (Inno Setup)
Prerequisites:
- Inno Setup 6 (`ISCC.exe` on PATH or installed in default location)

Build framework-dependent installer:
```powershell
powershell -ExecutionPolicy Bypass -File .\installer\Build-Installer.ps1
```

Build self-contained installer:
```powershell
powershell -ExecutionPolicy Bypass -File .\installer\Build-Installer.ps1 -SelfContained
```

Outputs:
- `artifacts\installer\WinPanX-Setup-FD.exe`
- `artifacts\installer\WinPanX-Setup-SC.exe`

Installer features:
- per-user install (`%LocalAppData%\Programs\WinPanX`)
- optional startup checkbox
- start menu shortcut (desktop shortcut optional)
- bootstraps user config to `%LocalAppData%\WinPanX\winpanx.json`

## Known limitations
- Some apps/sessions do not expose `IChannelAudioVolume`; those sessions cannot be panned.
- Chromium-family apps may emit audio from child processes without windows; WinPan X uses parent-window fallback, which can still fail for some tab/process layouts.
- Only horizontal stereo panning is implemented.

## Releases
Installers are published under [Releases](https://github.com/nexxyz/WinPanX/releases/).

## Project docs
- [ARCHITECTURE.md](ARCHITECTURE.md)
- [MANUAL.md](src/WinPanX.Agent/MANUAL.md)
- [CHANGELOG.md](CHANGELOG.md)
- [RELEASE_CHECKLIST.md](RELEASE_CHECKLIST.md)
- [CONTRIBUTING.md](CONTRIBUTING.md)
- [SECURITY.md](SECURITY.md)
- [LICENSE](LICENSE)


