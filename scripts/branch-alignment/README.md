# Backend ↔ ISL-Backend branch alignment

Reconciles the shared projects (`QiSL.Fabric`, `QiSL.App.WF`, `Directory.Packages.props`) of two **new** branches so that both PR to their `development` branches with byte-identical shared code and nothing lost in either direction.

| Repo | New branch | Base | PR target |
|---|---|---|---|
| Backend | `migrations/24326-isl-alignment` | `origin/migrations/24326-upgrade-angular-to-version-17` | `Development` |
| ISL-Backend | `migration/24404-qisl-alignment` | `origin/migration/24404-plt-ng17-fork-be` + `origin/fix/plt-core-module-types-enum` | `development` |

All names, paths and refs live in `Align.Config.psd1`.

`QiSL.Platform.Web.Host` is excluded alongside the two ISL hosts. Hosts are per-product composition roots (module wiring and environment config), not shared code. Each promotion or merge row in `plan.md` is tagged with whether Backend or ISL matches `origin/Development` (`ReferenceRef`), since QiSL is the primary repo.

## How it works

- **Real 3-way merge on a recovered base.** The repos share history (common root `b69cf6fcd`), but their only true merge base is `7b994f87d` (7 Aug). The 21-Oct overlay `385c4f8ef` copied Backend `ef3ce9136` (`feature/sync-ebs-repo`) into ISL without recording ancestry. `SyncBase = ef3ce9136` restores that base, so `git merge-tree` sees what each side actually changed since then.
- **Scratch repo, no cross-pollution.** `StateDir\xrepo.git` borrows both object stores (alternates). Neither repository gets a new remote, ref or foreign object. Content crosses repositories only inside the binary patches that `03` applies.
- **Rules that keep every change** (from `02`, all editable in `plan.tsv`):

| Category | Default decision | Meaning |
|---|---|---|
| `BE_CHANGED/ADDED/DELETED` | `TAKE_BE` | Only Backend changed since the sync; ISL follows |
| `ISL_CHANGED` | `TAKE_ISL` | Semantic ISL change; promoted to Backend |
| `FORMAT_ONLY` | `TAKE_BE` | Differs only in whitespace or BOM; Backend's formatting is kept and no code is lost |
| `BOTH_CHANGED` | `MERGED` | Clean 3-way merge; both sides get the merged result |
| `CONFLICT` | `MANUAL` | `03` refuses until you set a decision |
| `ISL_STALE` | `DELETE_BOTH` | ISL-only file whose exact content Backend held before deleting it. Kept (`KEEP_ISL_ONLY`) if surviving ISL code references one of its top-level types; declaring a type with the same name, or mentioning it in a comment, does not count |
| `ISL_RENAMED` / `ISL_RENAME_SOURCE` | `DELETE_BOTH` / `TAKE_BE` | ISL renamed the file without changing its content; the Backend name is restored |
| `FORK_OWNED`, `ISL_ONLY` | `KEEP_ISL_ONLY` | Fork additions; never pushed to Backend |
| `ISL_DELETED` | `TAKE_BE` | The file is restored in ISL rather than deleted in Backend |
| `PACKAGES` | `PACKAGES_UNION` | Each side gains the other's missing `PackageVersion` entries. Removals never propagate. Version differences are only reported, except those in `PackageVersionOverrides` |
| `Overrides` in config | as configured | Seeded: `packages.lock.json` and the CustomerOnboarding creator → `KEEP_BOTH` |

## Run (PowerShell 7, Git ≥ 2.49, from this folder)

```powershell
pwsh -File .\02-New-AlignmentPlan.ps1 -Preview          # optional: plan against the bases before creating anything
pwsh -File .\01-New-AlignmentBranches.ps1               # dry-run
pwsh -File .\01-New-AlignmentBranches.ps1 -Apply        # fetch, new worktrees + branches, merge the ISL fix
pwsh -File .\02-New-AlignmentPlan.ps1                   # writes plans\<ts>\plan.tsv + plan.md
#   review plan.md; edit Decision cells in plan.tsv if needed
pwsh -File .\03-Invoke-Alignment.ps1                    # dry-run: builds both patches, git apply --check
pwsh -File .\03-Invoke-Alignment.ps1 -Apply -Commit     # one commit per branch + sync-ledger.json
pwsh -File .\04-Test-Alignment.ps1 -Build               # fitness: shared scope identical; guards; builds
pwsh -File .\05-Publish-Alignment.ps1                   # prints push / az / PR URLs
pwsh -File .\05-Publish-Alignment.ps1 -Push -CreatePr   # pushes ONLY the two new branches; draft PRs
```

