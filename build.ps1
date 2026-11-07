#Requires -Version 7.0
<#
.SYNOPSIS
    Builds everything: the Angular UI, then the .NET solution. Optionally runs the tests.

.DESCRIPTION
    Order matters: the UI builds into src/ForkBridge.Web/wwwroot, which the .NET build then copies next to the
    forkbridge-web binaries. `npm ci` only runs when node_modules is missing or package-lock.json changed since the
    last install. Stops at the first failing step and prints a timing summary.

.EXAMPLE
    ./build.ps1                     # UI + .NET (Debug)
    ./build.ps1 -Test               # ... then UI unit tests and dotnet test
    ./build.ps1 -Configuration Release -SkipUi
    ./build.ps1 -Test -TestProject tests/ForkBridge.Merge.Tests
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$SkipUi,
    [switch]$SkipDotnet,
    [switch]$Test,
    # Limit `dotnet test` to one project (path relative to the repo root); default: the whole solution.
    [string]$TestProject,
    # Force `npm ci` even when node_modules looks current.
    [switch]$CleanInstall
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$repo = $PSScriptRoot
trap { Write-Host "`nBuild failed: $($_.Exception.Message)" -ForegroundColor Red; exit 1 }
$ui = Join-Path $repo 'ui'
$timings = [ordered]@{}

function Step([string]$Name, [scriptblock]$Body) {
    Write-Host "`n==> $Name" -ForegroundColor Cyan
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Push-Location $repo
    try { & $Body }
    catch { Write-Host "FAILED: $Name" -ForegroundColor Red; throw }
    finally { Pop-Location; $timings[$Name] = $sw.Elapsed }
}

function Require([string]$Exe, [string]$Hint) {
    if (-not (Get-Command $Exe -ErrorAction SilentlyContinue)) { throw "$Exe not found on PATH. $Hint" }
}

if (-not $SkipUi) {
    Require 'npm' 'Install Node.js >= 20.19 (see README).'
    $lock = Join-Path $ui 'package-lock.json'
    $modules = Join-Path $ui 'node_modules'
    # npm writes node_modules/.package-lock.json on install; fall back to the folder itself if it is absent.
    $stamp = @((Join-Path $modules '.package-lock.json'), $modules) | Where-Object { Test-Path $_ } | Select-Object -First 1
    $needInstall = $true
    if (-not $CleanInstall -and $stamp -and (Test-Path (Join-Path $modules '@angular/cli'))) {
        $needInstall = (Get-Item -Force $lock).LastWriteTimeUtc -gt (Get-Item -Force $stamp).LastWriteTimeUtc
    }
    if ($needInstall) { Step 'UI: npm ci' { npm --prefix $ui ci --no-audit --no-fund } }
    else { Write-Host "`n==> UI: npm ci (skipped: node_modules is current; -CleanInstall to force)" -ForegroundColor DarkGray }
    Step 'UI: build -> src/ForkBridge.Web/wwwroot' { npm --prefix $ui run build }
}

if (-not $SkipDotnet) {
    Require 'dotnet' 'Install the .NET SDK pinned in global.json.'
    Step ".NET: build ($Configuration)" { dotnet build (Join-Path $repo 'ForkBridge.slnx') -c $Configuration --nologo }
}

if ($Test) {
    if (-not $SkipUi) { Step 'UI: unit tests' { npm --prefix $ui test } }
    if (-not $SkipDotnet) {
        $target = if ($TestProject) { Join-Path $repo $TestProject } else { Join-Path $repo 'ForkBridge.slnx' }
        Step ".NET: test ($(if ($TestProject) { $TestProject } else { 'solution' }))" { dotnet test $target -c $Configuration --no-build --nologo }
    }
}

Write-Host "`nDone." -ForegroundColor Green
$timings.GetEnumerator() | ForEach-Object { '{0,-48} {1,8:mm\:ss}' -f $_.Key, $_.Value } | Write-Host
Write-Host ('{0,-48} {1,8:mm\:ss}' -f 'Total', [TimeSpan]::FromTicks(($timings.Values | Measure-Object Ticks -Sum).Sum))
