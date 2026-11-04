#Requires -Version 7.0
<# Step 6: WFSeederService -> partial class + SeedForkWorkflows hook; remove Backend copy of the ISL CustomerOnboarding seed. #>
[CmdletBinding()] param([switch]$Apply, [switch]$Commit, [switch]$Yes, [switch]$KeepCustomerOnboardingSeed)
Import-Module (Join-Path $PSScriptRoot 'Alignment.Common.psm1') -Force
$d = Get-AlignmentDefaults; Set-AlignmentYes $Yes; $R = $d.BackendWorktree
$patch = Join-Path $PSScriptRoot 'patches/01-backend-wfseeder-fork-hook.patch'
$seeder = 'QiSL.App.WF/src/QiSL.App.WF.EntityFrameworkCore/Migrations/Seed/WFSeederService.cs'
$onboarding = 'QiSL.App.WF/src/QiSL.App.WF.EntityFrameworkCore/Migrations/Seed/Workflows/CustomerOnboarding'
$wfEf = 'QiSL.App.WF/src/QiSL.App.WF.EntityFrameworkCore/QiSL.App.WF.EntityFrameworkCore.csproj'
Start-AlignmentLog 'step12-wfhook'
try {
    Assert-Branch $R $d.BackendBranch; Assert-CleanScope $R @('QiSL.App.WF')
    Write-Step "git apply --check"
    $blob = Get-BlobSha $R HEAD $seeder
    if ($blob -notlike '452e5b7*') { Write-Warn2 "WFSeederService.cs blob is $($blob.Substring(0,9)); patch was verified against 452e5b7 - --check decides" }
    & git -C $R apply --check $patch; if ($LASTEXITCODE) { throw "patch does not apply cleanly; regenerate from EXECUTION-PLAN step 6" }
    Write-Ok "patch applies"
    if (-not $Apply) { Write-Warn2 "dry-run. Re-run with -Apply"; return }
    if (-not (Confirm-Step "Apply patch 01 and $(if($KeepCustomerOnboardingSeed){'keep'}else{'git rm'}) $onboarding ?")) { return }
    Invoke-Git $R apply --index $patch | Out-Null
    if (-not $KeepCustomerOnboardingSeed) { Invoke-Git $R rm -r -q -- $onboarding | Out-Null; Write-Ok "removed $onboarding (moves to ISL in step 20)" }
    Write-Step "Build WF EF"
    Invoke-Dotnet $R build $wfEf --nologo -v q
    $chk = Get-Content (Join-Path $R $seeder) -Raw
    if ($chk -notmatch 'partial void SeedForkWorkflows' -or ($chk -match 'CustomerOnboarding' -and -not $KeepCustomerOnboardingSeed)) { throw "post-condition failed on WFSeederService.cs" }
    Write-Ok "WFSeederService is partial with SeedForkWorkflows hook"
    Commit-IfRequested $R $Commit "feat(wf): open WFSeederService as an extension point for fork-owned workflows`n`npartial void SeedForkWorkflows(...) is implemented by forks in WFSeederService.Fork.cs; the compiler erases the call when no implementation exists. CustomerOnboarding seed moves to ISL-Backend."
} finally { Stop-AlignmentLog }
