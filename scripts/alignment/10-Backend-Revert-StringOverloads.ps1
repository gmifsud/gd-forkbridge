#Requires -Version 7.0
<# Step 4: revert 4412b4f48 (string module overloads). Verifies result blobs == Development. #>
[CmdletBinding()] param([switch]$Apply, [switch]$Commit, [switch]$Yes)
Import-Module (Join-Path $PSScriptRoot 'Alignment.Common.psm1') -Force
$d = Get-AlignmentDefaults; Set-AlignmentYes $Yes; $R = $d.BackendWorktree
$files = @(
  'QiSL.Fabric/BaseTemplate.0/aspnet-core/src/QiSL.App.BaseTemplate.EntityFrameworkCore/EntityFrameworkCore/BaseDbContext.cs',
  'QiSL.Fabric/BaseTemplate.0/aspnet-core/src/QiSL.App.BaseTemplate.EntityFrameworkCore/Migrations/Seed/BaseCreator.cs',
  'QiSL.Fabric/Kernel/QiSL.Platform.SharedKernel.EntityFrameworkCore/EntityFrameworkCore/Repositories/QiSLRepositoryBase.cs')
Start-AlignmentLog 'step10-revert'
try {
    Assert-Branch $R $d.BackendBranch
    Assert-CleanScope $R $files
    Write-Step "Simulate revert (merge-tree, no working-tree change)"
    $sim = & git -C $R merge-tree $d.OverloadCommit HEAD "$($d.OverloadCommit)^" 2>&1
    if ($sim -match '<<<<<<<') { throw "Revert would conflict:`n$sim" }
    Write-Ok "no conflicts"
    if (-not $Apply) { Write-Warn2 "dry-run. Re-run with -Apply"; return }
    if (-not (Confirm-Step "Apply 'git revert --no-commit $($d.OverloadCommit.Substring(0,9))' in $R ?")) { return }
    Invoke-Git $R revert --no-commit $d.OverloadCommit | Out-Null
    Write-Step "Verify result == $($d.BackendTarget)"
    foreach ($f in $files) { Assert-BlobEquals $R ':0' $d.BackendTarget $f }   # :0 = index
    Write-Step "Static guard: no string-overload callers remain"
    $g = & git -C $R grep -nE ': base\(options, "|ConfigureModularStatuses\(tenantId, "|GetLobModule\(tenantId, "' -- '*.cs'
    if ($g) { throw "String-overload callers found:`n$g" } ; Write-Ok "none"
    Commit-IfRequested $R $Commit "revert: drop string module overloads from shared kernel/base template`n`nReverts 4412b4f48. Fork-owned module codes live in ISL-Backend's ISL.Overrides enum; nothing in Backend called these overloads."
} finally { Stop-AlignmentLog }
