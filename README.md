# WinPan X

Windows tray background tool for app-to-virtual-endpoint routing and stereo spatial mixing.

## Current State
- Architecture and contracts are in place.
- Agent runtime orchestration scaffold is in place.
- Real CoreAudio implementations are active for:
  - endpoint discovery/validation and default-output monitoring (`CoreAudioEndpointCatalog`)
  - session polling, per-process aggregation, and window-based pan tracking (`CoreAudioAppTracker`)
  - per-process routing (`AudioPolicyRouter`) via undocumented policy APIs
  - slot loopback capture, pan/mix/limit, and WASAPI output rendering (`WasapiMixer`)
  - tray host with menu actions (`Routing enabled/disabled`, `Run on startup`, `Open manual`, `Open config`, `Open log`, `Exit`)
- Known MVP limits:
  - no resampling between slot sample rates and output sample rate
  - undocumented routing APIs may be unavailable on some Windows builds (graceful `NotSupported` fallback)
  - overflow slot cannot pan per app independently
 
For releases, installers are available under ["Releases"](https://github.com/nexxyz/WinPanX/releases/).

## Requirements
- Windows 11
- 8 pre-existing virtual stereo render endpoints that share a name prefix
  - example: `Line 1 (Virtual Audio Cable)` .. `Line 8 (Virtual Audio Cable)` with `VirtualEndpointNamePrefix = "Line "`
  - example: `WinPanX Slot 01` .. `WinPanX Slot 08` with `VirtualEndpointNamePrefix = "WinPanX Slot "`
- If you want to build: .NET 10 SDK (required to build `net10.0-windows` project)

## Build
```powershell
dotnet restore .\WinPanX.sln
dotnet build .\WinPanX.sln -c Debug
```

## Run
If you're not building it, just use the installer and configure "Run on startup".

```powershell
dotnet run --project .\src\WinPanX.Agent\WinPanX.Agent.csproj
```

The app starts in the system tray.
The process exits at startup if the required virtual endpoints are not found or output resolves to a virtual slot endpoint (fail-fast by design).

Tray menu:
- `Routing enabled` / `Routing disabled`: toggles processing without exiting
- `Run on startup`: toggles app auto-start at user sign-in
- `Open manual`: opens `MANUAL.md`
- `Open config`: opens active config (`%LocalAppData%\WinPanX\winpanx.json` by default)
- `Open log`: opens `winpanx.log`
- `Exit`: graceful shutdown with route reset to system default

Optional config path argument:
```powershell
dotnet run --project .\src\WinPanX.Agent\WinPanX.Agent.csproj -- C:\path\to\winpanx.json
```

## Installer (Inno Setup)
Prerequisites:
- Inno Setup 6 (`ISCC.exe` available on PATH or installed in default location)
- .NET Desktop Runtime 10 x64 on target machines when using framework-dependent installer build (default)

Build installer (default: framework-dependent, smaller package):
```powershell
powershell -ExecutionPolicy Bypass -File .\installer\Build-Installer.ps1
```

Build installer (self-contained, no runtime prerequisite, larger package):
```powershell
powershell -ExecutionPolicy Bypass -File .\installer\Build-Installer.ps1 -SelfContained
```

Output:
- `artifacts\installer\WinPanX-Setup-FD.exe` (framework-dependent)
- `artifacts\installer\WinPanX-Setup-SC.exe` (self-contained)

Installer features:
- per-user installation (`%LocalAppData%\Programs\WinPanX`)
- optional `Run on startup` checkbox
- start menu shortcut (desktop shortcut optional)
- user config bootstrapped to `%LocalAppData%\WinPanX\winpanx.json`

## Publish Checklist
- Bump version in `Directory.Build.props` (`Version`, `AssemblyVersion`, `FileVersion`) before release tagging.
- Verify `src\WinPanX.Agent\winpanx.json` remains a generic template (`OutputDeviceId = "default"`).
- Ensure generated folders are not committed (`artifacts/`, `**/bin/`, `**/obj/`).
- Run a final release build before tagging:
```powershell
dotnet build .\WinPanX.sln -c Release
```

Repository cleanup helper:
```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Clean-Generated.ps1 -IncludeArtifacts
```

One-shot release prep (build installers + generate notes from `CHANGELOG.md`):
```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Prepare-Release.ps1
```

Quick variant (skip self-contained build):
```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Prepare-Release.ps1 -SkipSelfContained
```

## Project Docs
- `ARCHITECTURE.md` - runtime architecture and constraints
- `src\WinPanX.Agent\MANUAL.md` - user-facing manual
- `CHANGELOG.md` - release history
- `RELEASE_CHECKLIST.md` - release process checklist
- `scripts\Prepare-Release.ps1` - one-shot release helper
- `CONTRIBUTING.md` - contribution workflow
- `SECURITY.md` - security reporting policy
- `LICENSE` - project license

List active render devices and endpoint IDs:
```powershell
dotnet run --project .\src\WinPanX.Agent\WinPanX.Agent.csproj -- --list-render-devices
```

