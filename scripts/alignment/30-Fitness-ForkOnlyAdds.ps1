#Requires -Version 7.0
<# Step 17 / standing rule: in ISL, every file under the shared folders that ALSO exists in Backend <Branch> must be byte-identical
   (same blob SHA). The fork may only ADD files. Cross-repo without remotes: compares rev-parse <rev>:<path> blob ids. Exit 1 on violation. #>
[CmdletBinding()] param([string]$BackendBranch = 'Development', [string]$IslRev = 'HEAD', [string[]]$Folders = @('QiSL.App.WF','QiSL.Fabric/Kernel','QiSL.Fabric/BaseTemplate.0','QiSL.Fabric/Platform'),
  [string[]]$Allow = @('QiSL.Fabric/Platform/aspnet-core/src/QiSL.Platform.Web.ISL.Host/*','QiSL.Fabric/Platform/aspnet-core/src/QiSL.Platform.Web.ThirdParty.ISL.Host/*'))
Import-Module (Join-Path $PSScriptRoot 'Alignment.Common.psm1') -Force
$d = Get-AlignmentDefaults
$isl = Invoke-Git $d.IslWorktree ls-tree -r $IslRev -- @Folders
$bk  = @{}; (Invoke-Git $d.BackendRoot ls-tree -r $BackendBranch -- @Folders) -split "`n" | ForEach-Object { $p = $_ -split "`t"; $bk[$p[1]] = ($p[0] -split ' ')[2] }
$viol = @(); $added = 0
foreach ($line in ($isl -split "`n")) { $p = $line -split "`t"; $path = $p[1]; $sha = ($p[0] -split ' ')[2]
    if ($Allow | Where-Object { $path -like $_ }) { continue }
    if (-not $bk.ContainsKey($path)) { $added++; continue }
    if ($bk[$path] -ne $sha) { $viol += $path } }
Write-Host "fork-only additions: $added"
if ($viol) { Write-Host "VIOLATIONS - fork edited upstream-owned files ($($viol.Count)):" -ForegroundColor Red; $viol | ForEach-Object { Write-Host "  $_" }; exit 1 }
Write-Host "OK - no upstream-owned file differs from Backend $BackendBranch" -ForegroundColor Green
