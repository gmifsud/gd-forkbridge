# CLI reference

`forkbridge <command> [options]` — config from `--config`, `FORKBRIDGE_CONFIG`, or `./forkbridge.yml`.
`--json` prints machine-readable output for `discover`, `drift`, `plan`, `simulate`, `resume`, `runs`, `plans`, `approve`, `publish`, `bundle`.

## Discovery and drift (read-only for your repositories)

```powershell
forkbridge discover --config .\forkbridge.yml                       # local refs only
forkbridge discover --fetch --depth 3 --write-config .\draft.yml    # contact remotes; write a PROPOSED-only draft
forkbridge drift --pair backend --config .\forkbridge.yml           # default direction = first two products
forkbridge drift --pair frontend --from isl --to qisl --offline
```

## Planning (immutable)

```powershell
# ISL feature branch -> QiSL
forkbridge plan --pair backend --from isl --to qisl `
  --source-ref feature/shared-kernel-fix --source-base development --target-base development

# Selected frontend commits QiSL -> ISL, applied in this order
forkbridge plan --pair frontend --from qisl --to isl --commits 3e82b4d5cf 6a0e6c91fd --target-base development

# Explicit bounded range
forkbridge plan --pair backend --from qisl --to isl --range development~5..development

# Reconcile shared components between snapshots (one reconciliation commit)
forkbridge plan --pair backend --from qisl --to isl --mode reconcile --components shared-kernel --baseline 1a2b3c4d

# Restrict / extend components
forkbridge plan ... --components shared-kernel platform
forkbridge plan ... --allow-proposed                 # proposed mappings (adds a REVIEW finding)
forkbridge plan ... --reapply 3e82b4d5cf             # provenance says transferred, content missing
forkbridge plan ... --offline                        # no remote contact; recorded staleness (REVIEW)
```

Output lists every resolved SHA, the merge-base explanation, per-commit state and findings; the full
Markdown is written to `state/plans/<id>/plan.md` (`forkbridge report --plan <id>`).

## Simulation

```powershell
forkbridge simulate --plan <plan-id> --trust-validation [--profile default] [--preserve-workspace]
forkbridge resume   --run <run-id> --trust-validation      # after resolving a conflict in the run workspace, or after crash/cancel
forkbridge abort    --run <run-id>                         # drop workspace + harness branch, keep reports
forkbridge report   --run <run-id> --format markdown|json
```

Ctrl+C cancels safely (run marked CANCELLED, `resume` continues).

## Approval and publication

```powershell
forkbridge approve --run <run-id> --candidate <sha-prefix(≥7)> --accept FB-MIXED_COMMIT-8b28cfed
forkbridge publish --run <run-id> [--create-pr]
forkbridge publish --run <run-id> --approve --candidate <sha-prefix> --accept <ids>   # approve + publish in one step
```

Interactive `publish --approve` without `--candidate` asks you to type the first 10 characters of the SHA.
Approver identity: `FORKBRIDGE_APPROVER` or the OS user name.

## Bundles (coordinated backend + frontend)

```powershell
forkbridge bundle create --plans <backend-plan> <frontend-plan> --name audit-dto
forkbridge bundle simulate --bundle <bundle-id> --trust-validation --bundle-profile fullstack
forkbridge bundle publish --bundle <bundle-id> [--create-pr]     # per repository; partial publication reported
forkbridge bundle show --bundle <bundle-id>
```

Combined validation steps receive `FORKBRIDGE_WS_BACKEND` / `FORKBRIDGE_WS_FRONTEND` (candidate workspaces).

## History and cleanup

```powershell
forkbridge runs [--limit 50]
forkbridge plans
forkbridge cleanup --run <run-id>          # ownership-checked; harness artifacts only
forkbridge cleanup --all-finished
forkbridge validate-config
```

## Web UI

```powershell
forkbridge-web --config .\forkbridge.yml [--port 5178]
# open the printed http://127.0.0.1:5178/#token=… (token valid for this process only)
```
