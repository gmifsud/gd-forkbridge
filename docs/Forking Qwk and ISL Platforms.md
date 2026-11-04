<img src="https://r2cdn.perplexity.ai/pplx-full-logo-primary-dark%402x.png" style="height:64px;margin-right:32px"/>

# i have 4 repositories 2 backend called Backend and ISL-Backend and two frontend called Frontend and ISL-Frontend. Now both of these two sets of repositories share a a number of libraries or projects which should be common between them. The essentially have the same code files just forked. Then there are modules specific to each set which are line of business modules. These typical import from the same shared libraries or projects. Now we are having issues with keeping these shared libraries in sync between these two repository sets due to something changes happening on ISL-Backend or ISL-Frontend that should happen on the main repos Backend and Frontend. Can you give me a prompt to pass it to an agent to build an application or a harness of sorts that given a branch from either Backend (QiSL) or ISL a fork is created and issues are identified before applying a branch, commit etc. Also take a look at the script i have been using to handle this to an extent.

Yes. What you need is a **synchronization** harness, not just a more sophisticated merge script: something that understands which components are shared, which belong to QiSL or ISL, and whether a proposed transfer remains compatible with the modules that consume those components.

I would build it around this workflow:

```text
Select source branch / commits and target base
                     ↓
Resolve exact commit SHAs
                     ↓
Classify changes: shared / product-specific / configuration / unknown
                     ↓
Identify existing drift and dependency risks
                     ↓
Apply the proposed transfer in an isolated workspace
                     ↓
Build and test affected shared components AND their consumers
                     ↓
Produce a reviewable report and candidate branch
                     ↓
Explicit approval before publishing
```

Here, “fork” means a candidate integration branch/workspace in the receiving repository—not creating another permanent hosted repository.

I reviewed your attached `Fork-Over.ps1`. It provides a useful foundation, but several behaviors need tightening before an agent turns it into a reliable application.[^1_1]

## Assessment of your existing script

### What is worth retaining

Your script already implements several useful mechanisms:

- Bidirectional transfers between Backend/QiSL and ISL-Backend.
- Separate merge and selected-commit transfer modes.
- Resolution of source branches from both origin and local refs.
- Path exclusions and paths pinned to the target’s version.
- Authorship-preserving patches with `Fork-Over-Source` provenance.
- Temporary-worktree trial application in pick-mode dry runs.
- Opt-in pushing rather than automatic publishing.[^1_1]

The important improvement is to preserve those capabilities while replacing the implicit assumptions with explicit policies and reproducible validation.

### What I would change

| Area | Issue in the current script | Requirement for the harness |
| :-- | :-- | :-- |
| Repository coverage | It handles the backend pair only. | Support both backend and frontend pairs, plus coordinated backend/frontend transfers. |
| Shared-code classification | A source-only top-level entry is automatically excluded. A source-only folder could instead be a new shared component; product-specific code can also sit inside a directory present in both repos. | Use explicit component mappings and policies. Treat unclassified paths as requiring review. |
| Workspace isolation | The description says it never touches your current branch, but non-dry-run paths execute `git switch -c` in the target clone. | Perform all candidate work in managed isolated workspaces. Leave developer checkouts unchanged. |
| Validation | A clean application is followed by instructions to build and test manually. | Run configured builds, tests, dependency checks, and consumer validation before declaring readiness. |
| Failure reporting | Pick-mode dry runs report a failed trial application but then finish without a failure-specific exit code. Merge-tree failures also need to be distinguished from “no conflicts.” | Return explicit machine-readable outcomes and nonzero exit codes for blocked or failed runs. |
| Duplicate detection | It skips by source SHA trailer or ancestry. That does not establish that the relevant shared content is present and correct. | Track component/path scope and transfer policy, then combine provenance with content and patch-equivalence checks. |
| Merge commits | Pick mode skips merge commits. Its warning suggests their changes are represented by the non-merge commits, but merge-only conflict-resolution changes need separate handling. | Detect merge commits and block or require an explicit, tested strategy for their resolution changes. |
| Freshness | Failed origin fetches fall back to last-fetched state. | Make stale/offline operation explicit and label the resulting evidence; do not silently present it as current. |

