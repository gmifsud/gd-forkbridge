#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# =============================================================================
# Shared helpers for the Frontend <-> ISL-Frontend branch alignment scripts.
# Mirrors scripts/branch-alignment (Backend) with Frontend/Isl sides, TypeScript-aware
# reference checks and an add-only package.json dependency union.
# Conventions: dry-run unless -Apply; no commit unless -Commit; push only in
# 05 with -Push. All cross-repo computation happens in a scratch bare repo
# whose object store borrows both repositories' objects (alternates), so
# neither repository receives refs or objects it did not create itself.
# =============================================================================

$script:Yes = $false
$script:ZeroSha = '0' * 40
$script:Sides = @('Frontend', 'Isl')

function Set-AlignYes([bool]$Value) { $script:Yes = $Value }

function Import-AlignConfig {
    param([string]$Path)
    if (-not $Path) { $Path = Join-Path $PSScriptRoot 'Align.Config.psd1' }
    $cfg = Import-PowerShellDataFile -Path $Path
    foreach ($s in $script:Sides) {
        foreach ($k in 'Root', 'WorktreeRoot', 'Worktree') {
            if ($cfg[$s].ContainsKey($k) -and $cfg[$s][$k] -match '^[Cc]:\\Repos(\\|$)') { throw "$s.$k uses the C:\Repos junction; use the canonical D:\Repos path." }
        }
        if (-not ($cfg[$s].ContainsKey('Worktree') -and $cfg[$s].Worktree)) {
            $cfg[$s].Worktree = [IO.Path]::Combine($cfg[$s].WorktreeRoot, ($cfg[$s].NewBranch -replace '/', [IO.Path]::DirectorySeparatorChar))
        }
    }
    foreach ($k in 'ForkOwned', 'Overrides', 'Guards', 'PackageFiles', 'PackageSections', 'MergeRefs') { if (-not $cfg.ContainsKey($k)) { $cfg[$k] = @() } }
    if (-not $cfg.ContainsKey('PackageVersionOverrides')) { $cfg.PackageVersionOverrides = @{} }
    $cfg.ConfigPath = (Resolve-Path $Path).Path
    return $cfg
}

function Get-OtherSide([string]$Side) { if ($Side -eq 'Isl') { 'Frontend' } else { 'Isl' } }
function Get-SideLabel([string]$Side) { if ($Side -eq 'Isl') { 'ISL' } else { 'Frontend' } }

# ---------------------------------------------------------------- output ----
function Write-Step([string]$m) { Write-Host "`n== $m" -ForegroundColor Cyan }
function Write-Ok([string]$m) { Write-Host "   OK  $m" -ForegroundColor Green }
function Write-Note([string]$m) { Write-Host "   !!  $m" -ForegroundColor Yellow }
function Write-Plan([string]$m) { Write-Host "   ->  $m" -ForegroundColor DarkGray }

function Confirm-Step([string]$Prompt) {
    if ($script:Yes) { return $true }
    return ((Read-Host "$Prompt  [y/N]") -match '^(y|yes)$')
}

function Start-AlignLog($Cfg, [string]$Name) {
    New-Item -ItemType Directory -Force -Path (Join-Path $Cfg.StateDir 'logs') | Out-Null
    $p = Join-Path $Cfg.StateDir ('logs/{0}-{1}.log' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $Name)
    Start-Transcript -Path $p | Out-Null
    Write-Plan "transcript: $p"
}
function Stop-AlignLog { try { Stop-Transcript | Out-Null } catch { } }

