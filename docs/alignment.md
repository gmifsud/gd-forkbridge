# Branch alignment (Harmony)

Bidirectional reconciliation of the **shared scope** of a primary repository and its fork
(QiSL Backend ↔ ISL-Backend, QiSL Frontend ↔ ISL-Frontend), on **any worktree** of either repository.
It is a separate module (`src/ForkBridge.Alignment`) next to the transfer pipeline: `ForkBridge.Core`
is unchanged and the transfer commands, plans and runs are not touched.

Design: ADR-0002 and Addendum A (project docs `analysis/2026-10-27-alignment-*`). Behavioural
reference: `scripts/branch-alignment` (Backend) and `scripts/branch-alignment-frontend` (Frontend).

## Status

| Phase | Scope | State |
|---|---|---|
| 0 | PS suites frozen as reference, golden corpus | done |
| 1 | profiles, worktree catalogue, scratch repo, plan store, CLI | done |
| 2 | planner (classification, merge, fork additions, overrides, reference tags, stale fixpoint, manifest unions), plan.json/tsv/md, parity checker | done - real-repo parity pending a Windows run |
| 3 / 3b | web API + Harmony screen; SyncBase Advisor | done - plus worktree search, fetch / create worktree / fast-forward, selection kept across screens and refresh, loading lock. Apply/Verify/Publish locked until phase 4 |
| 4–6 | apply / verify / undo, publish, restore, cut-over | later |

