# Architecture and threat model

## 1. Shape

Modular monolith, one engine shared by two front doors.

```
                ┌──────────────── ForkBridge.Core ────────────────────────────────────────────┐
 forkbridge ───►│ Config ─ Policy(Glob, PathClassifier) ─ Planner ─ Simulator ─ Publisher     │
 (CLI, CI)      │   │          │                        │           │           │              │
                │   │      DriftAnalyzer        DependencyGraph/Analyzer   ValidationRunner     │
 forkbridge-web►│   │                                                        UserRepoGuard      │
 (loopback UI)  │ GitClient ── IProcessRunner (argv arrays, timeouts, bounded output, kill tree)│
                │ MirrorManager ── StateStore (plans immutable, runs crash-safe) ── SafeFileSystem│
                └─────────────────────────────────────────────────────────────────────────────┘
```

* **Process layer.** Every external call goes through `IProcessRunner` with `ProcessStartInfo.ArgumentList`
  (no shell, no string concatenation), a timeout, cancellation (kills the process tree) and a bounded
  output buffer that keeps draining so the child never blocks. Git runs with `GIT_TERMINAL_PROMPT=0`,
  `LC_ALL=C` and `GIT_OPTIONAL_LOCKS=0` so read-only commands never refresh a user index.
* **Mirrors, not checkouts.** For each repository alias ForkBridge keeps a bare mirror in
  `state/mirrors/<alias>.git`. It is filled by `git fetch <checkout-path>` (upload-pack on your
  checkout: read-only) and by `git fetch <remote-url>` *into the mirror* (your remote-tracking refs are
  not moved). Objects are copied, never shared via `alternates`, so `git gc` in your repo cannot break
  a plan. The source mirror is fetched into the target mirror under `refs/fb/peer/<alias>/…`.
* **Plans are immutable.** `plan.json` is written once, made read-only and hashed; patch artifacts are
  stored with SHA-256. Loading recomputes both. Simulation and publication use the pinned SHAs and
  artifacts only — they never re-resolve branches. Before simulate/approve/publish the target base is
  re-fetched and the policy hash recomputed; any change → `StaleException` (exit 13).
* **Runs are crash-safe.** `run.json` is replaced atomically after every applied commit. `resume`
  either continues after a manual conflict resolution or resets the harness worktree to the last
  recorded commit (crash/cancel) and continues from `nextCommitIndex`.

## 2. Transfer semantics

| Selection | Semantics | Result commits |
|---|---|---|
| Feature branch | `merge-base(sourceBase, ref)..ref`, explained; refused for base/protected branches | one per source commit, original author/date/message + trailers |
| Range `A..B` | explicit, `A` must be an ancestor of `B` | same |
| Commit list | exact order given | same |
| Reconcile | `diff(baseline, sourceSnapshot)` over selected components, 3-way applied | **one new commit** with `ForkBridge-Reconcile-*` trailers — never `Fork-Over-Source` |
| Merge (advanced) | real merge; directory-rename inference disabled; every path not explicitly included is reset to the target and reported; component content verified independently of ancestry | merge commit + `MERGE_ANCESTRY` review finding |

Per changed path the planner decides **Include / Exclude / Block** using the classification order
*target-pinned → product-specific → generated → ignored → shared component (longest path wins) →
unknown*. Unknown blocks. Renames whose ends fall in different classes block with both ends reported.
Patches are built **per file** (`git diff --binary --full-index`), headers rewritten for the target
layout (git C-quoting rules), and applied with `git apply --3way --index`. Conflicts are never
resolved with ours/theirs; base/target/source blobs are saved as evidence.

Provenance trailers on every transferred commit:

```
Fork-Over-Source: isl@<sha>          (compatible with Fork-Over.ps1)
ForkBridge-Scope: platform           (components actually included)
ForkBridge-Plan / -Run / -Policy
ForkBridge-Partial: N path(s) excluded by policy      (mixed commits only)
ForkBridge-Resolved: manual conflict resolution       (resumed conflicts only)
```

## 3. Evidence model for idempotency

A provenance trailer or ancestry alone is never treated as proof. For each included **file** the
planner checks the mapped patch against the target base with a throw-away index:

