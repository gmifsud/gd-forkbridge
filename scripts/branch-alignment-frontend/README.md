# Frontend ↔ ISL-Frontend branch alignment

Frontend counterpart of [`../branch-alignment`](../branch-alignment/README.md) (Backend). It reconciles the shared Angular projects (`abp-zero-template`, `qisl-client-ng-fabric`, `qisl-client-ng-mod-wf`) of two **new** branches so that both PR into their source branches with byte-identical shared code and nothing lost in either direction.

| Repo | New branch | Base | PR target |
|---|---|---|---|
| Frontend (QiSL) | `migration/configuration-cache-synchronisation-ng17-isl-alignment` | `origin/migration/configuration-cache-synchronisation-ng17` | `migration/configuration-cache-synchronisation-ng17` |
| ISL-Frontend (EBS) | `migration/24403-qisl-fe-alignment` | `origin/migration/24403-plt-ng17-fork-fe` | `migration/24403-plt-ng17-fork-fe` |

The Frontend base is the branch checked out in `D:\Repos\Frontend.worktrees\feature\configuration-cache-synchronisation`. The folder name is older than the branch name. That worktree is never touched: `01` creates new worktrees under `WorktreeRoot`.

All names, paths, refs and rules live in [`Align.Config.psd1`](Align.Config.psd1).

## Setup

| Requirement | Why |
|---|---|
| PowerShell 7+ (`pwsh`) | All scripts use `#Requires -Version 7.0` |
| Git ≥ 2.49 | Tree-ish `merge-tree --merge-base` |
| `D:\Repos\Frontend` and `D:\Repos\ISL\ISL-Frontend` cloned, with `origin` credentials | `01` fetches both |
| `yarn` on PATH (only for `04 -Build`) | Fresh worktrees need `yarn install` before `yarn build:dev` |
| `az` CLI + `azure-devops` extension (only for `05 -CreatePr`) | Otherwise `05` prints the PR URLs |

Edit `Align.Config.psd1` only for these cases:

