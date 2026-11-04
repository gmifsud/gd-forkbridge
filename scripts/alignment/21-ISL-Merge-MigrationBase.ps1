#Requires -Version 7.0
<# Step 12: bring origin/migration/24404-plt-ng17-fork-be into the fix branch (simulated clean on 2026-10-26). PR fix -> migration afterwards. #>
[CmdletBinding()] param([switch]$Apply, [switch]$Yes)
Import-Module (Join-Path $PSScriptRoot 'Alignment.Common.psm1') -Force
$d = Get-AlignmentDefaults; Set-AlignmentYes $Yes; $R = $d.IslWorktree
Start-AlignmentLog 'step21-isl-merge'
try {
    Assert-Branch $R $d.IslBranch
    if (Invoke-Git $R status --porcelain) { throw "worktree dirty; merge needs a clean tree" }
    Invoke-Git $R fetch origin --quiet | Out-Null
    $base = Invoke-Git $R merge-base HEAD $d.IslMigrationBase
    $sim = & git -C $R merge-tree $base HEAD $d.IslMigrationBase 2>&1
    $conf = ($sim | Select-String '<<<<<<<' ).Count
    if ($conf) { Write-Warn2 "$conf conflict marker(s) in simulation - resolve interactively after merge" } else { Write-Ok "simulation: no conflicts" }
    if (-not $Apply) { Write-Warn2 "dry-run. Re-run with -Apply"; return }
    if (-not (Confirm-Step "git merge $($d.IslMigrationBase) into $($d.IslBranch) ?")) { return }
    & git -C $R merge --no-edit $d.IslMigrationBase
    if ($LASTEXITCODE) { Write-Warn2 "merge stopped with conflicts: resolve, 'git add', 'git merge --continue'"; return }
    Write-Ok "merged. Open PR $($d.IslBranch) -> migration/24404-plt-ng17-fork-be"
} finally { Stop-AlignmentLog }
