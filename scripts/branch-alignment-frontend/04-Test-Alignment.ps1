#Requires -Version 7.0
<#
.SYNOPSIS
  Fitness check. Every path in the shared scope must be byte-identical on both branches unless the
  plan explicitly allows the divergence (KEEP_ISL_ONLY, KEEP_BOTH, MANUAL, PACKAGES_UNION) or the
  path is fork-owned. Also runs the configured grep guards and, with -Build, the configured
  install/build commands in each worktree. Exit 0 = aligned. Writes verified.json into the plan
  folder (05 requires it).
#>
[CmdletBinding()]
param([string]$PlanDir, [switch]$UseIndex, [switch]$Build, [ValidateSet('Both', 'Frontend', 'Isl')][string]$BuildSide = 'Both', [string]$ConfigPath)
Import-Module (Join-Path $PSScriptRoot 'Align.Common.psm1') -Force
$cfg = Import-AlignConfig $ConfigPath
Start-AlignLog $cfg 'verify'
$failed = $false
try {
    Assert-GitVersion
    $x = Initialize-Scratch $cfg
    $PlanDir = Resolve-PlanDir $cfg $PlanDir
    $plan = Read-AlignPlan $PlanDir
    $allowed = [hashtable]::new([StringComparer]::Ordinal)
    foreach ($r in $plan.Rows) { if ($r.Decision -in 'KEEP_ISL_ONLY', 'KEEP_BOTH', 'MANUAL', 'PACKAGES_UNION') { $allowed[$r.Path] = $r.Decision } }

    $rev = @{}
    foreach ($s in 'Frontend', 'Isl') {
        $tip = Assert-WorktreeBranch $cfg $s
        # write-tree only creates a tree object from the existing index (as git stash does).
        $rev[$s] = if ($UseIndex) { Invoke-Git $cfg[$s].Worktree @('write-tree') } else { $tip }
        Write-Ok "$s $(if ($UseIndex) { 'index tree' } else { 'HEAD' }) $($rev[$s].Substring(0, 10))"
    }

    Write-Step 'Shared scope must be identical'
    $FeMap = Get-ScopedEntries $x $cfg $rev.Frontend
    $IslMap = Get-ScopedEntries $x $cfg $rev.Isl
    $paths = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    foreach ($k in $FeMap.Keys) { [void]$paths.Add($k) }; foreach ($k in $IslMap.Keys) { [void]$paths.Add($k) }
    $same = 0; $ok = @{}; $bad = [Collections.Generic.List[string]]::new()
    foreach ($p in $paths) {
        $e = if ($FeMap.Contains($p)) { $FeMap[$p].Sha }; $i = if ($IslMap.Contains($p)) { $IslMap[$p].Sha }
        if ($e -eq $i) { $same++; continue }
        if ($allowed.ContainsKey($p)) { $ok[$allowed[$p]] = 1 + [int]$ok[$allowed[$p]]; continue }
        if (-not $e -and (Test-Like $p $cfg.ForkOwned)) { $ok['FORK_OWNED'] = 1 + [int]$ok['FORK_OWNED']; continue }
        $bad.Add("$(if (-not $e) { 'ISL-only ' } elseif (-not $i) { 'FE-only  ' } else { 'differs  ' }) $p")
    }
    Write-Ok "identical: $same"
    foreach ($k in $ok.Keys) { Write-Plan "allowed divergence $k : $($ok[$k])" }
    if ($bad.Count) { $failed = $true; Write-Note "UNEXPECTED divergence ($($bad.Count)):"; $bad | Select-Object -First 100 | ForEach-Object { Write-Host "      $_" -ForegroundColor Red } }
    else { Write-Ok 'no unexpected divergence' }

    Write-Step 'Guards'
    foreach ($g in @($cfg.Guards)) {
        $globs = if ($g.ContainsKey('Include') -and $g.Include) { @($g.Include) } else { @('*.ts') }
        foreach ($s in ($(if ($g.Side -eq 'Both') { 'Frontend', 'Isl' } else { $g.Side }))) {
            $hit = Invoke-GitX -Repo $x -GitArgs (@('-c', 'core.quotePath=false', 'grep', '-n', '-E', $g.Pattern, $rev[$s], '--') + $globs) -OkExit @(0, 1)
            # rev:path:line:text -> keep hits inside the aligned scope only
            $lines = @($hit.Out -split "`n" | Where-Object { $_ } | Where-Object { $p = $_.Substring($rev[$s].Length + 1).Split(':', 2)[0]; Test-InScope $cfg $p })
            if ($lines.Count) { $failed = $true; Write-Note "$s violates '$($g.Name)':"; $lines | Select-Object -First 20 | ForEach-Object { Write-Host "      $($_.Substring($rev[$s].Length + 1))" -ForegroundColor Red } }
            else { Write-Ok "$s : $($g.Name)" }
        }
    }

    if ($Build) {
        Write-Step 'Builds'
        # Transcripts don't reliably capture native output, so each command writes its own log file.
        $buildLogDir = Join-Path $cfg.StateDir ('logs/build-{0}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
        New-Item -ItemType Directory -Force -Path $buildLogDir | Out-Null
        Write-Plan "build logs: $buildLogDir"
        $buildSides = if ($BuildSide -eq 'Both') { @('Frontend', 'Isl') } else { @($BuildSide) }
        foreach ($s in $buildSides) {
            $before = @(Invoke-Git $cfg[$s].Worktree @('status', '--porcelain=v1', '--untracked-files=no') -split "`n" | Where-Object { $_ })
            foreach ($b in @($cfg.Builds[$s])) {
                if (-not (Get-Command $b.Exe -ErrorAction SilentlyContinue)) { throw "$($b.Exe) not on PATH" }
                $log = Join-Path $buildLogDir ('{0}-{1}.log' -f $s, $b.Name)
                Write-Plan "$s : $($b.Exe) $($b.Args -join ' ')"
                Push-Location $cfg[$s].Worktree
                try {
                    $argv = @($b.Args)
                    & $b.Exe @argv *> $log
                    if ($LASTEXITCODE) {
                        $failed = $true; Write-Note "$s $($b.Name) failed (exit $LASTEXITCODE, log: $log)"
                        Select-String -Path $log -Pattern '(?i)\berror\b' | Select-Object -First 15 | ForEach-Object { Write-Host "      $($_.Line.Trim())" -ForegroundColor Red }
                        break      # later steps depend on this one
                    }
                    Write-Ok "$s $($b.Name)"
                } finally { Pop-Location }
            }
            $after = @(Invoke-Git $cfg[$s].Worktree @('status', '--porcelain=v1', '--untracked-files=no') -split "`n" | Where-Object { $_ })
            $touched = @($after | Where-Object { $before -notcontains $_ })
            if ($touched.Count) { Write-Note "$s build modified $($touched.Count) tracked file(s) (generated output). Do not commit them; inspect with: git -C `"$($cfg[$s].Worktree)`" status" }
        }
    }

    $result = @{ verified = -not $failed; at = (Get-Date).ToString('o'); frontend = $rev.Frontend; isl = $rev.Isl; useIndex = [bool]$UseIndex; build = [bool]$Build; unexpected = $bad.Count }
    $result | ConvertTo-Json | Set-Content (Join-Path $PlanDir 'verified.json') -Encoding utf8
    if ($failed) { Write-Note 'NOT aligned.'; exit 1 }
    Write-Ok 'Aligned. Next: 05-Publish-Alignment.ps1'
} finally { Stop-AlignLog }
