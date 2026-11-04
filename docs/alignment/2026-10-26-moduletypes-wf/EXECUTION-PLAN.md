---
title: Backend ↔ ISL-Backend alignment — execution plan
date: 2026-10-26
status: proposed
supersedes: Part A §6 and Part B §B.6 of the impact analysis
---

# Execution plan (single flow)

Read top to bottom. Each step says **where** (repo / branch / worktree), **what**, and **done-when**. Patches referenced are in `patches/` next to this file. Nothing below has been applied; every command is for you or Kim to run on Windows.

Paths: Backend worktree `D:\Repos\Backend\worktrees\Backe-migra-1655c` (branch `migrations/24326-upgrade-angular-to-version-17`); ISL worktree `D:\Repos\ISL\ISL-Backend.worktrees\fix\plt-core-module-types-enum` (branch `fix/plt-core-module-types-enum` = `d5380bd9a`).

## Gate 0 — decisions (before any code)

1. **Confirm** the Backend target is `Development` via a PR from `migrations/24326-upgrade-angular-to-version-17`. (Inferred from the Codex review; not stated anywhere.)
2. **Confirm** `origin/feature/sync-ebs-repo` is retired after step 8 — it carries `4412b4f48` and is stale vs Development (missing #24317, cache, email-key, csproj fixes).
3. **Confirm** the WF fork model = partial-method hook (`SeedForkWorkflows`) now; IoC contributors later when WF becomes a package.

## Backend — branch `migrations/24326-upgrade-angular-to-version-17`, one PR per step

4. **Revert the string module overloads.**
   `git revert --no-edit 4412b4f48` → 3 files; expected result blobs `a2bdf61e6` (`BaseDbContext.cs`), `f1665c927` (`BaseCreator.cs`), `4e5624bf6` (`QiSLRepositoryBase.cs`) — identical to `Development`. Simulated clean.
   Done-when: `git grep -nE ": base\(options, \"|\.ModuleCode\b" -- "*.cs"` returns nothing relevant; Kernel EF + BaseTemplate EF build.
5. **Central package versions** (fixes NU1010 / Codex F1). Add to `Directory.Packages.props`:
   `xunit 2.4.2`, `xunit.extensibility.execution 2.4.2`, `xunit.runner.visualstudio 2.4.5`, `NSubstitute 4.3.0`, `Abp.TestBase 8.3.1`, set `Microsoft.NET.Test.Sdk 17.4.1`, `Shouldly 4.1.0`.
   Done-when: `dotnet restore` succeeds for `QiSL.Platform.Test.Base`, `QiSL.Platform.Tests`, `QiSL.Platform.GraphQL.Tests`.
6. **WF extension point.** `git apply patches/01-backend-wfseeder-fork-hook.patch` (verified with `--check` against this branch). Then delete `QiSL.App.WF/src/QiSL.App.WF.EntityFrameworkCore/Migrations/Seed/Workflows/CustomerOnboarding/` (5 files) — they move to ISL in step 11.
   Done-when: `QiSL.App.WF.EntityFrameworkCore` builds; `WFSeederService.cs` contains `partial void SeedForkWorkflows` and no `CustomerOnboarding` reference.
7. **ProcessFlowRule_Code width** (Codex F2). `git apply patches/03-backend-processflowrule-code-length.patch` (`[QiSLCode(10)]`), then regenerate the snapshot so it matches migration `20260817190500_WF_WidenWorkflowStatusCodes` — keep that migration id, do not add a new migration:
   `dotnet ef migrations add Tmp -p QiSL.App.WF/src/QiSL.App.WF.EntityFrameworkCore -s <WF startup project>` → copy the snapshot delta only, `dotnet ef migrations remove`. Confirm `Up()` of a fresh scaffold is empty.
   Done-when: `WFDbContextModelSnapshot.cs` says `HasMaxLength(10)` for `ProcessFlowRule_Code`; a fresh `migrations add` produces no column change.
8. **Optional, Development directly:** cherry-pick the `ExternalAppService.cs` JSON `error`-property block from ISL `385c4f8ef` (+20 lines). Separate small PR.
9. **Verify the branch** (CI set + targeted):
   `dotnet build QiSL.Fabric/Platform/aspnet-core/src/QiSL.Platform.Web.Host/QiSL.Platform.Web.Host.csproj -c Release`
   `dotnet test QiSL.App.Practice/test/QiSL.App.Practice.Application.Tests/...csproj`
   `dotnet test QiSL.App.Registration/test/QiSL.App.Registration.Application.Tests/...csproj`
   `dotnet test QiSL.Fabric/Platform/aspnet-core/test/QiSL.Platform.Tests/...csproj` (now restores after step 5).
10. **Merge to `Development`.** Then delete `feature/sync-ebs-repo` (step 2). Rule from here on: ISL imports from `Development`, never from a feature branch.

## ISL-Backend — branch `fix/plt-core-module-types-enum` → `migration/24404-plt-ng17-fork-be`

11. **Add the fork hook file.** Create `QiSL.App.WF/src/QiSL.App.WF.EntityFrameworkCore/Migrations/Seed/WFSeederService.Fork.cs` from `patches/02-isl-wfseeder-fork.cs`. Move the CustomerOnboarding creators to `Migrations/Seed/Workflows/_Fork/CustomerOnboarding/` (namespace unchanged). ISL's copy already uses `ModuleTypes.CUSTOMER` — keep it.
    Done-when: `QiSL.App.WF.EntityFrameworkCore` builds in ISL with no edit to any upstream-owned WF file.
12. **Merge `d5380bd9a` onto `origin/migration/24404-plt-ng17-fork-be`** (`e5ad977c6`; simulated 0 conflicts).
13. **Import Backend `Development` (≥ step 10) with the fork scripts**, in this order, committing after each:
    `pwsh -File scripts/Fork/FromUpstreamFabric.ps1 -Branch Development`
    `pwsh -File scripts/Fork/FromUpstreamWF.ps1 -Branch Development`
    Expected: `WFSeederService.cs` takes the upstream partial version cleanly (ISL never edited it after step 11); `WFSeederService.Fork.cs` is untouched (fork-only addition).
14. **Delete stale upstream-deleted files** the overlay left behind (the import scripts do not remove them): `Kernel/…Job.Engine/Helpers/{CronUtil,RecurringJobHelper,ScehduledJobsHelper}.cs`, `Job.Engine/Services/{ExampleTask,JobTaskService}.cs`, `BaseTemplate.Core.Shared/Enums/ConfigurationTypes.cs`, `BaseTemplate.Core/Localization/QiSLLocalizableStringBuilder.cs`, `Kernel/…EntityFrameworkCore/…/DisplayAbpUserDto.cs`, `Kernel/…Domain/Dtos/ObjectValueSelectListItem.cs`, the `*.bak` files, `QiSL.App.BaseTemplate.{All,Mobile}.sln`, the two compatibility shims (`BaseTemplate…Authorization/PermissionData.cs`, `BaseTemplate.Core/Interfaces/IQiSLRepository.cs`) and the two unused Account DTOs. Retarget `using` statements in `QiSLAbpUserRepository.cs`, `EnumManager.cs`, WRH replenishment services, etc.
    Done-when: full ISL solution builds; `git grep -l "QiSL.App.BaseTemplate.Interfaces;"` returns nothing.
15. **Regenerate T4** (`QiSL.App.BaseTemplate.Application.Query.Services.tt`) and rebuild in order: BaseTemplate → Customer/WRH/EyeConnect/MTL EF → ISL host; commit refreshed `lib/*.dll` per repo convention.
    `dotnet build "QiSL.Fabric\Platform\aspnet-core\src\QiSL.Platform.Web.ISL.Host\QiSL.Platform.Web.ISL.Host.csproj" -c Development -m:1 -nr:false`
16. **ISL housekeeping (same branch or follow-ups):** fix `scripts/EFCore/ef-config.json` `backendRoot` (currently `C:\Repos\Backend\QiSL.Fabric` — wrong repo and the C: junction) to `..\..\QiSL.Fabric`; fix `scripts/Build-WebHost.ps1` / `launch*.ps1` to discover `QiSL.App.*\src\*.Web.Core\*.Web.Core.csproj`; decide the fate of data migration `20260803093822_BaseTemplate_Update-ModularStatuses-Type` (heuristic rewrite of `Gen$LobConfigurations.ConfigurationType`, empty `Down()`) — it diverges `__EFMigrationsHistory` from Backend fleets.
17. **Add the fork fitness check** to `scripts/Fork/FromUpstream.Common.psm1`: fail the import if any file that exists upstream under `QiSL.App.WF` differs from upstream after the merge *and* is not in a conflict state — i.e. the fork must only add files to WF, never edit upstream-owned ones.

## Standing rules after this

- Fork never edits an upstream-owned file in `QiSL.App.WF` or `QiSL.Fabric`; it adds files (`*.Fork.cs`, `_Fork/` folders, `ISL.Overrides/`).
- Fork-owned workflow seeds live behind `SeedForkWorkflows`; shared schema changes they need (column widths, enums) are raised upstream first.
- ISL imports from Backend `Development` only, after Backend merges.

## Rollback

- Steps 4–7 are independent PRs; each reverts with `git revert` and needs no data rollback (no persisted representation changes).
- Step 7's snapshot change has no DB effect; migration `20260817190500` is already applied on ISL DBs and never on Backend DBs — unchanged either way.
- Step 13 imports are staged, not committed, by the scripts; `git restore --source=HEAD --staged --worktree -- QiSL.App.WF QiSL.Fabric` (excluding the two protected ISL hosts) discards them.

## Not verified in this analysis

No build or test was run (no `dotnet` in the bridge VM). Patch 01 was verified only with `git apply --check` against the branch blob. Step 7's snapshot regeneration output and step 14's build are the first real compile gates.
