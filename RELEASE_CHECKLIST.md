# Release Checklist

## 1. Prepare
- [ ] Clean generated outputs (`artifacts/`, `bin/`, `obj/`) from working tree.
- [ ] Update `Directory.Build.props` version fields.
- [ ] Update `CHANGELOG.md`:
  - [ ] Move `Unreleased` entries into new version section with date.
  - [ ] Add a fresh empty `Unreleased` section.

## 2. Validate
- [ ] Restore and build:

```powershell
dotnet restore .\WinPanX.sln
dotnet build .\WinPanX.sln -c Release
```

- [ ] Smoke test runtime:
  - [ ] App starts to tray.
  - [ ] Routing toggle works.
  - [ ] Run-on-startup toggle works.
  - [ ] Open manual/config/log menu items work.
  - [ ] Exit restores modified session channel volumes.

## 3. Package
- [ ] Optional one-shot path:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Prepare-Release.ps1
```

- [ ] Confirm generated notes file:
  - [ ] `artifacts\release\ReleaseNotes-vX.Y.Z.md`

- [ ] Build framework-dependent installer:

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\Build-Installer.ps1
```

- [ ] Optional: build self-contained installer:

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\Build-Installer.ps1 -SelfContained
```

- [ ] Verify installer metadata and icon in file properties.

## 4. Publish
- [ ] Tag release (`vX.Y.Z`).
- [ ] Create GitHub release notes from `CHANGELOG.md`.
- [ ] Attach installer artifacts:
  - [ ] `WinPanX-Setup-FD.exe`
  - [ ] `WinPanX-Setup-SC.exe` (if built)
