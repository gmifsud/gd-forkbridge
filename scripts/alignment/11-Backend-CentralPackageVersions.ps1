#Requires -Version 7.0
<# Step 5: add missing central PackageVersions (fixes NU1010 on Platform test projects). Idempotent. #>
[CmdletBinding()] param([switch]$Apply, [switch]$Commit, [switch]$Yes, [switch]$SkipRestore)
Import-Module (Join-Path $PSScriptRoot 'Alignment.Common.psm1') -Force
$d = Get-AlignmentDefaults; Set-AlignmentYes $Yes; $R = $d.BackendWorktree
$props = Join-Path $R 'Directory.Packages.props'
$want = [ordered]@{
  'xunit'                          = '2.4.2'
  'xunit.extensibility.execution'  = '2.4.2'
  'xunit.runner.visualstudio'      = '2.4.5'
  'NSubstitute'                    = '4.3.0'
  'Abp.TestBase'                   = '8.3.1'     # matches Abp 8.3.1 (ISL uses the same)
  'Microsoft.NET.Test.Sdk'         = '17.4.1'    # was 16.5.0
  'Shouldly'                       = '4.1.0'     # was 4.0.0-beta0002
}
$testProjects = @(
  'QiSL.Fabric/Platform/aspnet-core/test/QiSL.Platform.Test.Base/QiSL.Platform.Test.Base.csproj',
  'QiSL.Fabric/Platform/aspnet-core/test/QiSL.Platform.Tests/QiSL.Platform.Tests.csproj',
  'QiSL.Fabric/Platform/aspnet-core/test/QiSL.Platform.GraphQL.Tests/QiSL.Platform.GraphQL.Tests.csproj')
Start-AlignmentLog 'step11-packages'
try {
    Assert-Branch $R $d.BackendBranch; Assert-CleanScope $R @('Directory.Packages.props')
    $text = Get-Content $props -Raw
    $changes = @(); $adds = @()
    foreach ($k in $want.Keys) {
        $m = [regex]::Match($text, '<PackageVersion\s+Include="' + [regex]::Escape($k) + '"\s+Version="([^"]*)"\s*/>')
        if (-not $m.Success) { $changes += "ADD    $k $($want[$k])"; $adds += "    <PackageVersion Include=`"$k`" Version=`"$($want[$k])`" />" }
        elseif ($m.Groups[1].Value -ne $want[$k]) { $changes += "UPDATE $k $($m.Groups[1].Value) -> $($want[$k])"
            if ($Apply) { $text = $text.Replace($m.Value, "<PackageVersion Include=`"$k`" Version=`"$($want[$k])`" />") } }
        else { Write-Ok "$k already $($want[$k])" }
    }
    $changes | ForEach-Object { Write-Plan $_ }
    if (-not $Apply) { Write-Warn2 "dry-run. Re-run with -Apply"; return }
    if ($changes.Count -gt 0) {
        if (-not (Confirm-Step "Write $($changes.Count) change(s) to Directory.Packages.props?")) { return }
        if ($adds) {   # insert before the LAST </ItemGroup>, as a labelled block - surgical diff, no reformatting
            $nl = if ($text -match "`r`n") { "`r`n" } else { "`n" }
            $block = "    <!-- Test packages: central versions (alignment 2026-10-26) -->$nl" + ($adds -join $nl) + $nl
            $i = $text.LastIndexOf('</ItemGroup>'); $text = $text.Insert($i, $block) }
        [IO.File]::WriteAllText($props, $text, [Text.UTF8Encoding]::new($text.StartsWith([char]0xFEFF)))
        Invoke-Git $R add Directory.Packages.props | Out-Null
    }
    if (-not $SkipRestore) { Write-Step "dotnet restore (Platform test projects)"; foreach ($p in $testProjects) { Invoke-Dotnet $R restore $p } }
    Commit-IfRequested $R $Commit "build(cpm): add central versions for xunit, NSubstitute, Abp.TestBase; align Test.Sdk and Shouldly`n`nFixes NU1010 on Platform test projects after 890a282f4 removed per-project versions."
} finally { Stop-AlignmentLog }
