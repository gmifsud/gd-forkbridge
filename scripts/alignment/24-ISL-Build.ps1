#Requires -Version 7.0
<# Step 15: dependency-ordered ISL build (lib/ HintPath chain), then the ISL host. Commits refreshed lib/*.dll only with -Commit. #>
[CmdletBinding()] param([switch]$Commit, [switch]$Yes)
Import-Module (Join-Path $PSScriptRoot 'Alignment.Common.psm1') -Force
$d = Get-AlignmentDefaults; Set-AlignmentYes $Yes; $R = $d.IslWorktree
$order = @(
  'QiSL.Fabric/BaseTemplate.0/aspnet-core/src/QiSL.App.BaseTemplate.EntityFrameworkCore/QiSL.App.BaseTemplate.EntityFrameworkCore.csproj',
  'QiSL.App.Customer/src/QiSL.App.Customer.EntityFrameworkCore/QiSL.App.Customer.EntityFrameworkCore.csproj',
  'QiSL.App.WRH/src/QiSL.App.WRH.EntityFrameworkCore/QiSL.App.WRH.EntityFrameworkCore.csproj',
  'QiSL.App.EyeConnect/src/QiSL.App.EyeConnect.EntityFrameworkCore/QiSL.App.EyeConnect.EntityFrameworkCore.csproj',
  'QiSL.App.MTL/src/QiSL.App.MTL.EntityFrameworkCore/QiSL.App.MTL.EntityFrameworkCore.csproj',
  'QiSL.App.WF/src/QiSL.App.WF.EntityFrameworkCore/QiSL.App.WF.EntityFrameworkCore.csproj',
  'QiSL.Fabric/Platform/aspnet-core/src/QiSL.Platform.Web.ISL.Host/QiSL.Platform.Web.ISL.Host.csproj')
Start-AlignmentLog 'step24-isl-build'
try {
    Assert-Branch $R $d.IslBranch
    Write-Warn2 "T4: if QiSL.App.BaseTemplate.Application.Query.Services.tt changed in the import, regenerate it in VS (Run Custom Tool) before this build; the generated .cs is what compiles."
    foreach ($p in $order) { Write-Step (Split-Path $p -Leaf); $cfg = if ($p -like '*ISL.Host*') { 'Development' } else { 'Debug' }; Invoke-Dotnet $R build $p -c $cfg --nologo -v q -m:1 -nr:false }
    $libs = & git -C $R status --porcelain -- lib
    if ($libs) { Write-Step "Refreshed lib/ DLLs"; $libs | ForEach-Object { Write-Plan $_ }
        if ($Commit -and (Confirm-Step "Commit refreshed lib/*.dll (repo convention)?")) { Invoke-Git $R add -- lib | Out-Null; Commit-IfRequested $R $true "build(lib): refresh shared assemblies after upstream import" }
        else { Write-Warn2 "lib/ changes left uncommitted" } }
    Write-Ok "ISL host builds."
} finally { Stop-AlignmentLog }
