#Requires -Version 7.0
<# Step 13: import Backend Development into ISL with ISL's own fork scripts (3-way merge-tree; leaves changes STAGED, no commit). Uses the local D:\Repos\Backend checkout as upstream. #>
[CmdletBinding()] param([switch]$Apply, [switch]$Yes, [string]$Branch = 'Development', [ValidateSet('Fabric','WF','Both')][string]$Scope = 'Both')
Import-Module (Join-Path $PSScriptRoot 'Alignment.Common.psm1') -Force
$d = Get-AlignmentDefaults; Set-AlignmentYes $Yes; $R = $d.IslWorktree
Start-AlignmentLog 'step22-isl-import'
try {
    Assert-Branch $R $d.IslBranch
    if (Invoke-Git $R status --porcelain -- QiSL.Fabric QiSL.App.WF) { throw "QiSL.Fabric / QiSL.App.WF must be clean before import (ISL script precondition)" }
    # Guard: Backend must already contain the revert + hook, otherwise the import re-introduces the string overloads.
    $bk = $d.BackendRoot
    $ok = (Get-BlobSha $bk $Branch 'QiSL.Fabric/Kernel/QiSL.Platform.SharedKernel.EntityFrameworkCore/EntityFrameworkCore/Repositories/QiSLRepositoryBase.cs') -like '4e5624bf6*'
    if (-not $ok) { Write-Warn2 "Backend $Branch QiSLRepositoryBase.cs is not the enum-only version (4e5624bf6). Expected if Backend PRs are not merged yet." ; if (-not (Confirm-Step "Import anyway?")) { return } }
    $hook = (& git -C $bk show "${Branch}:QiSL.App.WF/src/QiSL.App.WF.EntityFrameworkCore/Migrations/Seed/WFSeederService.cs" 2>$null) -match 'SeedForkWorkflows'
    if (-not $hook) { Write-Warn2 "Backend $Branch WFSeederService.cs has no SeedForkWorkflows hook yet - the WF import would conflict with ISL's hook version." }
    $cmds = @()
    if ($Scope -in 'Fabric','Both') { $cmds += "scripts/Fork/FromUpstreamFabric.ps1" }
    if ($Scope -in 'WF','Both')     { $cmds += "scripts/Fork/FromUpstreamWF.ps1" }
    foreach ($c in $cmds) { Write-Plan "pwsh -File $c -Branch $Branch -UpstreamUrl $bk" }
    if (-not $Apply) { Write-Warn2 "dry-run. Re-run with -Apply"; return }
    foreach ($c in $cmds) {
        if (-not (Confirm-Step "Run $c ?")) { continue }
        Push-Location $R; try { & pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $R $c) -Branch $Branch -UpstreamUrl $bk } finally { Pop-Location }
        Write-Step "Result (staged): $c"; & git -C $R status --short -- QiSL.Fabric QiSL.App.WF | Select-Object -First 60
        $conf = & git -C $R diff --name-only --diff-filter=U
        if ($conf) { Write-Warn2 "CONFLICTS - resolve, git add, then commit:`n$conf"; break }
        Write-Warn2 "Review 'git diff --cached' then commit the import before the next scope."
    }
} finally { Stop-AlignmentLog }