| Reverse applies | Forward applies | Provenance (trailer covering scope, or ancestor) | State |
|---|---|---|---|
| yes | no | any | already present → file dropped (all files → `EquivalentPresent`) |
| no | – | yes | `PreviouslyTransferredMissing` → REVIEW (reverted/edited); `--reapply` overrides |
| no | – | no / partial scope | `Pending` |

`git patch-id --stable` matches against target history are recorded as evidence only. A partial
earlier transfer (`ForkBridge-Partial`, narrower scope) never suppresses a later transfer of the
excluded component.

Drift compares blob ids of every mapped file between the pinned snapshots. "Target diverged" is
decided by history (the target last changed the file *after* integrating the baseline), which is
what exposes filtered-merge ancestry: source is an ancestor, yet shared content differs →
`ANCESTRY_CONTENT_MISMATCH` with a reconcile-from-baseline remedy.

## 4. Dependency and compatibility analysis

Built from git objects (nothing executed): csproj `ProjectReference`, `Reference`+`HintPath`
(mapped back to producing projects by assembly name — this is how ISL modules consume Fabric via
`lib/*.dll`), `PackageReference`, `Directory.Packages.props`, `package.json` (deps, `packageManager`,
`engines`), lockfiles, `tsconfig` path aliases, TypeScript imports/exports, Angular selectors.

Blocking (deterministic facts): new reference to a product-specific/unclassified project, referenced
project/assembly producer missing in the target, package without a central version, dependency change
without lockfile update, included change using a type declared in an excluded file of the same commit,
broken project references introduced in the candidate. Warnings/review (evidence, not proof):
removed/changed public C# declarations and TS exports with the target consumers that mention them,
DTO/contract path changes, central package version changes, TFM changes, generated-client staleness.
**Blocking decisions never rely on AI**; no source is sent to any external service.

## 5. Validation

The configured profile runs on a baseline worktree (pinned target base) and on the candidate with the
same toolchain, and results are compared: `OK`, `NEW_FAILURE`, `BASELINE_FAILURE`, `RESOLVED`,
`UNAVAILABLE`. A required check that fails on both, times out, cannot start, lacks `requiresEnv`
infrastructure, or was not trusted makes the run VALIDATION_FAILED — never READY.

## 6. Threat model

| Asset / boundary | Threat | Control |
|---|---|---|
| User checkouts | harness mutates branch/index/worktree/remotes/in-progress ops | only read via fetch/rev-parse/ls-tree with `GIT_OPTIONAL_LOCKS=0`; all work in harness mirrors/worktrees; `UserRepoGuard` snapshots (HEAD, symbolic ref, index bytes, local config, status incl. untracked, stash, refs, MERGE_HEAD…) asserted in tests |
| User checkouts | validation scripts (build targets, npm lifecycle hooks) modify them | execution requires profile `trusted: true` **and** `--trust-validation`; credentials scrubbed from env; checkouts snapshotted before/after validation → `USER_REPO_MODIFIED` (critical, blocking); `runner: docker` for isolation. **Worktrees are not a sandbox.** |
| Filesystem | cleanup deletes outside harness / follows planted links | deletion only via `SafeFileSystem`: inside state root, ownership marker must match, reparse points/symlinks removed as links never traversed, `..` rejected; state root may not contain or be inside a repository |
| Remote | force-push, protected-branch overwrite, publishing an unreviewed candidate | no `+` refspec; existing remote branch refused; protected patterns refused; branch must carry the harness prefix; approval bound to candidate SHA + remote + branch + report hash + policy hash + target base, re-verified at publish |
| Credentials | tokens in config/logs/reports | git credential helpers only; `Redactor` on URLs, headers, key/value secrets and known token shapes for every persisted/logged string; ADO adapter fetches the secret per call via `git credential fill`; tests assert the secret never lands in state files |
| Local web service | CSRF, DNS rebinding, other local users/sites driving git | loopback bind only; per-launch 256-bit token via URL fragment + header; Host must be loopback; cross-site `Origin`/`Sec-Fetch-Site` rejected; POST requires `application/json`; no endpoint accepts paths or git args (ids sanitised); strict CSP |
| Plans | tampering, stale refs | content hash + artifact hashes; offline mode recorded as REVIEW finding; target-base/policy re-check before simulate, approve and publish |
| Concurrency | two operations on one mirror | per-mirror lock files; web jobs serialised |
