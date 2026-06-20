#requires -Version 7.0
<#
.SYNOPSIS
    Build CharM for Mac Catalyst (x64, arm64, or universal). Requires macOS host.

.DESCRIPTION
    Mac Catalyst cannot be cross-compiled from Windows or Linux — Apple's
    toolchain (Xcode) is required for linking. Run this on macOS with .NET 10
    SDK and Xcode 26+ installed.

    Publishes CharM.Maui for net10.0-maccatalyst and packages the resulting
    .app bundle into scripts/out/charm-macos-<arch>.zip using `ditto` (which
    preserves macOS metadata and resource forks — `zip` / Compress-Archive
    do not).

    Default is the universal (fat) build with both maccatalyst-x64 and
    maccatalyst-arm64 slices, matching the csproj's RuntimeIdentifiers and
    the Mac App Store requirement that any arm64 submission also include x64.

.PARAMETER Configuration
    Debug or Release. Default: Release.

.PARAMETER Arch
    Target architecture: x64, arm64, or universal. Default: universal.
    Single-arch builds publish ~half the size at the cost of not running
    natively on the other CPU family.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]         [string]$Configuration = 'Release',
    [ValidateSet('x64','arm64','universal')] [string]$Arch          = 'universal'
)

$ErrorActionPreference = 'Stop'

if (-not $IsMacOS) {
    Write-Error @"
Mac Catalyst cannot be cross-compiled. Run this script on macOS with:
  - .NET 10 SDK (the repo's global.json pins 10.0.100 / latestFeature)
  - Xcode 26 or higher (Mac Catalyst 15.0 minimum)
  - PowerShell 7 (`brew install --cask powershell`)
"@
    exit 1
}

$repo = (Resolve-Path "$PSScriptRoot/..").Path
$out  = "$repo/scripts/out"
New-Item -ItemType Directory -Force -Path $out | Out-Null

# Workload check (skip install if already present — on macOS with Homebrew dotnet
# this works without sudo, but checking first is still nicer than reinstalling)
$hasWorkload = (& dotnet workload list 2>$null) -match '^\s*maui-maccatalyst\s'
if (-not $hasWorkload) {
    Write-Error @"
The 'maui-maccatalyst' workload is not installed. Run once:
    sudo dotnet workload install maui-maccatalyst
or use the helper:
    sudo pwsh -File $PSScriptRoot/install-workloads.ps1 -Workloads maui-maccatalyst
"@
    exit 1
}

# Map -Arch to RID list. For universal we let the csproj's RuntimeIdentifiers
# property (maccatalyst-x64;maccatalyst-arm64) drive the multi-slice publish;
# for single-arch we override BOTH RuntimeIdentifier (singular) and
# RuntimeIdentifiers (plural) so MSBuild doesn't try to build the other slice.
$publishArgs = @(
    "$repo/src/CharM.Maui/CharM.Maui.csproj"
    '-f', 'net10.0-maccatalyst'
    '-c', $Configuration
    '-p:CreatePackage=false'
    '-p:TrimMode=copy'
    '-p:UseSharedCompilation=false'
)
switch ($Arch) {
    'universal' { Write-Host "==> Publishing CharM.Maui for net10.0-maccatalyst (universal: x64 + arm64)" }
    default     {
        $rid = "maccatalyst-$Arch"
        Write-Host "==> Publishing CharM.Maui for net10.0-maccatalyst ($rid only)"
        $publishArgs += "-p:RuntimeIdentifier=$rid"
        $publishArgs += "-p:RuntimeIdentifiers=$rid"
    }
}

dotnet publish @publishArgs
if ($LASTEXITCODE -ne 0) { throw "Mac Catalyst publish failed" }

Write-Host "==> Locating .app bundle"
$publishRoot = "$repo/src/CharM.Maui/bin/$Configuration/net10.0-maccatalyst"
$appBundle = Get-ChildItem $publishRoot -Recurse -Filter '*.app' -Directory `
    | Sort-Object FullName -Descending `
    | Select-Object -First 1
if (-not $appBundle) {
    throw "No .app bundle found under $publishRoot — Mac Catalyst publish may have produced an unexpected layout"
}
Write-Host "    Found: $($appBundle.FullName)"

$zip = "$out/charm-macos-$Arch.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

Write-Host "==> Packaging with ditto -> $zip"
# --sequesterRsrc: store resource forks in dedicated AppleDouble files
# --keepParent:    include the .app folder itself as the zip's top-level entry
& ditto -c -k --sequesterRsrc --keepParent $appBundle.FullName $zip
if ($LASTEXITCODE -ne 0) { throw "ditto packaging failed" }

$sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "==> Built: $zip ($sizeMb MB)"
