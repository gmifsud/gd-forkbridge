using ForkBridge.Core.Config;
using ForkBridge.Core.Discovery;
using ForkBridge.Core.Git;
using ForkBridge.Core.Infrastructure;
using ForkBridge.Core.Model;
using ForkBridge.Core.Planning;
using ForkBridge.Core.Publishing;
using ForkBridge.Core.Reporting;
using ForkBridge.Core.Storage;
using ForkBridge.Core.Transfer;
using ForkBridge.Core.Validation;

namespace ForkBridge.Core;

/// <summary>Single entry point shared by the CLI and the web UI.</summary>
public sealed class ForkBridgeEngine
{
    private readonly IProcessRunner _runner;
    private readonly IPullRequestProvider? _customPrProvider;

    public ForkBridgeConfig Config { get; private set; } = null!;
    public GitClient Git { get; }
    public StateStore Store { get; }
    public MirrorManager Mirrors { get; private set; } = null!;
    public Planner Planner { get; private set; } = null!;
    public Simulator Simulator { get; private set; } = null!;
    public Publisher Publisher { get; private set; } = null!;
    public DiscoveryService Discovery { get; private set; } = null!;

    public ForkBridgeEngine(ForkBridgeConfig cfg, IProcessRunner? runner = null, string gitExe = "git", IPullRequestProvider? prProvider = null)
    {
        _runner = runner ?? new ProcessRunner();
        _customPrProvider = prProvider;
        Git = new GitClient(_runner, gitExe);
        Store = new StateStore(cfg.StateRoot);
        ApplyConfigInstance(cfg);
    }

    private void ApplyConfigInstance(ForkBridgeConfig cfg)
    {
        foreach (var (alias, repo) in cfg.Repositories.Where(r => !r.Value.Placeholder))
        {
            var p = cfg.ResolvePath(repo.Path);
            if (Store.Fs.IsInsideRoot(p) || IsInside(cfg.StateRoot, p))
                throw new HarnessSafetyException($"State directory '{cfg.StateRoot}' must not be inside or contain repository '{alias}' ({p}).");
        }
        Config = cfg;
        Mirrors = new MirrorManager(Git, Store, cfg);
        Planner = new Planner(cfg, Git, Store, Mirrors);
        Simulator = new Simulator(cfg, Git, Store, Mirrors, new ValidationRunner(_runner));
        var prProvider = _customPrProvider ?? (cfg.Publish.Provider == "azure-devops" ? new AzureDevOpsPullRequestProvider(Git, Store.Root) : null);
        Publisher = new Publisher(cfg, Git, Store, Simulator, prProvider);
        Discovery = new DiscoveryService(cfg, Git, Store, Mirrors);
    }

    public ForkBridgeConfig ReloadConfig()
    {
        if (string.IsNullOrWhiteSpace(Config.SourceFile)) return Config;
        var reloaded = ConfigLoader.Load(Config.SourceFile);
        ApplyConfigInstance(reloaded);
        return Config;
    }

    public ForkBridgeConfig UpdateRawYaml(string yaml)
    {
        var updated = ConfigLoader.SaveRawYaml(yaml, Config.SourceFile);
        ApplyConfigInstance(updated);
        return Config;
    }