Planning, review and the SyncBase Advisor are **read-only for every repository**: planning reads objects through
a harness-owned scratch repository (`<stateDir>/<profile>/xrepo.git`, alternates to both object stores) and
writes only under the state directory. The one exception is [worktree maintenance](#worktree-maintenance)
(fetch, add a worktree, fast-forward), which you trigger and confirm explicitly. Applying a plan to a worktree
is phase 4.

## Configuration

`forkbridge.alignment.yml` ([schema](../schema/forkbridge.alignment.schema.json), [example](../examples/forkbridge.alignment.example.yml)).
Repositories are referenced by their `forkbridge.yml` aliases. Real profiles name product paths and branches:
keep them beside the state directory, not in this repository. Lookup order: `--alignment-config`,
`FORKBRIDGE_ALIGNMENT_CONFIG`, then `forkbridge.alignment.yml` next to `forkbridge.yml`.

`rules` keeps each profile at parity with its PS suite:

| Flag | Backend | Frontend | Effect |
|---|---|---|---|
| `manifestPrecedence` | `first` | `afterBehind` | union manifests always vs only when both sides changed |
| `formatGated` | `false` | `true` | fork-vs-primary format-only check limited to `formatInsensitive` extensions |
| `behindDetection` | `false` | `true` | a fork blob equal to an earlier primary version is *behind* (take primary) |

## Commands

```powershell
forkbridge align profiles                                   # validate profiles
forkbridge align worktrees --profile backend                # every worktree of both repos: id, branch, HEAD, scope state
forkbridge align plan --profile backend --preview           # base refs (+ merge refs in memory), no branch needed
forkbridge align plan --profile backend --primary-worktree <id> --fork-worktree <id>
forkbridge align plan --profile backend --primary-rev <ref> --fork-rev <ref> [--sync-base <sha>]
forkbridge align show --profile backend [--plan <id>]
forkbridge align parity --profile backend --golden D:\ForkBridge\state\branch-alignment\plans\<ts>
forkbridge align syncbase --profile backend --primary-rev <sha> --fork-rev <sha> [--json]   # SyncBase Advisor
```

Add `--config <forkbridge.yml>` and `--alignment-config <file>` as needed. Exit codes follow the CLI table:
0 ok, 10 plan has MANUAL rows, 11 parity failed / refused, 2 usage, 3 config, 13 stale/tampered plan.

Plans are immutable (`plan.json` read-only, content-hashed; the hash excludes id and time, so identical
inputs give an identical hash). `plan.tsv` uses the PS layout and vocabulary, so either implementation can
read the other's plans during the transition.

## Requirements

Git **2.40+**. Scoped trees are wrapped in parentless commits inside the scratch repository so that
`merge-tree --write-tree --merge-base` runs without tree-ish merge-base support (the PS suites need 2.49).

## Parity gate (phase 2)

`align parity` replays the inputs recorded in a PS plan's meta line (revisions, SyncBase) with the current
profile and compares `(Path, Category, Decision, Merged)`. Reason texts are not compared. The golden corpus
manifest is `D:\ForkBridge\state\alignment\golden\manifest.json`; golden data stays outside this repository.

## Harmony screen (phase 3)

Start it (absolute config path: `dotnet run` changes the working directory to the project folder):

```powershell
cd D:\Repos\Tools\gd-forkbridge
$env:FORKBRIDGE_ALIGNMENT_CONFIG = "D:\ForkBridge\state\alignment\forkbridge.alignment.yml"
dotnet run --project src\ForkBridge.Web -- --config "$PWD\src\ForkBridge.Web\forkbridge.yml"
```

Open the printed `http://127.0.0.1:5178/#token=…` link → **Harmony** (`/harmony`). The whole app uses one dark
grey / orange palette (tokens in `ui/src/styles.css`).

1. **Select** a profile (Backend / Frontend) and a worktree per side with the search box (every typed word must
   match the branch, path or HEAD). Badges: main checkout, protected (never pushed), scope dirty / clean,
   ahead / behind, plan pinned. Branches on the remote without a worktree are listed in the same search.
2. **Plan** (or *Preview plan* for the profile's base refs) runs as a job and stores an immutable plan.
3. **Review**: the harmony meter (identical today / converge on apply / declared divergence / needs a decision),
   queue facets, a filterable row table with a per-row decision, and a side panel with blob ids, on-apply
   effects and 3-way diffs. Decision edits never touch the plan: *Save as new plan* writes a derived plan
   (`ParentPlanId`, reason suffix `[changed in review: was X]`). *Add override to profile* writes the row's
   decision into the profile's `overrides` (surgical edit, `.bak` kept).

**State.** The selection (profile, worktrees, plan, row, filters) is kept in the URL and the browser session, so
a refresh, the nav link and *Back to Harmony* return to the same review; unsaved decision changes are kept per
plan until *Save as new plan* or *Discard*. Returning from SyncBase is instant (the loaded data is kept in memory
and refreshed quietly in the background). While data loads, a veil shows the current step and the page is
locked (`inert`); long jobs keep a Cancel.

**SyncBase** (`/harmony/<profile>/syncbase?plan=<id>`) runs the Advisor for the plan's two commits: the primary
lane (scope-touching commits, newest first, prefilter score and evidence badges), the fork's bulk imports
matched to the closest primary state, and fully simulated candidates scored
`revert × risks + merge × merges + conflict × conflicts` (profile `advisor` weights, adjustable per run).
*Use for this session* feeds the next plan only (browser session); *Make profile default* rewrites the
profile's `syncBase` line (previous value kept as a comment, `.bak` written). Set `ledger:` on a profile to
add `sync-ledger.json` entries as candidates.

### Worktree maintenance

The only repository writes before phase 4. Each is additive or fast-forward only, and confirmed inline first.

| Action | Where | What it does | Refused (nothing changed) when |
|---|---|---|---|
| **Fetch** | side card | `git fetch --no-tags <remote>`: remote-tracking refs only, no prune; refreshes behind counts and the remote branch list | - |
| **Create worktree** | search box → "On origin · no worktree yet" | `git worktree add` under the side's `worktreeRoot` (`<worktreeRoot>/<branch>`); creates a local branch tracking the remote one, or reuses an existing local branch. Idempotent | name rejected by `git check-ref-format`, branch not on the remote, target folder not empty |
| **Update to origin** | side card | fetches that branch, then `git merge --ff-only` to its upstream | detached/missing worktree, uncommitted tracked changes, local commits not on the remote, main checkout without the extra tick |

`worktreeRoot` defaults to `<repo>.worktrees` when not set. The remote name comes from `forkbridge.yml`.

API: `/api/align/*` - same loopback + token model as the rest of the API. Inputs are profile names, worktree
ids, plan ids, row paths of a plan and hex commit ids; blobs are served only when the plan references them.
