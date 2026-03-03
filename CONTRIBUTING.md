# Contributing

## Scope
WinPan X is a Windows-only per-app stereo panning utility.
Contributions should prioritize reliability, low-latency updates, and predictable restore behavior.

## Prerequisites
- Windows 11
- .NET 10 SDK

## Local setup
```powershell
dotnet restore .\WinPanX.sln
dotnet build .\WinPanX.sln -c Debug
```

## Development rules
- Keep callback/event paths allocation-light and non-blocking.
- Prefer explicit logs for session/window/pan failures.
- Preserve graceful restore behavior when runtime stops.
- Keep config backward compatible when practical.

## Pull request guidelines
- Keep PRs focused.
- Update `README.md`, `MANUAL.md`, and `ARCHITECTURE.md` for behavior changes.
- Update `CHANGELOG.md` under `Unreleased`.
- Ensure local release build passes:

```powershell
dotnet build .\WinPanX.sln -c Release
```

## Release versioning
Version metadata is centralized in `Directory.Build.props`:
- `Version`
- `AssemblyVersion`
- `FileVersion`
- `InformationalVersion`
