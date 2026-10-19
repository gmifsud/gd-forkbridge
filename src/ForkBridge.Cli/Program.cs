using System.Text.Json;
using ForkBridge.Core;
using ForkBridge.Core.Config;
using ForkBridge.Core.Git;
using ForkBridge.Core.Infrastructure;
using ForkBridge.Core.Model;
using ForkBridge.Core.Planning;
using ForkBridge.Core.Publishing;
using ForkBridge.Core.Reporting;
using ForkBridge.Core.Storage;
using ForkBridge.Core.Transfer;

namespace ForkBridge.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); Console.Error.WriteLine("Cancelling… (state is saved; use `forkbridge resume`)"); };
        return await RunAsync(args, Console.Out, Console.Error, cts.Token);
    }

    public static async Task<int> RunAsync(string[] argv, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        Args a;
        try { a = Args.Parse(argv); }
        catch (ArgumentException ex) { await stderr.WriteLineAsync(ex.Message); return ExitCodes.UsageError; }

        if (a.Command is null or "help" || a.Has("help")) { await stdout.WriteAsync(Help.Text(a.Command == "help" ? a.Positional.FirstOrDefault() : a.Command)); return ExitCodes.Ok; }
        if (a.Command == "version") { await stdout.WriteLineAsync("forkbridge 0.1.0"); return ExitCodes.Ok; }

        try
        {
            var cfgPath = a.Get("config") ?? Environment.GetEnvironmentVariable("FORKBRIDGE_CONFIG") ?? "forkbridge.yml";
            if (a.Command == "validate-config")
            {
                var c = ConfigLoader.Load(cfgPath);
                await stdout.WriteLineAsync($"OK: {c.Repositories.Count} repositories, {c.Pairs.Count} pairs, version {c.Version}.");
                foreach (var (n, p) in c.Pairs)
                    await stdout.WriteLineAsync($"  {n}: {string.Join(", ", p.Components.Select(x => $"{x.Id}[{x.Status}]"))}; policy {ConfigLoader.PolicyHash(c, n)[..23]}");
                return ExitCodes.Ok;
            }
            var engine = ForkBridgeEngine.Load(cfgPath);
            bool json = a.Has("json");
            async Task Emit(object obj, string text) => await stdout.WriteLineAsync(json ? JsonSerializer.Serialize(obj, StateStore.Json) : text);

            switch (a.Command)
            {
                case "discover":
                {
                    var (report, jp, mp, draft) = await engine.DiscoverAsync(a.Has("fetch"), int.Parse(a.Get("depth") ?? "2"), a.Get("write-config"), ct);
                    await Emit(report, $"Discovery written:\n  {mp}\n  {jp}{(draft is null ? "" : $"\nDraft config (all mappings PROPOSED): {draft}")}\n\n" +
                                        string.Join("\n", report.Pairs.Select(p => $"pair {p.Pair}: shares history={p.SharesHistory}, proposed={p.Directories.Count(d => d.Verdict == "proposed-shared")}, name-only={p.Directories.Count(d => d.Verdict == "name-only")}")));
                    return ExitCodes.Ok;
                }
                case "drift":
                {
                    var pair = a.Require("pair");
                    var pc = engine.Config.Pairs.GetValueOrDefault(pair) ?? throw new PlanningException($"Unknown pair '{pair}'.");
                    var prods = pc.Products.Keys.ToList();
                    var from = a.Get("from") ?? prods[0]; var to = a.Get("to") ?? prods[1];
                    var drift = await engine.DriftAsync(pair, from, to, a.Has("offline"), a.Has("allow-proposed"), ct);
                    await Emit(drift, string.Join("\n", drift.Select(d =>
                        $"{d.Component,-30} {from}:{d.SourcePath} -> {to}:{d.TargetPath}{(d.IntentionallyAbsentInTarget ? " (intentionally absent)" : !d.PresentInTarget ? " (MISSING in target)" : "")}\n" +
                        $"    identical {d.Identical}, differing {d.Differing.Count} (target diverged {d.TargetDivergedSinceBaseline.Count}, source changed {d.SourceChangedSinceBaseline.Count}), " +
                        $"only-{from} {d.OnlyInSource.Count}, only-{to} {d.OnlyInTarget.Count}, preserved product diffs {d.PreservedProductDifferences.Count}")));
                    return ExitCodes.Ok;
                }
                case "plan":
                {
                    var mode = a.Get("mode") switch
                    {
                        "reconcile" => SelectionMode.Reconcile,
                        "merge" => SelectionMode.Merge,
                        null or "pick" => a.GetMany("commits").Count > 0 ? SelectionMode.Commits : a.Get("range") is not null ? SelectionMode.Range : SelectionMode.Feature,
                        var m => throw new ArgumentException($"Unknown --mode '{m}' (pick | reconcile | merge)."),
                    };
                    var plan = await engine.Planner.PlanAsync(new PlanRequest
                    {
                        Pair = a.Require("pair"), From = a.Require("from"), To = a.Require("to"), Mode = mode,
                        SourceRef = a.Get("source-ref"), SourceBase = a.Get("source-base"), TargetBase = a.Get("target-base"),
                        Range = a.Get("range"), Commits = a.GetMany("commits"), Components = a.GetMany("components"),
                        AllowProposed = a.Has("allow-proposed"), Offline = a.Has("offline"), PreferLocal = a.Has("prefer-local"),
                        Baseline = a.Get("baseline"), Reapply = a.GetMany("reapply"),
                    }, ct);
                    var md = Path.Combine(engine.Store.PlanDir(plan.Id), "plan.md");
                    await File.WriteAllTextAsync(md, ReportWriter.PlanMarkdown(plan), ct);
                    await Emit(plan, Summaries.Plan(plan, md));
                    return plan.PreliminaryStatus == RunStatus.PLANNED ? ExitCodes.Ok : ExitCodes.For(plan.PreliminaryStatus);
                }
                case "simulate":
                {
                    var run = await engine.Simulator.SimulateAsync(a.Require("plan"), Opts(a), ct);
                    await Emit(run, Summaries.Run(engine, run));
                    return ExitCodes.For(run.Status);
                }
                case "resume":
                {
                    var run = await engine.Simulator.ResumeAsync(a.Require("run"), Opts(a), ct);
                    await Emit(run, Summaries.Run(engine, run));
                    return ExitCodes.For(run.Status);
                }
                case "abort":
                {
                    var run = engine.Abort(a.Require("run"));
                    await Emit(run, $"Run {run.Id} aborted. Harness workspace and branch removed; reports kept in {run.WorkspaceDir}.");
                    return ExitCodes.Ok;
                }
                case "report":
                {
                    if (a.Get("plan") is { } pid)
                    {
                        var p = engine.Store.LoadPlan(pid);
                        await stdout.WriteLineAsync(a.Get("format") == "json" ? JsonSerializer.Serialize(p, StateStore.Json) : ReportWriter.PlanMarkdown(p));
                        return ExitCodes.For(p.PreliminaryStatus);
                    }
                    var run = engine.Store.LoadRun(a.Require("run"));
                    var path = a.Get("format") == "json" ? ReportWriter.JsonPath(engine.Store, run.Id) : ReportWriter.MarkdownPath(engine.Store, run.Id);
                    if (!File.Exists(path)) ReportWriter.Write(engine.Store, engine.Store.LoadPlan(run.PlanId), run);
                    await stdout.WriteLineAsync(await File.ReadAllTextAsync(path, ct));
                    return ExitCodes.For(run.Status);
                }
                case "approve":
                {
                    var appr = await engine.Publisher.ApproveAsync(a.Require("run"), a.Require("candidate"), a.GetMany("accept"), WhoAmI(), ct);
                    await Emit(appr, $"Approved.\n  binding: {appr.Binding}");
                    return ExitCodes.Ok;
                }
                case "publish":
                {
                    var runId = a.Require("run");
                    if (a.Has("approve"))
                    {
                        var cand = a.Get("candidate");
                        if (cand is null)
                        {
                            var run0 = engine.Store.LoadRun(runId);
                            if (Console.IsInputRedirected) throw new ArgumentException("--approve needs --candidate <sha> when not interactive.");
                            await stdout.WriteLineAsync($"Candidate {run0.CandidateSha}\nBranch    {run0.Branch}\nType the first 10 characters of the candidate SHA to approve:");
                            cand = Console.ReadLine()?.Trim() ?? "";
                            if (cand.Length < 10) throw new ArgumentException("Approval cancelled.");
                        }
                        await engine.Publisher.ApproveAsync(runId, cand, a.GetMany("accept"), WhoAmI(), ct);
                    }
                    var rec = await engine.Publisher.PublishAsync(runId, a.Has("create-pr"), ct);
                    await Emit(rec, $"Pushed {rec.CandidateSha[..10]} to new branch '{rec.Branch}'.{(rec.PullRequestUrl is null ? "" : $"\nPull request: {rec.PullRequestUrl}")}{(rec.Error is null ? "" : $"\nNote: {rec.Error}")}");
                    return ExitCodes.Ok;
                }
                case "cleanup":
                {
                    var ids = a.Has("all-finished")
                        ? engine.Store.ListRuns().Where(r => r.Status is not RunStatus.IN_PROGRESS && r.Conflict is null).Select(r => r.Id).ToList()
                        : [a.Require("run")];
                    foreach (var id in ids) { engine.Cleanup(id); await stdout.WriteLineAsync($"Removed harness artifacts of {id}."); }
                    return ExitCodes.Ok;
                }
                case "runs":
                {
                    var runs = engine.Store.ListRuns().Take(int.Parse(a.Get("limit") ?? "30")).ToList();
                    await Emit(runs.Select(r => new { r.Id, r.PlanId, r.Status, r.CandidateSha, r.Branch, r.StartedAt, r.BundleId }),
                        string.Join("\n", runs.Select(r => $"{r.Id}  {r.Status,-18} plan {r.PlanId}  {r.CandidateSha?[..10] ?? "-",-10}  {r.StartedAt:u}")));
                    return ExitCodes.Ok;
                }
                case "plans":
                {
                    var plans = engine.Store.ListPlans().Take(int.Parse(a.Get("limit") ?? "30")).ToList();
                    await Emit(plans.Select(p => new { p.Id, p.Pair, p.FromProduct, p.ToProduct, p.Mode, p.PreliminaryStatus, p.CreatedAt }),
                        string.Join("\n", plans.Select(p => $"{p.Id}  {p.PreliminaryStatus,-16} {p.Pair} {p.FromProduct}->{p.ToProduct} {p.Mode}  {p.CreatedAt:u}")));
                    return ExitCodes.Ok;
                }
                case "bundle":
                {
                    var sub = a.Positional.FirstOrDefault() ?? throw new ArgumentException("bundle create | simulate | publish | show");
                    switch (sub)
                    {
                        case "create":
                            var b = engine.CreateBundle(a.GetMany("plans"), a.Get("name"));
                            await Emit(b, $"Bundle {b.Id} with plans {string.Join(", ", b.PlanIds)}\n" + string.Join("\n", b.Findings.Select(f => $"  {f.Id}: {f.Title}")));
                            return ExitCodes.Ok;
                        case "simulate":
                            var bs = await engine.SimulateBundleAsync(a.Require("bundle"), Opts(a), a.Get("bundle-profile"), ct);
                            await Emit(bs, $"Bundle {bs.Id}: {bs.Status}\n" + string.Join("\n", bs.RunIds.Select(kv => $"  plan {kv.Key} -> run {kv.Value} ({engine.Store.LoadRun(kv.Value).Status})")) + "\nSHA matrix:\n" +
                                string.Join("\n", bs.PlanIds.Select(engine.Store.LoadPlan).Select(p => $"  {p.Pair} {p.FromProduct}@{p.SourceHeadSha[..10]} -> {p.ToProduct}@{p.TargetBaseSha[..10]}")));
                            return ExitCodes.For(bs.Status);
                        case "publish":
                            var bp = await engine.PublishBundleAsync(a.Require("bundle"), a.Has("create-pr"), ct);
                            await Emit(bp, string.Join("\n", bp.PublicationLog) + "\nEach repository was published separately; there is no cross-repository atomicity.");
                            return ExitCodes.Ok;
                        case "show":
                            var bshow = engine.Store.LoadBundle(a.Require("bundle"));
                            await stdout.WriteLineAsync(JsonSerializer.Serialize(bshow, StateStore.Json));
                            return ExitCodes.For(bshow.Status);
                        default: throw new ArgumentException($"Unknown bundle subcommand '{sub}'.");
                    }
                }
                default:
                    await stderr.WriteLineAsync($"Unknown command '{a.Command}'. Run `forkbridge help`.");
                    return ExitCodes.UsageError;
            }
        }
        catch (ArgumentException ex) { await stderr.WriteLineAsync("error: " + ex.Message); return ExitCodes.UsageError; }
        catch (ConfigException ex) { await stderr.WriteLineAsync("config error: " + ex.Message); return ExitCodes.ConfigError; }
        catch (PlanningException ex) { await stderr.WriteLineAsync("refused: " + Redactor.Redact(ex.Message)); return ex.ExitCode; }
        catch (StaleException ex) { await stderr.WriteLineAsync("stale: " + Redactor.Redact(ex.Message)); return ExitCodes.Stale; }
        catch (PlanIntegrityException ex) { await stderr.WriteLineAsync("stale: " + ex.Message); return ExitCodes.Stale; }
        catch (FreshnessException ex) { await stderr.WriteLineAsync("fetch failed: " + Redactor.Redact(ex.Message)); return ExitCodes.FetchFailed; }
        catch (PublishRefusedException ex) { await stderr.WriteLineAsync("publish refused: " + Redactor.Redact(ex.Message)); return ex.ExitCode; }
        catch (HarnessSafetyException ex) { await stderr.WriteLineAsync("safety: " + ex.Message); return ExitCodes.Safety; }
        catch (FileNotFoundException ex) { await stderr.WriteLineAsync("error: " + ex.Message); return ExitCodes.UsageError; }
        catch (OperationCanceledException) { await stderr.WriteLineAsync("cancelled"); return ExitCodes.Cancelled; }
        catch (GitException ex) { await stderr.WriteLineAsync("git error: " + Redactor.Redact(ex.Message)); return ExitCodes.Internal; }
        catch (Exception ex) { await stderr.WriteLineAsync("internal error: " + Redactor.Redact(ex.ToString())); return ExitCodes.Internal; }
    }

    private static SimulateOptions Opts(Args a) => new()
    {
        TrustValidation = a.Has("trust-validation") || Environment.GetEnvironmentVariable("FORKBRIDGE_TRUST_VALIDATION") == "1",
        Profile = a.Get("profile"), PreserveWorkspace = a.Has("preserve-workspace"), SkipValidation = a.Has("skip-validation"),
    };

    private static string WhoAmI() => Environment.GetEnvironmentVariable("FORKBRIDGE_APPROVER") ?? Environment.UserName;
}

