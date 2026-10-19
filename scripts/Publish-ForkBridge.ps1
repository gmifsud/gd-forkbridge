#Requires -Version 7.0
<#
.SYNOPSIS
    Publishes ForkBridge and adds the output folder to PATH when it is not already there.

.DESCRIPTION
    Publishes forkbridge (and forkbridge-web unless -SkipWeb) to -Output, then appends that
    folder to the User or Machine PATH only if an equivalent entry is absent. The current
    session PATH is refreshed so the new tools are usable immediately.

.EXAMPLE
    pwsh scripts/Publish-ForkBridge.ps1 -Output D:\ForkBridge\bin
#>
[CmdletBinding()]
param(
    [string]$Output = 'D:\ForkBridge\bin',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('User', 'Machine')]
    [string]$Scope = 'User',
    [switch]$SkipWeb
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$repo = Split-Path $PSScriptRoot -Parent
$out = [IO.Path]::GetFullPath($Output)

dotnet publish (Join-Path $repo 'src/ForkBridge.Cli') '-c' $Configuration '-o' $out
if (-not $SkipWeb) {
    dotnet publish (Join-Path $repo 'src/ForkBridge.Web') '-c' $Configuration '-o' $out
}

function Normalize-PathEntry([string]$entry) {
    if ([string]::IsNullOrWhiteSpace($entry)) { return '' }
    return $entry.Trim().TrimEnd('\', '/').ToLowerInvariant()
}

$current = [Environment]::GetEnvironmentVariable('Path', $Scope)
$want = Normalize-PathEntry $out
$present = @($current -split ';' | Where-Object { (Normalize-PathEntry $_) -eq $want }).Count -gt 0
if (-not $present) {
    $joined = if ([string]::IsNullOrWhiteSpace($current)) { $out } else { $current.TrimEnd(';') + ';' + $out }
    [Environment]::SetEnvironmentVariable('Path', $joined, $Scope)
    Write-Host "Added '$out' to $Scope PATH."
}
else {
    Write-Host "'$out' is already on $Scope PATH."
}

function Install-CommandShim([string]$Name, [string]$ExePath, [string]$ShimDir) {
    New-Item -ItemType Directory -Force $ShimDir | Out-Null
    $shim = Join-Path $ShimDir "$Name.cmd"
    [IO.File]::WriteAllText($shim, "@echo off`r`n`"$ExePath`" %*`r`n")
    Write-Host "Shim $shim -> $ExePath"
}

# Terminals started before PATH changed still search directories they already inherited.
# A .cmd there is found immediately; a newly appended PATH entry is not.
$shimDir = Join-Path $env:USERPROFILE '.local\bin'
$shimWant = Normalize-PathEntry $shimDir
$shimOnPath = @(([Environment]::GetEnvironmentVariable('Path', 'User'), [Environment]::GetEnvironmentVariable('Path', 'Machine')) -join ';' -split ';' |
    Where-Object { (Normalize-PathEntry $_) -eq $shimWant }).Count -gt 0
if (-not $shimOnPath) {
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $joinedShim = if ([string]::IsNullOrWhiteSpace($userPath)) { $shimDir } else { $userPath.TrimEnd(';') + ';' + $shimDir }
    [Environment]::SetEnvironmentVariable('Path', $joinedShim, 'User')
    Write-Host "Added '$shimDir' to User PATH."
}

Install-CommandShim 'forkbridge' (Join-Path $out 'forkbridge.exe') $shimDir
if (-not $SkipWeb) {
    Install-CommandShim 'forkbridge-web' (Join-Path $out 'forkbridge-web.exe') $shimDir
}

$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')

# Let Explorer-spawned processes pick up the registry PATH. Already-open terminals do not.
Add-Type -Namespace ForkBridge -Name Native -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
'@
[UIntPtr]$broadcastResult = [UIntPtr]::Zero
[void][ForkBridge.Native]::SendMessageTimeout([IntPtr]0xffff, 0x001A, [UIntPtr]::Zero, 'Environment', 2, 5000, [ref]$broadcastResult)

Write-Host "Published to $out."
Write-Host "This terminal can run 'forkbridge' via $shimDir. A terminal opened before that folder was on PATH still needs a new window."
