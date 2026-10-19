# Fork-Over.ps1 audit — what was retained, replaced or fixed

Source: `Fork-Over.ps1` in the Backend project (2026-10-12). Read as evidence of the workflow, not a spec.
Each row names the script behaviour and what ForkBridge does instead.

## Retained (concept kept, implementation hardened)

| Script behaviour | ForkBridge |
|---|---|
| Cross-repo fetch from the other local clone (no credentials) | Kept — but into **harness mirrors**, never into the user's checkout |
| Origin-vs-local branch resolution with divergence warning (`_local` namespace) | Kept: `refs/fb/local/heads/*`, warning becomes a `REF_DIVERGENCE` finding; `--prefer-local` |
| Merge-base-relative feature range; refusal when SourceRef == SourceBase | Kept and extended: also refused for protected/base names; merge-base explained in the plan |
| Explicit `-Range` / `-Commits` | Kept as distinct selection modes; ranges must be ancestor-bounded |
| `Fork-Over-Source: <alias>@<sha>` trailer | Kept verbatim for compatibility (legacy trailers are read as provenance) plus scope/plan/run/policy/partial trailers |
| Pinned paths whose target version wins | Kept as `targetPinned`, per product, glob-based, reported per file |
| Auto-exclusion of source-only top-level folders | Replaced by explicit `productSpecific` policy at any depth (see fixes) |
| Byte-transparent patch handling (Latin1 round-trip) | Kept in `PatchBuilder` header rewriting |
| `merge-tree --write-tree` conflict prediction | Kept, repurposed to detect merge-only resolution changes |
| Trial apply in a temporary worktree | Generalised: every simulation runs in a harness worktree |
| Merge mode refused towards QiSL without `-Force` | Generalised: modes allowed per direction in policy; merge is an advanced, explicitly enabled mode |

## Replaced

| Script behaviour | Problem | ForkBridge |
|---|---|---|
| `git fetch origin --prune` in **both user checkouts** | moves the user's remote-tracking refs | remote fetched directly into the mirror |
| `Set-CrossRemote` adds/edits a remote and its fetch/push config in the **target checkout** | mutates user remote config | no remote config is ever written to user repos |
| `git switch --no-track -c <branch>` in the **target checkout** | changes the user's current branch (contradicting the script's own header comment); fails on dirty trees | candidate branch lives only in the harness mirror |
| Merge / `git am` run in the user's checkout; on conflict the user is left mid-merge/mid-am | in-progress operation in the developer's working copy | conflicts happen in the harness workspace; evidence saved; `resume` / `abort` |
| `git worktree add` (dry run) registers a worktree in the user's `.git` | user repo metadata modified | worktrees belong to harness mirrors |
| Fetch failure → warning, continue with stale refs | silent staleness | fail by default (exit 15); `--offline` records a REVIEW finding |
| Refs re-resolved on every invocation | dry run and real run can differ | immutable plan with pinned SHAs and hashed artifacts |
| `-Push` with `push -u` from the checkout | sets upstream config; no review gate | approval-bound publish from the mirror; never forced; protected branches refused |
| No validation | "applies cleanly" treated as done | baseline vs candidate validation; READY only when required checks pass |

## Fixed (correctness defects)

1. **Partial transfer claimed as complete.** `format-patch -- <pathspec>` silently drops excluded parts
   of mixed commits, yet the trailer claims the whole commit. Later runs then skip it forever.
   → Mixed commits are a REVIEW finding listing included/excluded files; trailers carry the actual
   scope and `ForkBridge-Partial`; partial provenance never suppresses a later wider transfer.
2. **Idempotency by trailer/ancestry only.** Reverted or edited transfers were skipped; manual
   equivalent changes were re-applied. → per-file content evidence (reverse/forward apply on a
   throw-away index) + patch-id + provenance, with conservative REVIEW states.
3. **Merge commits skipped silently.** "Their parents' changes are picked individually" loses
   merge-only conflict resolutions. → `MERGE_RESOLUTION_CHANGES` blocks with the affected files.
4. **Only top-level exclusion.** Nested product-specific paths (e.g. `QiSL.Platform.Web.ISL.Host`
   inside shared `QiSL.Fabric`) were transferred/overwritten; unknown new folders slipped through.
   → classification at any depth; unknown paths block by default.
5. **No path mapping.** Components at different paths (frontend fork moved its shared library) were
   unsupported. → per-component source/target path mapping, including renames.
6. **Filtered merges poison ancestry.** Merge mode + pinned resets recorded excluded content as
   merged; later merges silently skip it. → reported as `MERGE_ANCESTRY`; drift detects
   `ANCESTRY_CONTENT_MISMATCH`; reconcile-from-baseline repairs it.
7. **Directory-rename inference in merges.** Found by the fixture tests: Git moved a new
   `App.VAM/*` file into `App.MTL/*` because the fork "renamed" one module into the other. → merges run
   with `merge.directoryRenames=false` and every unexpected path is reset and reported.
8. **Minute-resolution branch names** could collide. → timestamp + 48 random bits.
9. **No dependency awareness.** → project/assembly/package/TS graph, consumer impact, blocking on
   missing prerequisites.
