using System.Text.RegularExpressions;
using ForkBridge.Alignment.Advisor;
using ForkBridge.Alignment.Config;
using ForkBridge.Alignment.Git;
using ForkBridge.Alignment.Model;
using ForkBridge.Alignment.Planning;
using ForkBridge.Alignment.Storage;
using ForkBridge.Alignment.Text;
using ForkBridge.Alignment.Workspace;
using ForkBridge.Core.Config;
using ForkBridge.Core.Infrastructure;
using ForkBridge.Core.Planning;
using ForkBridge.Core.Storage;

namespace ForkBridge.Web;

public sealed record AlignPlanBody(string? PrimaryWorktreeId, string? ForkWorktreeId, string? PrimaryRev, string? ForkRev, bool Preview, string? SyncBase);
public sealed record AlignDecisionsBody(Dictionary<string, string> Edits);
public sealed record AlignAdviseBody(string? PrimaryWorktreeId, string? ForkWorktreeId, string? PrimaryRev, string? ForkRev, string? PlanId, int? RevertWeight, int? MergeWeight, int? ConflictWeight);
public sealed record AlignSyncBaseBody(string Sha, string? Note);
public sealed record AlignOverrideBody(string Pattern, string Decision, string? Reason);
public sealed record AlignCreateWorktreeBody(string Side, string Branch);
public sealed record AlignFastForwardBody(bool ConfirmMainCheckout);

/// <summary>/api/align/* - the Harmony screens. Same security model as the rest of the API (loopback, per-launch
/// token, JSON-only POSTs); inputs are profile names, worktree ids, plan ids, row paths that exist in a plan and
/// hex commit ids - never filesystem paths or git arguments. Long operations run as serialized jobs.</summary>
public static class AlignmentEndpoints
{
    private static readonly Regex Hex = new("^[0-9a-f]{7,40}$", RegexOptions.Compiled);

