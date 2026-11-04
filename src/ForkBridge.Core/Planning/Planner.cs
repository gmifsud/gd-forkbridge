using System.Text;
using System.Text.RegularExpressions;
using ForkBridge.Core.Analysis;
using ForkBridge.Core.Config;
using ForkBridge.Core.Git;
using ForkBridge.Core.Model;
using ForkBridge.Core.Policy;
using ForkBridge.Core.Storage;
using ForkBridge.Core.Transfer;

namespace ForkBridge.Core.Planning;

public sealed record PlanRequest
{
    public required string Pair { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }
    public required SelectionMode Mode { get; init; }
    public string? SourceRef { get; init; }
    public string? SourceBase { get; init; }
    public string? TargetBase { get; init; }
    /// <summary>Explicit range "A..B" in source terms (both ends resolved to SHAs).</summary>
    public string? Range { get; init; }
    public List<string> Commits { get; init; } = new();
    public List<string> Components { get; init; } = new();
    public bool AllowProposed { get; init; }
    public bool Offline { get; init; }
    public bool PreferLocal { get; init; }
    /// <summary>Reconcile mode: explicit synchronization baseline (source-side commit).</summary>
    public string? Baseline { get; init; }
    /// <summary>Source commits to re-apply even though provenance says they were transferred.</summary>
    public List<string> Reapply { get; init; } = new();
    /// <summary>Per-file decision overrides (source path -> Include or Exclude).</summary>
    public Dictionary<string, Decision> PathOverrides { get; init; } = new();
    /// <summary>Finding IDs or codes (or "*") whose blocking status is downgraded to review-required.</summary>
    public List<string> OverriddenFindings { get; init; } = new();
}

public sealed record PlanResolutionRequest
{
    public Dictionary<string, Decision>? PathOverrides { get; init; }
    public List<string>? OverrideFindings { get; init; }
    public List<string>? AcceptFindings { get; init; }
    public Decision? DefaultFileDecision { get; init; }
    public bool UnblockAll { get; init; }
    public List<string>? Reapply { get; init; }
}

public sealed class PlanningException(string message, int exitCode = ExitCodes.UsageError) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

public sealed record PriorTransfer(string TargetCommit, string SourceProduct, string SourceSha, List<string> Scope, bool Partial, string Kind);

public sealed class Planner
{
    private readonly ForkBridgeConfig _cfg;
    private readonly GitClient _git;
    private readonly StateStore _store;
    private readonly MirrorManager _mirrors;

    private static readonly string[] PathAndPolicyBlockerCodes =
    [
        "PATH_BLOCKED", "UNKNOWN_PATH", "MODIFY_DELETE", "RENAME_ACROSS_BOUNDARY",
        "PRODUCT_CONTAMINATION", "UNMAPPED_SHARED_PATHS", "TARGET_PINNED_MODIFIED",
        "GENERATED_CHANGED", "MISSING_SHARED_DEPENDENCY",
    ];

    public Planner(ForkBridgeConfig cfg, GitClient git, StateStore store, MirrorManager mirrors)
    {
        _cfg = cfg; _git = git; _store = store; _mirrors = mirrors;
    }

    public async Task<Plan> PlanAsync(PlanRequest req, CancellationToken ct = default)
    {
        if (!_cfg.Pairs.TryGetValue(req.Pair, out var pair)) throw new PlanningException($"Unknown pair '{req.Pair}'.");
        if (!pair.Products.ContainsKey(req.From) || !pair.Products.ContainsKey(req.To) || req.From == req.To)
            throw new PlanningException($"Invalid direction {req.From} -> {req.To} for pair '{req.Pair}' (products: {string.Join(", ", pair.Products.Keys)}).");
        var dir = pair.Directions.FirstOrDefault(d => d.From == req.From && d.To == req.To)
                  ?? throw new PlanningException($"Direction {req.From} -> {req.To} is not enabled for pair '{req.Pair}'.", ExitCodes.PolicyViolation);
        string modeName = req.Mode switch { SelectionMode.Reconcile => "reconcile", SelectionMode.Merge => "merge", _ => "pick" };
        if (!dir.Modes.Contains(modeName))
            throw new PlanningException($"Mode '{modeName}' is not allowed for {req.From} -> {req.To} (allowed: {string.Join(", ", dir.Modes)}).", ExitCodes.PolicyViolation);

        var srcAlias = pair.Products[req.From];
        var tgtAlias = pair.Products[req.To];
        var findings = new List<Finding>();

        // 1. Freshness: sync harness mirrors (user repos are only read).
        var srcSync = await _mirrors.SyncAsync(srcAlias, req.Offline, ct);
        var tgtSync = await _mirrors.SyncAsync(tgtAlias, req.Offline, ct);
        var mirror = tgtSync.MirrorDir;
        await _mirrors.FetchPeerAsync(mirror, srcAlias, srcSync.MirrorDir, ct);
        var freshness = req.Offline ? srcSync.Freshness with { Note = $"{srcSync.Freshness.Note} | target: {tgtSync.Freshness.Note}" } : tgtSync.Freshness;
        if (req.Offline)
            findings.Add(new Finding
            {
                Id = Finding.MakeId("OFFLINE_REFS", req.Pair), Code = "OFFLINE_REFS", Category = "freshness", Severity = Severity.Warning,
                Origin = Origin.Environment, Title = "Planned from possibly stale refs (offline mode)", RequiresReview = true,
                Evidence = [srcSync.Freshness.Note ?? "", tgtSync.Freshness.Note ?? ""],
                SuggestedAction = "Re-plan online before publishing.",
            });

        // 2. Resolve every ref to an exact SHA.
        var targetBaseName = req.TargetBase ?? pair.Defaults.TargetBase;
        var tgtBase = await _mirrors.ResolveAsync(mirror, targetBaseName, req.Offline, req.PreferLocal);
        if (tgtBase.Warning is not null) findings.Add(RefWarning(tgtBase));
        var srcResolve = (string name) => _mirrors.ResolveAsync(mirror, name, req.Offline, req.PreferLocal, srcAlias);

        string sourceHead; string? sourceBaseSha = null; string? mergeBase = null; string? explanation = null; string? rangeExpr = null;
        List<string> commitList;
        string? sourceBaseName = req.SourceBase ?? pair.Defaults.SourceBase;
        switch (req.Mode)
        {
            case SelectionMode.Feature:
            {
                if (string.IsNullOrWhiteSpace(req.SourceRef)) throw new PlanningException("--source-ref is required for a feature selection.");
                var head = await srcResolve(req.SourceRef);
                var baseRef = await srcResolve(sourceBaseName!);
                foreach (var w in new[] { head, baseRef }) if (w.Warning is not null) findings.Add(RefWarning(w));
                if (head.Sha == baseRef.Sha || IsProtectedName(pair, req.SourceRef))
                    throw new PlanningException($"'{req.SourceRef}' is a base/development branch (or equals '{sourceBaseName}'). ForkBridge will not invent a feature range. " +
                        "Use --range A..B, --commits, or --mode reconcile.");
                sourceHead = head.Sha; sourceBaseSha = baseRef.Sha;
                mergeBase = await _git.MergeBaseAsync(mirror, baseRef.Sha, head.Sha)
                            ?? throw new PlanningException($"'{req.SourceRef}' shares no history with '{sourceBaseName}'.");
                rangeExpr = $"{mergeBase}..{head.Sha}";
                commitList = (await _git.LinesAsync(mirror, "rev-list", "--reverse", "--topo-order", rangeExpr)).ToList();
                var behind = (await _git.OutAsync(mirror, "rev-list", "--count", $"{head.Sha}..{baseRef.Sha}")).Trim();
                explanation = $"merge-base({sourceBaseName}@{baseRef.Sha[..10]}, {req.SourceRef}@{head.Sha[..10]}) = {mergeBase[..10]}; " +
                              $"{commitList.Count} commit(s) on the branch since then; the branch is {behind} commit(s) behind {sourceBaseName}.";
                break;
            }
            case SelectionMode.Range:
            {
                var m = Regex.Match(req.Range ?? "", @"^(?<a>[^.\s]+)\.\.(?<b>[^.\s]+)$");
                if (!m.Success) throw new PlanningException("--range must be of the form A..B (exclusive A, inclusive B).");
                var a = await srcResolve(m.Groups["a"].Value);
                var b = await srcResolve(m.Groups["b"].Value);
                if (!await _git.IsAncestorAsync(mirror, a.Sha, b.Sha))
                    throw new PlanningException($"Range start {a.Sha[..10]} is not an ancestor of {b.Sha[..10]}; the range would be unbounded.");
                sourceHead = b.Sha; rangeExpr = $"{a.Sha}..{b.Sha}"; mergeBase = a.Sha;
                commitList = (await _git.LinesAsync(mirror, "rev-list", "--reverse", "--topo-order", rangeExpr)).ToList();
                explanation = $"Explicit range {a.Input}@{a.Sha[..10]}..{b.Input}@{b.Sha[..10]} ({commitList.Count} commit(s)).";
                break;
            }
            case SelectionMode.Commits:
            {
                if (req.Commits.Count == 0) throw new PlanningException("--commits requires at least one SHA.");
                commitList = new();
                foreach (var c in req.Commits)
                {
                    var r = await srcResolve(c);
                    if (commitList.Contains(r.Sha)) throw new PlanningException($"Commit {c} listed twice.");
                    commitList.Add(r.Sha);
                }
                sourceHead = commitList[^1];
                explanation = $"Explicit ordered list of {commitList.Count} commit(s) (applied in the given order).";
                break;
            }
            case SelectionMode.Reconcile:
            case SelectionMode.Merge:
            {
                var head = await srcResolve(req.SourceRef ?? sourceBaseName!);
                if (head.Warning is not null) findings.Add(RefWarning(head));
                sourceHead = head.Sha; commitList = new();
                mergeBase = req.Baseline is not null ? (await srcResolve(req.Baseline)).Sha : await _git.MergeBaseAsync(mirror, head.Sha, tgtBase.Sha);
                if (mergeBase is null) throw new PlanningException("No common ancestor; pass --baseline for reconciliation.");
                explanation = req.Baseline is not null
                    ? $"Reconciliation baseline (explicit) {mergeBase[..10]} -> source snapshot {head.Sha[..10]}."
                    : $"Baseline = merge-base(source {head.Sha[..10]}, target {tgtBase.Sha[..10]}) = {mergeBase[..10]}. If earlier fork-overs merged with " +
                      "discarded paths, this ancestry can hide missing content - see drift findings.";
                break;
            }
            default: throw new PlanningException("Unsupported selection mode.");
        }

        var sourceIdentity = await IdentityAsync(srcAlias, req.From, srcSync, mirror, sourceHead);
        var targetIdentity = await IdentityAsync(tgtAlias, req.To, tgtSync, mirror, tgtBase.Sha);

        return await ExecutePlanCoreAsync(pair, req, mirror, sourceHead, sourceBaseSha,
            req.Mode == SelectionMode.Feature ? sourceBaseName : null,
            tgtBase.Sha, targetBaseName, mergeBase, explanation, rangeExpr, commitList,
            sourceIdentity, targetIdentity, freshness, findings, parentPlanId: null, acceptedFindings: [], ct);
    }

