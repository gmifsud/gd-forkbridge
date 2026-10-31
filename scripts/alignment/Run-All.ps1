#Requires -Version 7.0
<# Orchestrator. Runs a phase in order and stops at the first non-zero step. Always dry-run unless -Apply. #>
[CmdletBinding()] param([Parameter(Mandatory)][ValidateSet('Backend','ISL','Fitness')][string]$Phase, [switch]$Apply, [switch]$Commit, [switch]$Yes)
$common = @{}; if ($Apply) { $common.Apply = $true }; if ($Commit) { $common.Commit = $true }; if ($Yes) { $common.Yes = $true }
$steps = switch ($Phase) {
  'Backend' { @('00-Preflight.ps1','10-Backend-Revert-StringOverloads.ps1','11-Backend-CentralPackageVersions.ps1','12-Backend-WFSeeder-ForkHook.ps1','13-Backend-ProcessFlowRuleCode.ps1','14-Backend-Verify.ps1') }
  'ISL'     { @('20-ISL-Add-ForkHook.ps1','21-ISL-Merge-MigrationBase.ps1','22-ISL-Import-Upstream.ps1','23-ISL-Remove-StaleOverlayFiles.ps1','24-ISL-Build.ps1','25-ISL-Housekeeping.ps1','30-Fitness-ForkOnlyAdds.ps1') }
  'Fitness' { @('30-Fitness-ForkOnlyAdds.ps1') }
}
foreach ($s in $steps) {
    Write-Host "`n################ $s ################" -ForegroundColor Magenta
    $path = Join-Path $PSScriptRoot $s
    $params = (Get-Command $path).Parameters.Keys
    $splat = @{}; foreach ($k in $common.Keys) { if ($params -contains $k) { $splat[$k] = $common[$k] } }
    & $path @splat
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { Write-Host "stopped at $s (exit $LASTEXITCODE)" -ForegroundColor Red; exit $LASTEXITCODE }
}
