#Requires -Version 7.0
<#
.SYNOPSIS
  Read-only. Computes a per-path alignment plan for the shared scope of the two branches:
  a scoped 3-way merge (base = SyncBase, ours = ISL, theirs = Frontend) plus rules that
  guarantee no semantic change is dropped. Writes plan.tsv (editable Decision column) and
  plan.md (review summary) under StateDir\plans\<timestamp>.

  Frontend-specific rule: ISL-Frontend imports Frontend commit by commit, so an ISL file that
  equals an EARLIER Frontend version of the same path (ISL_BEHIND) carries no ISL change and
  takes the Frontend version, instead of being mistaken for an ISL edit or a conflict.

  -Preview plans against the configured BaseRefs (+ MergeRefs merged in memory) without
  needing the branches from 01 - use it before creating anything.
  -SyncBase overrides the configured base for this run (compare candidates with -Preview).
#>
[CmdletBinding()]
param([switch]$Preview, [string]$SyncBase, [string]$ConfigPath)
Import-Module (Join-Path $PSScriptRoot 'Align.Common.psm1') -Force
$cfg = Import-AlignConfig $ConfigPath
if ($SyncBase) { $cfg.SyncBase = $SyncBase }

# TypeScript with comments removed and string/template literals emptied (import specifiers are strings:
# the file-stem check below reads the raw text for that reason).
function Get-TsCodeOnly([string]$Text) {
    return [regex]::Replace($Text, '(?<c>/\*.*?\*/|//[^\r\n]*)|(?<s>`(?:\\.|[^`\\])*`|"(?:\\.|[^"\\\r\n])*"|''(?:\\.|[^''\\\r\n])*'')',
        { param($m) if ($m.Groups['c'].Success) { '' } else { '""' } }, 'Singleline')
}
# Names a TS file exports (class, interface, enum, type, function, const/let/var).
function Get-TsExportNames([string]$Text) {
    $re = '(?m)^\s*export\s+(?:default\s+)?(?:declare\s+)?(?:abstract\s+)?(?:const\s+enum|class|interface|enum|type|function\*?|const|let|var)\s+([A-Za-z_$][\w$]*)'
    return @([regex]::Matches((Get-TsCodeOnly $Text), $re) | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
}
# Token a file is referenced by: a .ts file through its import specifier (stem), anything else by file name.
$script:GenericStems = @('index', 'main', 'public-api', 'public_api', 'polyfills', 'test', 'module', 'models', 'types', 'enums', 'constants')
function Get-FileToken([string]$Path) {
    $leaf = [IO.Path]::GetFileName($Path)
    if ($Path -like '*.ts') {
        $stem = [IO.Path]::GetFileNameWithoutExtension($leaf)
        if ($stem.Length -lt 4 -or $script:GenericStems -contains $stem.ToLowerInvariant()) { return $null }
        return $stem
    }
    return $leaf
}
# True if $Text uses any of $Names (outside a same-name declaration) or mentions $Token as a word.
function Test-TsReferences([string]$Text, [string[]]$Names, [string]$Token) {
    if ($Token -and [regex]::IsMatch($Text, '(?<![\w.-])' + [regex]::Escape($Token) + '(?![\w-])')) { return $true }
    if (-not $Names) { return $false }
    $alt = ($Names | ForEach-Object { [regex]::Escape($_) }) -join '|'
    $code = [regex]::Replace((Get-TsCodeOnly $Text), "\b(?:class|interface|enum|type|function|const|let|var)\s+(?:$alt)\b", '')
    return [regex]::IsMatch($code, "(?<![\w$])(?:$alt)(?![\w$])")
}

Start-AlignLog $cfg 'plan'
try {
    Assert-GitVersion
    $x = Initialize-Scratch $cfg
    Write-Ok "scratch repo $x (borrows objects from both repositories)"

    # ---- revisions ---------------------------------------------------------
    $rev = @{}; $commitRev = @{}
    foreach ($s in 'Frontend', 'Isl') {
        $c = $cfg[$s]
        if ($Preview) {
            $r = Invoke-Git $c.Root @('rev-parse', '--verify', "$($c.BaseRef)^{commit}")
            $commitRev[$s] = $r
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
            $commitRev[$s] = $rev[$s]
        }
        Write-Ok "$s rev $($rev[$s].Substring(0, 10))$(if ($Preview) { ' (preview)' })"
    }
    if (-not (Test-Git $x @('cat-file', '-e', "$($cfg.SyncBase)^{commit}"))) { throw "SyncBase $($cfg.SyncBase) not found in either object store (fetch Frontend first)." }
    $histRevs = @($commitRev.Frontend, $cfg.SyncBase) + @($cfg.Frontend.HistoryRefs | Where-Object { $_ } | ForEach-Object { Invoke-Git $cfg.Frontend.Root @('rev-parse', '--verify', "$_^{commit}") })

    # ---- scoped trees + 3-way merge ---------------------------------------
    Write-Step 'Scoped 3-way merge'
    $BaseMap = Get-ScopedEntries $x $cfg $cfg.SyncBase
    $IslMap = Get-ScopedEntries $x $cfg $rev.Isl
    $FeMap = Get-ScopedEntries $x $cfg $rev.Frontend
    $bT = New-TreeFromEntries $x $BaseMap; $iT = New-TreeFromEntries $x $IslMap; $eT = New-TreeFromEntries $x $FeMap
    $mr = Invoke-GitX -Repo $x -GitArgs @('-c', 'core.quotePath=false', 'merge-tree', '--write-tree', '-z', '--name-only', '--no-messages', "--merge-base=$bT", $iT, $eT) -OkExit @(0, 1)
    $tok = $mr.Out.Split([char]0)
    $mT = $tok[0].Trim()
    $conflicts = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($t in ($tok | Select-Object -Skip 1)) { if ($t) { [void]$conflicts.Add($t) } }
    $MrgMap = Get-ScopedEntries $x $cfg $mT
    Write-Ok "paths: base=$($BaseMap.Count) isl=$($IslMap.Count) frontend=$($FeMap.Count); merge-tree conflicts=$($conflicts.Count)"

    # ---- Frontend history of every differing ISL blob ----------------------
    Write-Step 'Frontend history (ISL_BEHIND / ISL_STALE)'
    $hist = [hashtable]::new([StringComparer]::Ordinal)     # paths are case-sensitive in git
    $cand = @($IslMap.Keys | Where-Object { -not $FeMap.Contains($_) -or $FeMap[$_].Sha -ne $IslMap[$_].Sha })
    for ($k = 0; $k -lt $cand.Count; $k += 40) {
        $chunk = $cand[$k..([Math]::Min($k + 39, $cand.Count - 1))]
        $log = Invoke-Git $x (@('-c', 'core.quotePath=false', 'log', '--format=', '--raw', '--no-abbrev', '--no-renames') + $histRevs + @('--') + $chunk)
        foreach ($l in ($log -split "`n")) {
            if ($l -notmatch '^:\d+ \d+ [0-9a-f]+ ([0-9a-f]+) \w+\t(.+)$') { continue }
            if (-not $hist[$Matches[2]]) { $hist[$Matches[2]] = [Collections.Generic.HashSet[string]]::new() }
            [void]$hist[$Matches[2]].Add($Matches[1])
        }
    }
    Write-Ok "$($cand.Count) differing ISL path(s) checked against $($histRevs.Count) Frontend ref(s)"

    # ---- classification ----------------------------------------------------
    Write-Step 'Classify'
    $paths = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    foreach ($d in $BaseMap, $IslMap, $FeMap) { foreach ($k in $d.Keys) { [void]$paths.Add($k) } }
    $rows = [Collections.Generic.List[hashtable]]::new()
    $same = 0
    foreach ($p in $paths) {
        $b = if ($BaseMap.Contains($p)) { $BaseMap[$p].Sha }; $i = if ($IslMap.Contains($p)) { $IslMap[$p].Sha }; $e = if ($FeMap.Contains($p)) { $FeMap[$p].Sha }
        if ($i -eq $e) { $same++; continue }
        $row = @{ Path = $p; Base = $b; Isl = $i; Frontend = $e; Refs = ''; Reason = ''
                  Merged = $(if ($MrgMap.Contains($p)) { $MrgMap[$p].Sha }); MergedMode = $(if ($MrgMap.Contains($p)) { $MrgMap[$p].Mode })
                  IslMode = $(if ($IslMap.Contains($p)) { $IslMap[$p].Mode }); FrontendMode = $(if ($FeMap.Contains($p)) { $FeMap[$p].Mode }) }
        $row.Clean = [bool]($i -and $e -and $row.Merged -and -not $conflicts.Contains($p))
        $fi = $cfg.FormatInsensitive -contains [IO.Path]::GetExtension($p).ToLowerInvariant()
        $fmtI = $fi -and $b -and $i -and $b -ne $i -and (Test-FormatOnlyEqual $x $i $b)
        $fmtE = $fi -and $b -and $e -and $b -ne $e -and (Test-FormatOnlyEqual $x $e $b)
        $iEff = if ($fmtI) { $b } else { $i }; $eEff = if ($fmtE) { $b } else { $e }
        $behind = $i -and $null -ne $hist[$p] -and $hist[$p].Contains($i) -and -not (Test-Like $p $cfg.ForkOwned)
        $cat, $dec, $why = $null, $null, ''
        if ($iEff -eq $eEff -or ($i -and $e -and $fi -and (Test-FormatOnlyEqual $x $i $e))) {
            $cat, $dec, $why = 'FORMAT_ONLY', 'TAKE_FE', 'whitespace/BOM-only difference; Frontend formatting kept'
        } elseif ($iEff -eq $b) {
            $cat = if (-not $e) { 'FE_DELETED' } elseif (-not $b) { 'FE_ADDED' } else { 'FE_CHANGED' }
            $dec = 'TAKE_FE'; $why = 'Frontend-only change since sync' + $(if ($fmtI) { '; ISL change was format-only' } else { '' })
        } elseif ($behind) {
            if ($e) { $cat, $dec, $why = 'ISL_BEHIND', 'TAKE_FE', 'ISL holds an earlier Frontend version of this file (partial per-commit import); Frontend version taken' }
            else { $cat, $dec, $why = 'ISL_STALE', 'DELETE_BOTH', 'exact content Frontend held before deleting it; left behind by an import' }
        } elseif ($i -and $e -and (Test-Like $p $cfg.PackageFiles)) {
            $cat, $dec, $why = 'PACKAGES', 'PACKAGES_UNION', 'add-only union of dependency entries; version differences reported'
        } elseif ($eEff -eq $b) {
            if (-not $i) { $cat, $dec, $why = 'ISL_DELETED', 'TAKE_FE', 'ISL deletion not propagated: restored in ISL (no code loss). TAKE_ISL deletes it in Frontend.' }
            elseif (-not $b) { $cat, $dec = 'ISL_ADDED', 'KEEP_ISL_ONLY' }
            else { $cat, $dec, $why = 'ISL_CHANGED', 'TAKE_ISL', ('semantic ISL change since sync: promoted to Frontend' + $(if ($fmtE) { '; Frontend change was format-only' } else { '' })) }
        } elseif ($row.Clean) {
            $cat, $dec, $why = 'BOTH_CHANGED', 'MERGED', 'both changed; clean 3-way merge'
        } else {
            $cat, $dec, $why = 'CONFLICT', 'MANUAL', $(if ($i -and $e) { 'content conflict; Merged column holds the blob with conflict markers' } else { 'modify/delete conflict' })
        }
        $row.Category = $cat; $row.Decision = $dec; $row.Reason = $why
        $rows.Add($row)
    }

    # ---- package.json unions must be computable both ways -------------------
    foreach ($r in @($rows | Where-Object { $_.Decision -eq 'PACKAGES_UNION' })) {
        try {
            $fe = ConvertTo-Text (Get-BlobBytes $x $r.Frontend); $is = ConvertTo-Text (Get-BlobBytes $x $r.Isl)
            $null = Merge-PackageJson $fe $is 'ISL' $cfg.PackageSections $cfg.PackageVersionOverrides
            $null = Merge-PackageJson $is $fe 'Frontend' $cfg.PackageSections $cfg.PackageVersionOverrides
        } catch { $r.Decision = 'MANUAL'; $r.Reason = "package union not possible: $($_.Exception.Message)" }
    }

    # ---- ISL-only additions: fork-owned / rename / other -------------------
    $added = @($rows | Where-Object { $_.Category -eq 'ISL_ADDED' })
    $deleted = @($rows | Where-Object { $_.Category -eq 'ISL_DELETED' })
    $delKeys = @{}; foreach ($r in $deleted) { $delKeys[(Get-ContentKey $x $r.Frontend)] = $r }
    foreach ($r in $added) {
        if (Test-Like $r.Path $cfg.ForkOwned) { $r.Category = 'FORK_OWNED'; $r.Reason = 'fork-owned path (config ForkOwned)'; continue }
        $key = Get-ContentKey $x $r.Isl
        if ($delKeys.ContainsKey($key)) {
            $src = $delKeys[$key]
            $r.Category, $r.Decision, $r.Reason = 'ISL_RENAMED', 'DELETE_BOTH', "pure rename of $($src.Path) (identical content); Frontend name kept"
            $src.Category, $src.Reason = 'ISL_RENAME_SOURCE', "ISL renamed this to $($r.Path) with identical content; Frontend name restored in ISL"
            continue
        }
        $r.Category = 'ISL_ONLY'
        $r.Reason = if ($r.Path -match '\.(bak|orig|backup)$') { 'fork addition; cleanup candidate (backup file)' }
                    elseif ($null -ne $hist[$r.Path]) { 'fork addition; Frontend once had this path with different content - review' }
                    else { 'fork addition (not in Frontend history)' }
    }

    # ---- overrides ---------------------------------------------------------
    foreach ($r in $rows) {
        foreach ($o in @($cfg.Overrides)) {
            if ($r.Path -clike $o.Pattern) {
                $note = "default $($r.Decision)" + $(if ($r.Clean -and $o.Decision -ne 'MERGED') { '; clean 3-way merge available - set MERGED to take it' } else { '' })
                $r.Decision = $o.Decision; $r.Reason = "override: $($o.Reason) [$note]"; break
            }
        }
    }

    # ---- annotate against the Frontend reference branch (QiSL is primary) ---
    if ($cfg.Frontend.ContainsKey('ReferenceRef') -and $cfg.Frontend.ReferenceRef) {
        $refName = $cfg.Frontend.ReferenceRef
        $refSha = Invoke-Git $cfg.Frontend.Root @('rev-parse', '--verify', "$refName^{commit}")
        foreach ($r in $rows) {
            if ($r.Decision -notin 'TAKE_ISL', 'MERGED', 'KEEP_BOTH', 'MANUAL') { continue }
            $d = (Invoke-GitX -Repo $x -GitArgs @('rev-parse', '--verify', '--quiet', "${refSha}:$($r.Path)") -OkExit @(0, 1)).Out.Trim()
            $tag = if (-not $d) { "absent in $refName" } elseif ($d -eq $r.Frontend -and $d -eq $r.Isl) { 'both match' } elseif ($d -eq $r.Frontend) { "Frontend matches $refName" } elseif ($d -eq $r.Isl) { "ISL matches $refName" } else { "neither matches $refName" }
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
    $symbols = [hashtable]::new([StringComparer]::Ordinal); $tokens = [hashtable]::new([StringComparer]::Ordinal)
    $refGlobs = @('*.ts', '*.html', '*.json', '*.scss', '*.css', '*.less', '*.js')
    foreach ($r in $delRows) { $r.ExtRefs = @(); $r.KeptBy = @() }
    foreach ($r in $delRows) {
        $names = if ($r.Path -like '*.ts') { @(Get-TsExportNames (ConvertTo-Text (Get-BlobBytes $x $r.Isl))) } else { @() }
        $token = Get-FileToken $r.Path
        $symbols[$r.Path] = $names; $tokens[$r.Path] = $token
        $hits = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        if ($names) {
            $alt = ($names | ForEach-Object { [regex]::Escape($_) }) -join '|'
            # a line that only declares the same name is not a reference
            $ga = @('grep', '-l', '-w', '-E', '(') + @($names | ForEach-Object { '-e'; [regex]::Escape($_) }) + @(')', '--and', '--not', '-e', "(class|interface|enum|type|function|const|let|var)\s+($alt)\b", $projected, '--', '*.ts', '*.html')
            (Invoke-GitX -Repo $x -GitArgs $ga -OkExit @(0, 1)).Out -split "`n" | Where-Object { $_ } | ForEach-Object { [void]$hits.Add($_.Substring($projected.Length + 1)) }
        }
        if ($token) {
            $ga = @('grep', '-l', '-w', '-F', '-e', $token, $projected, '--') + $refGlobs
            (Invoke-GitX -Repo $x -GitArgs $ga -OkExit @(0, 1)).Out -split "`n" | Where-Object { $_ } | ForEach-Object { [void]$hits.Add($_.Substring($projected.Length + 1)) }
        }
        $r.ExtRefs = @($hits)
    }
    # stale files referenced from surviving code (or from other kept stale files) are kept
    $keep = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $movable = @($delRows | Where-Object { $_.Category -eq 'ISL_STALE' })
    do {
        $grew = $false
        foreach ($r in $movable) {
            if ($keep.Contains($r.Path)) { continue }
            $byKept = @($movable | Where-Object { $keep.Contains($_.Path) -and (Test-TsReferences (ConvertTo-Text (Get-BlobBytes $x $_.Isl)) $symbols[$r.Path] $tokens[$r.Path]) } | ForEach-Object { $_.Path })
            if ($r.ExtRefs.Count -gt 0 -or $byKept.Count -gt 0) { [void]$keep.Add($r.Path); $r.KeptBy = @($r.ExtRefs) + $byKept; $grew = $true }
        }
    } while ($grew)
    foreach ($r in $delRows) {
        $refs = if ($r.KeptBy.Count) { @($r.KeptBy) } else { @($r.ExtRefs) }
        if ($refs.Count) { $r.Refs = "$($refs.Count): " + (($refs | Select-Object -First 4 | ForEach-Object { Split-Path $_ -Leaf }) -join ', ') }
        if ($keep.Contains($r.Path)) { $r.Decision = 'KEEP_ISL_ONLY'; $r.Reason = 'stale Frontend file still referenced in ISL; retarget the references, then set DELETE_BOTH' }
        elseif ($refs.Count -and $r.Decision -eq 'TAKE_FE') { $r.Reason += '; WARNING ISL references remain - fix before build' }
    }
    Write-Ok "deletions in ISL: $(@($delRows | Where-Object { -not $keep.Contains($_.Path) }).Count); kept because referenced: $($keep.Count)"

    # ---- write plan --------------------------------------------------------
    $dir = Join-Path $cfg.StateDir ('plans/{0}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $meta = @{
        created = (Get-Date).ToString('o'); preview = [bool]$Preview; syncBase = $cfg.SyncBase; scope = $cfg.Scope
        frontend = @{ branch = $cfg.Frontend.NewBranch; rev = $rev.Frontend; scopedTree = $eT }
        isl      = @{ branch = $cfg.Isl.NewBranch; rev = $rev.Isl; scopedTree = $iT }
        baseTree = $bT; mergedTree = $mT; same = $same
    }
    Write-AlignPlan $dir $meta $rows
    Set-Content -Path (Join-Path $cfg.StateDir 'latest-plan.txt') -Value $dir -NoNewline

    # ---- plan.md -----------------------------------------------------------
    $md = [Collections.Generic.List[string]]::new()
    $md.Add("# Frontend alignment plan $(Split-Path $dir -Leaf)$(if ($Preview) { ' (preview)' })"); $md.Add('')
    $md.Add("| | Frontend | ISL |"); $md.Add('|---|---|---|')
    $md.Add("| branch | ``$($cfg.Frontend.NewBranch)`` | ``$($cfg.Isl.NewBranch)`` |")
    $md.Add("| rev | ``$($rev.Frontend.Substring(0,10))`` | ``$($rev.Isl.Substring(0,10))`` |")
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
    & $section 'Promoted ISL -> Frontend (review each)' { $_.Decision -eq 'TAKE_ISL' } -Numstat
    & $section 'Merged into both' { $_.Decision -eq 'MERGED' }
    & $section 'Accepted divergence (KEEP_BOTH)' { $_.Decision -eq 'KEEP_BOTH' }
    & $section 'Deleted in ISL' { $_.Isl -and $null -eq (Get-SideTarget $_ 'Isl') }
    & $section 'Stale but kept (referenced)' { $_.Category -eq 'ISL_STALE' -and $_.Decision -eq 'KEEP_ISL_ONLY' }
    & $section 'ISL-only additions kept (not fork-owned)' { $_.Category -eq 'ISL_ONLY' }
    & $section 'ISL behind Frontend (Frontend version taken)' { $_.Category -eq 'ISL_BEHIND' -and $_.Decision -eq 'TAKE_FE' }
    foreach ($r in @($rows | Where-Object { $_.Decision -eq 'PACKAGES_UNION' })) {
        $fe = ConvertTo-Text (Get-BlobBytes $x $r.Frontend); $is = ConvertTo-Text (Get-BlobBytes $x $r.Isl)
        $md.Add(''); $md.Add("## $($r.Path) - dependency union"); $md.Add('')
        $md.Add('Frontend:'); (Merge-PackageJson $fe $is 'ISL' $cfg.PackageSections $cfg.PackageVersionOverrides).Report | ForEach-Object { $md.Add("- $_") }
        $md.Add('ISL:'); (Merge-PackageJson $is $fe 'Frontend' $cfg.PackageSections $cfg.PackageVersionOverrides).Report | ForEach-Object { $md.Add("- $_") }
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