    public static void Map(WebApplication app, string forkBridgeConfigPath, JobService jobs)
    {
        var api = app.MapGroup("/api/align");
        var git = new AlignGit(new ProcessRunner());
        string AlignConfigPath() => AlignmentConfigLoader.ResolvePath(null, forkBridgeConfigPath);
        ResolvedProfile Profile(string name)
            => AlignmentConfigLoader.Resolve(AlignmentConfigLoader.Load(AlignConfigPath()), ConfigLoader.Load(forkBridgeConfigPath), StateStore.Sanitize(name));
        string Code(ResolvedProfile p) => new AlignmentPlanner(p, git).PrimaryCode;
        WorktreeOps Ops(ResolvedProfile p)
        {
            var fb = ConfigLoader.Load(forkBridgeConfigPath);
            return new WorktreeOps(p, git, side => fb.Repositories[p.SideOf(side).Repo].Remote);
        }
        static string WtRoot(string configured, string root) => string.IsNullOrWhiteSpace(configured) ? root.TrimEnd('/', '\\') + ".worktrees" : configured;
        static Side SideOf(string s) => Enum.TryParse<Side>(s, ignoreCase: true, out var v) && Enum.IsDefined(v)
            ? v : throw new ArgumentException("side must be Primary or Fork.");

        api.MapGet("/profiles", () => Guard(() =>
        {
            var path = AlignConfigPath();
            if (!File.Exists(path)) return Results.Ok(new { configPath = path, found = false, profiles = Array.Empty<object>() });
            var cfg = AlignmentConfigLoader.Load(path);
            var fb = ConfigLoader.Load(forkBridgeConfigPath);
            var list = cfg.Profiles.Keys.Select(n =>
            {
                try
                {
                    var r = AlignmentConfigLoader.Resolve(cfg, fb, n);
                    return (object)new
                    {
                        name = n, valid = true, hash = r.Hash, syncBase = r.Profile.SyncBase, scope = r.Profile.Scope, code = Code(r),
                        primary = new { label = r.Profile.Primary.Label, repo = r.Profile.Primary.Repo, root = r.PrimaryRoot, plannedBranch = r.Profile.Primary.Session.NewBranch,
                            worktreeRoot = WtRoot(r.Profile.Primary.WorktreeRoot, r.PrimaryRoot), remote = fb.Repositories[r.Profile.Primary.Repo].Remote },
                        fork = new { label = r.Profile.Fork.Label, repo = r.Profile.Fork.Repo, root = r.ForkRoot, plannedBranch = r.Profile.Fork.Session.NewBranch,
                            worktreeRoot = WtRoot(r.Profile.Fork.WorktreeRoot, r.ForkRoot), remote = fb.Repositories[r.Profile.Fork.Repo].Remote },
                        neverPush = r.Profile.NeverPush, weights = r.Profile.Advisor,
                    };
                }
                catch (ConfigException ex) { return new { name = n, valid = false, error = ex.Message }; }
            }).ToList();
            return Results.Ok(new { configPath = path, found = true, profiles = list });
        }));

        api.MapGet("/{profile}/worktrees", (string profile, CancellationToken ct) => GuardAsync(async () =>
            Results.Ok(await new WorktreeCatalog(Profile(profile), git).ListAsync(inspect: true, ct))));

        // ---- Worktree maintenance: the only repository writes before phase 4 (fetch, add worktree, fast-forward).
        api.MapGet("/{profile}/remote/{side}/branches", (string profile, string side, CancellationToken ct) => GuardAsync(async () =>
            Results.Ok(await Ops(Profile(profile)).ListRemoteBranchesAsync(SideOf(side), ct))));

        api.MapPost("/{profile}/remote/{side}/fetch", (string profile, string side) => Guard(() =>
        {
            var p = Profile(profile); var sd = SideOf(side);
            return Results.Ok(jobs.Start($"align fetch {p.Name} {sd}", async ct => await Ops(p).FetchAsync(sd, ct)));
        }));

        api.MapPost("/{profile}/worktrees/create", (string profile, AlignCreateWorktreeBody b) => Guard(() =>
        {
            var p = Profile(profile); var sd = SideOf(b.Side);
            return Results.Ok(jobs.Start($"align worktree add {p.Name} {sd}", async ct => await Ops(p).CreateWorktreeAsync(sd, b.Branch, ct)));
        }));

        api.MapPost("/{profile}/worktrees/{id}/fast-forward", (string profile, string id, AlignFastForwardBody b) => Guard(() =>
        {
            var p = Profile(profile); var wid = StateStore.Sanitize(id);
            return Results.Ok(jobs.Start($"align fast-forward {p.Name}", async ct => await Ops(p).FastForwardAsync(wid, b.ConfirmMainCheckout, ct)));
        }));

        api.MapPost("/{profile}/plan", (string profile, AlignPlanBody b) => Guard(() =>
        {
            var p = Profile(profile);
            if (b.SyncBase is not null && !Hex.IsMatch(b.SyncBase)) throw new ArgumentException("syncBase must be a commit id.");
            return Results.Ok(jobs.Start($"align plan {p.Name}", async ct =>
            {
                var catalog = new WorktreeCatalog(p, git);
                async Task<SideSelection> Sel(string? wt, string? rev)
                {
                    if (b.Preview) return new SideSelection { Preview = true };
                    if (rev is not null) { if (!Hex.IsMatch(rev)) throw new ArgumentException("revisions must be commit ids."); return new SideSelection { Rev = rev }; }
                    if (wt is not null) return new SideSelection { WorktreePath = (await catalog.ResolveAsync(StateStore.Sanitize(wt), ct)).Path };
                    throw new ArgumentException("Choose a worktree or preview for each side.");
                }
                var planner = new AlignmentPlanner(p, git);
                var plan = await planner.PlanAsync(new AlignmentPlanRequest(await Sel(b.PrimaryWorktreeId, b.PrimaryRev), await Sel(b.ForkWorktreeId, b.ForkRev), b.SyncBase), ct);
                new AlignmentStore(p.StateDir).Save(plan, planner.PrimaryCode);
                return new { plan.Id, plan.ContentHash, rows = plan.Rows.Count, plan.Identical };
            }));
        }));

        api.MapGet("/{profile}/plans", (string profile) => Guard(() =>
        {
            var store = new AlignmentStore(Profile(profile).StateDir);
            var list = store.ListPlanIds().Take(100).Select(id =>
            {
                try
                {
                    var pl = store.Load(id);
                    return (object)new { pl.Id, pl.ParentPlanId, pl.CreatedAt, pl.Preview, rows = pl.Rows.Count, pl.Identical, pl.SyncBase,
                        primary = new { pl.Primary.Branch, pl.Primary.Rev }, fork = new { pl.Fork.Branch, pl.Fork.Rev },
                        manual = pl.Rows.Count(r => r.Decision == Decision.Manual) };
                }
                catch (Exception ex) when (ex is PlanIntegrityException or System.Text.Json.JsonException) { return new { Id = id, error = ex.Message }; }
            }).ToList();
            return Results.Ok(list);
        }));

        api.MapGet("/{profile}/plans/{id}", (string profile, string id) => Guard(() => Results.Ok(new AlignmentStore(Profile(profile).StateDir).Load(StateStore.Sanitize(id)))));

        api.MapGet("/{profile}/plans/{id}/blob", (string profile, string id, string sha, CancellationToken ct) => GuardAsync(async () =>
        {
            var p = Profile(profile);
            var plan = new AlignmentStore(p.StateDir).Load(StateStore.Sanitize(id));
            var known = plan.Rows.SelectMany(r => new[] { r.Base?.Sha, r.Primary?.Sha, r.Fork?.Sha, r.Merged?.Sha }).Where(s => s is not null).ToHashSet(StringComparer.Ordinal);
            if (!known.Contains(sha)) throw new ArgumentException("Only blobs referenced by the plan can be read.");
            using var x = await ScratchRepo.OpenAsync(git, p.StateDir, p.PrimaryRoot, p.ForkRoot, p.Profile.Scope, p.Profile.Protected, ct);
            var bytes = x.ReadBlob(sha);
            var binary = bytes.Take(8000).Contains((byte)0);
            const int max = 2 * 1024 * 1024;
            return Results.Ok(new { sha, size = bytes.Length, binary, truncated = bytes.Length > max, text = binary ? null : TextOps.ToText(bytes.Length > max ? bytes[..max] : bytes) });
        }));

        api.MapPost("/{profile}/plans/{id}/decisions", (string profile, string id, AlignDecisionsBody b) => Guard(() =>
        {
            var p = Profile(profile);
            var store = new AlignmentStore(p.StateDir);
            var parent = store.Load(StateStore.Sanitize(id));
            var edits = b.Edits.ToDictionary(kv => kv.Key, kv => DecisionNames.Parse(kv.Value), StringComparer.Ordinal);
            if (edits.Count == 0) throw new ArgumentException("No decision changes.");
            var derived = PlanDerivation.WithDecisions(parent, edits, Code(p));
            store.Save(derived, Code(p));
            return Results.Ok(new { derived.Id, derived.ParentPlanId, derived.ContentHash, changed = edits.Count });
        }));

        api.MapPost("/{profile}/syncbase/advise", (string profile, AlignAdviseBody b) => Guard(() =>
        {
            var p = Profile(profile);
            return Results.Ok(jobs.Start($"align advise {p.Name}", async ct =>
            {
                var catalog = new WorktreeCatalog(p, git);
                async Task<string> Rev(string? wt, string? rev, Func<AlignmentPlan, string> fromPlan)
                {
                    if (rev is not null) { if (!Hex.IsMatch(rev)) throw new ArgumentException("revisions must be commit ids."); return rev; }
                    if (b.PlanId is not null) return fromPlan(new AlignmentStore(p.StateDir).Load(StateStore.Sanitize(b.PlanId)));
                    if (wt is not null) return (await catalog.ResolveAsync(StateStore.Sanitize(wt), ct)).Head ?? throw new ArgumentException("worktree has no HEAD");
                    throw new ArgumentException("Choose worktrees, revisions or a plan.");
                }
                var weights = new AdvisorWeights(b.RevertWeight ?? p.Profile.Advisor.RevertWeight, b.MergeWeight ?? p.Profile.Advisor.MergeWeight, b.ConflictWeight ?? p.Profile.Advisor.ConflictWeight);
                return await new SyncBaseAdvisor(p, git).AdviseAsync(new AdvisorRequest(
                    await Rev(b.PrimaryWorktreeId, b.PrimaryRev, pl => pl.Primary.Rev), await Rev(b.ForkWorktreeId, b.ForkRev, pl => pl.Fork.Rev), weights), ct);
            }));
        }));

        api.MapPost("/{profile}/profile/syncbase", (string profile, AlignSyncBaseBody b, CancellationToken ct) => GuardAsync(() =>
            EditConfigAsync(() =>
            {
                var p = Profile(profile);
                AlignmentProfileEditor.SetSyncBase(AlignConfigPath(), p.Name, b.Sha, b.Note);
                return Results.Ok(new { profile = p.Name, syncBase = b.Sha, hash = Profile(profile).Hash });
            }, ct)));

        api.MapPost("/{profile}/profile/override", (string profile, AlignOverrideBody b, CancellationToken ct) => GuardAsync(() =>
            EditConfigAsync(() =>
            {
                var p = Profile(profile);
                AlignmentProfileEditor.AddOverride(AlignConfigPath(), p.Name, b.Pattern, DecisionNames.Parse(b.Decision), b.Reason ?? "", Code(p));
                return Results.Ok(new { profile = p.Name, pattern = b.Pattern, hash = Profile(profile).Hash });
            }, ct)));
    }