These observations follow from the script’s implementation, not from inspecting your actual repository contents.[^1_1]

### The most important subtlety: ancestry is not shared-content synchronization

Your merge mode merges the full source history and then restores excluded/pinned paths to the target’s version. The resulting merge still records the source commit as a parent. Git’s subsequent merges use the commit graph and merge base, not a record of which paths you deliberately discarded.[^1_2][^1_1]

Consequently:

- A source commit can be present in target ancestry even though some of its content was excluded.
- Later including that component does not automatically recover all previously discarded changes.
- “Already merged” and “shared component synchronized” are different states.

That is particularly relevant to your script’s ancestry-based skipping. I would make filtered patch transfer the harness’s default for shared components, while retaining full-history merge as an explicit advanced mode with warnings and content validation.[^1_2][^1_1]

Patch IDs can help identify equivalent changes despite differing commit SHAs, but they should be supporting evidence—not the only proof that a component is synchronized. Git describes them as reasonably stable identifiers derived from file diffs.[^1_3]

## Prompt to give your coding agent

The following is written to make the agent inspect the real repositories first, then implement a working harness rather than merely wrap the existing PowerShell script in a UI.

```text
You are a senior software architect and developer building a local-first
cross-repository synchronization and validation harness called ForkBridge.

Build a working application, not merely an architecture document or a wrapper
around an existing script.

CONTEXT

We maintain four repositories:

- Backend: the QiSL backend.
- ISL-Backend: the ISL backend.
- Frontend: the QiSL frontend.
- ISL-Frontend: the ISL frontend.

The normal synchronization pairs are:

- Backend <-> ISL-Backend
- Frontend <-> ISL-Frontend

Each pair contains shared libraries/projects whose code was forked and has
subsequently diverged. Each product also has line-of-business modules that
consume the shared components.

We need to transfer changes in either direction without accidentally copying
product-specific modules, overwriting product configuration, or breaking the
receiving product's consumers.

A shared-code transfer that applies cleanly in Git is not necessarily compatible.

Read the supplied Fork-Over.ps1 before designing the implementation. Treat it
as evidence of the existing workflow, not as the definitive specification.

The current script has useful capabilities including local cross-repository
fetching, branch resolution, path filtering, merge mode, format-patch/am
transfers, provenance trailers, and trial application.

The harness must improve its safety and extend it to all four repositories.

A "fork" in this application means an isolated candidate integration branch
and workspace in the target repository, not a new hosted repository.

Do not assume repository paths, project layouts, runtime versions, branch names,
or shared component boundaries beyond what you can verify.

1. DISCOVER THE REPOSITORIES FIRST

Accept configurable paths to all four repositories and the existing script.

Inspect:

- Repository identity, Git version and object format.
- Local branches and remote-tracking branches.
- Origin URLs, without exposing credentials.
- Whether each pair actually shares history.
- Solution/project files and project references.
- Package manifests, workspace definitions, lockfiles and TypeScript aliases.
- Shared libraries and their product-specific consumers.
- Build/test commands already used by the repositories.
- Deployment configuration and generated artifacts.
- Existing divergence and any previous Fork-Over-Source trailers.

Backend and frontend analysis should support .NET and Angular/TypeScript if
confirmed by discovery. Detect actual versions from repository files.

Produce a discovery report before implementing transfer logic.

Suggest shared-component mappings, but label them as proposed until confirmed.
Do not classify a component as shared solely because its name or directory
exists in both repositories.

Do not modify the four application repositories during discovery.

2. APPLICATION SHAPE

Implement:

- A reusable core engine.
- A CLI usable locally and in CI.
- A local web UI using the same engine.
- Persistent run metadata.
- JSON and Markdown reports.

Use a modular monolith for the first implementation, not microservices.

Prefer ASP.NET Core with an Angular/TypeScript UI if that fits the discovered
toolchain. Pin a supported SDK/toolchain explicitly. Explain any alternative.

Git correctness and a working CLI take priority over UI polish.

Use the installed Git CLI behind a typed process abstraction. Pass arguments
as argument arrays, not shell-concatenated strings.

3. EXPLICIT SYNCHRONIZATION POLICY

Create a versioned YAML configuration and schema defining:

- Repository aliases and paths.
- Backend and frontend pairs.
- Default target bases and source bases.
- Shared components.
- Source/target path mappings for each component.
- Product-specific paths.
- Target-pinned configuration paths.
- Generated files and regeneration rules.
- Dependency relationships.
- Validation profiles.
- Transfer directions.
- Allowed transfer modes.

Suggested path classifications:

- shared
- product-specific
- target-pinned
- generated
- unknown

Unknown changed paths must block transfer by default.

Support components at arbitrary directory depths.
Support different component paths between source and target.

Distinguish missing components from intentionally absent components.

Handle additions, modifications, deletions, renames, binary files and changes
to project/package references.

Do not silently exclude a newly introduced shared dependency.

Report both ends of renames that cross policy boundaries.
Block them unless an explicit supported policy resolves the situation.

Configuration placeholders must be visibly marked as placeholders. Never
pretend a suggested mapping was discovered in the repositories.

4. SOURCE SELECTION AND IMMUTABLE PLANS

Support:

- A feature branch relative to a specified source base.
- An explicitly bounded commit range.
- An explicit ordered list of commits.
- A shared-component reconciliation comparison between pinned source and
  target snapshots.

Keep these operations semantically distinct.

For a feature branch, explain the chosen merge base and candidate commits.
Warn if the branch includes merged development history or unrelated work.
Do not automatically choose commits based only on commit-message keywords.

For a development branch, do not invent a feature range. Require an explicit
range, commits, synchronization baseline, or reconciliation mode.

Resolve every selected ref to an exact SHA before planning.

Persist:

- Source and target repository identities.
- Source head and target base SHAs.
- Selection mode and resolved commit list.
- Merge-base information where relevant.
- Component mappings and path decisions.
- Policy/configuration hash.
- Relevant patch/content fingerprints.

Simulation and publication must use the approved immutable plan, not resolve
moving branches again.

If the target base or policy changes, invalidate readiness and require replanning.

5. ISOLATION AND NONDESTRUCTIVE OPERATION

Use harness-owned clones/workspaces for application and validation, with
worktrees where appropriate.

Never change the user's current branch, index, working files, remote settings,
or in-progress Git operation.

Do not use the user's application checkout as the merge/pick workspace.

Analysis may write harness-owned caches and reports but must not mutate user
repositories. Document this distinction clearly.

Do not use shared-object arrangements that break when the source repository
is garbage-collected.

Generate collision-resistant workspace and branch names.

Preserve failed workspaces when requested for investigation.
Support resume, abort and cleanup with clear ownership checks.

Bound filesystem deletion to harness-owned locations. Handle path traversal,
symlinks and platform-specific path behavior safely.

6. TRANSFER ENGINE

Default to selective shared-component patch transfer, preserving authorship
and original source provenance.

Separate commit-preserving transfer from snapshot reconciliation.
A reconciliation operation may produce a new reconciliation commit, but must
not misrepresent it as the original source commits.

Retain full-history merge only as an explicit advanced mode.

If full-history merge is combined with discarded/pinned paths, explain that
ancestry may record excluded content as merged. Validate component content
independently of ancestry.

Support correct source-to-target path mapping.

Treat mixed shared/product-specific commits explicitly:

- Show included changes.
- Show excluded changes.
- Identify dependencies crossing the boundary.
- Block incomplete transfers rather than claiming a complete commit transfer.

For merge commits, detect merge-only resolution changes.
Do not silently drop them.
Require an explicit supported strategy, or block with actionable guidance.

Do not automatically resolve shared-code conflicts with "ours" or "theirs".
Target-pinned policy is the exception and must be reported.

For every conflict, retain source/base/target evidence, file paths and the
failed operation, with instructions to resolve, continue or abort.

7. DRIFT AND IDEMPOTENCY

Analyze existing shared-component drift before applying the selected changes.

Distinguish:

- Changes introduced by this transfer.
- Pre-existing divergence.
- Equivalent changes already present.
- Intentionally preserved product differences.
- Missing prerequisites.
- Previously transferred changes subsequently reverted or edited.

Combine:

- Source provenance.
- Path-scoped patch fingerprints.
- Component content comparisons.
- Prior transfer records.

A matching source SHA trailer or source ancestry alone is not proof that the
selected content is present.

Record transfer scope, mapping/policy hash, included/excluded changes and
target result commits.

A previously partial transfer must not suppress a later transfer of the
previously excluded component.

Use patch equivalence as evidence, not as the sole readiness criterion.
Handle reverts and edited equivalents conservatively.

8. DEPENDENCY AND COMPATIBILITY ANALYSIS

Build a dependency graph from discovered project/package references.

For shared .NET changes, identify affected projects and transitive consumers.
Use compiler/build evidence and supported API comparison tooling where available.

For frontend changes, identify imports, exports, package references, TypeScript
path aliases, Angular consumers and relevant templates.

Report evidence for:

- Missing projects or packages.
- New dependencies on product-specific modules.
- Removed or changed public APIs.
- Changed DTOs or backend/frontend contracts.
- Package/runtime/toolchain version mismatches.
- Missing generated clients or stale generated code.
- Build and test regressions in receiving product modules.

Never claim static analysis proves behavioral compatibility.

AI-assisted explanations may be optional, but deterministic Git, compiler,
dependency and test evidence must drive blocking decisions.

Do not transmit private source code to an external AI provider by default.

9. VALIDATION AND BASELINE COMPARISON

Run a configured baseline validation on the pinned target base and the same
validation on the candidate, using comparable toolchains/environments.

Distinguish:

- Baseline failures.
- Newly introduced failures.
- Resolved failures.
- Unavailable or unexecuted checks.

A candidate cannot be marked ready merely because the baseline also fails.

Validation profiles should include:

- Restore/install.
- Shared-component build.
- Affected consumer builds.
- Relevant unit/integration tests.
- Frontend type checking and production build where configured.
- Linting where configured.
- Generated-code and contract checks.

Use repository-defined commands and declared package-manager versions.
Do not rewrite lockfiles merely to make validation succeed.

Permit timeouts, cancellation, output limits and configurable resource limits.

Run builds only in a trusted context or an appropriate sandbox: repository build
scripts and package lifecycle hooks execute code. Worktrees alone are not a
security sandbox.

Do not automatically connect tests to production services or databases.

Missing credentials or test infrastructure must be reported as unavailable
validation, not as a pass.

10. COORDINATED BACKEND/FRONTEND TRANSFERS

Support a transfer bundle containing backend and frontend plans.

Record the complete source/target SHA matrix.

Identify possible API/client coupling.
Allow validation against the proposed backend/frontend combination.

Publish separate candidate branches/PRs per repository and link them through
a bundle ID.

Do not claim atomic publication or merge across separate repositories.
Report partial publication and recovery steps explicitly.

11. RESULTS AND USER EXPERIENCE

Provide statuses:

- READY: all required checks passed and no blocking findings.
- REVIEW_REQUIRED: explicit human policy decisions are unresolved.
- BLOCKED: conflicts or policy violations prevent the transfer.
- VALIDATION_FAILED: required checks failed or could not be completed.
- NO_CHANGES: no transferable changes remain.

Do not equate "patch applied" with READY.

Every finding must include:

- Stable issue ID.
- Category and severity.
- Affected component, files and commits.
- Concrete evidence.
- Whether it is pre-existing or transfer-introduced.
- Whether it blocks publication.
- Suggested action.

The UI should provide:

- Repository/pair and direction selection.
- Source branch/range/commit selection.
- Exact resolved SHAs.
- Changed-file classification.
- Existing drift.
- Dependency impact.
- Conflict inspection.
- Baseline/candidate validation comparison.
- Candidate diff.
- Approval and publication controls.
- Run history and cleanup controls.

12. PUBLICATION AND SECURITY

Default to no push and no PR creation.

Require explicit approval for publishing.
Bind approval to the exact candidate SHA, target remote, branch name and report.

Never force-push or modify protected branches.

Use local Git credential helpers. Never store access tokens in configuration,
logs or reports.

Treat Azure DevOps as the intended initial hosted integration, while keeping
the core engine provider-independent. Local use must not require hosted access.

Bind the local web service to loopback by default.
Protect state-changing endpoints and do not allow arbitrary browser requests
to execute filesystem or Git operations.

Redact credentials and sensitive command output.

If remote fetch fails, fail by default. Allow an explicit offline mode that
clearly records freshness limitations.

13. AUTOMATED TESTS

Create temporary fixture repositories with real Git histories and test:

- Transfers in both directions and both repository pairs.
- Clean transfers and conflicting edits.
- Mixed shared/product-specific commits.
- New shared components and missing prerequisites.
- Product-specific paths nested under otherwise shared directories.
- Deletions and renames across policy boundaries.
- Differing source/target component paths.
- Binary and non-ASCII files.
- LF/CRLF behavior.
- Merge-only conflict-resolution changes.
- Existing filtered merge ancestry with missing shared content.
- Partial transfer followed by expanded component selection.
- Repeated runs, equivalents, reverts and edited equivalents.
- Branch movement after planning.
- Stale/offline refs.
- Baseline build failures versus new failures.
- Cancellation, crash recovery and cleanup.
- Dirty developer checkouts remaining unchanged.
- Validation scripts attempting unsafe filesystem actions.

Assert preservation of user checkout HEAD, index, worktree and remote config.

Use documented nonzero exit codes for blocked or failed CLI operations.

14. IMPLEMENTATION SEQUENCE

Phase A:
- Repository/script audit.
- Discovery report.
- Policy schema.
- Architecture and threat model.
- End-to-end fixture demonstrating the proposed workflow.

Phase B:
- CLI and core engine.
- Immutable plans.
- Isolated simulation.
- Dependency analysis.
- Baseline/candidate validation.
- Reports and automated tests.

Phase C:
- Local UI.
- Run history.
- Conflict review and approval workflow.

Phase D:
- Azure DevOps publishing adapter.
- Coordinated transfer bundles.
- CI examples.

Complete a working vertical slice early:
one fixture source commit -> classification -> isolated transfer -> consumer
validation -> JSON/Markdown report.

DELIVERABLES

- Working source code.
- Example YAML configuration with explicit placeholders.
- Configuration schema.
- CLI help and usage examples.
- Local UI.
- Git fixture tests.
- Setup and troubleshooting documentation.
- Known limitations.
- An audit explaining how Fork-Over.ps1 behavior was retained, replaced or fixed.

Do not stop at planning or pseudocode.
Do not mutate the real application repositories or publish branches without
approval.
```


