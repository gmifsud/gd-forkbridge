#Requires -Version 7.0
<#
.SYNOPSIS  Gate 0 + environment preflight. Read-only. Run first.
#>
[CmdletBinding()] param([switch]$Yes)
Import-Module (Join-Path $PSScriptRoot 'Alignment.Common.psm1') -Force
$d = Get-AlignmentDefaults; Set-AlignmentYes $Yes
Start-AlignmentLog 'preflight'
try {
    Write-Step "Tooling"
    foreach ($t in 'git','dotnet','pwsh') { if (-not (Get-Command $t -ErrorAction SilentlyContinue)) { throw "$t not on PATH" } ; Write-Ok "$t $((& $t --version 2>&1 | Select-Object -First 1))" }
    if (-not (Get-Command dotnet-ef -ErrorAction SilentlyContinue) -and -not (& dotnet ef --version 2>$null)) { Write-Warn2 "dotnet-ef not found: step 13 (snapshot regen) needs 'dotnet tool install -g dotnet-ef'" }

    Write-Step "Backend worktree"
    Assert-Branch $d.BackendWorktree $d.BackendBranch
    if (-not (Test-GitQuiet $d.BackendWorktree merge-base --is-ancestor $d.OverloadCommit HEAD)) { throw "4412b4f48 not in $($d.BackendBranch) - nothing to revert, or wrong branch" }
    Write-Ok "4412b4f48 present on branch"
    $behind = (Invoke-Git $d.BackendWorktree rev-list --left-right --count "$($d.BackendTarget)...HEAD") -split '\s+'
    Write-Plan "$($d.BackendTarget) ahead by $($behind[0]) / branch ahead by $($behind[1])"
    if ([int]$behind[0] -gt 0) { Write-Warn2 "$($d.BackendTarget) has $($behind[0]) commits not on the branch - merge $($d.BackendTarget) in before raising the PR" }

    Write-Step "ISL worktree"
    Assert-Branch $d.IslWorktree $d.IslBranch
    $head = Invoke-Git $d.IslWorktree rev-parse HEAD
    if ($head -ne $d.IslFixCommit) { Write-Warn2 "ISL branch tip is $head, analysis was done on $($d.IslFixCommit) - re-check before continuing" } else { Write-Ok "tip == d5380bd9a" }
    Invoke-Git $d.IslWorktree fetch origin --quiet | Out-Null
    $mb = Invoke-Git $d.IslWorktree merge-base HEAD $d.IslMigrationBase
    Write-Plan "merge-base with $($d.IslMigrationBase): $($mb.Substring(0,9))"

    Write-Step "Gate 0 decisions (record the answers in EXECUTION-PLAN.md)"
    Write-Host @"
   1. Backend target branch for the PR is '$($d.BackendTarget)'                        -> confirm
   2. origin/feature/sync-ebs-repo is retired after the Backend merge                   -> confirm
   3. WF fork model = partial-method hook (SeedForkWorkflows), IoC contributors later   -> confirm
"@
    if (-not (Confirm-Step "All three confirmed?")) { throw "Gate 0 not passed. Stop." }
    Write-Ok "Gate 0 passed. Next: 10-Backend-Revert-StringOverloads.ps1 -Apply"
} finally { Stop-AlignmentLog }