Safety model:

- Nothing changes without `-Apply`, and every write step asks for confirmation (`-Yes` skips the prompt).
- Existing branches and worktrees are never touched. `01` refuses if a branch name is already taken.
- `03` refuses a stale plan if a branch moved inside the aligned scope, and refuses while any `MANUAL` row remains.
- `05` refuses unless `04` passed on the current HEADs, and never pushes a branch in `NeverPush`.

## Expected on the real repos (simulated read-only on 2026-10-26)

From 6,101 shared paths:

| Count | Outcome |
|---|---|
| 5,931 | Identical |
| **0** | Conflicts |
| 30 / 8 / 2 | Backend-only changes / additions / deletions, applied to ISL |
| 7 | Promoted to Backend: the `d5380bd9a` revert (`BaseDbContext`, `BaseCreator`, `QiSLRepositoryBase`), `ExternalAppService` JSON error extraction, two `SystemUpdateNotification` services, `DocumentTemplateVariableResolver` usings |
| 9 | Format-only, Backend formatting kept: the WF T4 reflows, `WFSeederService`, `QiSLUserAppService` |
| 4 | Merged into both: the 3 `BaseTemplate*.json` files and `SharedKernel.Domain.csproj` |
| 2 | Pure renames: `TenantResolution*Enum.cs` → Backend names |
| 42 | ISL stale files (overlay leftovers). Each is deleted unless still referenced |
| 46 | Fork-owned files kept in ISL |
| 18 | Other ISL-only files kept: the shims, `.bak` files, MaxLength attributes and migration `20260803093822` |
| 2 | Held back as `KEEP_BOTH`: the CustomerOnboarding creator and `packages.lock.json` |

`Directory.Packages.props` is unioned (Backend gains ISL's xunit entries; `Microsoft.NET.Test.Sdk` is set to 17.4.1 on both sides).

## After both PRs merge

1. Set `SyncBase` in the config to `nextSyncBase` from `StateDir\sync-ledger.json`. Later alignments and ISL's own `scripts/Fork/FromUpstream*.ps1` imports then start from a true common state.
2. These follow-ups are deliberately not part of the alignment:
   - WF fork hook (patches 01/02/03), which resolves the CustomerOnboarding `KEEP_BOTH`
   - retargeting references to the kept stale files, then flipping those rows to `DELETE_BOTH` on the next run
   - T4 regeneration, if a `.tt` changed

`lib/*.dll` are never committed on these branches; `04 -Build` rebuilds them locally (discard with `git restore -- lib`).

## Restoring development changes lost to bulk copies (ISL only)

Five ISL commits on 24404 copied Backend folders over ISL instead of merging, which overwrote correct `development` code. The worst was `3b50526374` "Fork", at 159 ISL-only files. A restore plan (`plans\restore-<ts>\restore.tsv`) 3-way merges aligned (ours), `7b994f87d` (base) and `development` (theirs) per file. `resolutions.md` in the same folder records every conflict resolution and review call.

```powershell
pwsh -File .\06-Apply-Restore.ps1                      # dry-run: lists every path and decision
pwsh -File .\06-Apply-Restore.ps1 -Apply -Commit       # one commit on migration/24404-qisl-alignment
pwsh -File .\04-Test-Alignment.ps1 -Build              # builds; shared-scope divergence is expected until the re-run below
```

| Decision | Effect |
|---|---|
| `KEEP` | Aligned content stays |
| `RESTORE` | Writes `Merged`, the clean 3-way merge (or `Dev` when `Merged` is empty) |
| `RESOLVED` | Writes `Merged`, a hand-resolved conflict |
| `TAKE_DEV` | Writes `Dev`; an empty `Dev` deletes the file |
| `DELETE` | Deletes the file |
| `MANUAL` / `REVIEW` | Block the run (`-SkipBlocked` leaves those paths untouched) |

Safety model:

- Refuses if HEAD moved on any restored path since the plan was built, or if those paths have uncommitted changes.
- Blobs the repository lacks ship in `blobs\<sha>` and are hash-verified before use.
- The index is written byte-exact (`update-index`, no EOL filters).

After the restore commit, re-run `02`/`03`, which uses SyncBase `720bbf74f`, so that the shared restores reach Backend. Choose `KEEP_BOTH` for ISL-specific rows such as `ExternalServiceAuthenticationSource.cs`.