## Suggested CLI contract

I would also give the agent this interface as a target. These are proposed commands for the application it will build, not existing commands you can run today:

```powershell
# Discover repository layouts and propose component mappings.
forkbridge discover --config .\forkbridge.yml

# Review existing shared-component differences.
forkbridge drift --pair backend --config .\forkbridge.yml

# Plan an ISL backend feature transfer to QiSL.
forkbridge plan `
  --pair backend `
  --from isl `
  --to qisl `
  --source-ref feature/shared-kernel-fix `
  --source-base development `
  --target-base development `
  --config .\forkbridge.yml

# Plan selected frontend commits in the other direction.
forkbridge plan `
  --pair frontend `
  --from qisl `
  --to isl `
  --commits <sha1> <sha2> `
  --target-base development `
  --config .\forkbridge.yml

# Apply the immutable plan in an isolated workspace and validate it.
forkbridge simulate --plan <plan-id>

# Inspect evidence, findings, candidate diff and validation results.
forkbridge report --run <run-id> --format markdown

# Publish only the reviewed candidate, with explicit approval.
forkbridge publish --run <run-id> --approve

# Remove harness-owned artifacts only.
forkbridge cleanup --run <run-id>
```

The critical acceptance test is not simply “an ISL commit appears on a QiSL branch.” It is: “the intended shared changes reached QiSL, excluded product code stayed excluded, required dependencies came with the change, affected QiSL consumers were validated, and the developer’s original checkout remained untouched.”

That is the boundary your current script does not yet establish: it performs Git transfer and some path protection, but leaves compatibility verification to the operator.[^1_1]

<span style="display:none">[^1_10][^1_11][^1_12][^1_13][^1_14][^1_15][^1_16][^1_17][^1_18][^1_19][^1_20][^1_21][^1_22][^1_23][^1_24][^1_25][^1_4][^1_5][^1_6][^1_7][^1_8][^1_9]</span>

<div align="center">⁂</div>

[^1_1]: Fork-Over.ps1

[^1_2]: https://git-scm.com/docs/git-merge/2.49.0

[^1_3]: https://git-scm.com/docs/git-patch-id

[^1_4]: https://www.kernel.org/pub/software/scm/git/docs/git-format-patch.html

[^1_5]: https://www.kernel.org/pub/software/scm/git/docs/git-merge.html

[^1_6]: https://gitkraken.com/learn/git/git-patch

[^1_7]: https://www.kodeco.com/books/git-apprentice/v1.0/chapters/10-merging

[^1_8]: https://blog.sebastian-daschner.com/entries/git-patches-cli

[^1_9]: https://algomaster.io/learn/git/merge-strategies

[^1_10]: https://git.scripts.mit.edu/?p=git.git;a=history;f=Documentation/git-format-patch.txt;h=9b200b379bbc980f37033c68342a287637750583;hb=af04b1171b9ed16f5acce0a49fe3bda518a40b7e

[^1_11]: https://stackoverflow.com/questions/67640083/why-git-merge-does-not-create-a-common-ancestor

[^1_12]: https://git-memo.readthedocs.io/en/latest/merge.html

[^1_13]: https://dev.to/neuraldownload/git-merge-4cch

[^1_14]: https://www.geeksforgeeks.org/git/patch-operation-in-git/

[^1_15]: https://codecake.ai/blog/different-merge-strategies-in-git/

[^1_16]: https://stackoverflow.com/questions/78848408/does-git-support-applying-patches-patch-files-to-tree-ish

[^1_17]: work.environments.staging_endpoints

[^1_18]: work.devops.repository_hosting

[^1_19]: projects.angular_azure

[^1_20]: https://www.perplexity.ai/search/7dac8ab3-5434-4864-bb69-15c57be77567

[^1_21]: https://www.perplexity.ai/search/e0aba576-5711-418d-82cb-c8bac2610257

[^1_22]: https://www.perplexity.ai/search/0500e171-c223-4229-a2f8-9b7cdf57cd24

[^1_23]: https://www.perplexity.ai/search/2d9e962b-8b5f-4e34-81e0-c599d4b14918

[^1_24]: https://www.perplexity.ai/search/11b8479d-f7b8-477b-943a-2129439b85e5

[^1_25]: https://www.perplexity.ai/search/dd071d12-c9dc-4ac8-94bc-755d59c1aade

