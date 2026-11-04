using System.Text.Json;
using ForkBridge.Alignment.Advisor;
using ForkBridge.Alignment.Config;
using ForkBridge.Alignment.Git;
using ForkBridge.Alignment.Model;
using ForkBridge.Alignment.Planning;
using ForkBridge.Alignment.Storage;
using ForkBridge.Alignment.Workspace;
using ForkBridge.Core.Config;
using ForkBridge.Core.Infrastructure;
using ForkBridge.Core.Planning;
using ForkBridge.Core.Storage;

namespace ForkBridge.Cli;

/// <summary><c>forkbridge align …</c> - branch alignment (Harmony) front door. Separate from the transfer
/// commands: it loads forkbridge.yml only for repository aliases and never touches transfer plans or runs.</summary>
internal static class AlignCommands
{
    public const string HelpText = """
        forkbridge align <command> --profile <name> [options]

        Commands:
          profiles                              list alignment profiles and validate them
          worktrees --profile <p>               every worktree of both repositories (id, branch, HEAD, scope state)
          plan --profile <p> <selection>        compute a plan (read-only for the repositories) and save it
               selection: --preview                                base refs (+ merge refs) from the profile
                          --primary-worktree <id> --fork-worktree <id>  worktree HEADs (scope must be clean)
                          --primary-rev <ref> --fork-rev <ref>     explicit revisions
               --sync-base <sha>                 override the profile's SyncBase for this plan
          show --profile <p> [--plan <id>]      print a saved plan (default: latest)
          syncbase --profile <p> --primary-rev <ref> --fork-rev <ref>
                                                SyncBase Advisor: candidates, scores, recommendation
                                                (weights from the profile; --json for the full graph data)
          parity --profile <p> --golden <dir|plan.tsv>
                                                replay a PS reference plan's inputs and compare
                                                (Path, Category, Decision, Merged); exit 0 = parity

        Options: --alignment-config <file> (default: forkbridge.alignment.yml next to forkbridge.yml, or
        FORKBRIDGE_ALIGNMENT_CONFIG), --git <exe>, --json
        """;