    public ForkBridgeConfig ApplyPolicyAction(PolicyActionRequest req)
    {
        if (!Config.Pairs.TryGetValue(req.Pair, out var pair))
            throw new ConfigException($"Unknown pair '{req.Pair}'.");
        var products = string.IsNullOrWhiteSpace(req.Product) || req.Product.Equals("both", StringComparison.OrdinalIgnoreCase)
            ? pair.Products.Keys.ToList()
            : [req.Product];
        var cleanPatterns = req.Patterns.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct(StringComparer.Ordinal).ToList();

        switch (req.Action)
        {
            case "add-ignored":
                foreach (var pat in cleanPatterns)
                    if (!pair.Ignored.Contains(pat, StringComparer.Ordinal)) pair.Ignored.Add(pat);
                break;
            case "remove-ignored":
                pair.Ignored.RemoveAll(p => cleanPatterns.Contains(p, StringComparer.Ordinal));
                break;
            case "add-product-specific":
                foreach (var prod in products)
                {
                    if (!pair.ProductSpecific.TryGetValue(prod, out var list))
                        pair.ProductSpecific[prod] = list = new List<string>();
                    foreach (var pat in cleanPatterns)
                        if (!list.Contains(pat, StringComparer.Ordinal)) list.Add(pat);
                }
                break;
            case "remove-product-specific":
                foreach (var prod in products)
                    if (pair.ProductSpecific.TryGetValue(prod, out var list))
                        list.RemoveAll(p => cleanPatterns.Contains(p, StringComparer.Ordinal));
                break;
            case "add-target-pinned":
                foreach (var prod in products)
                {
                    if (!pair.TargetPinned.TryGetValue(prod, out var list))
                        pair.TargetPinned[prod] = list = new List<string>();
                    foreach (var pat in cleanPatterns)
                        if (!list.Contains(pat, StringComparer.Ordinal)) list.Add(pat);
                }
                break;
            case "remove-target-pinned":
                foreach (var prod in products)
                    if (pair.TargetPinned.TryGetValue(prod, out var list))
                        list.RemoveAll(p => cleanPatterns.Contains(p, StringComparer.Ordinal));
                break;
            case "set-component-status":
                if (req.ComponentId == "*")
                {
                    foreach (var comp in pair.Components.Where(c => c.Status == "proposed"))
                        comp.Status = req.Status ?? "confirmed";
                }
                else
                {
                    var comp = pair.Components.FirstOrDefault(c => c.Id == req.ComponentId)
                               ?? throw new ConfigException($"Unknown component '{req.ComponentId}' in pair '{req.Pair}'.");
                    comp.Status = req.Status ?? "confirmed";
                }
                break;
            case "upsert-component":
                if (string.IsNullOrWhiteSpace(req.ComponentId)) throw new ConfigException("componentId is required.");
                var existing = pair.Components.FirstOrDefault(c => c.Id == req.ComponentId);
                if (existing is null)
                {
                    existing = new ComponentConfig { Id = req.ComponentId, Status = req.Status ?? "confirmed" };
                    pair.Components.Add(existing);
                }
                else if (!string.IsNullOrWhiteSpace(req.Status))
                {
                    existing.Status = req.Status;
                }
                if (req.ComponentPaths is not null)
                {
                    foreach (var (k, v) in req.ComponentPaths) existing.Paths[k] = v;
                }
                break;
            case "set-validation-trusted":
                var profName = string.IsNullOrWhiteSpace(req.Profile) ? pair.Defaults.ValidationProfile : req.Profile;
                if (!pair.Validation.TryGetValue(profName, out var prof))
                    throw new ConfigException($"Unknown validation profile '{profName}' in pair '{req.Pair}'.");
                prof.Trusted = req.Trusted ?? true;
                break;
            case "set-generated-rule":
                var gAction = req.GeneratedAction ?? "exclude";
                foreach (var pat in cleanPatterns)
                {
                    var g = pair.Generated.FirstOrDefault(x => x.Pattern == pat);
                    if (g is null) pair.Generated.Add(new GeneratedRule { Pattern = pat, Action = gAction });
                    else g.Action = gAction;
                }
                break;
            default:
                throw new ConfigException($"Unsupported policy action '{req.Action}'.");
        }

        if (!string.IsNullOrWhiteSpace(Config.SourceFile) && File.Exists(Config.SourceFile) &&
            ConfigLoader.TryApplyPolicyActionInPlace(File.ReadAllText(Config.SourceFile), req, pair, out var updatedYaml))
        {
            var saved = ConfigLoader.SaveRawYaml(updatedYaml, Config.SourceFile);
            ApplyConfigInstance(saved);
            return Config;
        }

        ConfigLoader.Save(Config);
        ApplyConfigInstance(ConfigLoader.Load(Config.SourceFile));
        return Config;
    }

    public static ForkBridgeEngine Load(string configPath, IProcessRunner? runner = null) => new(ConfigLoader.Load(configPath), runner);

    private static bool IsInside(string child, string parent)
    {
        var c = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return c.Equals(p, cmp) || c.StartsWith(p + Path.DirectorySeparatorChar, cmp);
    }

