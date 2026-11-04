#Requires -Version 7.0
<#
.SYNOPSIS
  Applies plan.tsv decisions to both alignment branches. For each side it builds the target
  scoped tree in the scratch repo, emits a binary patch (current -> target) and applies it with
  git apply --index in that side's worktree. Content crosses repositories only inside these
  patches; no refs or remotes are added to either repository.
  Dry-run unless -Apply; commits only with -Commit; records sync-ledger.json after both commit.
#>
[CmdletBinding()]
param([string]$PlanDir, [ValidateSet('Both', 'Backend', 'Isl')][string]$Side = 'Both',
      [switch]$Apply, [switch]$Commit, [switch]$Yes, [switch]$SkipManual, [string]$ConfigPath)
Import-Module (Join-Path $PSScriptRoot 'Align.Common.psm1') -Force
$cfg = Import-AlignConfig $ConfigPath; Set-AlignYes $Yes
Start-AlignLog $cfg 'apply'
try {
    Assert-GitVersion
    $x = Initialize-Scratch $cfg
    $PlanDir = Resolve-PlanDir $cfg $PlanDir
    $plan = Read-AlignPlan $PlanDir
    Write-Ok "plan $PlanDir ($($plan.Rows.Count) rows)"
    if ($plan.Meta.preview) { throw 'This is a -Preview plan. Create the branches (01) and re-plan (02) before applying.' }

    $manual = @($plan.Rows | Where-Object { $_.Decision -eq 'MANUAL' })
    if ($manual.Count -and -not $SkipManual) {
        throw "$($manual.Count) MANUAL row(s) in plan.tsv. Set a decision for each, or pass -SkipManual to leave them untouched on both sides:`n$(($manual | ForEach-Object { $_.Path }) -join "`n")"
    }

    foreach ($r in $plan.Rows) {
        if ($r.Decision -eq 'MERGED' -and $r.Merged -and [regex]::IsMatch((ConvertTo-Text (Get-BlobBytes $x $r.Merged)), '(?m)^<{7} ')) {
            throw "$($r.Path): MERGED would commit conflict markers. Choose TAKE_BE / TAKE_ISL / KEEP_BOTH, or resolve by hand after applying with MANUAL + -SkipManual."
        }
    }

    $sides = if ($Side -eq 'Both') { @('Backend', 'Isl') } else { @($Side) }
    $metaKey = @{ Backend = 'backend'; Isl = 'isl' }
    $committed = @{}
    foreach ($s in $sides) {
        Write-Step "$s : $($cfg[$s].NewBranch)"
        $tip = Assert-WorktreeBranch $cfg $s
        $planned = $plan.Meta[$metaKey[$s]].rev
        if ($tip -ne $planned) {
            if (Test-Git $cfg[$s].Worktree @('merge-base', '--is-ancestor', $planned, $tip)) {
                $moved = Invoke-Git $x (@('diff', '--name-only', $planned, $tip, '--') + @($cfg.Scope))
                if ($moved) { throw "$s moved since the plan and touched the aligned scope - re-run 02:`n$moved" }
                Write-Note "$s moved since the plan (outside scope only) - continuing"
            } else { throw "$s HEAD $tip is not the planned $planned (or a descendant). Re-run 02." }
        }
        Assert-ScopeClean $cfg $s

        $cur = Get-ScopedEntries $x $cfg $tip
        $curTree = New-TreeFromEntries $x $cur
        $changes = [ordered]@{}; $dels = [Collections.Generic.List[string]]::new(); $pkgReport = @()
        foreach ($r in $plan.Rows) {
            if ($r.Decision -eq 'MANUAL') { continue }
            $t = Get-SideTarget $r $s
            if ($t -eq 'PACKAGES') {
                $mine = if ($s -eq 'Isl') { $r.Isl } else { $r.Backend }
                $other = if ($s -eq 'Isl') { $r.Backend } else { $r.Isl }
                $otherName = if ($s -eq 'Isl') { 'Backend' } else { 'ISL' }
                $mineBytes = Get-BlobBytes $x $mine
                $bom = $mineBytes.Length -ge 3 -and $mineBytes[0] -eq 0xEF -and $mineBytes[1] -eq 0xBB -and $mineBytes[2] -eq 0xBF
                $res = Merge-PackageProps (ConvertTo-Text $mineBytes) (ConvertTo-Text (Get-BlobBytes $x $other)) $otherName $cfg.PackageVersionOverrides
                $pkgReport += $res.Report
                $sha = New-Blob $x ([Text.UTF8Encoding]::new($bom).GetPreamble() + [Text.Encoding]::UTF8.GetBytes($res.Text))
                $t = @{ Mode = $cur[$r.Path].Mode; Sha = $sha }
            }
            $now = if ($cur.Contains($r.Path)) { $cur[$r.Path] } else { $null }
            if ($null -eq $t) { if ($now) { $dels.Add($r.Path) } }
            elseif (-not $now -or $now.Sha -ne $t.Sha -or $now.Mode -ne $t.Mode) { $changes[$r.Path] = $t }
        }
        Write-Plan "$($changes.Count) add/modify, $($dels.Count) delete"
        $pkgReport | ForEach-Object { Write-Plan "packages: $_" }
        if (-not $changes.Count -and -not $dels.Count) { Write-Ok 'nothing to change'; continue }

        $target = New-TreeFromEntries $x $changes $curTree $dels
        $patch = Join-Path $PlanDir "$s.patch"
        Invoke-Git $x @('diff', '--binary', '--full-index', '--no-renames', '--no-ext-diff', '--no-textconv', '--no-color',
                        '--src-prefix=a/', '--dst-prefix=b/', "--output=$patch", $curTree, $target) | Out-Null
        ((Invoke-Git $x @('diff', '--stat=200', '--no-renames', $curTree, $target)) -split "`n") | Select-Object -Last 1 | ForEach-Object { Write-Plan $_ }
        Invoke-Git $cfg[$s].Worktree @('apply', '--check', '--index', '--whitespace=nowarn', $patch) | Out-Null
        Write-Ok "patch applies cleanly: $patch"
        if (-not $Apply) { continue }
        if (-not (Confirm-Step "Apply $($changes.Count + $dels.Count) change(s) to $s worktree $($cfg[$s].Worktree)?")) { throw 'Stopped by user.' }
        Invoke-Git $cfg[$s].Worktree @('apply', '--index', '--whitespace=nowarn', $patch) | Out-Null
        Write-Ok 'applied and staged'

        if ($Commit) {
            $counts = ($plan.Rows | Where-Object { $_.Decision -ne 'MANUAL' } | Group-Object { $_.Decision } | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join ', '
            $other = if ($s -eq 'Isl') { 'Backend' } else { 'Isl' }
            $body = "Aligns $($cfg.Scope -join ', ') with $($cfg[$other].NewBranch) (plan $(Split-Path $PlanDir -Leaf)).`nSync base $($cfg.SyncBase.Substring(0,10)); decisions: $counts.`nThis side: $($changes.Count) added/modified, $($dels.Count) deleted."
            if ($pkgReport) { $body += "`nDirectory.Packages.props: $($pkgReport.Count) entr(y/ies) unioned/set; see plan.md." }
            Invoke-Git $cfg[$s].Worktree @('commit', '--quiet', '-m', (Get-CommitMessage $cfg "chore(fork-sync): align shared projects with $($cfg[$other].NewBranch)" $body)) | Out-Null
            $committed[$s] = Invoke-Git $cfg[$s].Worktree @('rev-parse', 'HEAD')
            Write-Ok "committed $($committed[$s].Substring(0, 10))"
        } else { Write-Note "staged, not committed. Review: git -C `"$($cfg[$s].Worktree)`" diff --cached --stat" }
    }

    if ($committed.Backend -and $committed.Isl) {
        $ledgerPath = Join-Path $cfg.StateDir 'sync-ledger.json'
        # A List, not an array: an 'if' expression unrolls a one-element array into a scalar, and += then fails.
        $ledger = [Collections.Generic.List[object]]::new()
        if (Test-Path $ledgerPath) { foreach ($e in @(Get-Content $ledgerPath -Raw | ConvertFrom-Json)) { $ledger.Add($e) } }
        $ledger.Add([pscustomobject]@{
            date = (Get-Date).ToString('o'); plan = (Split-Path $PlanDir -Leaf); previousSyncBase = $cfg.SyncBase
            backend = @{ branch = $cfg.Backend.NewBranch; commit = $committed.Backend }
            isl = @{ branch = $cfg.Isl.NewBranch; commit = $committed.Isl }
            nextSyncBase = $committed.Backend
        })
        ConvertTo-Json -InputObject $ledger.ToArray() -Depth 5 | Set-Content $ledgerPath -Encoding utf8
        Write-Ok "ledger: $ledgerPath (next SyncBase = $($committed.Backend.Substring(0, 10)) once both PRs merge)"
    }
    if (-not $Apply) { Write-Note 'Dry-run. Re-run with -Apply [-Commit].' } else { Write-Ok 'Next: 04-Test-Alignment.ps1' }
} finally { Stop-AlignLog }