# ------------------------------------------------------------------- git ----
# Binary-safe git runner: arguments passed as a list (no shell quoting), optional
# stdin bytes, optional extra environment. Returns @{ Code; Out; Err; Bytes }.
function Invoke-GitX {
    param([string]$Repo, [string[]]$GitArgs, [byte[]]$InputBytes, [hashtable]$Env, [int[]]$OkExit = @(0))
    $psi = [Diagnostics.ProcessStartInfo]::new('git')
    if ($Repo) { $psi.ArgumentList.Add('-C'); $psi.ArgumentList.Add($Repo) }
    foreach ($a in $GitArgs) { $psi.ArgumentList.Add($a) }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.RedirectStandardInput = ($null -ne $InputBytes)
    if ($Env) { foreach ($k in $Env.Keys) { $psi.Environment[$k] = $Env[$k] } }
    $p = [Diagnostics.Process]::Start($psi)
    $errTask = $p.StandardError.ReadToEndAsync()
    $ms = [IO.MemoryStream]::new()
    $outTask = $p.StandardOutput.BaseStream.CopyToAsync($ms)
    if ($null -ne $InputBytes) { $p.StandardInput.BaseStream.Write($InputBytes, 0, $InputBytes.Length); $p.StandardInput.Close() }
    $outTask.Wait(); $p.WaitForExit()
    $bytes = $ms.ToArray()
    $r = @{ Code = $p.ExitCode; Bytes = $bytes; Out = [Text.Encoding]::UTF8.GetString($bytes); Err = $errTask.Result }
    if ($OkExit -notcontains $r.Code) { throw "git $($GitArgs -join ' ') [in $Repo] exited $($r.Code): $($r.Err.Trim())" }
    return $r
}
# Always pass git arguments as an array: Invoke-Git $repo @('rev-parse', 'HEAD').
function Invoke-Git([string]$Repo, [string[]]$GitArgs) {
    return (Invoke-GitX -Repo $Repo -GitArgs $GitArgs).Out.TrimEnd("`n", "`r")
}
function Test-Git([string]$Repo, [string[]]$GitArgs) {
    return ((Invoke-GitX -Repo $Repo -GitArgs $GitArgs -OkExit @(0, 1, 128)).Code -eq 0)
}

function Assert-GitVersion {
    $v = Invoke-Git $null @('--version')
    if ($v -notmatch '(\d+\.\d+\.\d+)' -or [version]$Matches[1] -lt [version]'2.49.0') { throw "Git 2.49+ required (tree-ish merge-tree --merge-base). Found: $v" }
    Write-Ok $v
}

function Assert-WorktreeBranch($Cfg, [string]$Side) {
    $wt = $Cfg[$Side].Worktree
    if (-not (Test-Path $wt)) { throw "$Side worktree $wt not found. Run 01-New-AlignmentBranches.ps1 -Apply first." }
    $b = Invoke-Git $wt @('rev-parse', '--abbrev-ref', 'HEAD')
    if ($b -ne $Cfg[$Side].NewBranch) { throw "$wt is on '$b', expected '$($Cfg[$Side].NewBranch)'." }
    return (Invoke-Git $wt @('rev-parse', 'HEAD'))
}

function Assert-ScopeClean($Cfg, [string]$Side) {
    $spec = @(Get-ScopePathspecs $Cfg)
    $st = Invoke-Git $Cfg[$Side].Worktree (@('status', '--porcelain=v1', '--untracked-files=all', '--') + $spec)
    if ($st) { throw "$Side worktree has uncommitted changes in the aligned scope:`n$st" }
}

function Get-ScopePathspecs($Cfg) {
    $s = @($Cfg.Scope | ForEach-Object { ":(top,literal)$_" })
    $s += @($Cfg.Protected | ForEach-Object { ":(top,exclude,literal)$_" })
    return $s
}

# --------------------------------------------------------------- scratch ----
function Initialize-Scratch($Cfg) {
    $x = Join-Path $Cfg.StateDir 'xrepo.git'
    if (-not (Test-Path (Join-Path $x 'HEAD'))) {
        New-Item -ItemType Directory -Force -Path $x | Out-Null
        Invoke-Git $null @('init', '--quiet', '--bare', $x) | Out-Null
    }
    $alts = foreach ($s in $script:Sides) {
        $common = Invoke-Git $Cfg[$s].Root @('rev-parse', '--path-format=absolute', '--git-common-dir')
        ((Join-Path $common 'objects') -replace '\\', '/')
    }
    [IO.File]::WriteAllText((Join-Path $x 'objects/info/alternates'), (($alts -join "`n") + "`n"))
    return $x
}

function Test-InScope($Cfg, [string]$Path) {
    foreach ($p in $Cfg.Protected) { if ($Path -ceq $p -or $Path.StartsWith("$p/", [StringComparison]::Ordinal)) { return $false } }
    foreach ($s in $Cfg.Scope) { if ($Path -ceq $s -or $Path.StartsWith("$s/", [StringComparison]::Ordinal)) { return $true } }
    return $false
}

