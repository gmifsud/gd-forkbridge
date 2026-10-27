#Requires -Version 7.0
<#
.SYNOPSIS
  Applies a restore plan (plans\restore-<ts>\restore.tsv) to the ISL alignment branch. It puts back
  development changes that the bulk-copy commits overwrote; the alignment commit itself is unchanged.

  Per row (Decision column):
    KEEP      nothing (aligned content stays)
    RESTORE   writes Merged (clean 3-way merge of aligned/base/development), or Dev when Merged is empty
    RESOLVED  writes Merged (hand-resolved conflict; see resolutions.md)
    TAKE_DEV  writes Dev; an empty Dev deletes the file
    DELETE    deletes the file
    MANUAL / REVIEW  block the run (-SkipBlocked leaves those paths untouched)

  Blobs the ISL repository does not have (merge results, resolutions) ship as restore-dir\blobs\<sha>
  and are verified by hash before use. The index is written with update-index (byte-exact, no EOL
  filters) and the worktree refreshed with checkout-index. Dry-run unless -Apply; commits only with -Commit.
#>
[CmdletBinding()]
param([string]$RestoreDir, [switch]$Apply, [switch]$Commit, [switch]$Yes, [switch]$SkipBlocked, [string]$ConfigPath)
Import-Module (Join-Path $PSScriptRoot 'Align.Common.psm1') -Force
$cfg = Import-AlignConfig $ConfigPath; Set-AlignYes $Yes
Start-AlignLog $cfg 'restore'
try {
    if (-not $RestoreDir) {
        $RestoreDir = Get-ChildItem (Join-Path $cfg.StateDir 'plans') -Directory -Filter 'restore-*' | Sort-Object Name | Select-Object -Last 1 -ExpandProperty FullName
        if (-not $RestoreDir) { throw "No restore-* folder under $(Join-Path $cfg.StateDir 'plans')." }
    }
    $RestoreDir = (Resolve-Path $RestoreDir).Path
    $tsv = Join-Path $RestoreDir 'restore.tsv'
    $lines = [IO.File]::ReadAllLines($tsv)
    if ($lines[0] -notmatch '^# meta (\{.*\})$') { throw "$tsv : first line is not '# meta {...}'." }
    $meta = $Matches[1] | ConvertFrom-Json
    $hdr = $lines[2].Split("`t")
    $rows = foreach ($l in ($lines | Select-Object -Skip 3)) {
        if (-not $l) { continue }
        $f = $l.Split("`t"); $h = [ordered]@{}
        for ($i = 0; $i -lt $hdr.Count; $i++) { $h[$hdr[$i]] = if ($i -lt $f.Count) { $f[$i] } else { '' } }
        [pscustomobject]$h
    }
    Write-Ok "restore plan $RestoreDir ($(@($rows).Count) rows)"

    $allowed = 'KEEP', 'RESTORE', 'RESOLVED', 'TAKE_DEV', 'DELETE', 'MANUAL', 'REVIEW'
    $bad = @($rows | Where-Object { $_.Decision -notin $allowed })
    if ($bad) { throw "Unknown Decision value(s): $(($bad | ForEach-Object { "$($_.Decision) $($_.Path)" }) -join '; ')" }
    $blocked = @($rows | Where-Object { $_.Decision -in 'MANUAL', 'REVIEW' })
    if ($blocked -and -not $SkipBlocked) { throw "$($blocked.Count) MANUAL/REVIEW row(s). Decide them, or pass -SkipBlocked to leave them untouched:`n$(($blocked | ForEach-Object { $_.Path }) -join "`n")" }

    Write-Step "ISL : $($cfg.Isl.NewBranch)"
    $wt = $cfg.Isl.Worktree
    $tip = Assert-WorktreeBranch $cfg 'Isl'

    # Operations: path -> target sha ('' = delete).
    $ops = [ordered]@{}; $mode = @{}
    foreach ($r in $rows) {
        $t = switch ($r.Decision) {
            'RESTORE'  { if ($r.Merged) { $r.Merged } else { $r.Dev } }
            'RESOLVED' { if (-not $r.Merged) { throw "$($r.Path): RESOLVED without a Merged blob." }; $r.Merged }
            'TAKE_DEV' { $r.Dev }
            'DELETE'   { '' }
            default    { $null }
        }
        if ($null -eq $t) { continue }
        $ops[$r.Path] = $t; $mode[$r.Path] = if ($r.Mode) { $r.Mode } else { '100644' }
    }
    $paths = @($ops.Keys)
    if (-not $paths.Count) { Write-Ok 'nothing to change'; return }

    # The plan was computed against meta.aligned; refuse if any touched path moved since.
    if ($tip -ne $meta.aligned) {
        if (-not (Test-Git $wt @('merge-base', '--is-ancestor', $meta.aligned, $tip))) { throw "HEAD $tip does not descend from the planned $($meta.aligned). Rebuild the restore plan." }
        $moved = Invoke-GitX -Repo $wt -GitArgs (@('diff', '--name-only', '-z', $meta.aligned, $tip, '--') + ($paths | ForEach-Object { ":(top,literal)$_" }))
        if ($moved.Out) { throw "Paths in the restore plan changed since $($meta.aligned.Substring(0,10)):`n$($moved.Out.Replace([char]0, "`n"))" }
        Write-Note "HEAD moved since the plan (no restored path touched) - continuing"
    }
    $st = Invoke-GitX -Repo $wt -GitArgs (@('status', '--porcelain=v1', '-z', '--untracked-files=all', '--') + ($paths | ForEach-Object { ":(top,literal)$_" }))
    if ($st.Out) { throw "Uncommitted changes on restored paths:`n$($st.Out.Replace([char]0, "`n"))" }

    # Every target blob must exist in the repo or ship in blobs\ with a matching hash.
    $import = [ordered]@{}
    foreach ($sha in ($ops.Values | Where-Object { $_ } | Sort-Object -Unique)) {
        if (Test-Git $wt @('cat-file', '-e', "$sha^{blob}")) { continue }
        $f = Join-Path $RestoreDir "blobs/$sha"
        if (-not (Test-Path -LiteralPath $f)) { throw "Blob $sha is neither in the repository nor in $f." }
        $h = Invoke-Git $wt @('hash-object', '--no-filters', '--', $f)
        if ($h -ne $sha) { throw "$f hashes to $h (expected $sha). Do not edit blobs\; change the resolution after applying instead." }
        $import[$sha] = $f
    }

    $byDecision = $rows | Where-Object { $ops.Contains($_.Path) } | Group-Object Decision | ForEach-Object { "$($_.Name)=$($_.Count)" }
    $nDel = @($ops.Values | Where-Object { -not $_ }).Count
    Write-Plan "$($paths.Count) path(s): $($byDecision -join ', '); $($paths.Count - $nDel) write, $nDel delete; $($import.Count) blob(s) to import"
    foreach ($r in ($rows | Where-Object { $ops.Contains($_.Path) } | Sort-Object Decision, Path)) { Write-Plan ("{0,-9} {1}" -f $r.Decision, $r.Path) }
    if (-not $Apply) { Write-Note 'Dry-run. Re-run with -Apply [-Commit].'; return }
    if (-not (Confirm-Step "Write $($paths.Count) path(s) into $wt ?")) { throw 'Stopped by user.' }

    foreach ($sha in $import.Keys) {
        $h = Invoke-Git $wt @('hash-object', '-w', '--no-filters', '--', $import[$sha])
        if ($h -ne $sha) { throw "import of $sha produced $h" }
    }
    # --index-info: "<mode> <sha>\t<path>"; mode 0 with a zero sha removes the entry.
    $zero = '0' * 40
    $info = ($paths | ForEach-Object { if ($ops[$_]) { "$($mode[$_]) $($ops[$_])`t$_" } else { "0 $zero`t$_" } }) -join "`n"
    Invoke-GitX -Repo $wt -GitArgs @('update-index', '--index-info') -InputBytes ([Text.Encoding]::UTF8.GetBytes($info + "`n")) | Out-Null
    # Delete before writing: on a case-insensitive filesystem a deleted path can name the same file
    # as a written one (appsettings.Development.WRH.json vs appsettings.development.WRH.json).
    foreach ($p in ($paths | Where-Object { -not $ops[$_] })) {
        $full = Join-Path $wt $p
        if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Force }
    }
    $write = @($paths | Where-Object { $ops[$_] })
    if ($write) { Invoke-GitX -Repo $wt -GitArgs @('checkout-index', '-f', '-z', '--stdin') -InputBytes ([Text.Encoding]::UTF8.GetBytes(($write -join [char]0) + [char]0)) | Out-Null }

    # Verify the index holds exactly the planned blobs.
    $ls = (Invoke-GitX -Repo $wt -GitArgs (@('ls-files', '-s', '-z', '--') + ($paths | ForEach-Object { ":(top,literal)$_" }))).Out.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)
    $now = @{}; foreach ($e in $ls) { $m, $s, $rest = $e.Split(' ', 3); $now[$rest.Substring($rest.IndexOf("`t") + 1)] = $s }
    $wrong = @($paths | Where-Object { [string]$now[$_] -ne [string]$ops[$_] })
    if ($wrong) { throw "Index does not match the plan for:`n$($wrong -join "`n")" }
    Write-Ok "index matches the plan ($($paths.Count) paths)"
    ((Invoke-Git $wt @('diff', '--cached', '--shortstat')) -split "`n") | ForEach-Object { Write-Plan $_ }

    if ($Commit) {
        $bulk = @($meta.bulk | ForEach-Object { $_.Substring(0, 10) }) -join ', '
        $body = "Restores development ($($meta.development.Substring(0,10))) changes that the bulk-copy commits ($bulk) overwrote.`n" +
                "Restore plan $(Split-Path $RestoreDir -Leaf): $($byDecision -join ', ').`n" +
                "Conflict resolutions and review calls: resolutions.md in the restore plan folder."
        Invoke-Git $wt @('commit', '--quiet', '-m', (Get-CommitMessage $cfg 'fix(fork-sync): restore development changes overwritten by bulk copies' $body)) | Out-Null
        $sha = Invoke-Git $wt @('rev-parse', 'HEAD')
        @{ appliedAt = (Get-Date).ToString('o'); commit = $sha; branch = $cfg.Isl.NewBranch; paths = $paths.Count } | ConvertTo-Json | Set-Content (Join-Path $RestoreDir 'applied.json') -Encoding utf8
        Write-Ok "committed $($sha.Substring(0, 10))"
    } else { Write-Note "staged, not committed. Review: git -C `"$wt`" diff --cached --stat" }
    Write-Ok 'Next: build the ISL list (04-Test-Alignment.ps1 -Build), then re-run 02/03 so shared restores reach Backend.'
} finally { Stop-AlignLog }