    public async Task<(DiscoveryReport Report, string JsonPath, string MarkdownPath, string? DraftConfigPath)> DiscoverAsync(bool fetch, int depth, string? writeConfig, CancellationToken ct = default)
    {
        var report = await Discovery.DiscoverAsync(fetch, depth, ct);
        var dir = Path.Combine(Store.Root, "reports", "discovery-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);
        var json = Path.Combine(dir, "discovery.json");
        var md = Path.Combine(dir, "discovery.md");
        await File.WriteAllTextAsync(json, System.Text.Json.JsonSerializer.Serialize(report, StateStore.Json), ct);
        await File.WriteAllTextAsync(md, DiscoveryService.Markdown(report), ct);
        string? draft = null;
        if (writeConfig is not null)
        {
            draft = Path.GetFullPath(writeConfig);
            if (File.Exists(draft)) throw new IOException($"Refusing to overwrite existing file {draft}.");
            await File.WriteAllTextAsync(draft, DiscoveryService.DraftConfig(report, Config), ct);
        }
        return (report, json, md, draft);
    }

    /// <summary>Drift of every component of a pair between the default bases (both directions' view).</summary>
    public async Task<List<ComponentDrift>> DriftAsync(string pairName, string from, string to, bool offline, bool includeProposed, CancellationToken ct = default)
    {
        var pair = Config.Pairs[pairName];
        var srcAlias = pair.Products[from]; var tgtAlias = pair.Products[to];
        var s = await Mirrors.SyncAsync(srcAlias, offline, ct);
        var t = await Mirrors.SyncAsync(tgtAlias, offline, ct);
        await Mirrors.FetchPeerAsync(t.MirrorDir, srcAlias, s.MirrorDir, ct);
        var srcHead = await Mirrors.ResolveAsync(t.MirrorDir, pair.Defaults.SourceBase, offline, false, srcAlias);
        var tgtHead = await Mirrors.ResolveAsync(t.MirrorDir, pair.Defaults.TargetBase, offline, false);
        var mb = await Git.MergeBaseAsync(t.MirrorDir, srcHead.Sha, tgtHead.Sha);
        var comps = pair.Components.Where(c => c.Status == "confirmed" || includeProposed && c.Status == "proposed").ToList();
        return await DriftAnalyzer.AnalyzeAsync(Git, t.MirrorDir, pair, from, to, srcHead.Sha, tgtHead.Sha, mb, comps);
    }

    public Run Abort(string runId)
    {
        var run = Store.LoadRun(runId);
        if (run.Publications.Any(p => p.Pushed)) throw new PublishRefusedException("Run was already published; abort only applies to unpublished runs.");
        Simulator.RemoveWorktrees(run);
        var plan = Store.LoadPlan(run.PlanId);
        var mirror = Store.MirrorDir(Config.Pairs[plan.Pair].Products[plan.ToProduct]);
        DeleteRunBranch(mirror, run);
        run.Status = RunStatus.ABORTED;
        run.Conflict = null;
        run.Log.Add("aborted; workspace removed, reports kept");
        Store.SaveRun(run);
        ReportWriter.Write(Store, plan, run);
        return run;
    }

    /// <summary>Removes everything a run owns: worktrees, the harness branch in the mirror and the
    /// run directory. Ownership markers are verified; nothing outside the state root is touched.</summary>
    public void Cleanup(string runId)
    {
        var run = Store.LoadRun(runId);
        var dir = Store.RunDir(runId);
        if (Store.Fs.ReadOwner(dir) != run.Id) throw new HarnessSafetyException($"Run directory {dir} is not owned by run {run.Id}.");
        if (!Path.GetFullPath(run.CandidateWorktree).StartsWith(Path.GetFullPath(dir)))
            throw new HarnessSafetyException("Run metadata points outside its own directory; refusing.");
        var plan = Store.LoadPlan(run.PlanId);
        var mirror = Store.MirrorDir(Config.Pairs[plan.Pair].Products[plan.ToProduct]);
        Simulator.RemoveWorktrees(run);
        DeleteRunBranch(mirror, run);
        Store.Fs.DeleteOwnedDirectory(dir, run.Id);
    }

    private void DeleteRunBranch(string mirror, Run run)
    {
        // Only branches created by this run (prefix + run id) are ever deleted, and only in the mirror.
        if (!run.Branch.EndsWith("/" + run.Id)) throw new HarnessSafetyException($"Branch '{run.Branch}' is not a harness branch of {run.Id}.");
        Git.RunAsync(mirror, ["branch", "-D", run.Branch]).GetAwaiter().GetResult();
    }

    // ---- bundles --------------------------------------------------------------------------------
    public Bundle CreateBundle(IEnumerable<string> planIds, string? name)
    {
        var plans = planIds.Select(Store.LoadPlan).ToList();
        if (plans.Count < 2) throw new PlanningException("A bundle needs at least two plans.");
        if (plans.Select(p => p.Target.Alias).Distinct().Count() != plans.Count)
            throw new PlanningException("Each plan in a bundle must target a different repository.");
        var b = new Bundle { Id = StateStore.NewId("bd"), CreatedAt = DateTimeOffset.UtcNow, Name = name, PlanIds = plans.Select(p => p.Id).ToList() };
        // API/client coupling evidence.
        var contract = plans.Where(p => p.Findings.Any(f => f.Code == "CONTRACT_CHANGED")).ToList();
        var clients = plans.Where(p => p.Commits.Concat(p.Reconciliation is null ? [] : [p.Reconciliation]).SelectMany(c => c.Paths)
            .Any(x => System.Text.RegularExpressions.Regex.IsMatch(x.SourcePath, "(?i)(service-proxies|api-client|nswag|generated)"))).ToList();
        if (contract.Count > 0)
            b.Findings.Add(new Finding
            {
                Id = Finding.MakeId("API_CLIENT_COUPLING", b.Id), Code = "API_CLIENT_COUPLING", Category = "contract", Severity = Severity.Warning,
                RequiresReview = clients.Count == 0, Title = "Backend contract changes travel in this bundle",
                Evidence = [$"Contract-like changes in plan(s): {string.Join(", ", contract.Select(c => c.Id))}",
                            clients.Count > 0 ? $"Generated client changes in plan(s): {string.Join(", ", clients.Select(c => c.Id))}" : "No plan in the bundle changes a generated API client."],
                SuggestedAction = "Validate the combination with the bundle validation profile; publication is per repository and NOT atomic.",
            });
        Store.SaveBundle(b);
        return b;
    }

    public async Task<Bundle> SimulateBundleAsync(string bundleId, SimulateOptions opt, string? validationProfile, CancellationToken ct = default)
    {
        var b = Store.LoadBundle(bundleId);
        var env = new Dictionary<string, string>();
        var runs = new List<Run>();
        foreach (var pid in b.PlanIds)
        {
            Run run;
            try { run = await Simulator.SimulateAsync(pid, opt with { BundleId = b.Id, PreserveWorkspace = true }, ct); }
            catch (PlanningException ex) when (ex.ExitCode == ExitCodes.NoChanges) { b.Findings.Add(new Finding { Id = Finding.MakeId("BUNDLE_PLAN_NO_CHANGES", pid), Code = "BUNDLE_PLAN_NO_CHANGES", Category = "bundle", Severity = Severity.Info, Title = $"Plan {pid} has no changes" }); continue; }
            b.RunIds[pid] = run.Id;
            runs.Add(run);
            var plan = Store.LoadPlan(pid);
            env[$"FORKBRIDGE_WS_{plan.Pair.ToUpperInvariant().Replace('-', '_')}"] = run.CandidateWorktree;
        }
        if (validationProfile is not null && Config.Bundles.TryGetValue(validationProfile, out var bv) && runs.Count > 0)
        {
            var vr = new ValidationRunner(new ProcessRunner());
            b.CombinedValidation = await vr.RunAsync(bv.Validation, runs[0].CandidateWorktree, "bundle", opt.TrustValidation, [], env, ct);
        }
        var statuses = runs.Select(r => r.Status).ToList();
        b.Status = statuses.Count == 0 ? RunStatus.NO_CHANGES
            : statuses.Contains(RunStatus.BLOCKED) ? RunStatus.BLOCKED
            : statuses.Contains(RunStatus.VALIDATION_FAILED) || b.CombinedValidation.Any(v => v.Required && v.Outcome != "PASSED" && v.Outcome != "SKIPPED") ? RunStatus.VALIDATION_FAILED
            : statuses.Contains(RunStatus.REVIEW_REQUIRED) || b.Findings.Any(f => f.RequiresReview) ? RunStatus.REVIEW_REQUIRED
            : RunStatus.READY;
        Store.SaveBundle(b);
        return b;
    }

    /// <summary>Publishes each approved run separately. Not atomic: partial publication is recorded
    /// with recovery steps.</summary>
    public async Task<Bundle> PublishBundleAsync(string bundleId, bool createPr, CancellationToken ct = default)
    {
        var b = Store.LoadBundle(bundleId);
        var published = new List<string>();
        foreach (var (pid, rid) in b.RunIds)
        {
            if (Store.LoadRun(rid).Publications.Any(p => p.Pushed)) { published.Add(rid); continue; }
            try
            {
                var rec = await Publisher.PublishAsync(rid, createPr, ct);
                published.Add(rid);
                b.PublicationLog.Add($"{DateTimeOffset.UtcNow:u} {rid}: pushed {rec.Branch}{(rec.PullRequestUrl is null ? "" : " PR " + rec.PullRequestUrl)}");
            }
            catch (Exception ex)
            {
                b.PublicationLog.Add($"{DateTimeOffset.UtcNow:u} {rid}: FAILED {Redactor.Redact(ex.Message)}");
                b.PublicationLog.Add($"PARTIAL PUBLICATION: {published.Count}/{b.RunIds.Count} published ({string.Join(", ", published)}). " +
                    "Recovery: fix the cause and re-run `forkbridge bundle publish` (already-published runs are skipped), " +
                    "or close the already-created PRs / delete their branches on the remote. Repositories are independent; nothing was rolled back automatically.");
                Store.SaveBundle(b);
                throw new PublishRefusedException(string.Join("\n", b.PublicationLog.TakeLast(2)), ExitCodes.Blocked);
            }
        }
        Store.SaveBundle(b);
        return b;
    }
}