    /// <summary>Creates a new immutable derived plan from <paramref name="planId"/> with file decision
    /// overrides, finding overrides, accepted review items, or updated policy rules from the current
    /// config. The original plan and its patch artifacts are never mutated.</summary>
    public async Task<Plan> ResolvePlanAsync(string planId, PlanResolutionRequest res, CancellationToken ct = default)
    {
        var plan = _store.LoadPlan(planId);
        if (!_cfg.Pairs.TryGetValue(plan.Pair, out var pair))
            throw new PlanningException($"Unknown pair '{plan.Pair}'.");

        var pathOverrides = new Dictionary<string, Decision>(plan.PathOverrides ?? new(), StringComparer.Ordinal);
        if (res.PathOverrides is not null)
        {
            foreach (var (k, v) in res.PathOverrides)
                pathOverrides[k] = v;
        }

        var overriddenFindings = (plan.OverriddenFindings ?? []).ToHashSet(StringComparer.Ordinal);
        if (res.OverrideFindings is not null)
        {
            foreach (var f in res.OverrideFindings.Where(x => !string.IsNullOrWhiteSpace(x)))
                overriddenFindings.Add(f);
        }

        var acceptedFindings = plan.AcceptedFindings.ToHashSet(StringComparer.Ordinal);
        if (res.AcceptFindings is not null)
        {
            foreach (var f in res.AcceptFindings.Where(x => !string.IsNullOrWhiteSpace(x)))
                acceptedFindings.Add(f);
        }

        if (res.UnblockAll)
        {
            foreach (var code in PathAndPolicyBlockerCodes)
                overriddenFindings.Add(code);
            foreach (var f in plan.Findings.Where(f => f.Blocking))
                overriddenFindings.Add(f.Id);
            foreach (var d in plan.Commits.SelectMany(c => c.Paths).Concat(plan.Reconciliation?.Paths ?? []))
            {
                if (d.Decision == Decision.Block && !pathOverrides.ContainsKey(d.SourcePath))
                    pathOverrides[d.SourcePath] = res.DefaultFileDecision ?? Decision.Exclude;
            }
        }

        // If the user overrode a specific path-blocking finding without specifying per-file decisions,
        // apply DefaultFileDecision (defaulting to Exclude) to those files so the commit/reconcile unblocks.
        foreach (var f in plan.Findings.Where(f => f.Blocking && (overriddenFindings.Contains(f.Id) || overriddenFindings.Contains(f.Code) || overriddenFindings.Contains("*"))))
        {
            if (f.Code is "PATH_BLOCKED" or "UNKNOWN_PATH" or "MODIFY_DELETE" or "RENAME_ACROSS_BOUNDARY")
            {
                foreach (var file in f.Files)
                {
                    if (!pathOverrides.ContainsKey(file))
                        pathOverrides[file] = res.DefaultFileDecision ?? Decision.Exclude;
                }
            }
        }

        var tgtAlias = pair.Products[plan.ToProduct];
        var mirror = _store.MirrorDir(tgtAlias);
        if (!Directory.Exists(mirror))
        {
            var srcAlias = pair.Products[plan.FromProduct];
            var srcSync = await _mirrors.SyncAsync(srcAlias, offline: true, ct);
            var tgtSync = await _mirrors.SyncAsync(tgtAlias, offline: true, ct);
            await _mirrors.FetchPeerAsync(tgtSync.MirrorDir, srcAlias, srcSync.MirrorDir, ct);
        }

        var allowProposed = plan.AllowProposed
            || plan.Findings.Any(f => f.Code == "PROPOSED_COMPONENTS")
            || pair.Components.Where(c => plan.SelectedComponents.Contains(c.Id)).Any(c => c.Status == "proposed");

        var req = new PlanRequest
        {
            Pair = plan.Pair, From = plan.FromProduct, To = plan.ToProduct, Mode = plan.Mode,
            SourceRef = plan.SourceRef, SourceBase = plan.SourceBaseRef, TargetBase = plan.TargetBaseRef,
            Components = plan.SelectedComponents, AllowProposed = allowProposed, Offline = plan.Freshness.Offline,
            Baseline = plan.ReconcileBaseline, Reapply = res.Reapply ?? [.. (plan.Reapply ?? [])],
            PathOverrides = pathOverrides, OverriddenFindings = overriddenFindings.ToList(),
        };

        var initialFindings = new List<Finding>();
        if (plan.Freshness.Offline)
        {
            var existingOffline = plan.Findings.FirstOrDefault(f => f.Code == "OFFLINE_REFS");
            if (existingOffline is not null) initialFindings.Add(existingOffline);
        }

        var commitList = plan.Commits.Select(c => c.Sha).ToList();
        var mergeBase = plan.Mode is SelectionMode.Reconcile or SelectionMode.Merge ? (plan.ReconcileBaseline ?? plan.MergeBase) : plan.MergeBase;

        return await ExecutePlanCoreAsync(pair, req, mirror, plan.SourceHeadSha, plan.SourceBaseSha,
            plan.SourceBaseRef, plan.TargetBaseSha, plan.TargetBaseRef, mergeBase, plan.MergeBaseExplanation,
            plan.RangeExpression, commitList, plan.Source, plan.Target, plan.Freshness, initialFindings,
            parentPlanId: plan.Id, acceptedFindings: acceptedFindings.ToList(), ct);
    }

