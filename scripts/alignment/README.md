# Backend ↔ ISL-Backend alignment scripts

Implements EXECUTION-PLAN.md (2026-10-26). PowerShell 7. Every script is **dry-run by default**; `-Apply` executes, `-Commit` commits (with session trailer), **nothing ever pushes**. Destructive steps prompt (`-Yes` to skip). Transcripts go to `D:\ForkBridge\state\alignment-2026-10-26\` (outside both repos). Paths/branches are in `Alignment.Common.psm1` → `$Defaults`; always the `D:` canonical paths.

| Script | Plan step | Repo | Needs |
|---|---|---|---|
| `00-Preflight.ps1` | Gate 0 | both | git, dotnet, dotnet-ef |
| `10-Backend-Revert-StringOverloads.ps1` | 4 | Backend `24326` | clean scope on 3 files |
| `11-Backend-CentralPackageVersions.ps1` | 5 | Backend | — |
| `12-Backend-WFSeeder-ForkHook.ps1` | 6 | Backend | `patches/01` |
| `13-Backend-ProcessFlowRuleCode.ps1` | 7 | Backend | `patches/03`, dotnet-ef, WF startup project |
| `14-Backend-Verify.ps1` | 9 | Backend | — |
| `20-ISL-Add-ForkHook.ps1` | 11 | ISL fix branch | `patches/02`, `patches/01` |
| `21-ISL-Merge-MigrationBase.ps1` | 12 | ISL | clean tree |
| `22-ISL-Import-Upstream.ps1` | 13 | ISL | Backend PRs merged to `Development` first |
| `23-ISL-Remove-StaleOverlayFiles.ps1` | 14 | ISL | manual using retargeting afterwards |
| `24-ISL-Build.ps1` | 15 | ISL | T4 regen first if `.tt` changed |
| `25-ISL-Housekeeping.ps1` | 16 | ISL | — |
| `30-Fitness-ForkOnlyAdds.ps1` | 17 / standing | both (read-only) | — |

Typical run:

```powershell
pwsh -File .\Run-All.ps1 -Phase Backend              # dry-run everything
pwsh -File .\10-Backend-Revert-StringOverloads.ps1 -Apply -Commit
pwsh -File .\11-Backend-CentralPackageVersions.ps1 -Apply -Commit
pwsh -File .\12-Backend-WFSeeder-ForkHook.ps1 -Apply -Commit
pwsh -File .\13-Backend-ProcessFlowRuleCode.ps1 -Apply -Commit
pwsh -File .\14-Backend-Verify.ps1
# PR -> Development; merge; delete origin/feature/sync-ebs-repo
pwsh -File .\Run-All.ps1 -Phase ISL                  # dry-run
pwsh -File .\20-ISL-Add-ForkHook.ps1 -Apply -Commit
pwsh -File .\21-ISL-Merge-MigrationBase.ps1 -Apply
pwsh -File .\22-ISL-Import-Upstream.ps1 -Apply       # commit each staged import yourself after review
pwsh -File .\23-ISL-Remove-StaleOverlayFiles.ps1 -Apply   # then fix usings, then:
pwsh -File .\24-ISL-Build.ps1 -Commit
pwsh -File .\25-ISL-Housekeeping.ps1 -Apply -Commit
pwsh -File .\30-Fitness-ForkOnlyAdds.ps1
```

Known manual points (by design): step 13 inspects the temporary migration's `Up()` before keeping the snapshot; step 22 leaves imports staged for review; step 23 reports references but does not rewrite `using`s; step 25 only reports the `Build-WebHost.ps1` change.

Not run anywhere yet: these scripts were drafted from git-object analysis; `patches/01` is `git apply --check`-verified against `24326` blob `452e5b7`. First real execution should be `00-Preflight.ps1` then `10-…` dry-run.
