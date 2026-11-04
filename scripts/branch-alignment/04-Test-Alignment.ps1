#Requires -Version 7.0
<#
.SYNOPSIS
  Fitness check. Every path in the shared scope must be byte-identical on both branches unless the
  plan explicitly allows the divergence (KEEP_ISL_ONLY, KEEP_BOTH, MANUAL, PACKAGES_UNION) or the
  path is fork-owned. Also runs the configured grep guards and, with -Build, targeted dotnet builds.
  Exit 0 = aligned. Writes verified.json into the plan folder (05 requires it).
#>
[CmdletBinding()]
param([string]$PlanDir, [switch]$UseIndex, [switch]$Build, [string]$ConfigPath)
Import-Module (Join-Path $PSScriptRoot 'Align.Common.psm1') -Force
$cfg = Import-AlignConfig $ConfigPath
Start-AlignLog $cfg 'verify'
$failed = $false
try {
    Assert-GitVersion
    $x = Initialize-Scratch $cfg
    $PlanDir = Resolve-PlanDir $cfg $PlanDir
    $plan = Read-AlignPlan $PlanDir
    $allowed = @{}
    foreach ($r in $plan.Rows) { if ($r.Decision -in 'KEEP_ISL_ONLY', 'KEEP_BOTH', 'MANUAL', 'PACKAGES_UNION') { $allowed[$r.Path] = $r.Decision } }

    $rev = @{}
    foreach ($s in 'Backend', 'Isl') {
        $tip = Assert-WorktreeBranch $cfg $s
        # write-tree only creates a tree object from the existing index (as git stash does).
        $rev[$s] = if ($UseIndex) { Invoke-Git $cfg[$s].Worktree @('write-tree') } else { $tip }
        Write-Ok "$s $(if ($UseIndex) { 'index tree' } else { 'HEAD' }) $($rev[$s].Substring(0, 10))"
    }

    Write-Step 'Shared scope must be identical'
    $BeMap = Get-ScopedEntries $x $cfg $rev.Backend
    $IslMap = Get-ScopedEntries $x $cfg $rev.Isl
    $paths = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    foreach ($k in $BeMap.Keys) { [void]$paths.Add($k) }; foreach ($k in $IslMap.Keys) { [void]$paths.Add($k) }
    $same = 0; $ok = @{}; $bad = [Collections.Generic.List[string]]::new()
    foreach ($p in $paths) {
        $e = if ($BeMap.Contains($p)) { $BeMap[$p].Sha }; $i = if ($IslMap.Contains($p)) { $IslMap[$p].Sha }
        if ($e -eq $i) { $same++; continue }
        if ($allowed.ContainsKey($p)) { $ok[$allowed[$p]] = 1 + [int]$ok[$allowed[$p]]; continue }
        if (-not $e -and (Test-Like $p $cfg.ForkOwned)) { $ok['FORK_OWNED'] = 1 + [int]$ok['FORK_OWNED']; continue }
        $bad.Add("$(if (-not $e) { 'ISL-only ' } elseif (-not $i) { 'BE-only  ' } else { 'differs  ' }) $p")
    }
    Write-Ok "identical: $same"
    foreach ($k in $ok.Keys) { Write-Plan "allowed divergence $k : $($ok[$k])" }
    if ($bad.Count) { $failed = $true; Write-Note "UNEXPECTED divergence ($($bad.Count)):"; $bad | Select-Object -First 100 | ForEach-Object { Write-Host "      $_" -ForegroundColor Red } }
    else { Write-Ok 'no unexpected divergence' }

    Write-Step 'Guards'
    foreach ($g in @($cfg.Guards)) {
        foreach ($s in ($(if ($g.Side -eq 'Both') { 'Backend', 'Isl' } else { $g.Side }))) {
            $hit = Invoke-GitX -Repo $x -GitArgs @('grep', '-n', '-E', $g.Pattern, $rev[$s], '--', '*.cs') -OkExit @(0, 1)
            if ($hit.Code -eq 0) { $failed = $true; Write-Note "$s violates '$($g.Name)':"; ($hit.Out -split "`n" | Select-Object -First 20) | ForEach-Object { Write-Host "      $_" -ForegroundColor Red } }
            else { Write-Ok "$s : $($g.Name)" }
        }
    }

    if ($Build) {
        Write-Step 'Targeted builds'
        if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'dotnet not on PATH' }
        # Transcripts don't reliably capture native output, so each build also writes its own log file.
        $buildLogDir = Join-Path $cfg.StateDir ('logs/build-{0}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
        New-Item -ItemType Directory -Force -Path $buildLogDir | Out-Null
        Write-Plan "build logs: $buildLogDir"
        foreach ($s in 'Backend', 'Isl') {
            foreach ($spec in @($cfg.Builds[$s])) {
                $proj, $conf = $spec.Split('|'); if (-not $conf) { $conf = 'Debug' }
                $buildLog = Join-Path $buildLogDir ('{0}-{1}.log' -f $s, [IO.Path]::GetFileNameWithoutExtension($proj))
                Write-Plan "$s : dotnet build $proj -c $conf"
                Push-Location $cfg[$s].Worktree
                try {
                    & dotnet build $proj -c $conf --nologo -v q -m:1 -nr:false "-flp:logfile=$buildLog;verbosity=minimal"
                    if ($LASTEXITCODE) {
                        $failed = $true; Write-Note "build failed: $proj  (log: $buildLog)"
                        if (Test-Path $buildLog) { Select-String -Path $buildLog -Pattern ': error ' | Select-Object -First 10 | ForEach-Object { Write-Host "      $($_.Line.Trim())" -ForegroundColor Red } }
                    }
                }
                finally { Pop-Location }
            }
        }
        foreach ($s in 'Backend', 'Isl') {
            # dotnet build re-evaluates packages.lock.json; a rewritten lock file would block the next 02/03 run.
            $locks = Invoke-Git $cfg[$s].Worktree @('diff', '--name-only', '--', '*packages.lock.json')
            if ($locks) {
                Invoke-Git $cfg[$s].Worktree (@('restore', '--') + @($locks -split "`n")) | Out-Null
                Write-Plan "$s : restored $(@($locks -split "`n").Count) packages.lock.json file(s) rewritten by the build"
            }
            $lib = Invoke-Git $cfg[$s].Worktree @('status', '--porcelain', '--', 'lib')
            if ($lib) { Write-Note "$s lib/ DLLs rebuilt locally ($(@($lib -split "`n").Count) files). Do not commit them; discard with: git -C `"$($cfg[$s].Worktree)`" restore -- lib" }
        }
    }

    $result = @{ verified = -not $failed; at = (Get-Date).ToString('o'); backend = $rev.Backend; isl = $rev.Isl; useIndex = [bool]$UseIndex; build = [bool]$Build; unexpected = $bad.Count }
    $result | ConvertTo-Json | Set-Content (Join-Path $PlanDir 'verified.json') -Encoding utf8
    if ($failed) { Write-Note 'NOT aligned.'; exit 1 }
    Write-Ok 'Aligned. Next: 05-Publish-Alignment.ps1'
} finally { Stop-AlignLog }