    public static async Task<int> RunAsync(Args a, string fbConfigPath, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        var sub = a.Positional.FirstOrDefault();
        if (sub is null or "help" || a.Has("help")) { await stdout.WriteAsync(HelpText); return ExitCodes.Ok; }
        var fb = ConfigLoader.Load(fbConfigPath);
        var acfgPath = AlignmentConfigLoader.ResolvePath(a.Get("alignment-config"), fbConfigPath);
        var acfg = AlignmentConfigLoader.Load(acfgPath);
        var git = new AlignGit(new ProcessRunner(), a.Get("git") ?? "git");
        var json = a.Has("json");

        if (sub == "profiles")
        {
            foreach (var name in acfg.Profiles.Keys)
            {
                try
                {
                    var rp = AlignmentConfigLoader.Resolve(acfg, fb, name);
                    await stdout.WriteLineAsync($"{name}: {rp.Profile.Primary.Label} {rp.PrimaryRoot}  <->  {rp.Profile.Fork.Label} {rp.ForkRoot}");
                    await stdout.WriteLineAsync($"  scope {string.Join(", ", rp.Profile.Scope)}; syncBase {Short(rp.Profile.SyncBase)}; {rp.Hash[..23]}");
                }
                catch (ConfigException ex) { await stdout.WriteLineAsync($"{name}: INVALID\n  {ex.Message.Replace("\n", "\n  ")}"); }
            }
            return ExitCodes.Ok;
        }

        var profile = AlignmentConfigLoader.Resolve(acfg, fb, a.Require("profile"));
        var store = new AlignmentStore(profile.StateDir);

        switch (sub)
        {
            case "worktrees":
            {
                var list = await new WorktreeCatalog(profile, git).ListAsync(ct: ct);
                if (json) { await stdout.WriteLineAsync(JsonSerializer.Serialize(list, StateStore.Json)); return ExitCodes.Ok; }
                foreach (var side in new[] { Side.Primary, Side.Fork })
                {
                    var sp = profile.SideOf(side);
                    await stdout.WriteLineAsync($"{sp.Label} ({sp.Repo})");
                    foreach (var w in list.Where(w => w.Side == side))
                    {
                        var flags = string.Join(" ", new[]
                        {
                            w.IsMain ? "main" : null, w.Detached ? "detached" : null, w.Locked ? "locked" : null, w.Prunable ? "prunable" : null,
                            !w.Exists ? "missing" : null, w.Protected ? "protected" : null,
                            w.ScopeDirty switch { true => "scope-dirty", false => "scope-clean", _ => null },
                            w.Ahead is not null ? $"↑{w.Ahead} ↓{w.Behind}" : null,
                        }.Where(x => x is not null));
                        await stdout.WriteLineAsync($"  {w.Id}  {w.Branch ?? "(detached)",-58} {Short(w.Head)}  {flags}");
                        await stdout.WriteLineAsync($"                {w.Path}");
                    }
                }
                return ExitCodes.Ok;
            }
            case "plan":
            {
                var catalog = new WorktreeCatalog(profile, git);
                SideSelection Sel(string side) =>
                    a.Has("preview") ? new SideSelection { Preview = true }
                    : a.Get($"{side}-rev") is { } rev ? new SideSelection { Rev = rev }
                    : a.Get($"{side}-worktree") is { } id ? new SideSelection { WorktreePath = catalog.ResolveAsync(id, ct).GetAwaiter().GetResult().Path }
                    : throw new ArgumentException($"--{side}-worktree, --{side}-rev or --preview is required.");
                var planner = new AlignmentPlanner(profile, git, json ? null : s => stderr.WriteLine($"== {s}"));
                var plan = await planner.PlanAsync(new AlignmentPlanRequest(Sel("primary"), Sel("fork"), a.Get("sync-base")), ct);
                var dir = store.Save(plan, planner.PrimaryCode);
                if (json) { await stdout.WriteLineAsync(JsonSerializer.Serialize(new { plan.Id, plan.ContentHash, dir, rows = plan.Rows.Count, plan.Identical }, StateStore.Json)); }
                else await stdout.WriteAsync(Summary(plan, planner.PrimaryCode, dir));
                return plan.Rows.Any(r => r.Decision == Decision.Manual) ? ExitCodes.ReviewRequired : ExitCodes.Ok;
            }
            case "syncbase":
            {
                var advisor = new SyncBaseAdvisor(profile, git, json ? null : st => stderr.WriteLine($"== {st}"));
                var res = await advisor.AdviseAsync(new AdvisorRequest(a.Require("primary-rev"), a.Require("fork-rev")), ct);
                if (json) { await stdout.WriteLineAsync(JsonSerializer.Serialize(res, StateStore.Json)); return ExitCodes.Ok; }
                await stdout.WriteLineAsync($"SyncBase Advisor ({profile.Name}): primary {Short(res.PrimaryRev)} <-> fork {Short(res.ForkRev)}; weights revert {res.Weights.Revert} / merge {res.Weights.Merge} / conflict {res.Weights.Conflict}");
                await stdout.WriteLineAsync($"  recommended {Short(res.Recommended)}  confidence {res.Confidence}  (profile: {Short(res.Configured)})");
                foreach (var r in res.Rationale) await stdout.WriteLineAsync($"  - {r}");
                await stdout.WriteLineAsync($"  {"candidate",-10} {"score",5} {"risk",4} {"merge",5} {"confl",5} {"promo",5} {"fit",7}  sources");
                foreach (var c in res.Candidates)
                    await stdout.WriteLineAsync($"  {c.Sha[..10]} {c.Score,5} {c.Risk,4} {c.Merges,5} {c.Conflicts,5} {c.Promotions,5} {c.Fit,7:P1}  {string.Join(", ", c.Sources)}{(c.IsTransient ? "  [transient]" : "")}  {c.Date} {c.Subject}");
                foreach (var wn in res.Warnings) await stdout.WriteLineAsync($"  warning: {wn}");
                return ExitCodes.Ok;
            }
            case "show":
            {
                var id = a.Get("plan") ?? store.ListPlanIds().FirstOrDefault() ?? throw new FileNotFoundException("No alignment plans yet.");
                var plan = store.Load(id);
                var code = new AlignmentPlanner(profile, git).PrimaryCode;
                await stdout.WriteAsync(json ? JsonSerializer.Serialize(plan, StateStore.Json) + "\n" : Summary(plan, code, store.PlanDir(id)));
                return ExitCodes.Ok;
            }
            case "parity":
            {
                var golden = a.Require("golden");
                var tsv = Directory.Exists(golden) ? Path.Combine(golden, "plan.tsv") : golden;
                var planner = new AlignmentPlanner(profile, git, json ? null : s => stderr.WriteLine($"== {s}"));
                var code = planner.PrimaryCode;
                var g = ParityChecker.GoldenInputs(tsv, code);
                if (!g.Scope.SequenceEqual(profile.Profile.Scope))
                    await stderr.WriteLineAsync($"warning: golden scope [{string.Join(", ", g.Scope)}] differs from profile scope [{string.Join(", ", profile.Profile.Scope)}]");
                var plan = await planner.PlanAsync(new AlignmentPlanRequest(new SideSelection { Rev = g.PrimaryRev }, new SideSelection { Rev = g.ForkRev }, g.SyncBase), ct);
                var result = ParityChecker.Compare(tsv, plan, code);
                var outDir = Path.Combine(profile.StateDir, "parity", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
                Directory.CreateDirectory(outDir);
                await File.WriteAllTextAsync(Path.Combine(outDir, "parity.md"), result.ToMarkdown(), ct);
                await File.WriteAllTextAsync(Path.Combine(outDir, "port-plan.tsv"), PlanTsv.Write(plan, code), ct);
                if (json) await stdout.WriteLineAsync(JsonSerializer.Serialize(new { result.Pass, result.ReferenceRows, result.PortRows, result.ReferenceSame, result.PortSame, deltas = result.Deltas, outDir }, StateStore.Json));
                else
                {
                    await stdout.WriteLineAsync($"Parity {(result.Pass ? "PASS" : "FAIL")}: reference {result.ReferenceRows} rows / {result.ReferenceSame} identical, port {result.PortRows} rows / {result.PortSame} identical, {result.Deltas.Count} delta(s)");
                    foreach (var d in result.Deltas.Take(40)) await stdout.WriteLineAsync($"  {d.Field,-9} {d.Path}: {d.Reference} -> {d.Port}");
                    await stdout.WriteLineAsync($"  report: {Path.Combine(outDir, "parity.md")}");
                }
                return result.Pass ? ExitCodes.Ok : ExitCodes.Blocked;
            }
            default:
                await stderr.WriteLineAsync($"Unknown align command '{sub}'.\n\n{HelpText}");
                return ExitCodes.UsageError;
        }
    }

    public static async Task<int> GuardedAsync(Args a, string fbConfigPath, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        try { return await RunAsync(a, fbConfigPath, stdout, stderr, ct); }
        catch (ArgumentException ex) { await stderr.WriteLineAsync(ex.Message); return ExitCodes.UsageError; }
        catch (ConfigException ex) { await stderr.WriteLineAsync("config error: " + ex.Message); return ExitCodes.ConfigError; }
        catch (AlignmentException ex) { await stderr.WriteLineAsync("refused: " + ex.Message); return ex.ExitCode; }
        catch (PlanIntegrityException ex) { await stderr.WriteLineAsync("stale: " + ex.Message); return ExitCodes.Stale; }
        catch (AlignGitException ex) { await stderr.WriteLineAsync("git error: " + Redactor.Redact(ex.Message)); return ExitCodes.Internal; }
        catch (FileNotFoundException ex) { await stderr.WriteLineAsync("error: " + ex.Message); return ExitCodes.UsageError; }
        catch (OperationCanceledException) { await stderr.WriteLineAsync("cancelled"); return ExitCodes.Cancelled; }
    }

    private static string Short(string? s) => string.IsNullOrEmpty(s) ? "-" : s[..Math.Min(10, s.Length)];

    internal static string Summary(AlignmentPlan p, string code, string dir)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Alignment plan {p.Id}{(p.Preview ? " (preview)" : "")}  {p.ContentHash[..23]}");
        sb.AppendLine($"  {p.Primary.Label} {Short(p.Primary.Rev)} {p.Primary.Branch}   <->   {p.Fork.Label} {Short(p.Fork.Rev)} {p.Fork.Branch}");
        sb.AppendLine($"  sync base {Short(p.SyncBase)}; identical {p.Identical}; rows {p.Rows.Count}");
        foreach (var g in p.Rows.GroupBy(r => $"{DecisionNames.Legacy(r.Category, code)} / {DecisionNames.Legacy(r.Decision, code)}").OrderBy(g => g.Key, StringComparer.Ordinal))
            sb.AppendLine($"  {g.Key,-36} {g.Count(),5}");
        var manual = p.Rows.Count(r => r.Decision == Decision.Manual);
        if (manual > 0) sb.AppendLine($"  {manual} MANUAL row(s) need a decision");
        sb.AppendLine($"  plan: {dir}");
        return sb.ToString();
    }
}