# path -> @{ Mode; Sha } for the scoped part of a commit/tree
function Get-ScopedEntries([string]$X, $Cfg, [string]$Rev) {
    $r = Invoke-GitX -Repo $X -GitArgs (@('-c', 'core.quotePath=false', 'ls-tree', '-r', '-z', '--full-tree', $Rev, '--') + $Cfg.Scope)
    $d = [ordered]@{}
    foreach ($rec in $r.Out.Split([char]0)) {
        if (-not $rec) { continue }
        $meta, $path = $rec.Split("`t", 2)
        $m = $meta.Split(' ')
        if ($m[1] -ne 'blob' -or -not (Test-InScope $Cfg $path)) { continue }
        $d[$path] = @{ Mode = $m[0]; Sha = $m[2] }
    }
    return $d
}

# Writes a tree object in the scratch repo. $Entries: path -> @{Mode;Sha}. $FromTree optional starting tree.
function New-TreeFromEntries([string]$X, [System.Collections.IDictionary]$Entries, [string]$FromTree, [string[]]$Deletes) {
    $idx = Join-Path ([IO.Path]::GetTempPath()) ("align-{0}.index" -f [guid]::NewGuid().ToString('N'))
    $env = @{ GIT_INDEX_FILE = $idx }
    try {
        if ($FromTree) { Invoke-GitX -Repo $X -GitArgs @('read-tree', $FromTree) -Env $env | Out-Null }
        else { Invoke-GitX -Repo $X -GitArgs @('read-tree', '--empty') -Env $env | Out-Null }
        $sb = [Text.StringBuilder]::new()
        foreach ($k in $Entries.Keys) { [void]$sb.Append("$($Entries[$k].Mode) $($Entries[$k].Sha)`t$k").Append([char]0) }
        foreach ($k in @($Deletes)) { if ($k) { [void]$sb.Append("0 $script:ZeroSha`t$k").Append([char]0) } }
        if ($sb.Length) { Invoke-GitX -Repo $X -GitArgs @('update-index', '-z', '--index-info') -InputBytes ([Text.Encoding]::UTF8.GetBytes($sb.ToString())) -Env $env | Out-Null }
        return (Invoke-GitX -Repo $X -GitArgs @('write-tree') -Env $env).Out.Trim()
    } finally { Remove-Item -LiteralPath $idx -ErrorAction SilentlyContinue }
}

function Get-BlobBytes([string]$X, [string]$Sha) {
    if (-not $Sha) { return ,([byte[]]@()) }
    return ,([byte[]](Invoke-GitX -Repo $X -GitArgs @('cat-file', 'blob', $Sha)).Bytes)
}
function New-Blob([string]$X, [byte[]]$Bytes) {
    return (Invoke-GitX -Repo $X -GitArgs @('hash-object', '-w', '--stdin') -InputBytes $Bytes).Out.Trim()
}

function ConvertTo-Text([byte[]]$Bytes) { return [Text.Encoding]::UTF8.GetString($Bytes).TrimStart([char]0xFEFF) }
function Test-Bom([byte[]]$Bytes) { return ($Bytes.Length -ge 3 -and $Bytes[0] -eq 0xEF -and $Bytes[1] -eq 0xBB -and $Bytes[2] -eq 0xBF) }

# Equal ignoring every whitespace char and BOM (format-only change). Caveat: also ignores
# whitespace inside string/template literals; such rows still appear in plan.tsv for review.
function Test-FormatOnlyEqual([string]$X, [string]$A, [string]$B) {
    if (-not $A -or -not $B) { return $false }
    $ta = [regex]::Replace((ConvertTo-Text (Get-BlobBytes $X $A)), '[\s\uFEFF]+', '')
    $tb = [regex]::Replace((ConvertTo-Text (Get-BlobBytes $X $B)), '[\s\uFEFF]+', '')
    return ($ta -ceq $tb)
}
# Content key ignoring BOM and CRLF/LF (rename detection).
function Get-ContentKey([string]$X, [string]$Sha) {
    $t = (ConvertTo-Text (Get-BlobBytes $X $Sha)) -replace "`r`n", "`n"
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($t)))
}

function Test-Like([string]$Path, [string[]]$Patterns) {
    foreach ($p in @($Patterns)) { if ($p -and $Path -clike $p) { return $true } }
    return $false
}

# -------------------------------------------------------------- plan io ----
$script:Decisions = @('TAKE_FE', 'TAKE_ISL', 'MERGED', 'DELETE_BOTH', 'KEEP_ISL_ONLY', 'KEEP_BOTH', 'PACKAGES_UNION', 'MANUAL')
$script:PlanColumns = @('Path', 'Category', 'Decision', 'Reason', 'Refs', 'Base', 'Isl', 'Frontend', 'Merged', 'IslMode', 'FrontendMode', 'MergedMode')

