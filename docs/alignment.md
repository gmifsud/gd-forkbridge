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
| 3 / 3b | web API + Harmony screen; SyncBase Advisor | next |
| 4–6 | apply / verify / undo, publish, restore, cut-over | later |

Phases 1–2 are **read-only for every repository**: planning reads objects through a harness-owned scratch
repository (`<stateDir>/<profile>/xrepo.git`, alternates to both object stores) and writes only under the
state directory.

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
