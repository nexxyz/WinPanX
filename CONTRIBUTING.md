# Contributing

## Scope

WinPan X is a Windows-only audio routing/mixing utility. Contributions should
prioritize reliability, low-latency behavior, and predictable runtime behavior.

## Prerequisites

- Windows 11
- .NET 10 SDK
- 8 virtual stereo render endpoints (for runtime validation/testing)

## Local Setup

```powershell
dotnet restore .\WinPanX.sln
dotnet build .\WinPanX.sln -c Debug
```

## Development Rules

- Keep audio callback paths allocation-light and non-blocking.
- Prefer explicit logging for runtime/device/routing failures.
- Preserve fail-fast startup behavior for invalid endpoint/output topology.
- Keep config backward compatible when possible.

## Pull Request Guidelines

- Keep PRs focused and scoped.
- Update `README.md`, `MANUAL.md`, and `ARCHITECTURE.md` when behavior changes.
- Add or update `CHANGELOG.md` entries under `Unreleased`.
- Ensure local build passes:

```powershell
dotnet build .\WinPanX.sln -c Release
```

## Release Versioning

Version metadata is centralized in `Directory.Build.props`.
Before release, update:

- `Version`
- `AssemblyVersion`
- `FileVersion`
- `InformationalVersion`
