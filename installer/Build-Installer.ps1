param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SelfContained,
    [string]$SatelliteResourceLanguages = "en"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$versionPropsPath = Join-Path $repoRoot "Directory.Build.props"
if (-not (Test-Path $versionPropsPath)) {
    throw "Version metadata file not found: $versionPropsPath"
}

[xml]$versionProps = Get-Content $versionPropsPath
$appVersion = $versionProps.Project.PropertyGroup.Version | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($appVersion)) {
    throw "Could not read <Version> from $versionPropsPath"
}

$publishMode = if ($SelfContained) { "self-contained" } else { "framework-dependent" }
$installerSuffix = if ($SelfContained) { "-SC" } else { "-FD" }
$selfContainedArg = if ($SelfContained) { "true" } else { "false" }
$publishDir = Join-Path $repoRoot "artifacts\publish\$Runtime\$publishMode"
$issFile = Join-Path $PSScriptRoot "WinPanX.iss"

Write-Host "Publishing WinPanX ($Configuration, $Runtime, $publishMode)..." -ForegroundColor Cyan
dotnet publish (Join-Path $repoRoot "src\WinPanX.Agent\WinPanX.Agent.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained $selfContainedArg `
    -p:PublishSingleFile=false `
    -p:SatelliteResourceLanguages=$SatelliteResourceLanguages `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$iscc = Get-Command iscc.exe -ErrorAction SilentlyContinue
if (-not $iscc) {
    $fallbackCandidates = @(
        "C:\Program Files\Inno Setup 6\ISCC.exe",
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
    )

    $fallback = $fallbackCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($fallback) {
        $isccPath = $fallback
    } else {
        throw "ISCC.exe not found. Install Inno Setup 6 and ensure ISCC is on PATH."
    }
} else {
    $isccPath = $iscc.Source
}

Write-Host "Building installer with Inno Setup..." -ForegroundColor Cyan
& $isccPath "/DBuildOutput=$publishDir" "/DInstallerSuffix=$installerSuffix" "/DAppVersion=$appVersion" $issFile

if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compile failed with exit code $LASTEXITCODE"
}

Write-Host "Installer complete: artifacts\\installer\\WinPanX-Setup$installerSuffix.exe" -ForegroundColor Green