    private async Task<Plan> ExecutePlanCoreAsync(
        PairConfig pair, PlanRequest req, string mirror, string sourceHead, string? sourceBaseSha, string? sourceBaseName,
        string targetBaseSha, string targetBaseName, string? mergeBase, string? explanation, string? rangeExpr,
        List<string> commitList, RepoIdentity sourceIdentity, RepoIdentity targetIdentity, Freshness freshness,
        List<Finding> findings, string? parentPlanId, List<string> acceptedFindings, CancellationToken ct)
    {
        var tgtAlias = pair.Products[req.To];
        var shared = await _git.MergeBaseAsync(mirror, sourceHead, targetBaseSha);
        if (shared is null)
            findings.Add(new Finding
            {
                Id = Finding.MakeId("NO_SHARED_HISTORY", sourceHead, targetBaseSha), Code = "NO_SHARED_HISTORY", Category = "history",
                Severity = Severity.Warning, Origin = Origin.PreExisting, Title = "Source and target share no history",
                Evidence = [$"git merge-base {sourceHead[..10]} {targetBaseSha[..10]} found nothing."],
                SuggestedAction = "Three-way application has less context; expect conflicts.",
            });

        var selected = SelectComponents(pair, req, findings);
        var srcCls = new PathClassifier(pair, req.From, asTarget: false);
        var tgtCls = new PathClassifier(pair, req.To, asTarget: true);
        var tgtTreeFiles = (await _git.LsTreeAsync(mirror, targetBaseSha)).Where(e => e.Type == "blob").Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
        var planId = StateStore.NewId("pl");
        var planDir = _store.PlanDir(planId);
        _store.Fs.CreateOwnedDirectory(planDir, planId);
        var scratch = Path.Combine(planDir, "scratch");
        var patches = new PatchBuilder(_git, mirror);
        var priors = await PriorTransfersAsync(mirror, targetBaseSha);
        var targetPatchIds = await TargetPatchIdsAsync(mirror, shared, targetBaseSha, selected, req.To);
        var gitVersion = await _git.VersionAsync();
        var analyzerChanges = new List<AnalyzedChange>();
        var plannedCommits = new List<PlannedCommit>();
        PlannedCommit? reconciliation = null;

        foreach (var c in selected)
        {
            var tp = c.Paths.GetValueOrDefault(req.To);
            if (string.IsNullOrWhiteSpace(tp)) continue;
            var norm = Glob.Normalize(tp);
            if (!tgtTreeFiles.Any(f => f == norm || f.StartsWith(norm + "/")))
                findings.Add(new Finding
                {
                    Id = Finding.MakeId("COMPONENT_MISSING_IN_TARGET", c.Id, req.To), Code = "COMPONENT_MISSING_IN_TARGET", Category = "prerequisite",
                    Severity = Severity.Warning, Origin = Origin.PreExisting, Component = c.Id, RequiresReview = true,
                    Title = $"Component '{c.Id}' has no files at '{norm}' in the target",
                    Evidence = [$"{tgtAlias}@{targetBaseSha[..10]} contains nothing under {norm}. It is MISSING (not declared intentionally absent)."],
                    SuggestedAction = "If the component should exist in the target, transfer it (new shared component) and accept this finding; otherwise declare it under 'absent'.",
                });
        }

        if (req.Mode is SelectionMode.Reconcile or SelectionMode.Merge)
        {
            var changes = await _git.DiffTreeAsync(mirror, mergeBase!, sourceHead);
            var decisions = changes.Select(ch => Decide(ch, pair, req, selected, srcCls, tgtCls, findings, "reconcile")).ToList();
            decisions = await FollowTargetRenamesAsync(mirror, shared ?? mergeBase!, targetBaseSha, decisions, tgtTreeFiles, req, findings, "reconcile");
            reconciliation = await BuildPlannedCommitAsync(mirror, new CommitInfo("reconcile:" + sourceHead, [mergeBase!], "ForkBridge reconciliation", "", DateTimeOffset.UtcNow.ToString("o"), "", "", "",
                    $"Reconcile {req.From} -> {req.To} snapshot {sourceHead[..10]}"), mergeBase!, sourceHead, decisions, patches, planDir, "reconcile.patch",
                targetBaseSha, scratch, priors, targetPatchIds, req, findings, reconcile: true);
            analyzerChanges.AddRange(decisions.Select(d => new AnalyzedChange(sourceHead, mergeBase!, d)));
            if (req.Mode == SelectionMode.Merge)
                findings.Add(new Finding
                {
                    Id = Finding.MakeId("MERGE_ANCESTRY", sourceHead, targetBaseSha), Code = "MERGE_ANCESTRY", Category = "history", Severity = Severity.Warning,
                    Title = "Full-history merge records excluded content as merged", RequiresReview = true,
                    Evidence = ["Merge mode makes every source commit an ancestor of the target, including product-specific and pinned paths that are discarded.",
                                "Later merges/merge-bases will treat that content as already integrated."],
                    SuggestedAction = "Prefer pick or reconcile. If merging, component content is validated independently after the merge.",
                });
        }
        else
        {
            int idx = 0;
            foreach (var sha in commitList)
            {
                ct.ThrowIfCancellationRequested();
                idx++;
                var info = await _git.GetCommitAsync(mirror, sha);
                if (info.Parents.Length > 2)
                {
                    findings.Add(BlockFinding("OCTOPUS_MERGE", "Octopus merge in selection", sha, [$"{sha[..10]} has {info.Parents.Length} parents."],
                        "Select the constituent commits explicitly."));
                    plannedCommits.Add(Stub(info, CommitState.Blocked, "octopus merge"));
                    continue;
                }
                if (info.IsMerge)
                {
                    plannedCommits.Add(await AnalyzeMergeAsync(mirror, info, selected, req, gitVersion, findings));
                    continue;
                }
                var parent = info.Parents.Length == 0 ? await _mirrors.EmptyTreeAsync(mirror) : info.Parents[0];
                var changes = await _git.DiffTreeAsync(mirror, parent, sha);
                var decisions = changes.Select(ch => Decide(ch, pair, req, selected, srcCls, tgtCls, findings, sha)).ToList();
                if (shared is not null) decisions = await FollowTargetRenamesAsync(mirror, shared, targetBaseSha, decisions, tgtTreeFiles, req, findings, sha);
                var pc = await BuildPlannedCommitAsync(mirror, info, parent, sha, decisions, patches, planDir, $"{idx:D4}-{sha[..12]}.patch",
                    targetBaseSha, scratch, priors, targetPatchIds, req, findings);
                plannedCommits.Add(pc);
                analyzerChanges.AddRange(decisions.Select(d => new AnalyzedChange(sha, parent, d)));
            }
            if (req.Mode == SelectionMode.Feature)
            {
                var unrelated = plannedCommits.Where(p => p.State == CommitState.NothingToTransfer).ToList();
                if (unrelated.Count > 0)
                    findings.Add(new Finding
                    {
                        Id = Finding.MakeId("UNRELATED_COMMITS", string.Join(",", unrelated.Select(u => u.Sha))), Code = "UNRELATED_COMMITS", Category = "selection",
                        Severity = Severity.Info, Title = $"{unrelated.Count} commit(s) on the branch touch no selected shared component",
                        Commits = unrelated.Select(u => u.Sha).ToList(),
                        Evidence = unrelated.Select(u => $"{u.Sha[..10]} {u.Subject}").Take(20).ToList(),
                        SuggestedAction = "Expected for product-only work; confirm the branch does not mix unrelated work.",
                    });
                if (plannedCommits.Any(p => p.IsMerge))
                    findings.Add(new Finding
                    {
                        Id = Finding.MakeId("BRANCH_HAS_MERGES", rangeExpr), Code = "BRANCH_HAS_MERGES", Category = "selection", Severity = Severity.Warning,
                        Title = "The feature branch contains merge commits", Commits = plannedCommits.Where(p => p.IsMerge).Select(p => p.Sha).ToList(),
                        Evidence = ["Merged history may include development work that is not part of this feature; review the candidate commit list."],
                        SuggestedAction = "Prefer an explicit --commits list if the merges brought in unrelated work.",
                    });
            }
        }

        var drift = await DriftAnalyzer.AnalyzeAsync(_git, mirror, pair, req.From, req.To, sourceHead, targetBaseSha, shared ?? mergeBase, selected, req.PathOverrides);
        findings.AddRange(DriftAnalyzer.Findings(drift, sourceHead, targetBaseSha, shared));

        var tgtGraph = await DependencyGraph.BuildAsync(_git, mirror, targetBaseSha);
        var srcGraph = await DependencyGraph.BuildAsync(_git, mirror, sourceHead);
        var pendingShas = plannedCommits.Where(p => p.State == CommitState.Pending).Select(p => p.Sha).ToHashSet();
        var relevant = analyzerChanges.Where(a => pendingShas.Contains(a.SourceCommit) || reconciliation is not null).ToList();
        var analyzer = new DependencyAnalyzer(_git, mirror, pair, req.From, req.To);
        var (depFindings, impact) = await analyzer.AnalyzeAsync(relevant, sourceHead, targetBaseSha, tgtGraph, srcGraph);
        findings.AddRange(depFindings);

        try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); } catch { }

        var deduped = Dedupe(findings);
        ApplyFindingOverrides(deduped, req.OverriddenFindings, plannedCommits);

        var plan = new Plan
        {
            Id = planId, ParentPlanId = parentPlanId, CreatedAt = DateTimeOffset.UtcNow,
            Pair = req.Pair, FromProduct = req.From, ToProduct = req.To,
            Source = sourceIdentity, Target = targetIdentity,
            Mode = req.Mode, SourceRef = req.SourceRef, SourceBaseRef = sourceBaseName,
            TargetBaseRef = targetBaseName, SourceHeadSha = sourceHead, SourceBaseSha = sourceBaseSha, TargetBaseSha = targetBaseSha,
            MergeBase = mergeBase, MergeBaseExplanation = explanation, RangeExpression = rangeExpr,
            SelectedComponents = selected.Select(c => c.Id).ToList(), AllowProposed = req.AllowProposed,
            Reapply = req.Reapply.Count > 0 ? [.. req.Reapply] : null,
            Commits = plannedCommits, Reconciliation = reconciliation,
            ReconcileBaseline = req.Mode is SelectionMode.Reconcile or SelectionMode.Merge ? mergeBase : null,
            Drift = drift, Findings = deduped, PolicyHash = ConfigLoader.PolicyHash(_cfg, req.Pair), Freshness = freshness,
            Impact = impact,
            PathOverrides = req.PathOverrides.Count > 0 ? new Dictionary<string, Decision>(req.PathOverrides) : null,
            OverriddenFindings = req.OverriddenFindings.Count > 0 ? [.. req.OverriddenFindings] : null,
            AcceptedFindings = [.. acceptedFindings],
        };
        plan.PreliminaryStatus = StatusEvaluator.ForPlan(plan);
        _store.SavePlan(plan);
        return plan;
    }

    private static void ApplyFindingOverrides(List<Finding> findings, List<string> overridden, List<PlannedCommit> commits)
    {
        if (overridden.Count == 0) return;
        bool all = overridden.Contains("*");
        var skippedMerges = new List<string>();
        for (int i = 0; i < findings.Count; i++)
        {
            var f = findings[i];
            if (f.Blocking && (all || overridden.Contains(f.Id) || overridden.Contains(f.Code)))
            {
                findings[i] = f with
                {
                    Blocking = false,
                    RequiresReview = true,
                    Severity = Severity.Warning,
                    Title = f.Title.EndsWith("[OVERRIDDEN]", StringComparison.Ordinal) ? f.Title : f.Title + " [OVERRIDDEN]",
                };
            }
        }
        foreach (var c in commits)
        {
            if (c.State == CommitState.Blocked && c.Paths.All(p => p.Decision != Decision.Block))
            {
                var matchingFindingIds = findings.Where(f => f.Commits.Contains(c.Sha)).Select(f => f.Id).ToList();
                if (all || overridden.Contains(c.Sha) || matchingFindingIds.Any(overridden.Contains) ||
                    overridden.Contains("OCTOPUS_MERGE") || overridden.Contains("MERGE_UNVERIFIABLE") || overridden.Contains("MERGE_RESOLUTION_CHANGES"))
                {
                    c.State = CommitState.NothingToTransfer;
                    c.StateEvidence = (c.StateEvidence ?? "") + " (skipped by override)";
                    skippedMerges.Add(c.Sha);
                }
            }
        }
        if (skippedMerges.Count > 0)
        {
            findings.Add(new Finding
            {
                Id = Finding.MakeId("MERGE_COMMIT_SKIPPED", string.Join(",", skippedMerges)),
                Code = "MERGE_COMMIT_SKIPPED", Category = "merge", Severity = Severity.Warning, RequiresReview = true,
                Commits = skippedMerges,
                Title = $"{skippedMerges.Count} blocked merge commit(s) skipped by override",
                Evidence = skippedMerges.Select(s => $"Merge commit {s[..Math.Min(10, s.Length)]} was skipped; only its non-merge constituent commits are transferred.").ToList(),
                SuggestedAction = "Confirm that no conflict-resolution-only edits in the merge commit are needed in the target.",
            });
        }
    }

    // ---------------------------------------------------------------------------------------------
    private static bool IsProtectedName(PairConfig pair, string name)
        => pair.ProtectedBranches.Any(p => Glob.HasWildcard(p) ? Glob.IsMatch(p, name) : p == name);

    private List<ComponentConfig> SelectComponents(PairConfig pair, PlanRequest req, List<Finding> findings)
    {
        var list = new List<ComponentConfig>();
        var requested = req.Components.Count > 0 ? req.Components : pair.Components.Select(c => c.Id).ToList();
        foreach (var id in requested)
        {
            var c = pair.Components.FirstOrDefault(x => x.Id == id) ?? throw new PlanningException($"Unknown component '{id}'.");
            if (c.Status == "placeholder")
            {
                if (req.Components.Count > 0) throw new PlanningException($"Component '{id}' is a placeholder and cannot be transferred.", ExitCodes.PolicyViolation);
                continue;
            }
            if (c.Status == "proposed" && !req.AllowProposed)
            {
                if (req.Components.Count > 0)
                    throw new PlanningException($"Component '{id}' is only PROPOSED. Confirm it in the config (status: confirmed) or pass --allow-proposed.", ExitCodes.PolicyViolation);
                continue;
            }
            list.Add(c);
        }
        if (list.Count == 0)
            throw new PlanningException("No transferable components: none are confirmed (or none matched). Confirm components in the config, or pass --allow-proposed.", ExitCodes.PolicyViolation);
        if (req.AllowProposed && list.Any(c => c.Status == "proposed"))
            findings.Add(new Finding
            {
                Id = Finding.MakeId("PROPOSED_COMPONENTS", string.Join(",", list.Where(c => c.Status == "proposed").Select(c => c.Id))),
                Code = "PROPOSED_COMPONENTS", Category = "policy", Severity = Severity.Warning, Origin = Origin.Policy, RequiresReview = true,
                Title = "Transfer uses components that are only proposed",
                Evidence = list.Where(c => c.Status == "proposed").Select(c => $"{c.Id}: {c.Evidence ?? "no evidence recorded"}").ToList(),
                SuggestedAction = "Confirm the mapping in the config once verified.",
            });
        foreach (var c in list)
            foreach (var dep in c.DependsOn.Where(d => list.All(x => x.Id != d)))
                findings.Add(new Finding
                {
                    Id = Finding.MakeId("DEPENDENCY_NOT_SELECTED", c.Id, dep), Code = "DEPENDENCY_NOT_SELECTED", Category = "prerequisite",
                    Severity = Severity.Info, Component = c.Id, Title = $"'{c.Id}' depends on unselected component '{dep}'",
                    Evidence = [$"Configured dependsOn: {c.Id} -> {dep}."],
                    SuggestedAction = "Changes in the dependency are not transferred; the consumer build verifies compatibility.",
                });
        return list;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, string>> _targetRenames = new();

    /// <summary>The target may have renamed a shared file since the baseline (e.g. ISL renamed
    /// TenantResolutionSource.cs to TenantResolutionSourceEnum.cs). A modification or deletion
    /// addressed to the old path would make the whole `git apply` fail ("does not exist in index"),
    /// so it is re-addressed to the renamed path. A file the target deleted is a modify/delete
    /// conflict and blocks with evidence at plan time instead of failing during simulation.</summary>
    private async Task<List<PathDecision>> FollowTargetRenamesAsync(string mirror, string baseline, string targetBase,
        List<PathDecision> decisions, HashSet<string> targetFiles, PlanRequest req, List<Finding> findings, string commit)
    {
        var key = $"{mirror}|{baseline}|{targetBase}";
        if (!_targetRenames.TryGetValue(key, out var renames))
        {
            renames = new(StringComparer.Ordinal);
            var r = await _git.RunAsync(mirror, ["diff", "--name-status", "-M50%", "--diff-filter=R", "-z", baseline, targetBase]);
            if (r.Success)
            {
                var parts = r.StdOut.Split('\0');
                for (int i = 0; i + 2 < parts.Length; i += 3)
                    if (parts[i].StartsWith('R')) renames[parts[i + 1]] = parts[i + 2];
            }
            _targetRenames[key] = renames;
        }
        var result = new List<PathDecision>(decisions.Count);
        foreach (var d in decisions)
        {
            var addr = d.Status == 'R' ? d.TargetOldPath : d.Status is 'M' or 'D' ? d.TargetPath : null;
            if (d.Decision != Decision.Include || addr is null || targetFiles.Contains(addr)) { result.Add(d); continue; }
            if (renames.TryGetValue(addr, out var renamed) && targetFiles.Contains(renamed))
            {
                result.Add(d.Status == 'R'
                    ? d with { TargetOldPath = renamed, Reason = d.Reason + $"; target renamed {addr} -> {renamed}" }
                    : d with { TargetPath = renamed, Reason = d.Reason + $"; target renamed {addr} -> {renamed}" });
                findings.Add(new Finding
                {
                    Id = Finding.MakeId("TARGET_RENAMED", commit, d.SourcePath), Code = "TARGET_RENAMED", Category = "history",
                    Severity = Severity.Info, Origin = Origin.PreExisting, Component = d.Component, Files = [d.SourcePath, renamed],
                    Title = "Target renamed this file since the baseline; change re-addressed",
                    Evidence = [$"git diff -M {baseline[..10]} {targetBase[..10]}: {addr} -> {renamed}. The source change to {d.SourcePath} is applied to {renamed}."],
                    SuggestedAction = "None; the three-way apply uses the renamed file as the target version.",
                });
                continue;
            }
            if (TryGetPathOverride(req.PathOverrides, d.SourcePath, out var ov, out var ovRule)
                || (d.TargetPath is not null && TryGetPathOverride(req.PathOverrides, d.TargetPath, out ov, out ovRule)))
            {
                if (ov == Decision.Exclude)
                {
                    result.Add(d with { Decision = Decision.Exclude, Reason = $"excluded by override '{ovRule}' (deleted in target)" });
                    continue;
                }
                if (ov == Decision.Include)
                {
                    if (d.Status == 'D')
                    {
                        result.Add(d with { Decision = Decision.Exclude, Reason = "already deleted in target (override)" });
                        continue;
                    }
                    result.Add(d with { Decision = Decision.Include, Status = 'A', Reason = d.Reason + " [restore-in-target]" });
                    findings.Add(new Finding
                    {
                        Id = Finding.MakeId("MODIFY_DELETE_RESTORED", commit, d.SourcePath), Code = "MODIFY_DELETE_RESTORED", Category = "conflict",
                        Severity = Severity.Warning, RequiresReview = true, Component = d.Component, Files = [d.SourcePath],
                        Title = "Target-deleted file restored by override",
                        Evidence = [$"{d.SourcePath} -> {addr} will be re-created in the target."],
                        SuggestedAction = "Verify the restored file compiles cleanly in the target.",
                    });
                    continue;
                }
            }
            result.Add(d with { Decision = Decision.Block, Reason = $"{(d.Status == 'D' ? "deleted" : "modified")} in source, but '{addr}' no longer exists in the target (deleted since the baseline)" });
            findings.Add(new Finding
            {
                Id = Finding.MakeId("MODIFY_DELETE", commit, d.SourcePath), Code = "MODIFY_DELETE", Category = "conflict",
                Severity = Severity.Error, Blocking = true, Component = d.Component, Files = [d.SourcePath],
                Title = "Source changes a file the target has deleted",
                Evidence = [$"{d.SourcePath} is {(d.Status == 'D' ? "deleted" : "modified")} in the source; {addr} is absent at the target base and no rename was detected since {baseline[..10]}."],
                SuggestedAction = "Decide whether the target should regain the file (transfer it as an addition) or the source change is obsolete for the target.",
            });
        }
        return result;
    }

    public static bool TryGetPathOverride(IReadOnlyDictionary<string, Decision>? overrides, string? path, out Decision decision, out string? matchedRule)
    {
        decision = default;
        matchedRule = null;
        if (overrides is null || overrides.Count == 0 || string.IsNullOrWhiteSpace(path)) return false;
        var norm = Glob.Normalize(path);
        if (overrides.TryGetValue(path, out decision)) { matchedRule = path; return true; }
        if (overrides.TryGetValue(norm, out decision)) { matchedRule = norm; return true; }
        var match = overrides
            .Where(kv => Glob.IsMatch(kv.Key, norm))
            .OrderByDescending(kv => Glob.Specificity(kv.Key))
            .FirstOrDefault();
        if (match.Key is not null)
        {
            decision = match.Value;
            matchedRule = match.Key;
            return true;
        }
        return false;
    }

    private static PathDecision Decide(FileChange ch, PairConfig pair, PlanRequest req, List<ComponentConfig> selected,
        PathClassifier srcCls, PathClassifier tgtCls, List<Finding> findings, string commit)
    {
        (Decision d, string reason, string? mapped, string srcClass, string? tgtClass, string? comp) One(string path)
        {
            var c = srcCls.Classify(path);
            switch (c.Class)
            {
                case PathClass.Ignored: return (Decision.Exclude, $"ignored by policy ({c.Rule})", null, c.ToString(), null, null);
                case PathClass.ProductSpecific: return (Decision.Exclude, $"product-specific to '{req.From}' ({c.Rule})", null, c.ToString(), null, null);
                case PathClass.Generated:
                {
                    var rule = pair.Generated.First(g => g.Pattern == c.Rule);
                    // A generated file inside a component that is not part of this transfer is out of scope:
                    // it is excluded like any other path of that component, not a reason to block the plan.
                    if (c.ComponentId is { } gc && selected.All(s => s.Id != gc))
                        return (Decision.Exclude, $"generated ({c.Rule}) in unselected component '{gc}'", null, c.ToString(), null, gc);
                    return rule.Action == "exclude"
                        ? (Decision.Exclude, $"generated ({c.Rule}); regenerate in target{(rule.Regenerate is null ? "" : $" via '{rule.Regenerate}'")}", null, c.ToString(), null, c.ComponentId)
                        : (Decision.Block, $"generated file changed ({c.Rule}); policy action is 'block'", null, c.ToString(), null, c.ComponentId);
                }
                case PathClass.Unknown: return (Decision.Block, "path is not classified by the policy (unknown paths block by default)", null, c.ToString(), null, null);
            }
            var comp = srcCls.ComponentOf(path)!;
            if (comp.Absent.TryGetValue(req.To, out var why))
                return (Decision.Exclude, $"component '{comp.Id}' is intentionally absent in '{req.To}': {why}", null, c.ToString(), null, comp.Id);
            if (selected.All(s => s.Id != comp.Id))
                return (Decision.Exclude, comp.Status == "confirmed" ? $"component '{comp.Id}' not selected" : $"component '{comp.Id}' is {comp.Status} (not transferable without --allow-proposed)", null, c.ToString(), null, comp.Id);
            var mapped = PathClassifier.MapPath(comp, req.From, req.To, path)!;
            var t = tgtCls.Classify(mapped);
            return t.Class switch
            {
                PathClass.TargetPinned => (Decision.Exclude, $"target-pinned in '{req.To}' ({t.Rule}); target version kept", mapped, c.ToString(), t.ToString(), comp.Id),
                PathClass.ProductSpecific => (Decision.Block, $"maps onto '{req.To}' product-specific path ({t.Rule})", mapped, c.ToString(), t.ToString(), comp.Id),
                PathClass.Generated => (Decision.Block, $"maps onto a generated path in '{req.To}' ({t.Rule})", mapped, c.ToString(), t.ToString(), comp.Id),
                PathClass.Shared when t.ComponentId != comp.Id => (Decision.Block, $"maps into a different target component '{t.ComponentId}'", mapped, c.ToString(), t.ToString(), comp.Id),
                PathClass.Ignored => (Decision.Exclude, $"ignored in target ({t.Rule})", mapped, c.ToString(), t.ToString(), comp.Id),
                _ => (Decision.Include, $"shared component '{comp.Id}'", mapped, c.ToString(), t.ToString(), comp.Id),
            };
        }

        var cur = One(ch.Path);
        if (TryGetPathOverride(req.PathOverrides, ch.Path, out var ov, out var ovRule)
            || (ch.OldPath is not null && TryGetPathOverride(req.PathOverrides, ch.OldPath, out ov, out ovRule))
            || (cur.mapped is not null && TryGetPathOverride(req.PathOverrides, cur.mapped, out ov, out ovRule)))
        {
            var oldInfo = ch.OldPath is not null ? One(ch.OldPath) : default;
            if (ov == Decision.Exclude)
            {
                return new PathDecision
                {
                    SourcePath = ch.Path, SourceOldPath = ch.OldPath, TargetPath = cur.mapped, TargetOldPath = oldInfo.mapped, Status = ch.Status,
                    SourceClass = cur.srcClass, TargetClass = cur.tgtClass, Component = cur.comp ?? oldInfo.comp,
                    Decision = Decision.Exclude, Reason = $"excluded by override '{ovRule}' ({cur.reason})", Binary = ch.IsBinary,
                };
            }
            if (ov == Decision.Include)
            {
                var mappedPath = cur.mapped ?? (srcCls.ComponentOf(ch.Path) is { } mc ? PathClassifier.MapPath(mc, req.From, req.To, ch.Path) : null) ?? Glob.Normalize(ch.Path);
                var oldMapped = ch.OldPath is not null
                    ? oldInfo.mapped ?? (srcCls.ComponentOf(ch.OldPath) is { } oc ? PathClassifier.MapPath(oc, req.From, req.To, ch.OldPath) : null) ?? Glob.Normalize(ch.OldPath)
                    : null;
                var compId = cur.comp ?? oldInfo.comp ?? selected.FirstOrDefault()?.Id ?? "override";
                if (cur.d != Decision.Include)
                {
                    findings.Add(new Finding
                    {
                        Id = Finding.MakeId("PATH_OVERRIDE_INCLUDED", commit, ch.Path),
                        Code = "PATH_OVERRIDE_INCLUDED", Category = "policy", Severity = Severity.Warning,
                        RequiresReview = true, Component = compId, Files = [ch.Path],
                        Commits = commit.StartsWith("reconcile") ? [] : [commit],
                        Title = $"File force-included by override (was {cur.d}: {cur.srcClass})",
                        Evidence = [$"{ch.Status} {ch.Path} -> {mappedPath} (original policy decision: {cur.d} - {cur.reason})"],
                        SuggestedAction = "Confirm this file should be transferred despite the policy classification.",
                    });
                }
                return new PathDecision
                {
                    SourcePath = ch.Path, SourceOldPath = ch.OldPath, TargetPath = mappedPath, TargetOldPath = oldMapped, Status = ch.Status,
                    SourceClass = cur.srcClass, TargetClass = cur.tgtClass, Component = compId,
                    Decision = Decision.Include, Reason = $"included by override ({cur.reason})", Binary = ch.IsBinary,
                };
            }
        }

        if (ch.IsRename || ch.IsCopy)
        {
            var old = One(ch.OldPath!);
            bool sameSide = old.d == cur.d && old.comp == cur.comp && old.srcClass == cur.srcClass;
            if (!sameSide && !(old.d == Decision.Exclude && cur.d == Decision.Exclude))
            {
                var dec = new PathDecision
                {
                    SourcePath = ch.Path, SourceOldPath = ch.OldPath, TargetPath = cur.mapped, TargetOldPath = old.mapped, Status = ch.Status,
                    SourceClass = $"{old.srcClass} -> {cur.srcClass}", TargetClass = $"{old.tgtClass} -> {cur.tgtClass}", Component = cur.comp ?? old.comp,
                    Decision = Decision.Block, Binary = ch.IsBinary,
                    Reason = $"{(ch.IsRename ? "rename" : "copy")} crosses a policy boundary: '{ch.OldPath}' [{old.srcClass}: {old.reason}] -> '{ch.Path}' [{cur.srcClass}: {cur.reason}]",
                };
                findings.Add(new Finding
                {
                    Id = Finding.MakeId("RENAME_ACROSS_BOUNDARY", commit, ch.OldPath, ch.Path), Code = "RENAME_ACROSS_BOUNDARY", Category = "policy",
                    Severity = Severity.Error, Blocking = true, Component = dec.Component, Files = [ch.OldPath!, ch.Path],
                    Commits = commit.StartsWith("reconcile") ? [] : [commit], Title = "Rename/copy crosses a policy boundary",
                    Evidence = [dec.Reason], SuggestedAction = "Reclassify one end, or split the move into explicit delete/add commits handled separately.",
                });
                return dec;
            }
            return new PathDecision
            {
                SourcePath = ch.Path, SourceOldPath = ch.OldPath, TargetPath = cur.mapped, TargetOldPath = old.mapped, Status = ch.Status,
                SourceClass = cur.srcClass, TargetClass = cur.tgtClass, Component = cur.comp, Decision = cur.d, Reason = cur.reason, Binary = ch.IsBinary,
            };
        }
        if (cur.d == Decision.Block)
            findings.Add(new Finding
            {
                Id = Finding.MakeId("PATH_BLOCKED", commit, ch.Path, cur.reason), Code = cur.srcClass.StartsWith("Unknown") ? "UNKNOWN_PATH" : "PATH_BLOCKED",
                Category = "policy", Severity = Severity.Error, Blocking = true, Component = cur.comp, Files = [ch.Path],
                Commits = commit.StartsWith("reconcile") ? [] : [commit], Title = cur.srcClass.StartsWith("Unknown") ? "Changed path is not classified" : "Changed path blocked by policy",
                Evidence = [$"{ch.Status} {ch.Path}: {cur.reason}"],
                SuggestedAction = cur.srcClass.StartsWith("Unknown") ? "Classify the path in the config (component, productSpecific, generated or ignored)." : "Adjust the policy or the change.",
            });
        return new PathDecision
        {
            SourcePath = ch.Path, TargetPath = cur.mapped, Status = ch.Status, SourceClass = cur.srcClass, TargetClass = cur.tgtClass,
            Component = cur.comp, Decision = cur.d, Reason = cur.reason, Binary = ch.IsBinary,
        };
    }

    private async Task<PlannedCommit> BuildPlannedCommitAsync(string mirror, CommitInfo info, string parent, string sha, List<PathDecision> decisions,
        PatchBuilder patches, string planDir, string patchFile, string targetBase, string scratch, List<PriorTransfer> priors,
        Dictionary<string, string> targetPatchIds, PlanRequest req, List<Finding> findings, bool reconcile = false)
    {
        var included = decisions.Where(d => d.Decision == Decision.Include).ToList();
        var components = included.Select(d => d.Component!).Distinct().OrderBy(x => x).ToList();
        var pc = new PlannedCommit
        {
            Sha = sha, Subject = info.Subject, Author = info.AuthorName, AuthorEmail = info.AuthorEmail, AuthorDate = info.AuthorDate,
            Paths = decisions, Components = components,
        };
        if (decisions.Any(d => d.Decision == Decision.Block))
        {
            pc.State = CommitState.Blocked;
            pc.StateEvidence = "one or more changed paths are blocked by policy";
            return pc;
        }
        if (included.Count == 0)
        {
            pc.State = CommitState.NothingToTransfer;
            pc.StateEvidence = "no changed path belongs to a selected shared component";
            return pc;
        }

        // Per-file content evidence: files whose change is already present in the target are dropped.
        var keep = new List<(PathDecision d, byte[] patch)>();
        var present = new List<string>();
        foreach (var d in included)
        {
            var fp = await patches.FilePatchAsync(parent, sha, d);
            if (fp.Length == 0) { present.Add(d.TargetPath! + " (no textual change)"); continue; }
            var (revOk, _) = await patches.CheckAgainstTreeAsync(targetBase, fp, reverse: true, scratch);
            var (fwdOk, _) = await patches.CheckAgainstTreeAsync(targetBase, fp, reverse: false, scratch);
            if (revOk && !fwdOk) present.Add(d.TargetPath!);
            else keep.Add((d, fp));
        }
        var allPatch = PatchBuilder.Concat(keep.Select(k => k.patch));
        var fullPatchId = await patches.PatchIdAsync(PatchBuilder.Concat(await Task.WhenAll(included.Select(d => patches.FilePatchAsync(parent, sha, d)))));
        string? equivalentIn = fullPatchId is not null && targetPatchIds.TryGetValue(fullPatchId, out var t) ? t : null;
        var prior = reconcile ? new List<PriorTransfer>() : priors.Where(p => p.SourceSha == sha && p.SourceProduct == req.From).ToList();
        bool isAncestor = !reconcile && await _git.IsAncestorAsync(mirror, sha, targetBase);
        var evidence = new List<string>();
        if (equivalentIn is not null) evidence.Add($"patch-id match with target commit {equivalentIn[..10]}");
        if (prior.Count > 0) evidence.Add("provenance: " + string.Join("; ", prior.Select(p => $"{p.Kind} in {p.TargetCommit[..10]} scope=[{string.Join(",", p.Scope)}]{(p.Partial ? " partial" : "")}")));
        if (isAncestor) evidence.Add("source commit is an ancestor of the target base");
        if (present.Count > 0) evidence.Add($"{present.Count} file change(s) already present: {string.Join(", ", present.Take(10))}");

        bool provenanceCovers = isAncestor || prior.Any(p => !p.Partial || components.All(c => p.Scope.Contains(c)));
        string fileName = patchFile;
        if (keep.Count == 0)
        {
            pc.State = CommitState.EquivalentPresent;
            pc.StateEvidence = "content already present in target: " + string.Join("; ", evidence);
        }
        else if (provenanceCovers && !req.Reapply.Contains(sha) && !req.Reapply.Any(r => sha.StartsWith(r)))
        {
            pc.State = CommitState.PreviouslyTransferredMissing;
            pc.StateEvidence = "provenance says this change reached the target, but its content is not present (reverted or edited later): " + string.Join("; ", evidence);
            findings.Add(new Finding
            {
                Id = Finding.MakeId("TRANSFERRED_BUT_MISSING", sha), Code = "TRANSFERRED_BUT_MISSING", Category = "idempotency",
                Severity = Severity.Warning, Origin = Origin.PreExisting, RequiresReview = true, Commits = reconcile ? [] : [sha],
                Files = keep.Select(k => k.d.TargetPath!).ToList(), Title = "Previously transferred change is not present in the target",
                Evidence = [pc.StateEvidence], SuggestedAction = $"Confirm it was reverted/edited on purpose, or re-plan with --reapply {sha[..Math.Min(10, sha.Length)]}.",
            });
        }
        else
        {
            pc.State = CommitState.Pending;
            pc.StateEvidence = evidence.Count > 0 ? string.Join("; ", evidence) : "not present in target";
            if (present.Count > 0)
                findings.Add(new Finding
                {
                    Id = Finding.MakeId("PARTIALLY_PRESENT", sha), Code = "PARTIALLY_PRESENT", Category = "idempotency", Severity = Severity.Info,
                    Commits = reconcile ? [] : [sha], Files = present, Title = "Part of this change is already present in the target",
                    Evidence = [$"Only the remaining {keep.Count} file change(s) will be applied."],
                });
        }
        var excluded = decisions.Where(d => d.Decision == Decision.Exclude).ToList();
        if (pc.State == CommitState.Pending && excluded.Count > 0 && !reconcile)
            findings.Add(new Finding
            {
                Id = Finding.MakeId("MIXED_COMMIT", sha), Code = "MIXED_COMMIT", Category = "mixed-commit", Severity = Severity.Warning, RequiresReview = true,
                Commits = [sha], Files = excluded.Select(e => e.SourcePath).ToList(),
                Title = "Commit mixes shared and excluded changes - it will be transferred PARTIALLY",
                Evidence = [.. included.Select(i => $"INCLUDED {i.Status} {i.SourcePath} -> {i.TargetPath}"),
                            .. excluded.Select(e => $"EXCLUDED {e.Status} {e.SourcePath}: {e.Reason}")],
                SuggestedAction = "Accept the partial transfer explicitly (the resulting commit is marked ForkBridge-Partial), or split the source commit.",
            });

        if (keep.Count > 0)
        {
            var path = Path.Combine(planDir, fileName);
            await File.WriteAllBytesAsync(path, allPatch);
            File.SetAttributes(path, FileAttributes.ReadOnly);
            return pc with { PatchFile = fileName, PatchSha256 = StateStore.Sha256(allPatch), PatchId = fullPatchId, State = pc.State, StateEvidence = pc.StateEvidence };
        }
        return pc with { PatchId = fullPatchId };
    }

    private async Task<PlannedCommit> AnalyzeMergeAsync(string mirror, CommitInfo info, List<ComponentConfig> selected, PlanRequest req, Version gitVersion, List<Finding> findings)
    {
        var pc = Stub(info, CommitState.NothingToTransfer, "merge commit: its parents' changes are transferred individually") with { IsMerge = true };
        var srcPaths = selected.Select(c => c.Paths.GetValueOrDefault(req.From)).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Glob.Normalize(p!)).ToList();
        if (gitVersion < new Version(2, 38))
        {
            pc.State = CommitState.Blocked;
            pc.StateEvidence = "cannot verify merge resolution (git >= 2.38 required for merge-tree --write-tree)";
            findings.Add(BlockFinding("MERGE_UNVERIFIABLE", "Merge resolution cannot be verified", info.Sha, [pc.StateEvidence], "Upgrade Git, or select commits explicitly without the merge."));
            return pc;
        }
        var mt = await _git.RunAsync(mirror, ["merge-tree", "--write-tree", "--no-messages", info.Parents[0], info.Parents[1]]);
        var autoTree = GitClient.SplitLines(mt.StdOut).FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(autoTree) || (mt.ExitCode != 0 && mt.ExitCode != 1))
        {
            pc.State = CommitState.Blocked;
            pc.StateEvidence = "merge-tree failed: " + mt.StdErr.Trim();
            findings.Add(BlockFinding("MERGE_UNVERIFIABLE", "Merge resolution cannot be verified", info.Sha, [pc.StateEvidence], "Select commits explicitly."));
            return pc;
        }
        var diffs = srcPaths.Count == 0 ? [] : await _git.DiffTreeAsync(mirror, autoTree, info.Sha, srcPaths, detectRenames: false);
        if (diffs.Count > 0)
        {
            pc.State = CommitState.Blocked;
            pc.StateEvidence = $"merge contains manual resolution changes in shared components ({diffs.Count} file(s))";
            findings.Add(new Finding
            {
                Id = Finding.MakeId("MERGE_RESOLUTION_CHANGES", info.Sha), Code = "MERGE_RESOLUTION_CHANGES", Category = "merge", Severity = Severity.Error,
                Blocking = true, Commits = [info.Sha], Files = diffs.Select(d => d.Path).ToList(),
                Title = "Merge commit carries changes that exist only in its conflict resolution",
                Evidence = [$"Automatic merge of {info.Parents[0][..10]} + {info.Parents[1][..10]} differs from the recorded merge {info.Sha[..10]} in: {string.Join(", ", diffs.Select(d => d.Path).Take(15))}"],
                SuggestedAction = "These edits would be silently lost by a commit-by-commit transfer. Use --mode reconcile for the component, or re-create the resolution as an explicit commit in the source.",
            });
        }
        else if (mt.ExitCode == 1)
            pc.StateEvidence = "merge had conflicts outside the selected components only";
        return pc;
    }

    private static PlannedCommit Stub(CommitInfo info, CommitState state, string why) => new()
    {
        Sha = info.Sha, Subject = info.Subject, Author = info.AuthorName, AuthorEmail = info.AuthorEmail, AuthorDate = info.AuthorDate,
        IsMerge = info.IsMerge, State = state, StateEvidence = why,
    };

    /// <summary>Provenance from target history: legacy Fork-Over.ps1 trailers and ForkBridge trailers.</summary>
    public async Task<List<PriorTransfer>> PriorTransfersAsync(string mirror, string targetBase)
    {
        var r = await _git.RunAsync(mirror, ["log", "--format=%H%x1f%B%x1e", "-E", "--grep=^(Fork-Over-Source|ForkBridge-Source|ForkBridge-Reconcile-Source):", targetBase]);
        var list = new List<PriorTransfer>();
        if (!r.Success) return list;
        foreach (var rec in r.StdOut.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = rec.Trim('\n').Split('\x1f');
            if (parts.Length < 2) continue;
            var msg = parts[1];
            var scope = Regex.Match(msg, @"^ForkBridge-Scope:\s*(.+)$", RegexOptions.Multiline) is { Success: true } s
                ? s.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList() : new List<string>();
            bool partial = Regex.IsMatch(msg, @"^ForkBridge-Partial:", RegexOptions.Multiline);
            foreach (Match m in Regex.Matches(msg, @"^(?<k>Fork-Over-Source|ForkBridge-Source):\s*(?<p>[\w.-]+)@(?<sha>[0-9a-f]{7,64})", RegexOptions.Multiline))
                list.Add(new PriorTransfer(parts[0].Trim(), m.Groups["p"].Value, m.Groups["sha"].Value, scope, partial,
                    m.Groups["k"].Value == "Fork-Over-Source" && scope.Count == 0 ? "legacy Fork-Over" : "ForkBridge"));
        }
        return list;
    }

    private async Task<Dictionary<string, string>> TargetPatchIdsAsync(string mirror, string? mergeBase, string targetBase, List<ComponentConfig> selected, string to)
    {
        var map = new Dictionary<string, string>();
        var paths = selected.Select(c => c.Paths.GetValueOrDefault(to)).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Glob.Normalize(p!)).ToList();
        if (mergeBase is null || paths.Count == 0) return map;
        var args = new List<string> { "-c", "core.quotePath=false", "log", "-p", "--binary", "--full-index", "--no-merges", "--max-count=2000", $"{mergeBase}..{targetBase}", "--" };
        args.AddRange(paths);
        var log = await _git.RunAsync(mirror, args, timeout: TimeSpan.FromMinutes(5));
        if (!log.Success || log.StdOutBytes.Length == 0) return map;
        var ids = await _git.RunAsync(mirror, ["patch-id", "--stable"], stdin: log.StdOutBytes);
        foreach (var line in GitClient.SplitLines(ids.StdOut))
        {
            var p = line.Split(' ');
            if (p.Length == 2) map.TryAdd(p[0], p[1]);
        }
        return map;
    }

    private async Task<RepoIdentity> IdentityAsync(string alias, string product, MirrorManager.SyncResult sync, string mirror, string sha)
    {
        var roots = await _git.RunAsync(mirror, ["rev-list", "--max-parents=0", sha]);
        return new RepoIdentity
        {
            Alias = alias, Product = product, Path = _mirrors.UserRepoPath(alias), RemoteUrlRedacted = sync.RemoteUrlRedacted,
            RootCommit = GitClient.SplitLines(roots.StdOut).OrderBy(x => x).FirstOrDefault(), ObjectFormat = sync.ObjectFormat,
        };
    }

    private static Finding RefWarning(MirrorManager.ResolvedRef r) => new()
    {
        Id = Finding.MakeId("REF_DIVERGENCE", r.Input, r.Sha), Code = "REF_DIVERGENCE", Category = "freshness", Severity = Severity.Warning,
        Origin = Origin.Environment, Title = "Branch resolution note", Evidence = [r.Warning!],
        SuggestedAction = "Push or pull so local and remote agree, or pass --prefer-local deliberately.",
    };

    private static Finding BlockFinding(string code, string title, string sha, List<string> evidence, string action) => new()
    {
        Id = Finding.MakeId(code, sha), Code = code, Category = "merge", Severity = Severity.Error, Blocking = true,
        Title = title, Commits = [sha], Evidence = evidence, SuggestedAction = action,
    };

    private static List<Finding> Dedupe(List<Finding> f) => f.GroupBy(x => x.Id).Select(g => g.First()).ToList();

    public async Task<PlanFileDiffResult> GetFileDiffAsync(string planId, string rawPath, string? commitSha = null, CancellationToken ct = default)
    {
        var plan = _store.LoadPlan(planId);
        var pair = _cfg.Pairs.TryGetValue(plan.Pair, out var p) ? p : throw new PlanningException($"Unknown pair '{plan.Pair}'.");
        var srcAlias = pair.Products[plan.FromProduct];
        var tgtAlias = pair.Products[plan.ToProduct];
        var mirror = _store.MirrorDir(tgtAlias);

        var checkHead = await _git.RunAsync(mirror, ["cat-file", "-e", $"{plan.SourceHeadSha}^{{commit}}"], ct: ct);
        if (!checkHead.Success)
        {
            var srcMirror = _store.MirrorDir(srcAlias);
            if (Directory.Exists(srcMirror))
                await _mirrors.FetchPeerAsync(mirror, srcAlias, srcMirror, ct);
        }

        var clean = rawPath.Trim();
        var parenIdx = clean.LastIndexOf(" (", StringComparison.Ordinal);
        if (parenIdx > 0 && clean.EndsWith(')'))
            clean = clean[..parenIdx].Trim();
        var norm = Glob.Normalize(clean);

        var allPlannedCommits = plan.Commits.Concat(plan.Reconciliation is null ? [] : [plan.Reconciliation]).ToList();
        var matchDecision = allPlannedCommits
            .SelectMany(c => c.Paths.Select(d => (commit: c, decision: d)))
            .FirstOrDefault(x =>
                Glob.Normalize(x.decision.SourcePath) == norm ||
                (x.decision.TargetPath is not null && Glob.Normalize(x.decision.TargetPath) == norm) ||
                (x.decision.SourceOldPath is not null && Glob.Normalize(x.decision.SourceOldPath) == norm));

        string srcPath = norm;
        string tgtPath = norm;
        string? component = null;
        string? decision = null;
        string? reason = null;

        if (matchDecision.decision is not null)
        {
            srcPath = Glob.Normalize(matchDecision.decision.SourcePath);
            tgtPath = Glob.Normalize(matchDecision.decision.TargetPath ?? matchDecision.decision.SourcePath);
            component = matchDecision.decision.Component;
            decision = matchDecision.decision.Decision.ToString();
            reason = matchDecision.decision.Reason;
        }
        else
        {
            var srcCls = new PathClassifier(pair, plan.FromProduct, asTarget: false);
            var tgtCls = new PathClassifier(pair, plan.ToProduct, asTarget: true);
            var sc = srcCls.ComponentOf(norm);
            var tc = tgtCls.ComponentOf(norm);
            if (sc is not null)
            {
                srcPath = norm;
                tgtPath = PathClassifier.MapPath(sc, plan.FromProduct, plan.ToProduct, norm) ?? norm;
                component = sc.Id;
            }
            else if (tc is not null)
            {
                tgtPath = norm;
                srcPath = PathClassifier.MapPath(tc, plan.ToProduct, plan.FromProduct, norm) ?? norm;
                component = tc.Id;
            }

            if (TryGetPathOverride(plan.PathOverrides, srcPath, out var ov, out var ovRule) ||
                TryGetPathOverride(plan.PathOverrides, tgtPath, out ov, out ovRule))
            {
                decision = ov.ToString();
                reason = $"override ({ovRule})";
            }
        }

        var baselineSha = plan.ReconcileBaseline ?? plan.MergeBase;
        var srcBlob = await ReadBlobInfoAsync(mirror, plan.SourceHeadSha, srcPath, ct);
        var tgtBlob = await ReadBlobInfoAsync(mirror, plan.TargetBaseSha, tgtPath, ct);
        var baseBlob = baselineSha is not null ? await ReadBlobInfoAsync(mirror, baselineSha, srcPath, ct) : default;

        var targetToSourceDiff = await DiffBlobsAsync(
            mirror,
            tgtBlob.Sha, $"{plan.ToProduct}:{tgtPath}", plan.TargetBaseSha, tgtPath,
            srcBlob.Sha, $"{plan.FromProduct}:{srcPath}", plan.SourceHeadSha, srcPath,
            ct);

        var baselineToTargetDiff = baselineSha is not null
            ? await DiffBlobsAsync(
                mirror,
                baseBlob.Sha, $"baseline:{srcPath}", baselineSha, srcPath,
                tgtBlob.Sha, $"{plan.ToProduct}:{tgtPath}", plan.TargetBaseSha, tgtPath,
                ct)
            : "";

        var baselineToSourceDiff = baselineSha is not null
            ? await DiffBlobsAsync(
                mirror,
                baseBlob.Sha, $"baseline:{srcPath}", baselineSha, srcPath,
                srcBlob.Sha, $"{plan.FromProduct}:{srcPath}", plan.SourceHeadSha, srcPath,
                ct)
            : "";

        var touchingCommits = !string.IsNullOrWhiteSpace(commitSha)
            ? [commitSha]
            : plan.Commits
                .Where(c => c.Paths.Any(p => Glob.Normalize(p.SourcePath) == srcPath || (p.TargetPath is not null && Glob.Normalize(p.TargetPath) == tgtPath)))
                .Select(c => c.Sha)
                .Take(5)
                .ToList();

        var commitDiffSb = new System.Text.StringBuilder();
        foreach (var cSha in touchingCommits)
        {
            var show = await _git.RunAsync(mirror, ["-c", "core.quotePath=false", "show", "--no-color", "-U5", "--format=commit %H%nAuthor: %an <%ae> (%aI)%nSubject: %s%n", cSha, "--", srcPath], ct: ct);
            if (show.Success && !string.IsNullOrWhiteSpace(show.StdOut))
            {
                commitDiffSb.AppendLine(show.StdOut.TrimEnd());
                commitDiffSb.AppendLine();
            }
        }

        var srcLast = (await _git.RunAsync(mirror, ["log", "-1", "--format=%h · %an · %as — %s", plan.SourceHeadSha, "--", srcPath], ct: ct)).StdOut.Trim();
        var tgtLast = (await _git.RunAsync(mirror, ["log", "-1", "--format=%h · %an · %as — %s", plan.TargetBaseSha, "--", tgtPath], ct: ct)).StdOut.Trim();

        return new PlanFileDiffResult
        {
            PlanId = plan.Id,
            RequestedPath = rawPath,
            SourcePath = srcPath,
            TargetPath = tgtPath,
            Component = component,
            Decision = decision,
            Reason = reason,
            ExistsInSource = srcBlob.Exists,
            ExistsInTarget = tgtBlob.Exists,
            ExistsInBaseline = baseBlob.Exists,
            IsBinary = srcBlob.IsBinary || tgtBlob.IsBinary,
            Identical = srcBlob.Exists && tgtBlob.Exists && srcBlob.Sha == tgtBlob.Sha,
            SourceLastCommit = string.IsNullOrWhiteSpace(srcLast) ? null : srcLast,
            TargetLastCommit = string.IsNullOrWhiteSpace(tgtLast) ? null : tgtLast,
            TargetToSourceDiff = targetToSourceDiff,
            BaselineToTargetDiff = baselineToTargetDiff,
            BaselineToSourceDiff = baselineToSourceDiff,
            CommitDiff = commitDiffSb.ToString().TrimEnd(),
            SourceContent = srcBlob.Content,
            TargetContent = tgtBlob.Content,
            BaselineContent = baseBlob.Content,
        };
    }

    private async Task<(bool Exists, string? Sha, string? Content, bool IsBinary)> ReadBlobInfoAsync(string mirror, string treeish, string filePath, CancellationToken ct)
    {
        var rev = await _git.RunAsync(mirror, ["rev-parse", "--verify", "--quiet", $"{treeish}:{filePath}"], ct: ct);
        if (!rev.Success) return (false, null, null, false);
        var sha = rev.StdOut.Trim();
        if (string.IsNullOrEmpty(sha)) return (false, null, null, false);

        var show = await _git.RunAsync(mirror, ["cat-file", "blob", sha], ct: ct);
        if (!show.Success) return (true, sha, null, false);
        var bytes = show.StdOutBytes;
        bool isBinary = bytes.Take(Math.Min(bytes.Length, 8000)).Contains((byte)0);
        if (isBinary) return (true, sha, null, true);

        const int maxBytes = 250_000;
        var text = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, maxBytes));
        if (bytes.Length > maxBytes)
            text += $"\n… (truncated at {maxBytes:N0} bytes; total {bytes.Length:N0} bytes)";
        return (true, sha, text, false);
    }

    private async Task<string> DiffBlobsAsync(
        string mirror,
        string? shaA, string labelA, string treeA, string pathA,
        string? shaB, string labelB, string treeB, string pathB,
        CancellationToken ct)
    {
        if (shaA is null && shaB is null) return "";
        if (shaA is not null && shaB is not null && shaA == shaB) return "";

        const string emptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
        if (shaA is not null && shaB is not null)
        {
            var r = await _git.RunAsync(mirror, ["-c", "core.quotePath=false", "diff", "--no-color", "-U5", shaA, shaB], ct: ct);
            return RewriteBlobDiffHeader(r.StdOut, labelA, labelB);
        }
        if (shaA is null && shaB is not null)
        {
            var r = await _git.RunAsync(mirror, ["-c", "core.quotePath=false", "diff", "--no-color", "-U5", emptyTree, treeB, "--", pathB], ct: ct);
            return r.StdOut;
        }
        else
        {
            var r = await _git.RunAsync(mirror, ["-c", "core.quotePath=false", "diff", "--no-color", "-U5", treeA, emptyTree, "--", pathA], ct: ct);
            return r.StdOut;
        }
    }

    private static string RewriteBlobDiffHeader(string rawDiff, string labelA, string labelB)
    {
        if (string.IsNullOrWhiteSpace(rawDiff)) return "";
        var lines = rawDiff.Replace("\r\n", "\n").Split('\n');
        var sb = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
                sb.AppendLine($"diff --git a/{labelA} b/{labelB}");
            else if (line.StartsWith("--- ", StringComparison.Ordinal))
                sb.AppendLine($"--- a/{labelA}");
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
                sb.AppendLine($"+++ b/{labelB}");
            else
                sb.AppendLine(line);
        }
        return sb.ToString().TrimEnd();
    }
}

public sealed record PlanFileDiffResult
{
    public required string PlanId { get; init; }
    public required string RequestedPath { get; init; }
    public required string SourcePath { get; init; }
    public required string TargetPath { get; init; }
    public string? Component { get; init; }
    public string? Decision { get; init; }
    public string? Reason { get; init; }
    public bool ExistsInSource { get; init; }
    public bool ExistsInTarget { get; init; }
    public bool ExistsInBaseline { get; init; }
    public bool IsBinary { get; init; }
    public bool Identical { get; init; }
    public string? SourceLastCommit { get; init; }
    public string? TargetLastCommit { get; init; }
    public string TargetToSourceDiff { get; init; } = "";
    public string BaselineToTargetDiff { get; init; } = "";
    public string BaselineToSourceDiff { get; init; } = "";
    public string CommitDiff { get; init; } = "";
    public string? SourceContent { get; init; }
    public string? TargetContent { get; init; }
    public string? BaselineContent { get; init; }
}