function Write-AlignPlan([string]$Dir, [hashtable]$Meta, [System.Collections.IList]$Rows) {
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('# meta ' + ($Meta | ConvertTo-Json -Compress -Depth 6))
    $lines.Add("# Edit only the Decision column. Allowed: $($script:Decisions -join ' ')")
    $lines.Add($script:PlanColumns -join "`t")
    foreach ($r in $Rows) { $lines.Add((($script:PlanColumns | ForEach-Object { ([string]$r[$_]) -replace "[`t`r`n]", ' ' }) -join "`t")) }
    [IO.File]::WriteAllLines((Join-Path $Dir 'plan.tsv'), $lines, [Text.UTF8Encoding]::new($false))
}

function Read-AlignPlan([string]$Dir) {
    $all = [IO.File]::ReadAllLines((Join-Path $Dir 'plan.tsv'))
    $meta = ($all[0] -replace '^# meta ', '') | ConvertFrom-Json -AsHashtable
    $hdr = $null; $rows = [Collections.Generic.List[hashtable]]::new()
    foreach ($l in $all) {
        if ($l.StartsWith('#') -or -not $l) { continue }
        $f = $l.Split("`t")
        if (-not $hdr) { $hdr = $f; continue }
        $h = @{}; for ($i = 0; $i -lt $hdr.Count; $i++) { $h[$hdr[$i]] = if ($i -lt $f.Count) { $f[$i] } else { '' } }
        if ($script:Decisions -notcontains $h.Decision) { throw "plan.tsv: unknown decision '$($h.Decision)' for $($h.Path)" }
        $rows.Add($h)
    }
    return @{ Meta = $meta; Rows = $rows }
}

function Resolve-PlanDir($Cfg, [string]$PlanDir) {
    if ($PlanDir) { return (Resolve-Path $PlanDir).Path }
    $ptr = Join-Path $Cfg.StateDir 'latest-plan.txt'
    if (-not (Test-Path $ptr)) { throw 'No plan found. Run 02-New-AlignmentPlan.ps1 first.' }
    return (Get-Content $ptr -Raw).Trim()
}

# ------------------------------------------------------------ package.json ----
# Locates a flat "section": { "name": "version", ... } object. Returns $null if absent.
function Get-JsonSection([string]$Text, [string]$Section) {
    $m = [regex]::Match($Text, '"' + [regex]::Escape($Section) + '"\s*:\s*\{')
    if (-not $m.Success) { return $null }
    $open = $m.Index + $m.Length
    $close = $Text.IndexOf('}', $open)
    if ($close -lt 0) { throw "package.json: unterminated '$Section'" }
    $body = $Text.Substring($open, $close - $open)
    $d = [ordered]@{}
    foreach ($e in [regex]::Matches($body, '"(?<k>[^"]+)"\s*:\s*"(?<v>[^"]*)"')) { $d[$e.Groups['k'].Value] = $e.Groups['v'].Value }
    return @{ Open = $open; Close = $close; Body = $body; Entries = $d }
}

