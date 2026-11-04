#Requires -Version 7.0
<#
.SYNOPSIS
  Creates the two alignment branches in NEW worktrees, off the remote bases, so no existing
  branch or worktree is touched (including D:\Repos\Frontend.worktrees\feature\configuration-cache-synchronisation).
  Then merges any configured prerequisite refs (MergeRefs) into the new branches.
  Dry-run unless -Apply. Never pushes.
#>
[CmdletBinding()]
param([switch]$Apply, [switch]$Yes, [switch]$SkipFetch, [switch]$SkipMergeRefs, [string]$ConfigPath)
Import-Module (Join-Path $PSScriptRoot 'Align.Common.psm1') -Force
$cfg = Import-AlignConfig $ConfigPath; Set-AlignYes $Yes
Start-AlignLog $cfg 'branches'
try {
    Write-Step 'Preflight'
    Assert-GitVersion
    foreach ($s in 'Frontend', 'Isl') {
        $c = $cfg[$s]
        if (-not (Test-Path (Join-Path $c.Root '.git'))) { throw "$s root $($c.Root) is not a git repository" }
        if (-not $SkipFetch) { Invoke-Git $c.Root @('fetch', 'origin', '--prune', '--quiet') | Out-Null; Write-Ok "$s fetched" }
    }

    foreach ($s in 'Frontend', 'Isl') {
        $c = $cfg[$s]
        Write-Step "$s : $($c.NewBranch)  <=  $($c.BaseRef)"
        $base = Invoke-Git $c.Root @('rev-parse', '--verify', "$($c.BaseRef)^{commit}")
        Write-Plan "base $($c.BaseRef) @ $($base.Substring(0, 10)) $(Invoke-Git $c.Root @('log', '-1', '--format=%cd %an: %s', '--date=short', $base))"
        $localExists = Test-Git $c.Root @('show-ref', '--verify', '--quiet', "refs/heads/$($c.NewBranch)")
        $remoteExists = Test-Git $c.Root @('show-ref', '--verify', '--quiet', "refs/remotes/origin/$($c.NewBranch)")
        if ($localExists -or $remoteExists) {
            if ((Test-Path $c.Worktree) -and (Invoke-Git $c.Worktree @('rev-parse', '--abbrev-ref', 'HEAD')) -eq $c.NewBranch) {
                Write-Ok "already exists in $($c.Worktree) @ $(Invoke-Git $c.Worktree @('rev-parse', '--short', 'HEAD')) - leaving it as is"
                continue
            }
            throw "Branch '$($c.NewBranch)' already exists (local=$localExists, origin=$remoteExists) but not in $($c.Worktree). Pick another NewBranch in the config."
        }
        if (Test-Path $c.Worktree) { throw "Worktree path $($c.Worktree) already exists." }
        Write-Plan "git -C `"$($c.Root)`" worktree add --no-track -b $($c.NewBranch) `"$($c.Worktree)`" $($c.BaseRef)"
        if (-not $Apply) { continue }
        if (-not (Confirm-Step "Create $s branch '$($c.NewBranch)' in $($c.Worktree)?")) { throw 'Stopped by user.' }
        Invoke-Git $c.Root @('worktree', 'add', '--quiet', '--no-track', '-b', $c.NewBranch, $c.Worktree, $c.BaseRef) | Out-Null
        Write-Ok "created $($c.NewBranch) @ $($base.Substring(0, 10))"
    }

    if (-not $SkipMergeRefs) {
        foreach ($s in 'Frontend', 'Isl') {
            $c = $cfg[$s]
            foreach ($ref in @($c.MergeRefs)) {
                if (-not $ref) { continue }
                Write-Step "$s : merge prerequisite $ref"
                $sha = Invoke-Git $c.Root @('rev-parse', '--verify', "$ref^{commit}")
                if (-not (Test-Path $c.Worktree)) { Write-Plan "would merge $ref ($($sha.Substring(0, 10))) after the branch exists"; continue }
                if (Test-Git $c.Worktree @('merge-base', '--is-ancestor', $sha, 'HEAD')) { Write-Ok 'already merged'; continue }
                $sim = Invoke-GitX -Repo $c.Worktree -GitArgs @('merge-tree', '--write-tree', '--name-only', '--no-messages', 'HEAD', $sha) -OkExit @(0, 1)
                if ($sim.Code -eq 1) { throw "Merging $ref conflicts on:`n$(($sim.Out -split "`n" | Select-Object -Skip 1) -join "`n")`nResolve manually (git merge $ref) and re-run." }
                Write-Ok 'simulation: no conflicts'
                if (-not $Apply) { continue }
                if (-not (Confirm-Step "Merge $ref into $($c.NewBranch)?")) { throw 'Stopped by user.' }
                $msg = Get-CommitMessage $cfg "merge: $ref into $($c.NewBranch)" 'Prerequisite for the Frontend <-> ISL-Frontend branch alignment.'
                Invoke-Git $c.Worktree @('merge', '--no-ff', '--quiet', '-m', $msg, $sha) | Out-Null
                Write-Ok "merged -> $(Invoke-Git $c.Worktree @('rev-parse', '--short', 'HEAD'))"
            }
        }
    }
    if (-not $Apply) { Write-Note 'Dry-run. Re-run with -Apply.' } else { Write-Ok 'Next: 02-New-AlignmentPlan.ps1' }
} finally { Stop-AlignLog }
