#Requires -Version 7.0
<# Step 7: [QiSLCode(10)] on ProcessFlowRule_Code and regenerate WFDbContextModelSnapshot without adding a migration.
   Technique: 'ef migrations add __Tmp' (updates snapshot) -> copy snapshot aside -> 'ef migrations remove' (reverts snapshot) -> restore copy. #>
[CmdletBinding()] param([switch]$Apply, [switch]$Commit, [switch]$Yes,
  [string]$StartupProject = 'QiSL.Fabric/Platform/aspnet-core/src/QiSL.Platform.Web.Host/QiSL.Platform.Web.Host.csproj')
Import-Module (Join-Path $PSScriptRoot 'Alignment.Common.psm1') -Force
$d = Get-AlignmentDefaults; Set-AlignmentYes $Yes; $R = $d.BackendWorktree
$patch = Join-Path $PSScriptRoot 'patches/03-backend-processflowrule-code-length.patch'
$wfEf  = 'QiSL.App.WF/src/QiSL.App.WF.EntityFrameworkCore'
$snap  = "$wfEf/Migrations/WFDbContextModelSnapshot.cs"
Start-AlignmentLog 'step13-flowrulecode'
try {
    Assert-Branch $R $d.BackendBranch; Assert-CleanScope $R @('QiSL.App.WF')
    & git -C $R apply --check --ignore-whitespace $patch; if ($LASTEXITCODE) { throw "patch 03 does not apply" }; Write-Ok "patch 03 applies"
    if (-not $Apply) { Write-Warn2 "dry-run. Re-run with -Apply"; return }
    if (-not (Confirm-Step "Apply [QiSLCode(10)] and regenerate the snapshot via a temporary migration (will be removed)?")) { return }
    Invoke-Git $R apply --index --ignore-whitespace $patch | Out-Null
    Write-Step "Temporary migration to refresh snapshot"
    Invoke-Dotnet $R ef migrations add __TmpSnapshotRefresh -p $wfEf -s $StartupProject
    $tmp = Get-ChildItem (Join-Path $R "$wfEf/Migrations") -Filter '*__TmpSnapshotRefresh.cs' | Where-Object Name -notlike '*.Designer.cs'
    if (-not $tmp) { throw "temporary migration not found" }
    $up = (Get-Content $tmp.FullName -Raw)
    Write-Plan "temporary migration Up() body:"; ($up -split "`n" | Select-String -Pattern 'AlterColumn|AddColumn|DropColumn|CreateTable|DropTable|name:' ) | ForEach-Object { Write-Plan $_.Line.Trim() }
    if ($up -notmatch 'ProcessFlowRule_Code' -or ($up | Select-String -AllMatches -Pattern 'migrationBuilder\.' ).Matches.Count -gt 2) {
        Write-Warn2 "Up() contains more than the ProcessFlowRule_Code widening (or not it). Inspect $($tmp.FullName) before continuing."
        if (-not (Confirm-Step "Continue anyway (keep snapshot, drop temp migration)?")) { Invoke-Dotnet $R ef migrations remove -p $wfEf -s $StartupProject --force; throw "aborted" }
    }
    Copy-Item (Join-Path $R $snap) "$env:TEMP\WFDbContextModelSnapshot.refreshed.cs" -Force
    Invoke-Dotnet $R ef migrations remove -p $wfEf -s $StartupProject --force
    Copy-Item "$env:TEMP\WFDbContextModelSnapshot.refreshed.cs" (Join-Path $R $snap) -Force
    Invoke-Git $R add -- $snap | Out-Null
    $s = Get-Content (Join-Path $R $snap) -Raw
    if ($s -notmatch 'ProcessFlowRule_Code[\s\S]{0,200}HasMaxLength\(10\)') { throw "snapshot does not show HasMaxLength(10) for ProcessFlowRule_Code" }
    Write-Ok "snapshot aligned with migration 20260817190500"
    Commit-IfRequested $R $Commit "fix(wf): declare ProcessFlowRule_Code max length 10; align model snapshot with 20260817190500`n`nPrevents the next scaffold from shrinking the column back to nvarchar(4)."
} finally { Stop-AlignmentLog }
