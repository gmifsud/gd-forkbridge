#Requires -Version 7.0
<#
.SYNOPSIS
  Read-only. Computes a per-path alignment plan for the shared scope of the two branches:
  a scoped 3-way merge (base = SyncBase, ours = ISL, theirs = Backend) plus rules that
  guarantee no semantic change is dropped. Writes plan.tsv (editable Decision column) and
  plan.md (review summary) under StateDir\plans\<timestamp>.

  -Preview plans against the configured BaseRefs (+ MergeRefs merged in memory) without
  needing the branches from 01 - use it before creating anything.
#>
[CmdletBinding()]
param([switch]$Preview, [string]$ConfigPath)
Import-Module (Join-Path $PSScriptRoot 'Align.Common.psm1') -Force
$cfg = Import-AlignConfig $ConfigPath

# C# text with comments removed and string/char literals emptied.
function Get-CodeOnly([string]$Text) {
    return [regex]::Replace($Text, '(?<c>/\*.*?\*/|//[^\r\n]*)|(?<s>@"(?:[^"]|"")*"|"(?:\\.|[^"\\\r\n])*"|''(?:\\.|[^''\\\r\n])*'')',
        { param($m) if ($m.Groups['c'].Success) { '' } else { '""' } }, 'Singleline')
}
# Types declared at namespace level (indent <= 4 spaces or one tab); nested types are file-private in practice.
function Get-TopLevelTypeNames([string]$Text) {
    $re = '(?m)^(?:\t| {0,4})(?:(?:public|internal|static|sealed|abstract|partial|readonly|unsafe|new|file|ref)\s+)*(?:record\s+(?:class|struct)|class|interface|enum|struct|record)\s+([A-Za-z_]\w*)'
    return @([regex]::Matches((Get-CodeOnly $Text), $re) | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
}
# True if $Text uses any of $Names other than by declaring a type of the same name.
function Test-CodeReferences([string]$Text, [string[]]$Names) {
    $alt = ($Names | ForEach-Object { [regex]::Escape($_) }) -join '|'
    $code = [regex]::Replace((Get-CodeOnly $Text), "\b(?:class|interface|enum|struct|record)\s+(?:$alt)\b", '')
    return [regex]::IsMatch($code, "\b(?:$alt)\b")
}
Start-AlignLog $cfg 'plan'
try {
    Assert-GitVersion
    $x = Initialize-Scratch $cfg
    Write-Ok "scratch repo $x (borrows objects from both repositories)"

    # ---- revisions ---------------------------------------------------------
    $rev = @{}
    foreach ($s in 'Backend', 'Isl') {
        $c = $cfg[$s]
        if ($Preview) {
            $r = Invoke-Git $c.Root @('rev-parse', '--verify', "$($c.BaseRef)^{commit}")
            foreach ($m in @($c.MergeRefs)) {
                if (-not $m) { continue }
                $ms = Invoke-Git $c.Root @('rev-parse', '--verify', "$m^{commit}")
                $out = Invoke-GitX -Repo $x -GitArgs @('merge-tree', '--write-tree', '--no-messages', $r, $ms) -OkExit @(0, 1)
                if ($out.Code -ne 0) { throw "Preview: $m does not merge cleanly into $($c.BaseRef)" }
                $r = ($out.Out -split "`n")[0].Trim()     # a tree from here on
                Write-Plan "$s preview includes $m"
            }
            $rev[$s] = $r
        } else {
            Assert-ScopeClean $cfg $s
            $rev[$s] = Assert-WorktreeBranch $cfg $s
        }
        Write-Ok "$s rev $($rev[$s].Substring(0, 10))$(if ($Preview) { ' (preview)' })"
    }
    if (-not (Test-Git $x @('cat-file', '-e', "$($cfg.SyncBase)^{commit}"))) { throw "SyncBase $($cfg.SyncBase) not found in the Backend object store (fetch Backend first)." }
    $histRevs = @($rev.Backend, $cfg.SyncBase) + @($cfg.Backend.HistoryRefs | Where-Object { $_ } | ForEach-Object { Invoke-Git $cfg.Backend.Root @('rev-parse', '--verify', "$_^{commit}") })

    # ---- scoped trees + 3-way merge ---------------------------------------
    Write-Step 'Scoped 3-way merge'
    $BaseMap = Get-ScopedEntries $x $cfg $cfg.SyncBase
    $IslMap = Get-ScopedEntries $x $cfg $rev.Isl
    $BeMap = Get-ScopedEntries $x $cfg $rev.Backend
    $bT = New-TreeFromEntries $x $BaseMap; $iT = New-TreeFromEntries $x $IslMap; $eT = New-TreeFromEntries $x $BeMap
    $mr = Invoke-GitX -Repo $x -GitArgs @('-c', 'core.quotePath=false', 'merge-tree', '--write-tree', '-z', '--name-only', '--no-messages', "--merge-base=$bT", $iT, $eT) -OkExit @(0, 1)
    $tok = $mr.Out.Split([char]0)
    $mT = $tok[0].Trim()
    $conflicts = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($t in ($tok | Select-Object -Skip 1)) { if ($t) { [void]$conflicts.Add($t) } }
    $MrgMap = Get-ScopedEntries $x $cfg $mT
    Write-Ok "paths: base=$($BaseMap.Count) isl=$($IslMap.Count) backend=$($BeMap.Count); merge-tree conflicts=$($conflicts.Count)"

    # ---- classification ----------------------------------------------------
    Write-Step 'Classify'
    $paths = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    foreach ($d in $BaseMap, $IslMap, $BeMap) { foreach ($k in $d.Keys) { [void]$paths.Add($k) } }
    $rows = [Collections.Generic.List[hashtable]]::new()
    $same = 0
    foreach ($p in $paths) {
        $b = if ($BaseMap.Contains($p)) { $BaseMap[$p].Sha }; $i = if ($IslMap.Contains($p)) { $IslMap[$p].Sha }; $e = if ($BeMap.Contains($p)) { $BeMap[$p].Sha }
        if ($i -eq $e) { $same++; continue }
        $row = @{ Path = $p; Base = $b; Isl = $i; Backend = $e; Refs = ''; Reason = ''
                  Merged = $(if ($MrgMap.Contains($p)) { $MrgMap[$p].Sha }); MergedMode = $(if ($MrgMap.Contains($p)) { $MrgMap[$p].Mode })
                  IslMode = $(if ($IslMap.Contains($p)) { $IslMap[$p].Mode }); BackendMode = $(if ($BeMap.Contains($p)) { $BeMap[$p].Mode }) }
        $fi = $cfg.FormatInsensitive -contains [IO.Path]::GetExtension($p).ToLowerInvariant()
        $fmtI = $fi -and $b -and $i -and $b -ne $i -and (Test-FormatOnlyEqual $x $i $b)
        $fmtE = $fi -and $b -and $e -and $b -ne $e -and (Test-FormatOnlyEqual $x $e $b)
        $iEff = if ($fmtI) { $b } else { $i }; $eEff = if ($fmtE) { $b } else { $e }
        $cat, $dec, $why = $null, $null, ''
        if ([IO.Path]::GetFileName($p) -eq 'Directory.Packages.props' -and $i -and $e) {
            $cat, $dec, $why = 'PACKAGES', 'PACKAGES_UNION', 'add-only union of PackageVersion entries; version differences reported'
        } elseif ($iEff -eq $eEff -or ($i -and $e -and (Test-FormatOnlyEqual $x $i $e))) {
            $cat, $dec, $why = 'FORMAT_ONLY', 'TAKE_BE', 'whitespace/BOM-only difference; Backend formatting kept'
        } elseif ($iEff -eq $b) {
            $cat = if (-not $e) { 'BE_DELETED' } elseif (-not $b) { 'BE_ADDED' } else { 'BE_CHANGED' }
            $dec = 'TAKE_BE'; $why = 'Backend-only change since sync' + $(if ($fmtI) { '; ISL change was format-only' } else { '' })
        } elseif ($eEff -eq $b) {
            if (-not $i) { $cat, $dec, $why = 'ISL_DELETED', 'TAKE_BE', 'ISL deletion not propagated: restored in ISL (no code loss). TAKE_ISL deletes it in Backend.' }
            elseif (-not $b) { $cat, $dec = 'ISL_ADDED', 'KEEP_ISL_ONLY' }
            else { $cat, $dec, $why = 'ISL_CHANGED', 'TAKE_ISL', 'semantic ISL change since sync: promoted to Backend' + $(if ($fmtE) { '; Backend change was format-only' } else { '' }) }
        } elseif ($i -and $e -and -not $conflicts.Contains($p) -and $row.Merged) {
            $cat, $dec, $why = 'BOTH_CHANGED', 'MERGED', 'both changed; clean 3-way merge'
        } else {
            $cat, $dec, $why = 'CONFLICT', 'MANUAL', $(if ($i -and $e) { 'content conflict; Merged column holds the blob with conflict markers' } else { 'modify/delete conflict' })
        }
        $row.Category = $cat; $row.Decision = $dec; $row.Reason = $why
        $rows.Add($row)
    }

    # ---- ISL-only additions: fork-owned / rename / stale / other -----------
    $added = @($rows | Where-Object { $_.Category -eq 'ISL_ADDED' })
    $deleted = @($rows | Where-Object { $_.Category -eq 'ISL_DELETED' })
    $delKeys = @{}; foreach ($r in $deleted) { $delKeys[(Get-ContentKey $x $r.Backend)] = $r }
    $hist = @{}
    $cand = @($added | Where-Object { -not (Test-Like $_.Path $cfg.ForkOwned) } | ForEach-Object { $_.Path })
    for ($k = 0; $k -lt $cand.Count; $k += 40) {
        $chunk = $cand[$k..([Math]::Min($k + 39, $cand.Count - 1))]
        $log = Invoke-Git $x (@('-c', 'core.quotePath=false', 'log', '--format=', '--raw', '--no-abbrev', '--no-renames') + $histRevs + @('--') + $chunk)
        foreach ($l in ($log -split "`n")) {
            if ($l -notmatch '^:\d+ \d+ [0-9a-f]+ ([0-9a-f]+) \w+\t(.+)$') { continue }
            if (-not $hist[$Matches[2]]) { $hist[$Matches[2]] = [Collections.Generic.HashSet[string]]::new() }
            [void]$hist[$Matches[2]].Add($Matches[1])
        }
    }
    foreach ($r in $added) {
        if (Test-Like $r.Path $cfg.ForkOwned) { $r.Category = 'FORK_OWNED'; $r.Reason = 'fork-owned path (config ForkOwned)'; continue }
        $key = Get-ContentKey $x $r.Isl
        if ($delKeys.ContainsKey($key)) {
            $src = $delKeys[$key]
            $r.Category, $r.Decision, $r.Reason = 'ISL_RENAMED', 'DELETE_BOTH', "pure rename of $($src.Path) (identical content); Backend name kept"
            $src.Category, $src.Reason = 'ISL_RENAME_SOURCE', "ISL renamed this to $($r.Path) with identical content; Backend name restored in ISL"
            continue
        }
        if ($hist[$r.Path] -and $hist[$r.Path].Contains($r.Isl)) {
            $r.Category, $r.Decision, $r.Reason = 'ISL_STALE', 'DELETE_BOTH', 'exact content Backend held before deleting it; left behind by the additive overlay'
        } else {
            $r.Category = 'ISL_ONLY'
            $r.Reason = if ($r.Path -match '\.(bak|orig)$') { 'fork addition; cleanup candidate (backup file)' }
                        elseif ($hist[$r.Path]) { 'fork addition; Backend once had this path with different content - review' }
                        else { 'fork addition (not in Backend history)' }
        }
    }

    # ---- overrides ---------------------------------------------------------
    foreach ($r in $rows) {
        foreach ($o in @($cfg.Overrides)) {
            if ($r.Path -clike $o.Pattern) { $r.Decision = $o.Decision; $r.Reason = "override: $($o.Reason)"; break }
        }
    }

    # ---- annotate against the Backend reference branch (QiSL is primary) ----
    if ($cfg.Backend.ReferenceRef) {
        $refSha = Invoke-Git $cfg.Backend.Root @('rev-parse', '--verify', "$($cfg.Backend.ReferenceRef)^{commit}")
        foreach ($r in $rows) {
            if ($r.Decision -notin 'TAKE_ISL', 'MERGED', 'KEEP_BOTH', 'MANUAL') { continue }
            $d = (Invoke-GitX -Repo $x -GitArgs @('rev-parse', '--verify', '--quiet', "${refSha}:$($r.Path)") -OkExit @(0, 1)).Out.Trim()
            $tag = if (-not $d) { "absent in $($cfg.Backend.ReferenceRef)" } elseif ($d -eq $r.Backend -and $d -eq $r.Isl) { 'both match' } elseif ($d -eq $r.Backend) { "Backend matches $($cfg.Backend.ReferenceRef)" } elseif ($d -eq $r.Isl) { "ISL matches $($cfg.Backend.ReferenceRef)" } else { "neither matches $($cfg.Backend.ReferenceRef)" }
            $r.Reason += " [$tag]"
        }
    }

    # ---- reference fixpoint for deletions in ISL ---------------------------
    Write-Step 'Reference check for ISL deletions'
    $islFull = Invoke-Git $x @('rev-parse', "$($rev.Isl)^{tree}")
    $changes = [ordered]@{}; $dels = [Collections.Generic.List[string]]::new()
    foreach ($r in $rows) {
        $t = Get-SideTarget $r 'Isl'
        if ($t -eq 'PACKAGES') { continue }
        if ($null -eq $t) { if ($r.Isl) { $dels.Add($r.Path) } } elseif ($t.Sha -ne $r.Isl) { $changes[$r.Path] = $t }
    }
    $projected = New-TreeFromEntries $x $changes $islFull $dels
    $delRows = @($rows | Where-Object { $_.Isl -and $null -eq (Get-SideTarget $_ 'Isl') -and $_.Category -ne 'ISL_RENAMED' })
    $symbols = @{}
    foreach ($r in $delRows) { $r.ExtRefs = @(); $r.KeptBy = @() }
    foreach ($r in $delRows) {
        $names = if ($r.Path -like '*.cs') { @(Get-TopLevelTypeNames (ConvertTo-Text (Get-BlobBytes $x $r.Isl))) } else { @([IO.Path]::GetFileName($r.Path)) }
        $symbols[$r.Path] = $names
        if (-not $names) { continue }
        $alt = ($names | ForEach-Object { [regex]::Escape($_) }) -join '|'
        # a line that only declares the same name (e.g. nested SpecsFor classes) is not a reference
        $ga = if ($r.Path -like '*.cs') {
            @('grep', '-l', '-w', '-E', '(') + @($names | ForEach-Object { '-e'; [regex]::Escape($_) }) + @(')', '--and', '--not', '-e', "(class|interface|enum|struct|record)\s+($alt)\b")
        } else { @('grep', '-l', '-w', '-F') + @($names | ForEach-Object { '-e'; $_ }) }
        $ga += @($projected, '--', '*.cs', '*.csproj', '*.sln', '*.props', '*.targets', '*.tt', '*.cshtml')
        $hits = (Invoke-GitX -Repo $x -GitArgs $ga -OkExit @(0, 1)).Out -split "`n" | Where-Object { $_ } | ForEach-Object { $_.Substring($projected.Length + 1) }
        $r.ExtRefs = @($hits)
    }
    # stale files referenced from surviving code (or from other kept stale files) are kept
    $keep = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $movable = @($delRows | Where-Object { $_.Category -eq 'ISL_STALE' })
    do {
        $grew = $false
        foreach ($r in $movable) {
            if ($keep.Contains($r.Path)) { continue }
            $byKept = @($movable | Where-Object { $keep.Contains($_.Path) -and $symbols[$r.Path] -and (Test-CodeReferences (ConvertTo-Text (Get-BlobBytes $x $_.Isl)) $symbols[$r.Path]) } | ForEach-Object { $_.Path })
            if ($r.ExtRefs.Count -gt 0 -or $byKept.Count -gt 0) { [void]$keep.Add($r.Path); $r.KeptBy = @($r.ExtRefs) + $byKept; $grew = $true }
        }
    } while ($grew)
    foreach ($r in $delRows) {
        $refs = if ($r.KeptBy.Count) { @($r.KeptBy) } else { @($r.ExtRefs) }
        if ($refs.Count) { $r.Refs = "$($refs.Count): " + (($refs | Select-Object -First 4 | ForEach-Object { Split-Path $_ -Leaf }) -join ', ') }
        if ($keep.Contains($r.Path)) { $r.Decision = 'KEEP_ISL_ONLY'; $r.Reason = 'stale Backend file still referenced in ISL; retarget the references, then set DELETE_BOTH' }
        elseif ($refs.Count -and $r.Decision -eq 'TAKE_BE') { $r.Reason += '; WARNING ISL references remain - fix before build' }
    }
    Write-Ok "deletions in ISL: $(@($delRows | Where-Object { -not $keep.Contains($_.Path) }).Count); kept because referenced: $($keep.Count)"

    # ---- write plan --------------------------------------------------------
    $dir = Join-Path $cfg.StateDir ('plans/{0}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $meta = @{
        created = (Get-Date).ToString('o'); preview = [bool]$Preview; syncBase = $cfg.SyncBase; scope = $cfg.Scope
        backend = @{ branch = $cfg.Backend.NewBranch; rev = $rev.Backend; scopedTree = $eT }
        isl     = @{ branch = $cfg.Isl.NewBranch; rev = $rev.Isl; scopedTree = $iT }
        baseTree = $bT; mergedTree = $mT; same = $same
    }
    Write-AlignPlan $dir $meta $rows
    Set-Content -Path (Join-Path $cfg.StateDir 'latest-plan.txt') -Value $dir -NoNewline

    # ---- plan.md -----------------------------------------------------------
    $md = [Collections.Generic.List[string]]::new()
    $md.Add("# Alignment plan $(Split-Path $dir -Leaf)$(if ($Preview) { ' (preview)' })"); $md.Add('')
    $md.Add("| | Backend | ISL |"); $md.Add('|---|---|---|')
    $md.Add("| branch | ``$($cfg.Backend.NewBranch)`` | ``$($cfg.Isl.NewBranch)`` |")
    $md.Add("| rev | ``$($rev.Backend.Substring(0,10))`` | ``$($rev.Isl.Substring(0,10))`` |")
    $md.Add(''); $md.Add("Sync base ``$($cfg.SyncBase.Substring(0,10))``; scope: $($cfg.Scope -join ', '). Identical paths: **$same**. Rows below: **$($rows.Count)**."); $md.Add('')
    $md.Add('| Category | Decision | Count |'); $md.Add('|---|---|---|')
    foreach ($g in ($rows | Group-Object { "$($_.Category)|$($_.Decision)" } | Sort-Object Name)) { $c, $d = $g.Name.Split('|'); $md.Add("| $c | $d | $($g.Count) |") }
    $section = {
        param($title, $filter, [switch]$Numstat)
        $sel = @($rows | Where-Object $filter)
        if (-not $sel) { return }
        $md.Add(''); $md.Add("## $title ($($sel.Count))"); $md.Add('')
        foreach ($r in $sel) {
            $ns = ''
            if ($Numstat -and $r.Base -and $r.Isl) { $n = (Invoke-Git $x @('diff', '--numstat', $r.Base, $r.Isl)) -split '\s+'; if ($n.Count -ge 2) { $ns = " (+$($n[0]) -$($n[1]))" } }
            $md.Add("- ``$($r.Path)``$ns - $($r.Reason)$(if ($r.Refs) { " [refs $($r.Refs)]" })")
        }
    }
    & $section 'MANUAL - resolve before 03' { $_.Decision -eq 'MANUAL' }
    & $section 'Promoted ISL -> Backend (review each)' { $_.Decision -eq 'TAKE_ISL' } -Numstat
    & $section 'Merged into both' { $_.Decision -eq 'MERGED' }
    & $section 'Accepted divergence (KEEP_BOTH)' { $_.Decision -eq 'KEEP_BOTH' }
    & $section 'Deleted in ISL' { $_.Isl -and $null -eq (Get-SideTarget $_ 'Isl') }
    & $section 'Stale but kept (referenced)' { $_.Category -eq 'ISL_STALE' -and $_.Decision -eq 'KEEP_ISL_ONLY' }
    & $section 'ISL-only additions kept (not fork-owned)' { $_.Category -eq 'ISL_ONLY' }
    $pk = @($rows | Where-Object { $_.Decision -eq 'PACKAGES_UNION' })
    foreach ($r in $pk) {
        $be = ConvertTo-Text (Get-BlobBytes $x $r.Backend); $is = ConvertTo-Text (Get-BlobBytes $x $r.Isl)
        $md.Add(''); $md.Add("## $($r.Path) - union"); $md.Add('')
        $md.Add('Backend:'); (Merge-PackageProps $be $is 'ISL' $cfg.PackageVersionOverrides).Report | ForEach-Object { $md.Add("- $_") }
        $md.Add('ISL:'); (Merge-PackageProps $is $be 'Backend' $cfg.PackageVersionOverrides).Report | ForEach-Object { $md.Add("- $_") }
    }
    $md.Add(''); $md.Add('Edit `plan.tsv` Decision values if needed, then run `03-Invoke-Alignment.ps1 -Apply -Commit`.')
    [IO.File]::WriteAllLines((Join-Path $dir 'plan.md'), $md, [Text.UTF8Encoding]::new($false))

    Write-Step 'Summary'
    $rows | Group-Object { "$($_.Category) / $($_.Decision)" } | Sort-Object Name | ForEach-Object { Write-Plan ("{0,-34} {1,5}" -f $_.Name, $_.Count) }
    Write-Ok "identical: $same"
    $man = @($rows | Where-Object { $_.Decision -eq 'MANUAL' }).Count
    if ($man) { Write-Note "$man MANUAL row(s) - edit plan.tsv before 03" }
    Write-Ok "plan: $(Join-Path $dir 'plan.md')"
} finally { Stop-AlignLog }