/// <summary>Minimal deterministic argument parser: `cmd [positional…] --key value --flag --list a b c`.</summary>
public sealed class Args
{
    public string? Command { get; private init; }
    public List<string> Positional { get; } = new();
    private readonly Dictionary<string, List<string>> _opts = new();
    private static readonly HashSet<string> Multi = ["commits", "components", "accept", "plans", "reapply"];
    private static readonly HashSet<string> Flags = ["json", "help", "offline", "fetch", "allow-proposed", "prefer-local", "trust-validation", "preserve-workspace",
        "skip-validation", "approve", "create-pr", "all-finished"];

    public static Args Parse(string[] argv)
    {
        var a = new Args { Command = argv.FirstOrDefault(x => !x.StartsWith("--")) is { } c && argv.Length > 0 && argv[0] == c ? c : null };
        int i = a.Command is null ? 0 : 1;
        while (i < argv.Length)
        {
            var t = argv[i];
            if (t.StartsWith("--"))
            {
                var key = t[2..];
                string? inline = null;
                var eq = key.IndexOf('=');
                if (eq > 0) { inline = key[(eq + 1)..]; key = key[..eq]; }
                if (!a._opts.TryGetValue(key, out var list)) a._opts[key] = list = new();
                if (inline is not null) { list.AddRange(Multi.Contains(key) ? inline.Split(',', StringSplitOptions.RemoveEmptyEntries) : [inline]); i++; continue; }
                if (Flags.Contains(key)) { i++; continue; }
                i++;
                if (Multi.Contains(key))
                    while (i < argv.Length && !argv[i].StartsWith("--")) list.AddRange(argv[i++].Split(',', StringSplitOptions.RemoveEmptyEntries));
                else
                {
                    if (i >= argv.Length || argv[i].StartsWith("--")) throw new ArgumentException($"--{key} requires a value.");
                    list.Add(argv[i++]);
                }
            }
            else { a.Positional.Add(t); i++; }
        }
        return a;
    }

