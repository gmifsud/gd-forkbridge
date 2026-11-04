#Requires -Version 7.0
<# Step 9: CI-equivalent verification of the Backend branch. Read-only apart from build output. #>
[CmdletBinding()] param([switch]$SkipTests)
Import-Module (Join-Path $PSScriptRoot 'Alignment.Common.psm1') -Force
$d = Get-AlignmentDefaults; $R = $d.BackendWorktree
Start-AlignmentLog 'step14-verify'
try {
    Assert-Branch $R $d.BackendBranch
    Write-Step "Static guards"
    $g = & git -C $R grep -nE ': base\(options, "|\.ModuleCode\b|ConfigureModularStatuses\(tenantId, "' -- '*.cs' | Select-String -NotMatch 'ModuleConfiguration|CurrentConfig|SharedKernelConsts|SecResourceService|AbpAspNetCore'
    if ($g) { throw "string-overload residue:`n$g" }; Write-Ok "no string-overload residue"
    Write-Step "Builds (nearest bounded contexts, then CI host)"
    Invoke-Dotnet $R build QiSL.Fabric/Kernel/QiSL.Platform.SharedKernel.EntityFrameworkCore/QiSL.Platform.SharedKernel.EntityFrameworkCore.csproj --nologo -v q
    Invoke-Dotnet $R build QiSL.Fabric/BaseTemplate.0/aspnet-core/src/QiSL.App.BaseTemplate.EntityFrameworkCore/QiSL.App.BaseTemplate.EntityFrameworkCore.csproj --nologo -v q
    Invoke-Dotnet $R build QiSL.App.WF/src/QiSL.App.WF.EntityFrameworkCore/QiSL.App.WF.EntityFrameworkCore.csproj --nologo -v q
    Invoke-Dotnet $R build QiSL.Fabric/Platform/aspnet-core/src/QiSL.Platform.Web.Host/QiSL.Platform.Web.Host.csproj -c Release --nologo -v q
    if ($SkipTests) { return }
    Write-Step "Tests (CI set + Platform tests now that restore works)"
    Invoke-Dotnet $R test QiSL.App.Practice/test/QiSL.App.Practice.Application.Tests/QiSL.App.Practice.Application.Tests.csproj --nologo
    Invoke-Dotnet $R test QiSL.App.Registration/test/QiSL.App.Registration.Application.Tests/QiSL.App.Registration.Application.Tests.csproj --nologo
    Invoke-Dotnet $R test QiSL.Fabric/Platform/aspnet-core/test/QiSL.Platform.Tests/QiSL.Platform.Tests.csproj --nologo
    Write-Ok "Backend branch verified. Raise PR -> $($d.BackendTarget); after merge delete origin/feature/sync-ebs-repo (step 10)."
} finally { Stop-AlignmentLog }