    /// <summary>Serialises edits of the alignment config file (read-modify-write with a .bak). Plans record the
    /// profile hash they were made with, so an edit racing a running plan job is detectable rather than silent.</summary>
    private static readonly SemaphoreSlim ConfigEditGate = new(1, 1);

    private static async Task<IResult> EditConfigAsync(Func<IResult> edit, CancellationToken ct)
    {
        await ConfigEditGate.WaitAsync(ct);
        try { return edit(); }
        finally { ConfigEditGate.Release(); }
    }

    private static IResult Guard(Func<IResult> f)
    {
        try { return f(); }
        catch (Exception ex) { return Error(ex); }
    }

    private static async Task<IResult> GuardAsync(Func<Task<IResult>> f)
    {
        try { return await f(); }
        catch (Exception ex) { return Error(ex); }
    }

    private static IResult Error(Exception ex)
    {
        var (code, status) = ex switch
        {
            ConfigException => (ExitCodes.ConfigError, 400),
            AlignmentException a => (a.ExitCode, 409),
            PlanIntegrityException => (ExitCodes.Stale, 409),
            FileNotFoundException => (ExitCodes.UsageError, 404),
            ArgumentException or FormatException => (ExitCodes.UsageError, 400),
            HarnessSafetyException => (ExitCodes.Safety, 403),
            _ => (ExitCodes.Internal, 500),
        };
        return Results.Json(new { error = Redactor.Redact(ex.Message), exitCode = code }, statusCode: status);
    }
}