    public bool Has(string k) => _opts.ContainsKey(k);
    public string? Get(string k) => _opts.TryGetValue(k, out var v) && v.Count > 0 ? v[^1] : null;
    public List<string> GetMany(string k) => _opts.TryGetValue(k, out var v) ? v : new();
    public string Require(string k) => Get(k) ?? throw new ArgumentException($"--{k} is required.");
}

internal static class Summaries
{
    public static string Plan(Plan p, string md)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Plan {p.Id}: {p.PreliminaryStatus}");
        sb.AppendLine($"  {p.Pair}: {p.FromProduct} -> {p.ToProduct} ({p.Mode})");
        sb.AppendLine($"  source head {p.SourceHeadSha}   target base {p.TargetBaseSha} ({p.TargetBaseRef})");
        if (p.MergeBaseExplanation is not null) sb.AppendLine($"  {p.MergeBaseExplanation}");
        foreach (var c in p.Commits) sb.AppendLine($"  {c.Sha[..10]} {c.State,-28} {(c.Mixed ? "[mixed] " : "")}{c.Subject}");
        if (p.Reconciliation is not null) sb.AppendLine($"  reconciliation: {p.Reconciliation.State}, {p.Reconciliation.Paths.Count(x => x.Decision == Decision.Include)} file(s) included");
        foreach (var f in p.Findings.Where(f => f.Blocking || f.RequiresReview || f.Severity >= Severity.Warning))
            sb.AppendLine($"  {(f.Blocking ? "BLOCK " : f.RequiresReview ? "REVIEW" : "warn  ")} {f.Id}: {f.Title}");
        sb.AppendLine($"  report: {md}");
        if (p.PreliminaryStatus is RunStatus.PLANNED or RunStatus.REVIEW_REQUIRED) sb.AppendLine($"  next: forkbridge simulate --plan {p.Id} [--trust-validation]");
        return sb.ToString();
    }

    public static string Run(ForkBridgeEngine e, Run r)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Run {r.Id}: {r.Status}");
        if (r.CandidateSha is not null) sb.AppendLine($"  candidate {r.CandidateSha} on harness branch {r.Branch}");
        foreach (var v in r.ValidationComparison) sb.AppendLine($"  validation {v.StepId,-24} baseline {v.Baseline,-12} candidate {v.Candidate,-12} {v.Verdict}");
        foreach (var f in r.Findings.Where(f => f.Blocking || f.RequiresReview)) sb.AppendLine($"  {(f.Blocking ? "BLOCK " : "REVIEW")} {f.Id}: {f.Title}");
        if (r.Conflict is not null) foreach (var i in r.Conflict.Instructions) sb.AppendLine($"  > {i}");
        sb.AppendLine($"  report: {ReportWriter.MarkdownPath(e.Store, r.Id)}");
        if (r.Status == RunStatus.READY) sb.AppendLine($"  next: forkbridge publish --run {r.Id} --approve --candidate {r.CandidateSha?[..12]}");
        if (r.Status == RunStatus.REVIEW_REQUIRED) sb.AppendLine($"  next: review, then forkbridge publish --run {r.Id} --approve --candidate {r.CandidateSha?[..12]} --accept <finding-ids>");
        return sb.ToString();
    }
}
