#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# =============================================================================
# Shared helpers for the Backend <-> ISL-Backend branch alignment scripts.
# Conventions: dry-run unless -Apply; no commit unless -Commit; push only in
# 05 with -Push. All cross-repo computation happens in a scratch bare repo
# whose object store borrows both repositories' objects (alternates), so
# neither repository receives refs or objects it did not create itself.
# =============================================================================

$script:Yes = $false
$script:ZeroSha = '0' * 40
$script:Sides = @('Backend', 'Isl')

function Set-AlignYes([bool]$Value) { $script:Yes = $Value }

function Import-AlignConfig {
    param([string]$Path)
    if (-not $Path) { $Path = Join-Path $PSScriptRoot 'Align.Config.psd1' }
    $cfg = Import-PowerShellDataFile -Path $Path
    foreach ($s in $script:Sides) {
        foreach ($k in 'Root', 'WorktreeRoot') {
            if ($cfg[$s][$k] -match '^[Cc]:\\Repos(\\|$)') { throw "$s.$k uses the C:\Repos junction; use the canonical D:\Repos path." }
        }
        $cfg[$s].Worktree = [IO.Path]::Combine($cfg[$s].WorktreeRoot, ($cfg[$s].NewBranch -replace '/', [IO.Path]::DirectorySeparatorChar))
    }
    $cfg.ConfigPath = (Resolve-Path $Path).Path
    return $cfg
}

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

# Equal ignoring every whitespace char and BOM (format-only change). Caveat: also ignores
# whitespace inside string literals; such rows still appear in plan.tsv for review.
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
$script:PlanColumns = @('Path', 'Category', 'Decision', 'Reason', 'Refs', 'Base', 'Isl', 'Backend', 'Merged', 'IslMode', 'BackendMode', 'MergedMode')

function Write-AlignPlan([string]$Dir, [hashtable]$Meta, [System.Collections.IList]$Rows) {
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('# meta ' + ($Meta | ConvertTo-Json -Compress -Depth 6))
    $lines.Add('# Edit only the Decision column. Allowed: TAKE_BE TAKE_ISL MERGED DELETE_BOTH KEEP_ISL_ONLY KEEP_BOTH PACKAGES_UNION MANUAL')
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

# ------------------------------------------------- Directory.Packages.props ----
$script:PvRegex = '<PackageVersion\s+Include="(?<id>[^"]+)"\s+Version="(?<v>[^"]+)"\s*/>'
function Get-PackageVersions([string]$Text) {
    $d = [ordered]@{}
    foreach ($m in [regex]::Matches($Text, $script:PvRegex, 'IgnoreCase')) { $d[$m.Groups['id'].Value] = $m.Groups['v'].Value }
    return $d
}
# Add-only union: each side gains the other side's missing entries; removals never propagate;
# version differences stay as they are unless listed in $Overrides. Returns new text + report.
function Merge-PackageProps([string]$Text, [string]$OtherText, [string]$OtherName, [hashtable]$Overrides) {
    $mine = Get-PackageVersions $Text; $theirs = Get-PackageVersions $OtherText
    $nl = if ($Text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $report = [Collections.Generic.List[string]]::new()
    foreach ($id in @($Overrides.Keys)) {
        if ($mine.Contains($id) -and $mine[$id] -ne $Overrides[$id]) {
            $Text = [regex]::Replace($Text, '<PackageVersion\s+Include="' + [regex]::Escape($id) + '"\s+Version="[^"]+"\s*/>', "<PackageVersion Include=`"$id`" Version=`"$($Overrides[$id])`" />", 'IgnoreCase')
            $report.Add("SET    $id $($mine[$id]) -> $($Overrides[$id]) (override)")
        }
    }
    $adds = foreach ($id in $theirs.Keys) {
        if (-not $mine.Contains($id)) {
            $v = if ($Overrides.ContainsKey($id)) { $Overrides[$id] } else { $theirs[$id] }
            $report.Add("ADD    $id $v (from $OtherName)")
            "    <PackageVersion Include=`"$id`" Version=`"$v`" />"
        }
    }
    foreach ($id in $mine.Keys) {
        if ($theirs.Contains($id) -and $theirs[$id] -ne $mine[$id] -and -not $Overrides.ContainsKey($id)) { $report.Add("DIFFER $id here=$($mine[$id]) $OtherName=$($theirs[$id]) (left as is)") }
    }
    if ($adds) {
        $i = $Text.LastIndexOf('</ItemGroup>')
        if ($i -lt 0) { throw 'Directory.Packages.props has no </ItemGroup>' }
        $lineStart = $Text.LastIndexOf("`n", $i) + 1
        $block = "    <!-- Added by branch alignment: present in $OtherName -->$nl" + (@($adds) -join $nl) + $nl
        $Text = $Text.Insert($lineStart, $block)
    }
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
    $own = if ($Side -eq 'Isl') { 'Isl' } else { 'Backend' }
    $pick = {
        param($col)
        if (-not $Row[$col]) { return $null }
        $mode = switch ($col) { 'Isl' { $Row.IslMode } 'Backend' { $Row.BackendMode } 'Merged' { $Row.MergedMode } }
        return @{ Mode = $mode; Sha = $Row[$col] }
    }
    switch ($Row.Decision) {
        'TAKE_BE'        { return (& $pick 'Backend') }
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
