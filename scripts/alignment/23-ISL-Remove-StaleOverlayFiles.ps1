#Requires -Version 7.0
<# Step 14: delete files the additive 21-Oct overlay left behind (deleted/moved upstream), shims, .bak, dead DTOs. Reports remaining references; does NOT rewrite usings. #>
[CmdletBinding()] param([switch]$Apply, [switch]$Commit, [switch]$Yes)
Import-Module (Join-Path $PSScriptRoot 'Alignment.Common.psm1') -Force
$d = Get-AlignmentDefaults; Set-AlignmentYes $Yes; $R = $d.IslWorktree
$K='QiSL.Fabric/Kernel'; $BT='QiSL.Fabric/BaseTemplate.0/aspnet-core'
$stale = @(
  "$K/QiSL.Platform.SharedKernel.Job.Engine/Helpers/CronUtil.cs",
  "$K/QiSL.Platform.SharedKernel.Job.Engine/Helpers/RecurringJobHelper.cs",
  "$K/QiSL.Platform.SharedKernel.Job.Engine/Helpers/ScehduledJobsHelper.cs",
  "$K/QiSL.Platform.SharedKernel.Job.Engine/Services/ExampleTask.cs",
  "$K/QiSL.Platform.SharedKernel.Job.Engine/Services/JobTaskService.cs",
  "$BT/src/QiSL.App.BaseTemplate.Core.Shared/Enums/ConfigurationTypes.cs",
  "$BT/src/QiSL.App.BaseTemplate.Core/Localization/QiSLLocalizableStringBuilder.cs",
  "$K/QiSL.Platform.SharedKernel.EntityFrameworkCore/EntityFrameworkCore/DisplayClasses/DisplayAbpUserDto.cs",
  "$K/QiSL.Platform.SharedKernel.Domain/Dtos/ObjectValueSelectListItem.cs",
  "$BT/QiSL.App.BaseTemplate.All.sln", "$BT/QiSL.App.BaseTemplate.Mobile.sln",
  "$BT/src/QiSL.App.BaseTemplate.Core/Localization/BaseTemplate/BaseTemplate-fr.json.bak",
  "$BT/src/QiSL.App.BaseTemplate.Core/Localization/BaseTemplate/BaseTemplate.json.bak",
  "QiSL.Fabric/Platform/aspnet-core/src/QiSL.Platform.Core/Localization/Platform/Platform-en-GB.xml.bak",
  "$K/QiSL.Platform.SharedKernel.Application/Command/ExternalServiceUserCommandAppService.cs.bak",
  # compatibility shims and dead DTOs
  "$BT/src/QiSL.App.BaseTemplate.Core.Shared/Authorization/PermissionData.cs",
  "$BT/src/QiSL.App.BaseTemplate.Core/Interfaces/IQiSLRepository.cs",
  "$K/QiSL.Platform.SharedKernel.Application/Dtos/Accounts/IsTenantValidInput.cs",
  "$K/QiSL.Platform.SharedKernel.Application/Dtos/Accounts/QislResolveTenantIdOutput.cs")
# symbols whose remaining references must be retargeted by hand after deletion
$symbols = 'CronUtil|RecurringJobHelper|ScehduledJobsHelper|JobTaskService|ExampleTask|\bConfigurationTypes\b|QiSLLocalizableStringBuilder|DisplayAbpUserDto|ObjectValueSelectListItem|QiSL\.App\.BaseTemplate\.Core\.Shared\.Authorization|using QiSL\.App\.BaseTemplate\.Interfaces;'
Start-AlignmentLog 'step23-isl-stale'
try {
    Assert-Branch $R $d.IslBranch; Assert-CleanScope $R @('QiSL.Fabric')
    $present = $stale | Where-Object { Test-Path (Join-Path $R $_) }
    $present | ForEach-Object { Write-Plan "git rm $_" }
    Write-Step "References that will need retargeting (reported, not auto-fixed)"
    $refs = & git -C $R grep -nE $symbols -- '*.cs' | Where-Object { $p = ($_ -split ':')[0]; $present -notcontains $p }
    $refs | Select-Object -First 80 | ForEach-Object { Write-Host "   $_" }
    Write-Plan "Backend now has: SharedKernel.Domain/Dtos/DisplayClasses/DisplayAbpUserDto.cs, SharedKernel.Domain/Localization/QiSLLocalizableStringBuilder.cs, SharedKernel.Helpers.Authorization.PermissionData, SharedKernel.Domain.Base.IQiSLRepository; ConfigurationTypes was removed upstream in #16445 (0f949a75b) - check EnumManager/WRH usages."
    if (-not $Apply) { Write-Warn2 "dry-run. Re-run with -Apply"; return }
    if (-not (Confirm-Step "git rm $($present.Count) stale file(s)? (references above must then be fixed before build)")) { return }
    foreach ($f in $present) { Invoke-Git $R rm -q -- $f | Out-Null }
    Write-Warn2 "Now retarget the usings listed above, then run 24-ISL-Build.ps1. Commit after a green build."
    Commit-IfRequested $R $Commit "chore(fork): remove files deleted upstream that the overlay import left behind; drop compatibility shims and unused DTOs"
} finally { Stop-AlignmentLog }
