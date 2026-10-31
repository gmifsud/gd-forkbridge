#Requires -Version 7.0
<# Step 16: ef-config.json backendRoot; Build-WebHost.ps1 module discovery (report + suggested patch); data-migration decision reminder. #>
[CmdletBinding()] param([switch]$Apply, [switch]$Commit, [switch]$Yes)
Import-Module (Join-Path $PSScriptRoot 'Alignment.Common.psm1') -Force
$d = Get-AlignmentDefaults; Set-AlignmentYes $Yes; $R = $d.IslWorktree
Start-AlignmentLog 'step25-isl-housekeeping'
try {
    Assert-Branch $R $d.IslBranch
    Write-Step "scripts/EFCore/ef-config.json backendRoot"
    $cfgPath = Join-Path $R 'scripts/EFCore/ef-config.json'; $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
    Write-Plan "current: $($cfg.backendRoot)   (wrong repo + C: junction)"
    if ($Apply -and $cfg.backendRoot -ne '..\..\QiSL.Fabric' -and (Confirm-Step "Set backendRoot to '..\..\QiSL.Fabric'?")) {
        $raw = Get-Content $cfgPath -Raw; $raw = $raw -replace '"backendRoot":\s*"[^"]*"', '"backendRoot": "..\\\\..\\\\QiSL.Fabric"'; Set-Content $cfgPath $raw -NoNewline -Encoding utf8
        Invoke-Git $R add -- scripts/EFCore/ef-config.json | Out-Null; Write-Ok "backendRoot fixed" }
    Write-Step "scripts/Build-WebHost.ps1 - hard-coded Backend module list"
    $bw = Get-Content (Join-Path $R 'scripts/Build-WebHost.ps1') -Raw
    ($bw -split "`n" | Select-String -Pattern 'QiSL\.App\.(NL|SL|VAM|Practice|Registration)|Web\.Host\.csproj') | ForEach-Object { Write-Plan $_.Line.Trim() }
    Write-Warn2 "Replace the list with discovery, e.g.:  Get-ChildItem 'QiSL.App.*\src\*.Web.Core\*.Web.Core.csproj' | ForEach-Object FullName   and build QiSL.Platform.Web.ISL.Host. Not auto-applied (script semantics differ per author)."
    Write-Step "Data migration 20260803093822_BaseTemplate_Update-ModularStatuses-Type"
    Write-Warn2 "Heuristic rewrite of Gen`$LobConfigurations.ConfigurationType, empty Down(). Decide: keep (ISL fleet only), or convert to an idempotent SQL script outside EF. Record the decision in EXECUTION-PLAN.md."
    Commit-IfRequested $R $Commit "chore(scripts): point ef-config backendRoot at the repo-relative QiSL.Fabric"
} finally { Stop-AlignmentLog }
