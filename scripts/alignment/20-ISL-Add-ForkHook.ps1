#Requires -Version 7.0
<# Step 11: ISL adds WFSeederService.Fork.cs and moves CustomerOnboarding creators under Workflows/_Fork/. No upstream-owned file is edited. #>
[CmdletBinding()] param([switch]$Apply, [switch]$Commit, [switch]$Yes)
Import-Module (Join-Path $PSScriptRoot 'Alignment.Common.psm1') -Force
$d = Get-AlignmentDefaults; Set-AlignmentYes $Yes; $R = $d.IslWorktree
$seedDir = 'QiSL.App.WF/src/QiSL.App.WF.EntityFrameworkCore/Migrations/Seed'
$src  = "$seedDir/Workflows/CustomerOnboarding"; $dst = "$seedDir/Workflows/_Fork/CustomerOnboarding"
$hook = "$seedDir/WFSeederService.Fork.cs"; $seeder = "$seedDir/WFSeederService.cs"
$wfEf = 'QiSL.App.WF/src/QiSL.App.WF.EntityFrameworkCore/QiSL.App.WF.EntityFrameworkCore.csproj'
Start-AlignmentLog 'step20-isl-hook'
try {
    Assert-Branch $R $d.IslBranch; Assert-CleanScope $R @('QiSL.App.WF')
    Write-Plan "will create $hook"; Write-Plan "will git mv $src -> $dst"
    Write-Warn2 "WFSeederService.cs still contains the inline CustomerOnboarding call until step 22 imports the upstream partial version; both coexisting = duplicate seed. This script therefore ALSO removes the inline call now (fork edit to an upstream file - last time, by design)."
    if (-not $Apply) { Write-Warn2 "dry-run. Re-run with -Apply"; return }
    if (-not (Confirm-Step "Apply in $R ?")) { return }
    Copy-Item (Join-Path $PSScriptRoot 'patches/02-isl-wfseeder-fork.cs') (Join-Path $R $hook)
    (Get-Content (Join-Path $R $hook)) | Where-Object { $_ -notmatch '^// (ISL-Backend only|This file never exists)' } | Set-Content (Join-Path $R $hook) -Encoding utf8
    New-Item -ItemType Directory -Force -Path (Split-Path (Join-Path $R $dst)) | Out-Null
    Invoke-Git $R mv -- $src $dst | Out-Null
    # Make upstream file identical to what step 12 produces upstream: apply the same patch 01.
    & git -C $R apply --check (Join-Path $PSScriptRoot 'patches/01-backend-wfseeder-fork-hook.patch')
    if ($LASTEXITCODE -eq 0) { Invoke-Git $R apply --index (Join-Path $PSScriptRoot 'patches/01-backend-wfseeder-fork-hook.patch') | Out-Null; Write-Ok "WFSeederService.cs now equals the upstream partial version (step 22 import becomes a no-op on it)" }
    else { Write-Warn2 "patch 01 does not apply to ISL's WFSeederService.cs - remove the inline CustomerOnboarding call manually, then re-run build" }
    Invoke-Git $R add -- $hook | Out-Null
    Write-Step "Build WF EF (ISL)"; Invoke-Dotnet $R build $wfEf --nologo -v q
    Commit-IfRequested $R $Commit "feat(wf): move CustomerOnboarding seed behind the SeedForkWorkflows fork hook`n`nWFSeederService.Fork.cs is ISL-only; creators live under Workflows/_Fork/. No upstream-owned WF file is edited from here on."
} finally { Stop-AlignmentLog }
