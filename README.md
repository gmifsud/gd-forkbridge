# ForkBridge

Local-first cross-repository transfer and validation harness for the four forked repositories:

| Pair | Products |
|---|---|
| `backend` | QiSL **Backend** ↔ **ISL-Backend** |
| `frontend` | QiSL **Frontend** ↔ **ISL-Frontend** |

A transfer that applies cleanly in Git is not necessarily compatible. ForkBridge treats a fork-over
as an evidence-producing pipeline:

```
discover ─► plan (immutable, every ref → SHA) ─► simulate (isolated workspace) ─► report ─► approve ─► publish
             │ path policy per file                 │ 3-way apply, never auto-resolve      │ bound to candidate SHA,
             │ drift + idempotency evidence         │ baseline vs candidate validation     │ remote, branch, report,
             │ dependency/compat analysis           │ conflict evidence + resume/abort     │ policy, target base
```

**Guarantees.** Your checkouts are only ever *read* (no branch, index, working-file, remote-config
or in-progress-operation changes, enforced by tests). All writes go to a harness-owned state
directory. Nothing is pushed without an approval bound to the exact candidate.

## Contents

| Path | What |
|---|---|
| `src/ForkBridge.Core` | Engine: typed git process layer, policy, planner, transfer engine, drift, dependency analysis, validation, reports, publishing |
| `src/ForkBridge.Cli` | `forkbridge` CLI (local + CI, documented exit codes) |
| `src/ForkBridge.Web` | `forkbridge-web` loopback service hosting the Angular UI |
| `ui/` | Angular 21 UI (signals, zoneless); builds into `src/ForkBridge.Web/wwwroot` |
| `tests/` | 56 xUnit tests over real temporary git repositories (+ `FixtureTool` validation stand-in) |
| `schema/forkbridge.schema.json` | Policy schema (YAML language server compatible) |
| `examples/forkbridge.example.yml` | Starting policy for the real repos — VERIFIED / UNVERIFIED / PLACEHOLDER marked |
| `examples/demo/New-ForkBridgeDemo.ps1` | Builds a sandbox of four forked fixture repos to try the full workflow |
| `ci/` | Azure Pipelines and GitHub Actions examples |
| `docs/` | Architecture & threat model, discovery report, Fork-Over.ps1 audit, CLI, troubleshooting, limitations |

## Setup

Requirements (pinned):

| Tool | Version | Why |
|---|---|---|
| .NET SDK | 10.0.1xx+ (`global.json`, rollForward latestFeature) | .NET 10 is the current LTS; .NET 8 support ends Nov 2026 |
| Git | ≥ 2.38 recommended (≥ 2.30 works) | `merge-tree --write-tree` for merge-resolution checks; older Git blocks merge commits instead of guessing |
| Node.js / npm | ≥ 20.19 / npm 10.9 (only to rebuild the UI) | Angular 21.2.23, TypeScript 5.9.3, exact versions in `ui/package.json` |
| PowerShell 7 | for the demo script only | |

```powershell
dotnet build -c Release
dotnet test                                    # 56 fixture tests, ~1.5 min
cd ui; npm ci; npm run build; cd ..            # optional: the built UI is produced into src/ForkBridge.Web/wwwroot
```

Run the CLI with `dotnet src/ForkBridge.Cli/bin/Release/net10.0/forkbridge.dll …` or publish it:
`dotnet publish src/ForkBridge.Cli -c Release -o D:\ForkBridge\bin` and put that folder on `PATH`.

## Quick start (sandbox, recommended first)

```powershell
pwsh examples/demo/New-ForkBridgeDemo.ps1 -Path D:\Temp\fb-demo
cd D:\Temp\fb-demo
forkbridge discover --config .\forkbridge.yml
forkbridge plan --pair backend --from isl --to qisl --source-ref feature/shared-kernel-fix --config .\forkbridge.yml
forkbridge simulate --plan <plan-id> --trust-validation --config .\forkbridge.yml
forkbridge report --run <run-id> --format markdown --config .\forkbridge.yml
forkbridge publish --run <run-id> --approve --candidate <sha> --accept <finding-id> --config .\forkbridge.yml
forkbridge-web --config .\forkbridge.yml        # prints http://127.0.0.1:5178/#token=…
```

The demo's ISL feature branch contains a mixed commit (shared `Describer.cs` + MTL change): ForkBridge
transfers the shared part, excludes the MTL file, marks the commit `ForkBridge-Partial`, and holds the
run at REVIEW_REQUIRED until you accept that decision explicitly.

## Real repositories

1. Copy `examples/forkbridge.example.yml`, set real frontend paths, keep `stateDir` **outside** all repos.
2. `forkbridge discover --write-config draft.yml` — read-only; proposes mappings only with evidence
   (directory existed at the merge-base **and** ≥30 % byte-identical files). Name matches are never proposed.
3. Review, then change verified components to `status: confirmed`. Unknown changed paths block by default.
4. Configure and review validation steps; set `trusted: true` only for commands you accept running.

## Statuses and exit codes

| Status | Meaning | Exit |
|---|---|---|
| READY | applied, no blocking findings, all review items accepted, all required checks passed on the candidate | 0 |
| NO_CHANGES | nothing transferable remains (content already present) | 9 |
| REVIEW_REQUIRED | explicit human decisions outstanding (mixed commits, proposed mappings, manual resolutions, offline refs…) | 10 |
| BLOCKED | conflicts or policy violations | 11 |
| VALIDATION_FAILED | required checks failed, timed out or could not run (a failing baseline never makes a candidate ready) | 12 |
| — | stale plan (target base, policy or artifacts changed) | 13 |
| — | refused by policy / fetch failed / safety refusal / usage / config / internal / cancelled | 14 / 15 / 16 / 2 / 3 / 20 / 130 |

See `docs/cli.md` for every command.
