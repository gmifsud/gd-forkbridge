# Branch merge workspace — implementation plan

Branch `feature/branch-merge-workspace` from `origin/main` @ `e1ed2eb` (2026-11-04).

## 1. Baseline (before any change)

| Check | Command | Result |
|---|---|---|
| .NET build | `dotnet build ForkBridge.slnx` (.NET SDK 10.0.100, Linux, git 2.43) | 0 warnings, 0 errors |
| .NET tests | `dotnet test ForkBridge.slnx` | 148/148 passed (Alignment 72, Core 76) |
| UI build | `cd ui && npm ci && npm run build` | ok (533 kB initial) |
| UI typecheck | `npm run typecheck` | ok |
| UI tests | — | **none exist** (no test runner in `ui/package.json`) |

No pre-existing failures. Repository has no `AGENTS.md`.

## 2. Findings from the current code

| Area | What exists | Reuse for Merge |
|---|---|---|
| `Core/Infrastructure/ProcessRunner` | argv-only processes, timeout, kill-tree, bounded output | yes, unchanged |
| `Core/Git/GitClient` | typed git CLI wrapper, `GIT_OPTIONAL_LOCKS=0`, `LC_ALL=C`, redacted errors, `-z` parsers (`DiffTreeAsync`, `LsTreeAsync`) | yes; Merge adds an **isolation environment** per call (see §6) |
| `Core/Git/MirrorManager` | harness-owned bare mirror per alias; namespaces `refs/remotes/origin/*` (fresh from remote), `refs/fb/local/heads/*` (checkout branches), `refs/fb/local/remote/*` (checkout's remote-tracking refs); offline mode | yes: `SyncAsync` is the ref refresh; provenance = namespace |
| `Core/Storage/StateStore` | immutable hashed plans, atomic run.json, `Lock(name)` cross-process file locks, `Sanitize`, `NewId` | yes: JSON options, ids, locks; Merge keeps its own `merge/` subtree |
| `Core/Infrastructure/SafeFileSystem` | owned-dir creation/deletion, no link traversal, root containment | yes, for every Merge directory and deletion |
| `Core/Validation/ValidationRunner` + `UserRepoGuard` | trusted-only execution (`profile.trusted` AND caller trust), credential scrub, checkout snapshot | yes, unchanged |
| `Core/Publishing` | `IPullRequestProvider` (Azure DevOps), no-`+` push, ls-remote checks | provider reused; Merge has its own publisher because the transfer `Publisher` is bound to `Plan/Run/pair` semantics |
| `Core/Transfer/Simulator` merge mode | real `git merge --no-ff --no-commit` **then resets every non-included path to target** (policy filter) | **not reused** — that is exactly the policy-filtered merge the brief forbids. Only patterns are borrowed (worktree add in mirror, crash recovery of half-created worktrees, lock-file recovery) |
| `Web/WebHost` | loopback + token + Host/Origin/Sec-Fetch-Site + JSON-only POST; `JobService` single gate; `RunExclusiveAsync` shares that gate | yes; Merge endpoints mapped from a separate `MergeEndpoints` class like `AlignmentEndpoints` |
| `Alignment` (Harmony) | product/fork semantics, SyncBase, TakePrimary/TakeFork/DeleteBoth/KeepBoth, manifest union, scratch repo with alternates | **not used** by the engine (brief §3) |
| `ui/harmony/diff-inspector.ts` | unified-diff rendering (`lines()` classifier + `<pre>` loop) welded to alignment decisions | the neutral part (line classification + rendering) is extracted to `ui/src/app/shared/diff/` and Harmony consumes it |
| `ForkBridgeConfig` | YamlDotNet with strict deserialization (unknown keys fail); validation profiles live under `pairs.<pair>.validation` | an additive `merge:` section is required (repository-scoped validation, protected branches, branch prefix) |

## 3. Module boundaries

```
ForkBridge.Core ◄── ForkBridge.Merge (new) ◄── ForkBridge.Web (MergeEndpoints)
       ▲                                         ▲
       └── ForkBridge.Alignment (unchanged) ─────┘
```

`src/ForkBridge.Merge/`

| Folder | Responsibility |
|---|---|
| `Model/` | `MergePlan`, `MergeEntry`, `MergeSession`, `ResolutionRecord`, `DraftRecord`, `ValidationRecord`, `MergeApproval`, `PublicationRecord`, enums |
| `Git/` | `MergeGit`: isolated git environment, pinned merge configuration, `merge-tree -z` parser, unmerged-index reader |
| `Planning/` | `RefCatalog` (refs + provenance + freshness), `MergeAnalyzer` (immutable plan) |
| `Workspace/` | `SessionWorkspace`: worktree create/verify/rebuild, conflict classification, content codec (encoding/EOL/BOM) |
| `Resolution/` | `ResolutionService`: draft/resolve with optimistic concurrency, index-level resolution |
| `Storage/` | `MergeStore`: plans (immutable, hashed), sessions (atomic, revisioned, audit log) |
| `Publishing/` | `MergeFinalizer` (candidate commit), `MergeValidator`, `MergePublisher` (approval + publish) |
| `MergeEngine.cs` | façade used by Web (and later a CLI) |

Core changes are additive only: `ForkBridgeConfig.Merge` (+ schema). No existing behaviour changes.

## 4. Capabilities

Reused: process runner, git client, mirrors/refresh, state store primitives, safe filesystem, validation runner + trust model, user-repo guard, redactor, PR provider, web security middleware and job service.

New:

1. Ref catalogue with explicit namespaces and freshness record per alias.
2. Merge analysis from `git merge-tree --write-tree -z` (machine-readable; exit 0 clean / 1 conflicts / other = invocation failure).
3. Executable session: detached worktree at pinned target, `git merge --no-commit --no-ff <pinned source>`, unmerged entries from `git ls-files -u -z`.
4. Content codec: UTF-8 (± BOM) and UTF-16 LE/BE with BOM; EOL LF/CRLF preserved; mixed EOL / undecodable / oversized → no text editor.
5. Index-level resolution (`hash-object -w --no-filters`, `update-index --index-info`, `checkout-index`, `rm`), never filesystem writes through client paths.
6. Candidate via `commit-tree` (no hooks) with parents `[target, source]`; tree integrity check against the analysis tree.
7. Exact-candidate validation, approval binding, integration-branch publication with idempotent retry.

## 5. Milestones

| # | Scope | Deliverable |
|---|---|---|
| 1 | inspection + plan | this document, baseline |
| 2 | shared inspection primitives | `ui/src/app/shared/diff/*`, Harmony inspector uses it |
| 3 | read-only Merge tab | repository/branch selection, refresh, pinned analysis, summary, file list + filters |
| 4 | first executable slice | session, editable text result, draft, resolve, take source/target, reload/restart recovery, target→result diff |
| 5 | more conflict types | add/add, modify/delete, binary; structural cases blocked with reasons |
| 6 | finalize + validate | candidate tree/ancestry, trusted checks bound to candidate, approval, invalidation |
| 7 | publish | integration branch, optional PR, stale-target refusal, idempotent retry |
| 8 | hardening + docs | concurrency, recovery, path safety, encoding/size, docs, demo |

## 6. Safety invariants

1. **User checkout is read-only.** Merge only runs `git fetch <checkout>` into the mirror (upload-pack) and `git config --get` reads. Asserted by `UserRepoGuard` snapshots in tests (files, index, HEAD, branch, merge state, remote config).
2. **All mutation is in harness storage**: `<state>/mirrors/<alias>.git` (objects, `refs/fb/merge/*`, worktree admin) and `<state>/merge/**`.
3. **Isolated git environment** for every analysis/workspace call: `GIT_CONFIG_NOSYSTEM=1`, `GIT_CONFIG_GLOBAL=<state>/merge/env/gitconfig` (harness file), `core.hooksPath=<empty harness dir>`, `core.fsmonitor=false`, `GIT_LITERAL_PATHSPECS=1`, `GIT_NO_REPLACE_OBJECTS=1`. Consequences: no user/system merge drivers, clean/smudge filters (incl. LFS), hooks or credential helpers can run during analysis or resolution. Repository `merge=`/`filter=` attributes can only name built-in drivers; `filter=` attributes on conflicted paths block editing (content would be the stored, unsmudged form).
4. **Pinned merge configuration** (fingerprinted into plans and approvals): strategy `ort`, `merge.renames=true`, `merge.directoryRenames=false`, `merge.conflictStyle=diff3`, `rerere.enabled=false`, `merge.renormalize=false`, `core.autocrlf=false`, `core.safecrlf=false`, `core.quotePath=false`. Same configuration for `merge-tree` (analysis) and `merge` (session); the session verifies its unmerged path set equals the analysis conflict set, otherwise it is blocked.
5. **Pinned SHAs only.** Analysis and sessions never re-resolve branch names; refs `refs/fb/merge/plans/<id>/{source,target}` and `refs/fb/merge/sessions/<id>/{source,target,candidate}` keep objects reachable.
6. **No client paths.** Requests carry `sessionId` + `entryId` (server-generated hash). Entry paths are validated (relative, no `..`, no `.git` component, no NUL/backslash) and worktree reads go through `SafeFileSystem.AssertNoLinksBetweenRootAndPath`.
7. **Optimistic concurrency**: every mutation needs `expectedSessionRevision`; content saves also `expectedContentHash`. Mutations hold the per-session lock (`StateStore.Lock("merge-session-<id>")`) and, in the web host, the shared `JobService` gate.
8. **Unsupported means blocked**: an entry without a supported resolution blocks finalization; no fake "Mark resolved".
9. **Never a truncated write**: content larger than the edit limit is never sent as editable; saves above the limit are refused.
10. **Candidate integrity**: finalize requires HEAD = target, `MERGE_HEAD` = source, no unmerged entries, no unstaged/untracked changes, every resolution present in the index, and `diff(analysisTree, candidateTree) ⊆ conflictPaths`. Parents are exactly `[target, source]`.
11. **Approval binds** repository identity + remote, plan id + analysis hash, session id + revision, source/target SHAs, candidate SHA, validation record id, options fingerprint, integration branch and PR target. Any change invalidates it.
12. **Publish never force-pushes**: refspec without `+`; existing integration branch accepted only if it already points at the approved candidate (idempotent retry); remote target must still equal the pinned target SHA.
13. **Deletion only of owned directories** via `SafeFileSystem`; worktree removal first verifies the ownership marker.
14. **Validation is not automatic**: runs only for `merge.repositories.<alias>.validation.trusted: true` AND an explicit per-request trust flag.

## 7. Test matrix (real temporary repositories)

| Group | Cases |
|---|---|
| Classification | already merged, identical tips, fast-forward, clean divergent, conflicts, unrelated refused, multiple merge bases |
| Resolution | text edit, add/add, modify/delete (keep / delete), binary source/target, draft vs resolved, rename/delete + file/directory blocked, file mode preserved |
| Content | unicode path, spaces, CRLF, missing final newline, UTF-8 BOM, UTF-16, undecodable refused, oversized not editable, traversal/symlink entry refused |
| Lifecycle | stale revision/content hash, restart with drafts, missing worktree rebuilt by replay, tampered index/worktree detected, unexpected untracked file, abort cleans owned dirs, cancellation during creation recovers |
| Integrity/publication | ordered parents, merge tree, approval invalidated by reopen, validation bound to candidate, target movement refused, source movement reported, retry idempotent, no force push |
| Isolation | user checkout files/index/HEAD/branch/merge-in-progress/remote config unchanged |
| Web | token/JSON security on `/api/merge`, typed errors (409 stale) |
| Frontend | pure logic (filters, labels, diff classification, shortcut guard) via `node --test`; Playwright smoke of navigation → analysis → resolve → reload → finalize against a live fixture |

## 8. Deviations from the brief

| Deviation | Reason |
|---|---|
| Development and verification ran in a cloud Linux workspace; the branch is delivered as a git bundle into the local checkout instead of being created there directly | the local machine's shell lacks .NET and its git (2.34) predates `merge-tree --write-tree`; git writes through that bridge cannot delete lock files and would leave `.lock` files in the user's `.git` |
| Merge requires git ≥ 2.38 (refused otherwise with an explicit message) | `merge-tree --write-tree` is the only machine-readable, worktree-free merge analysis; approximating it would violate "Git is the source of truth" |
| Plain `<textarea>` editor, no editor library | no editor dependency exists; a library adds bundle, worker/CSP and licence cost not needed for whole-file editing. Revisit with per-hunk controls |
| No frontend unit-test framework added; pure UI logic is tested with Node's built-in runner and component behaviour with a Playwright smoke script | avoids adding vitest/karma dependencies; Playwright is not added to `package.json` (script documents how to run it) |
| Fast-forwardable merges produce a `--no-ff` merge commit candidate | consistent, reviewable integration branch; "already merged" is detected separately and creates nothing |
| Validation runs the candidate only (no baseline comparison) | merge validation answers "does the exact candidate pass"; baseline comparison is a follow-up |
| No CLI commands for Merge yet | not required by the brief; the engine façade is UI-agnostic so a CLI is a thin follow-up |
| Pull requests: Azure DevOps only (existing provider) | GitHub provider does not exist in the codebase; branch push works for any host |

## 9. Status at delivery

| # | Milestone | State |
|---|---|---|
| 1 | inspection + plan | done |
| 2 | shared inspection primitives | done: `ui/src/app/shared/diff/{diff-lines,unified-diff}.ts`, `shared/keyboard.ts`; Harmony's inspector renders through them (behaviour unchanged) |
| 3 | read-only Merge tab | done: `/merge` (repository, provenance-grouped refs, refresh/offline refresh, swap, pinned analysis, outcomes, counts, merge bases, findings, filters) |
| 4 | first executable slice | done: `/merge/sessions/:id`, isolated session, edit/draft/resolve, take source/target, reload + restart recovery, final target→result diff |
| 5 | more conflict types | done: add/add, modify/delete (keep/delete), binary; structural cases blocked with reasons |
| 6 | finalize + validate | done: commit-tree candidate `[target, source]`, tree integrity vs. Git's merge, trusted validation on the exact candidate, reopen invalidation |
| 7 | publish | done: integration branch, optional PR (Azure DevOps provider), stale-target refusal, source-movement notice, idempotent retry, no force |
| 8 | hardening + docs | done: concurrency, recovery, path/symlink safety, encoding/size, `docs/merge.md`, demo script, Playwright smoke |

Verification (Linux, .NET SDK 10.0.100, git 2.43, Node 22, PowerShell 7.4):

| Check | Result |
|---|---|
| `dotnet build ForkBridge.slnx` | 0 warnings, 0 errors |
| `dotnet test ForkBridge.slnx` | 201/201 (Core 76, Alignment 72, Merge 53) |
| `ui: npm run build` / `npm run typecheck` / `npm test` | ok / ok / 7/7 |
| `pwsh examples/demo/New-ForkBridgeMergeDemo.ps1` + `ui/e2e/merge-smoke.mjs` against `forkbridge-web` | all 10 steps passed; demo checkout byte-identical before/after; integration branch on origin with parents `[develop, feature/checkout]` |
| Harmony / New plan / History screens | render, no console errors |

Not verified here: Windows host (path/junction behaviour, case-insensitive collision blocking, git for Windows), real Azure DevOps PR creation, the real QiSL repositories.
