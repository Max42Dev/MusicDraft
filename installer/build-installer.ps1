<#
.SYNOPSIS
    Publishes MusicDraft (self-contained win-x64) and builds the Inno Setup installer.

.EXAMPLE
    .\installer\build-installer.ps1 -Version 0.1.0

.EXAMPLE
    # Reuse an existing publish output (skip the dotnet publish step)
    .\installer\build-installer.ps1 -Version 0.1.0 -SkipPublish
#>
[CmdletBinding()]
param(
    [string]$Version = "0.1.0",
    [string]$Configuration = "Release",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\MusicDraft.App\MusicDraft.App.csproj"
$publishDir = Join-Path $root "src\MusicDraft.App\bin\$Configuration\net10.0-windows\win-x64\publish"
$iss = Join-Path $PSScriptRoot "MusicDraft.iss"

if (-not $SkipPublish) {
    Write-Host "Publishing $project ($Configuration, win-x64, self-contained)..." -ForegroundColor Cyan
    dotnet publish $project `
        -c $Configuration `
        -r win-x64 `
        --self-contained true `
        -p:PublishReadyToRun=true `
        -p:DebugType=none `
        -p:AllowedReferenceRelatedFileExtensions=none `
        -p:Version=$Version `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
}

if (-not (Test-Path (Join-Path $publishDir "MusicDraft.exe"))) {
    throw "Publish output not found at '$publishDir'. Run without -SkipPublish first."
}

# Locate the Inno Setup compiler.
$iscc = $null
$cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if ($cmd) { $iscc = $cmd.Source }
if (-not $iscc) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    $iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) {
    throw "Inno Setup 6 (ISCC.exe) not found. Install it from https://jrsoftware.org/isdl.php"
}

Write-Host "Compiling installer with $iscc..." -ForegroundColor Cyan
& $iscc "/DAppVersion=$Version" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

$output = Join-Path $root "artifacts\MusicDraftSetup-$Version.exe"
Write-Host "Done: $output" -ForegroundColor Green