- **Paths**: use `D:\Repos\...` everywhere. `C:\Repos` is a junction, and the module refuses it.
- **Branches**: `BaseRef`, `NewBranch` and `TargetBranch` per side. `NeverPush` lists branches that `05` never pushes.
- **In-place mode** (optional): set `Frontend.Worktree` to an existing worktree and `NewBranch` to the branch checked out there. `01` then only verifies that branch, and `03` commits onto it. `05` still refuses to push it while it is listed in `NeverPush`. The default (new branches) is safer.
- **`SyncBase`**: the 3-way base. See [Sync base](#sync-base).
- **`StateDir`**: `D:\ForkBridge\state\frontend-alignment` holds the scratch repo, plans, patches, logs and `sync-ledger.json`. It is separate from the Backend state folder.

## How it works

The core is the same as the Backend tool:

- **Scratch repo.** `StateDir\xrepo.git` borrows both object stores (alternates). Neither repository gets a new remote, ref or foreign object. Content crosses repositories only inside the binary patches that `03` applies.
- **Scoped 3-way merge.** Base is `SyncBase`, ours is ISL, theirs is Frontend. Only `Scope` minus `Protected` is considered.
- **Editable plan.** `02` writes `plan.tsv` (edit the Decision column) and `plan.md` (review summary).

### What differs from the Backend tool

| Topic | Backend tool | This tool | Reason |
|---|---|---|---|
| Sync base | Overlay source `ef3ce9136` | True merge base `c56a755` | ISL-Frontend imports Frontend **commit by commit** (`scripts/Fork/FromUpstreamCommit.ps1`, no ancestry), so no single Frontend commit is its source |
| New rule `ISL_BEHIND` | — | ISL blob equals an **earlier Frontend version** of the same path → `TAKE_FE` | Partial per-commit imports would otherwise look like ISL edits (promoted back as reverts) or conflicts. Simulated: 207 of 479 differing paths |
| Packages | `Directory.Packages.props` union | `*/package.json` dependency-section union (add-only, versions reported, rest of the file per side) | Same semantics, applied to npm manifests |
| Stale-file reference check | C# type names | TS export names plus import-specifier stem (`./qisl.grid.icon.component`); other files by file name (`templateUrl`, `styleUrls`, CSS `url()`) | Angular references files by path, not only by symbol |
| Protected | Hosts, `.Client` proxies | ISL-protected paths from ISL's own `FromUpstream.Common.psm1`, `appconfig*.json`, NSwag output (`.precompiled-proxies`, `src/shared/service-proxies`) | Proxies are generated per product from its own backend modules. Regenerate them after the Backend alignment instead of copying |
| Composition roots | Protected | `KEEP_BOTH` overrides: `angular.json`, `*/src/tsconfig.json`, `tsconfig.proxies.json`, `app-routing.module.ts`, `qisl.module.names.json`, `index.html`, `environments/*`, `*.module.registry.ts` | These files mix product wiring with shared changes. `plan.md` shows the default decision they replaced and whether a clean 3-way merge exists (set `MERGED` to take it) |
| Guards | `git grep` on `*.cs` | Per-guard `Include` globs, hits inside scope only. Seeded: Frontend must not reference ISL modules (`@qislwrh/`, `qisl-client-ng-mod-eyeconnect`, …) | Keeps fabric and WF free of fork knowledge |
| Builds | `dotnet build` per project | `Exe` + `Args` per step (`yarn install --frozen-lockfile`, `yarn build:dev`). `-BuildSide` limits it to one side. Tracked files the build changed are reported | npm workspaces |
| Restore (`06`) | Present | Not included | Specific to the Backend bulk-copy commits |

### Rules (from `02`, all editable in `plan.tsv`)

Checked top to bottom; the first match wins.

| Category | Default decision | Meaning |
|---|---|---|
| `FORMAT_ONLY` | `TAKE_FE` | Differs only in whitespace or BOM; Frontend formatting is kept and no code is lost |
| `FE_CHANGED/ADDED/DELETED` | `TAKE_FE` | Only Frontend changed since the sync; ISL follows (e.g. the configuration-cache services) |
| `ISL_BEHIND` | `TAKE_FE` | ISL holds an earlier Frontend version of the file (found in Frontend history) |
| `ISL_STALE` | `DELETE_BOTH` | ISL file whose exact content Frontend held before deleting it. Kept (`KEEP_ISL_ONLY`) while surviving ISL code still references it |
| `PACKAGES` | `PACKAGES_UNION` | `*/package.json`: each side gains the other's missing dependencies. Removals never propagate. Version differences are only reported, except those in `PackageVersionOverrides` |
| `ISL_CHANGED` | `TAKE_ISL` | Semantic ISL change since the sync; promoted to Frontend (review each one in `plan.md`) |
| `ISL_DELETED` | `TAKE_FE` | The file is restored in ISL rather than deleted in Frontend |
| `BOTH_CHANGED` | `MERGED` | Clean 3-way merge; both sides get the merged result |
| `CONFLICT` | `MANUAL` | `03` refuses until you set a decision |
| `ISL_RENAMED` / `ISL_RENAME_SOURCE` | `DELETE_BOTH` / `TAKE_FE` | ISL renamed the file without changing its content; the Frontend name is restored |
| `FORK_OWNED`, `ISL_ONLY` | `KEEP_ISL_ONLY` | Fork additions; never pushed to Frontend |
| `Overrides` in config | as configured | Composition roots → `KEEP_BOTH` |

`plan.md` tags every `TAKE_ISL`, `MERGED`, `KEEP_BOTH` and `MANUAL` row with whether Frontend or ISL matches `ReferenceRef` (`origin/migrations/24326-upgrade-angular-to-version-17`), since QiSL is primary.

## Run (from this folder)

```powershell
pwsh -File .\02-New-AlignmentPlan.ps1 -Preview          # optional: plan against the bases before creating anything
pwsh -File .\01-New-AlignmentBranches.ps1               # dry-run
pwsh -File .\01-New-AlignmentBranches.ps1 -Apply        # fetch, new worktrees + branches
pwsh -File .\02-New-AlignmentPlan.ps1                   # writes plans\<ts>\plan.tsv + plan.md
#   review plan.md; set a Decision for every MANUAL row in plan.tsv
pwsh -File .\03-Invoke-Alignment.ps1                    # dry-run: builds both patches, git apply --check
pwsh -File .\03-Invoke-Alignment.ps1 -Apply -Commit     # one commit per branch + sync-ledger.json
pwsh -File .\04-Test-Alignment.ps1                      # fitness: shared scope identical; guards
pwsh -File .\04-Test-Alignment.ps1 -Build               # + yarn install / build:dev in both worktrees (slow)
pwsh -File .\05-Publish-Alignment.ps1                   # prints push / az / PR URLs
pwsh -File .\05-Publish-Alignment.ps1 -Push -CreatePr   # pushes ONLY the two new branches; draft PRs
```

To compare sync-base candidates without editing the config, run `02 -Preview -SyncBase <sha>`.

### Safety model

- Nothing changes without `-Apply`, and every write step asks for confirmation (`-Yes` skips the prompt).
- Existing branches and worktrees are never touched. `01` refuses if a branch name is already taken anywhere other than the configured worktree.
- `03` refuses a `-Preview` plan, and a stale plan if a branch moved inside the aligned scope. It also refuses while any `MANUAL` row remains (`-SkipManual` leaves those rows untouched on both sides), and refuses `MERGED` rows that still contain conflict markers.
- `05` refuses unless `04` passed on the current HEADs, and never pushes a branch in `NeverPush` (both bases and both development lines are listed).

## Sync base

`SyncBase = c56a755` (2026-08-06, "#23707 Fix for Nswag Warnings") is `git merge-base` of the two branches. On 2026-10-27, 207 Frontend commits were checked as alternative bases. The nearest one (`27134019f`, the 24326 tip that ISL imported last) still differed from ISL on 444 scoped paths. With that base, every Frontend change ISL skipped would read as an ISL edit, which 02 would then promote back into Frontend as a revert. With the merge base plus `ISL_BEHIND`, the rows split as follows (Python simulation, before overrides, format-only and package rules):

| Rows | Outcome |
|---|---|
| 207 | `ISL_BEHIND` → Frontend version |
| ~162 | ISL changes or additions (most are ISL-only additions: themes, fonts, `isl-routes`, ISL proxies) |
| 40 | Clean 3-way merges |
| 19 | Textual conflicts, including `angular.json`, `package.json`, `tsconfig.json` and `environment.ts`, which the overrides and the package union absorb |
| 6 / 1 | Frontend-only changes / ISL deletion |

Run `02 -Preview` first; its `plan.md` gives the authoritative numbers.

## After both PRs merge

1. Set `SyncBase` to `nextSyncBase` from `StateDir\sync-ledger.json`. Later alignments then start from a true common state.
2. Regenerate NSwag proxies in each repo (`abp-zero-template/nswag/refresh.ps1`) once the Backend alignment has also merged. Protected proxy folders are never reconciled by this tool.
3. Flip `ISL_STALE` rows kept as `KEEP_ISL_ONLY` (still referenced) to `DELETE_BOTH` once their references are retargeted.
4. Review `KEEP_BOTH` rows marked "clean 3-way merge available". Shared Angular 17 settings in `angular.json` and `tsconfig.json` (e.g. `target`, `useDefineForClassFields`) belong on both sides by hand.

Root files (`package.json` workspaces, `yarn.lock`, `bun.lock`, `*.sln`, pipelines) are outside `Scope` on purpose: they describe each product's workspace set. `04 -Build` installs `node_modules` into the new worktrees; build output is reported and must not be committed.