# Add-only union of dependency sections: each side gains the other side's missing entries (in the same
# section; skipped if present in any of its own sections); removals never propagate; version differences
# stay as they are unless listed in $Overrides. Formatting of untouched lines is preserved byte for byte.
function Merge-PackageJson([string]$Text, [string]$OtherText, [string]$OtherName, [string[]]$Sections, [hashtable]$Overrides) {
    $report = [Collections.Generic.List[string]]::new()
    $nl = if ($Text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $mineAll = @{}; $theirsAll = @{}
    foreach ($sec in $Sections) {
        $a = Get-JsonSection $Text $sec; if ($a) { foreach ($k in $a.Entries.Keys) { $mineAll[$k] = $a.Entries[$k] } }
        $b = Get-JsonSection $OtherText $sec; if ($b) { foreach ($k in $b.Entries.Keys) { $theirsAll[$k] = $b.Entries[$k] } }
    }
    foreach ($sec in $Sections) {
        $theirs = Get-JsonSection $OtherText $sec
        if (-not $theirs) { continue }
        $mine = Get-JsonSection $Text $sec
        foreach ($id in @($Overrides.Keys)) {
            if ($mine -and $mine.Entries.Contains($id) -and $mine.Entries[$id] -ne $Overrides[$id]) {
                $body = [regex]::Replace($mine.Body, '("' + [regex]::Escape($id) + '"\s*:\s*")[^"]*(")', "`${1}$($Overrides[$id])`${2}")
                $Text = $Text.Substring(0, $mine.Open) + $body + $Text.Substring($mine.Close)
                $report.Add("SET    $sec/$id $($mine.Entries[$id]) -> $($Overrides[$id]) (override)")
                $mine = Get-JsonSection $Text $sec
            }
        }
        $adds = [Collections.Generic.List[string]]::new()
        foreach ($id in $theirs.Entries.Keys) {
            if ($mineAll.ContainsKey($id)) {
                if ($mine -and -not $mine.Entries.Contains($id)) { $report.Add("MOVED  $sec/$id is in another section here (left as is)") }
                continue
            }
            $v = if ($Overrides.ContainsKey($id)) { $Overrides[$id] } else { $theirs.Entries[$id] }
            $adds.Add("`"$id`": `"$v`"")
            $report.Add("ADD    $sec/$id $v (from $OtherName)")
        }
        if ($adds.Count) {
            if (-not $mine) { throw "package.json has no '$sec' section to receive $($adds.Count) entr(y/ies) from $OtherName; set KEEP_BOTH or edit by hand." }
            $indent = if ($mine.Body -match '(?m)^([ \t]+)"') { $Matches[1] } else { '    ' }
            if ($mine.Body.Trim()) {
                $ins = $nl + (($adds | ForEach-Object { "$indent$_," }) -join $nl)
                $lead = [regex]::Match($mine.Body, '^[ \t]*').Value       # keep '{' + same-line spacing; entries go on new lines
                $Text = $Text.Substring(0, $mine.Open + $lead.Length) + $ins + $Text.Substring($mine.Open + $lead.Length)
            } else {
                $closeIndent = [regex]::Match($Text.Substring(0, $mine.Open), '(?m)^([ \t]*).*$\z').Groups[1].Value
                $ins = $nl + (($adds | ForEach-Object { "$indent$_" }) -join ",$nl") + $nl + $closeIndent
                $Text = $Text.Substring(0, $mine.Open) + $ins + $Text.Substring($mine.Close)
            }
        }
    }
    foreach ($id in $mineAll.Keys) {
        if ($theirsAll.ContainsKey($id) -and $theirsAll[$id] -ne $mineAll[$id] -and -not $Overrides.ContainsKey($id)) { $report.Add("DIFFER $id here=$($mineAll[$id]) $OtherName=$($theirsAll[$id]) (left as is)") }
    }
    $null = $Text | ConvertFrom-Json -ErrorAction Stop    # the union must still be valid JSON
    return @{ Text = $Text; Report = $report }
}

function Get-CommitMessage($Cfg, [string]$Subject, [string]$Body) {
    # CommitTrailer is optional; StrictMode throws on a missing key, so test for it explicitly.
    $msg = "$Subject`n`n$Body"
    if ($Cfg.ContainsKey('CommitTrailer') -and $Cfg['CommitTrailer']) { $msg += "`n`n$($Cfg['CommitTrailer'])" }
    return $msg
}

# ------------------------------------------------------ decision -> blob ----
# Target entry for one side of one plan row. Returns @{Mode;Sha}, $null (absent) or 'PACKAGES'.
function Get-SideTarget([hashtable]$Row, [string]$Side) {
    $own = if ($Side -eq 'Isl') { 'Isl' } else { 'Frontend' }
    $pick = {
        param($col)
        if (-not $Row[$col]) { return $null }
        $mode = switch ($col) { 'Isl' { $Row.IslMode } 'Frontend' { $Row.FrontendMode } 'Merged' { $Row.MergedMode } }
        return @{ Mode = $mode; Sha = $Row[$col] }
    }
    switch ($Row.Decision) {
        'TAKE_FE'        { return (& $pick 'Frontend') }
        'TAKE_ISL'       { return (& $pick 'Isl') }
        'MERGED'         { return (& $pick 'Merged') }
        'DELETE_BOTH'    { return $null }
        'KEEP_ISL_ONLY'  { return (& $pick $own) }
        'KEEP_BOTH'      { return (& $pick $own) }
        'MANUAL'         { return (& $pick $own) }
        'PACKAGES_UNION' { return 'PACKAGES' }
        default          { throw "Unknown decision '$($Row.Decision)' for $($Row.Path)" }
    }
}

Export-ModuleMember -Function *
