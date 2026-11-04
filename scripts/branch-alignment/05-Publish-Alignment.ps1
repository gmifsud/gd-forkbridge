#Requires -Version 7.0
<#
.SYNOPSIS
  Pushes ONLY the two new alignment branches and opens the paired PRs (Backend -> Development,
  ISL -> development). Without -Push / -CreatePr it prints the exact commands and PR URLs.
  Requires a passing 04 run (verified.json) on the current branch tips.
#>
[CmdletBinding()]
param([string]$PlanDir, [switch]$Push, [switch]$CreatePr, [switch]$Ready, [switch]$Yes, [string]$ConfigPath)
Import-Module (Join-Path $PSScriptRoot 'Align.Common.psm1') -Force
$cfg = Import-AlignConfig $ConfigPath; Set-AlignYes $Yes
Start-AlignLog $cfg 'publish'
try {
    $PlanDir = Resolve-PlanDir $cfg $PlanDir
    $vf = Join-Path $PlanDir 'verified.json'
    if (-not (Test-Path $vf)) { throw 'Run 04-Test-Alignment.ps1 first.' }
    $v = Get-Content $vf -Raw | ConvertFrom-Json
    if (-not $v.verified -or $v.useIndex) { throw 'Last 04 run did not pass on committed HEADs (or used -UseIndex). Commit, then re-run 04.' }
    $tips = @{}
    foreach ($s in 'Backend', 'Isl') {
        $tips[$s] = Assert-WorktreeBranch $cfg $s
        if ($tips[$s] -ne $v.($s.ToLower())) { throw "$s HEAD changed since 04 passed. Re-run 04." }
        if ($cfg.NeverPush -contains $cfg[$s].NewBranch) { throw "Refusing to push protected branch $($cfg[$s].NewBranch)." }
    }
    $mdPath = Join-Path $PlanDir 'plan.md'
    $planMd = if (Test-Path $mdPath) { Get-Content $mdPath -Raw } else { '' }

    foreach ($s in 'Backend', 'Isl') {
        $c = $cfg[$s]; $other = if ($s -eq 'Isl') { 'Backend' } else { 'Isl' }
        Write-Step "$s : $($c.NewBranch) -> $($c.TargetBranch)"
        $ahead = Invoke-Git $c.Worktree @('rev-list', '--count', "origin/$($c.TargetBranch)..HEAD")
        Write-Plan "$ahead commit(s) ahead of origin/$($c.TargetBranch) (includes the base branch's own work)"
        $title = "Fork sync: align shared projects with $($cfg[$other].NewBranch)"
        $desc = "Paired PR: $($cfg[$other].AzureProject)/$($cfg[$other].AzureRepo) ``$($cfg[$other].NewBranch)`` -> ``$($cfg[$other].TargetBranch)``. Merge both together.`n`n" + $planMd
        if ($desc.Length -gt 3900) { $desc = $desc.Substring(0, 3900) + "`n`n(truncated - full plan.md in the alignment state folder)" }
        $url = "$($cfg.AzureOrg)/$([uri]::EscapeDataString($c.AzureProject))/_git/$($c.AzureRepo)/pullrequestcreate?sourceRef=$([uri]::EscapeDataString($c.NewBranch))&targetRef=$([uri]::EscapeDataString($c.TargetBranch))"
        Write-Plan "git -C `"$($c.Worktree)`" push -u origin $($c.NewBranch)"
        Write-Plan "az repos pr create --organization $($cfg.AzureOrg) --project `"$($c.AzureProject)`" --repository $($c.AzureRepo) --source-branch $($c.NewBranch) --target-branch $($c.TargetBranch) --title `"$title`" $(if (-not $Ready) { '--draft' })"
        Write-Plan "or open: $url"
        if ($Push) {
            if (-not (Confirm-Step "Push $($c.NewBranch) to origin ($s)?")) { continue }
            Invoke-Git $c.Worktree @('push', '-u', 'origin', $c.NewBranch) | Out-Null
            Write-Ok 'pushed'
        }
        if ($CreatePr) {
            if (-not (Get-Command az -ErrorAction SilentlyContinue)) { Write-Note 'az CLI not found - use the URL above'; continue }
            if (-not (Confirm-Step "Create $(if ($Ready) { '' } else { 'draft ' })PR for $s?")) { continue }
            $azArgs = @('repos', 'pr', 'create', '--organization', $cfg.AzureOrg, '--project', $c.AzureProject, '--repository', $c.AzureRepo,
                        '--source-branch', $c.NewBranch, '--target-branch', $c.TargetBranch, '--title', $title, '--description', $desc, '--output', 'json')
            if (-not $Ready) { $azArgs += @('--draft', 'true') }
            $pr = & az @azArgs | ConvertFrom-Json
            if ($LASTEXITCODE) { throw "az repos pr create failed for $s" }
            Write-Ok "PR #$($pr.pullRequestId) created"
        }
    }
    if (-not $Push -and -not $CreatePr) { Write-Note 'Nothing pushed. Re-run with -Push [-CreatePr] [-Ready].' }
} finally { Stop-AlignLog }
