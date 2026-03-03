param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$SatelliteResourceLanguages = "en",
    [string]$Version,
    [switch]$SkipSelfContained
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$buildInstallerScript = Join-Path $repoRoot "installer\Build-Installer.ps1"
$changelogPath = Join-Path $repoRoot "CHANGELOG.md"
$versionPropsPath = Join-Path $repoRoot "Directory.Build.props"
$releaseOutDir = Join-Path $repoRoot "artifacts\release"

if (-not (Test-Path $buildInstallerScript)) {
    throw "Installer build script not found: $buildInstallerScript"
}

if (-not (Test-Path $changelogPath)) {
    throw "CHANGELOG.md not found: $changelogPath"
}

if (-not (Test-Path $versionPropsPath)) {
    throw "Directory.Build.props not found: $versionPropsPath"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$propsXml = Get-Content $versionPropsPath
    $Version = $propsXml.Project.PropertyGroup.Version | Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    throw "Could not resolve release version from parameter or Directory.Build.props."
}

function Get-ChangelogSection {
    param(
        [string]$Path,
        [string]$TargetVersion
    )

    $lines = Get-Content $Path
    $headerPattern = "^## \[$([Regex]::Escape($TargetVersion))\](?:\s*-.*)?$"
    $startIndex = -1

    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match $headerPattern) {
            $startIndex = $i
            break
        }
    }

    if ($startIndex -lt 0) {
        throw "Version [$TargetVersion] was not found in CHANGELOG.md. Add a section like '## [$TargetVersion] - YYYY-MM-DD'."
    }

    $endIndex = $lines.Count
    for ($i = $startIndex + 1; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^## \[') {
            $endIndex = $i
            break
        }
    }

    return ($lines[$startIndex..($endIndex - 1)] -join [Environment]::NewLine).Trim()
}

Write-Host "Preparing WinPan X release v$Version" -ForegroundColor Cyan

Write-Host "Building framework-dependent installer..." -ForegroundColor Cyan
& powershell -ExecutionPolicy Bypass -File $buildInstallerScript `
    -Configuration $Configuration `
    -Runtime $Runtime `
    -SatelliteResourceLanguages $SatelliteResourceLanguages

if ($LASTEXITCODE -ne 0) {
    throw "Framework-dependent installer build failed with exit code $LASTEXITCODE"
}

if (-not $SkipSelfContained) {
    Write-Host "Building self-contained installer..." -ForegroundColor Cyan
    & powershell -ExecutionPolicy Bypass -File $buildInstallerScript `
        -Configuration $Configuration `
        -Runtime $Runtime `
        -SatelliteResourceLanguages $SatelliteResourceLanguages `
        -SelfContained

    if ($LASTEXITCODE -ne 0) {
        throw "Self-contained installer build failed with exit code $LASTEXITCODE"
    }
}

$section = Get-ChangelogSection -Path $changelogPath -TargetVersion $Version

New-Item -ItemType Directory -Path $releaseOutDir -Force | Out-Null
$notesPath = Join-Path $releaseOutDir "ReleaseNotes-v$Version.md"

$assetLines = @(
    "- artifacts\installer\WinPanX-Setup-FD.exe"
)

if (-not $SkipSelfContained) {
    $assetLines += "- artifacts\installer\WinPanX-Setup-SC.exe"
}

$notesLines = @(
    "# WinPan X v$Version",
    "",
    "## Assets"
)
$notesLines += $assetLines
$notesLines += ""
$notesLines += "## Changelog"
$notesLines += $section

$notesContent = $notesLines -join [Environment]::NewLine

Set-Content -Path $notesPath -Value $notesContent -Encoding UTF8

$tag = "v$Version"
Write-Host ""
Write-Host "Release notes written: $notesPath" -ForegroundColor Green
Write-Host "Suggested GitHub release commands:" -ForegroundColor Green
Write-Host "  git tag $tag"
Write-Host "  git push origin $tag"
Write-Host "  gh release create $tag artifacts\installer\WinPanX-Setup-FD.exe" `
    -NoNewline
if (-not $SkipSelfContained) {
    Write-Host " artifacts\installer\WinPanX-Setup-SC.exe" -NoNewline
}
Write-Host " --notes-file `"$notesPath`""
