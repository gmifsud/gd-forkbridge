# Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `fetch failed` (exit 15) | Remote unreachable or credentials need a prompt (prompts are disabled). Run `git fetch` once in the checkout so GCM caches the credential, or plan with `--offline` (recorded as REVIEW). |
| `stale:` (exit 13) | The target base moved, the policy changed, or a plan artifact was modified. Re-plan. |
| Repository path not found / empty listings | Use `D:\Repos\…` — `C:\Repos` is an NTFS junction some tools cannot traverse. |
| `State directory … must not be inside … repository` | Move `stateDir` outside all four repositories. |
| Every validation step `UNAVAILABLE` | Profile not `trusted: true`, `--trust-validation` not given, `requiresEnv` missing, or executable not on PATH. Unavailable is never a pass. |
| `BASELINE_FAILURE` | The target base already fails that check. Fix the target first; ForkBridge will not mark the candidate READY. |
| `MERGE_UNVERIFIABLE` | Git < 2.38. Upgrade Git, or select non-merge commits explicitly. |
| `UNKNOWN_PATH` | A changed path is not classified. Add it to a component, `productSpecific`, `generated` or `ignored`. |
| `MISSING_PROJECT` / `MISSING_IMPORT_TARGET` | A new shared dependency is not in the target. Include its component (`--components …`) or transfer the prerequisite first. |
| `TRANSFERRED_BUT_MISSING` | Provenance says transferred, content absent (reverted or edited). Confirm intent; `--reapply <sha>` to re-apply. |
| `ANCESTRY_CONTENT_MISMATCH` | Earlier filtered merge. `plan --mode reconcile --baseline <sha before that merge>`. |
| Conflict | Resolve in the **run workspace** printed in the report (never your checkout), remove markers, `forkbridge resume --run <id>`. Evidence: `state/runs/<id>/evidence/`. |
| Web UI says Unauthorized | Open the exact URL printed by `forkbridge-web` (token in the `#` fragment); tokens change per launch. |
| Disk usage | Mirrors are full copies. `forkbridge cleanup --all-finished`; delete `state/mirrors/<alias>.git` to rebuild. |

# Known limitations

* **Validation isolation.** The default `process` runner executes repository build scripts as your
  user. Detection (checkout snapshots) is not prevention. Use `runner: docker` or a disposable VM for
  untrusted branches. Network access during validation is not restricted by the process runner.
* **Behavioural compatibility is not proven.** Static analysis and green builds are evidence only.
  C#/TypeScript surface extraction is regex-based (no Roslyn/tsc API comparison yet); it reports
  removed/changed declarations and name-matching consumers, and the consumer build is the gate.
* **Binary `lib/` coupling.** ISL/QiSL LOB modules consume Fabric through `lib/*.dll`. Validation
  profiles must rebuild producers before consumers; ForkBridge never transfers `lib/`.
* **Discovery heuristics.** Proposed mappings need presence at the merge-base and ≥30 % identical
  files at a configurable depth (default 2). Moved components (different paths) must be mapped by hand.
* **Merge mode** necessarily records excluded content as merged in ancestry; it is advanced/opt-in.
* **Octopus merges** in a selection are blocked.
* **Bundles are not atomic.** Each repository is published separately; partial publication is
  recorded with recovery steps. Combined validation runs in the first candidate workspace with all
  workspace paths in environment variables.
* **Azure DevOps** is the only PR provider implemented; pushing works with any Git host.
* **Patch performance.** Patches are built per file; very large reconciliations (thousands of files)
  are slower but deterministic.
* **Frontend/QiSL facts** in `examples/forkbridge.example.yml` are placeholders/unverified until
  `forkbridge discover` runs against the real repositories.
* Tests were executed on Linux (git 2.43, .NET 10.0.401). Path handling is Windows-aware
  (reparse points, case-insensitive root checks), but run `dotnet test` on your Windows machine once.
