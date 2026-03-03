param(
    [switch]$IncludeArtifacts
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

Write-Host "Removing bin/ and obj/ folders..." -ForegroundColor Cyan
Get-ChildItem -Path . -Directory -Recurse -Force |
    Where-Object { $_.Name -in @("bin", "obj") } |
    ForEach-Object {
        Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
    }

if ($IncludeArtifacts -and (Test-Path ".\artifacts")) {
    Write-Host "Removing artifacts/..." -ForegroundColor Cyan
    Remove-Item ".\artifacts" -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Generated outputs cleaned." -ForegroundColor Green
