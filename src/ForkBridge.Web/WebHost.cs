using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForkBridge.Core;
using ForkBridge.Core.Git;
using ForkBridge.Core.Infrastructure;
using ForkBridge.Core.Model;
using ForkBridge.Core.Planning;
using ForkBridge.Core.Publishing;
using ForkBridge.Core.Reporting;
using ForkBridge.Core.Storage;
using ForkBridge.Core.Transfer;

namespace ForkBridge.Web;

public sealed record WebOptions
{
    public required string ConfigPath { get; init; }
    public int Port { get; init; } = 5178;
    public string? Token { get; init; }
    public string? WebRoot { get; init; }
}

/// <summary>Local web service. Security model:
///  * binds to the loopback interface only (non-loopback addresses are refused);
///  * every /api request needs the per-launch random token (X-ForkBridge-Token header), which is
///    delivered in the URL fragment so it never reaches logs or referrers;
///  * Host header must be loopback (DNS-rebinding defence); cross-site Origin/Sec-Fetch-Site rejected;
///  * state-changing endpoints are POST with application/json only (no simple-form CSRF);
///  * no endpoint accepts filesystem paths or git arguments - only config aliases and harness ids.</summary>
public static class WebHost
{
    public static WebApplication Build(WebOptions o)
    {
        var engine = ForkBridgeEngine.Load(o.ConfigPath);
        var token = o.Token ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            WebRootPath = o.WebRoot ?? Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, o.Port));
        builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(j =>
        {
            j.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            j.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            j.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
        });
        var app = builder.Build();
        var jobs = new JobService();

        app.Use(async (ctx, next) =>
        {
            var h = ctx.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["Referrer-Policy"] = "no-referrer";
            h["X-Frame-Options"] = "DENY";
            h["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'none'";
            h["Cache-Control"] = "no-store";

            var host = ctx.Request.Host.Host;
            if (host is not ("127.0.0.1" or "localhost" or "[::1]" or "::1"))
            { ctx.Response.StatusCode = 421; await ctx.Response.WriteAsync("Misdirected request: loopback host required."); return; }
            if (ctx.Connection.RemoteIpAddress is { } ip && !IPAddress.IsLoopback(ip))
            { ctx.Response.StatusCode = 403; return; }

            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                var site = ctx.Request.Headers["Sec-Fetch-Site"].ToString();
                if (site.Length > 0 && site is not ("same-origin" or "none")) { ctx.Response.StatusCode = 403; return; }
                var origin = ctx.Request.Headers.Origin.ToString();
                if (origin.Length > 0 && origin != $"{ctx.Request.Scheme}://{ctx.Request.Host}") { ctx.Response.StatusCode = 403; return; }
                var given = ctx.Request.Headers["X-ForkBridge-Token"].ToString();
                if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(token)))
                { ctx.Response.StatusCode = 401; return; }
                if (HttpMethods.IsPost(ctx.Request.Method) && !(ctx.Request.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                { ctx.Response.StatusCode = 415; return; }
                if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsPost(ctx.Request.Method)) { ctx.Response.StatusCode = 405; return; }
            }
            try { await next(); }
            catch (Exception ex) { await WriteError(ctx, ex); }
        });

        app.UseDefaultFiles();
        app.UseStaticFiles();
        MapApi(app, engine, jobs);
        app.MapFallbackToFile("index.html");
        return app;
    }

    private static async Task WriteError(HttpContext ctx, Exception ex)
    {
        var (code, status) = ex switch
        {
            PlanningException p => (p.ExitCode, 409),
            StaleException => (ExitCodes.Stale, 409),
            PlanIntegrityException => (ExitCodes.Stale, 409),
            PublishRefusedException p => (p.ExitCode, 409),
            HarnessSafetyException => (ExitCodes.Safety, 403),
            FileNotFoundException => (ExitCodes.UsageError, 404),
            ArgumentException => (ExitCodes.UsageError, 400),
            _ => (ExitCodes.Internal, 500),
        };
        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new { error = Redactor.Redact(ex.Message), exitCode = code });
    }

    private static string Id(string s) => StateStore.Sanitize(s);

    private static void MapApi(WebApplication app, ForkBridgeEngine e, JobService jobs)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/config", () => new
        {
            e.Config.SourceFile, stateDir = e.Store.Root,
            repositories = e.Config.Repositories.Select(r => new { alias = r.Key, r.Value.Path, r.Value.Kind, r.Value.Placeholder }),
            pairs = e.Config.Pairs.Select(p => new
            {
                name = p.Key, products = p.Value.Products, defaults = p.Value.Defaults, directions = p.Value.Directions,
                components = p.Value.Components.Select(c => new { c.Id, c.Status, c.Paths, c.Absent, c.DependsOn, c.Description, c.Evidence }),
                validationProfiles = p.Value.Validation.Select(v => new { name = v.Key, v.Value.Trusted, steps = v.Value.Steps.Select(s => new { s.Id, s.Kind, s.Required }) }),
            }),
            bundles = e.Config.Bundles.Keys,
        });

        api.MapGet("/pairs/{pair}/refs", async (string pair, string product) =>
        {
            var alias = e.Config.Pairs[pair].Products[product];
            var mirror = e.Store.MirrorDir(alias);
            if (!Directory.Exists(mirror)) return Results.Ok(new { synced = false, refs = Array.Empty<object>() });
            var lines = await e.Git.LinesAsync(mirror, "for-each-ref", "--sort=-committerdate", "--format=%(refname)%09%(objectname)%09%(committerdate:iso-strict)%09%(subject)", "refs/remotes/origin", "refs/fb/local/heads");
            return Results.Ok(new
            {
                synced = true,
                refs = lines.Take(400).Select(l => l.Split('\t')).Select(p => new
                {
                    name = p[0].StartsWith("refs/remotes/origin/") ? p[0]["refs/remotes/origin/".Length..] : p[0]["refs/fb/local/heads/".Length..],
                    local = p[0].StartsWith("refs/fb/local/"), sha = p[1], date = p[2], subject = p.Length > 3 ? p[3] : "",
                }),
            });
        });

        api.MapGet("/pairs/{pair}/commits", async (string pair, string product, string head, string? @base) =>
        {
            var alias = e.Config.Pairs[pair].Products[product];
            var mirror = e.Store.MirrorDir(alias);
            var h = await e.Mirrors.ResolveAsync(mirror, head, offline: false, preferLocal: false);
            var args = new List<string> { "log", "--max-count=200", "--format=%H%x09%an%x09%aI%x09%P%x09%s" };
            if (@base is not null) args.Add($"{(await e.Mirrors.ResolveAsync(mirror, @base, false, false)).Sha}..{h.Sha}"); else args.Add(h.Sha);
            var r = await e.Git.CheckedAsync(mirror, args);
            return GitClient.SplitLines(r.StdOut).Select(l => l.Split('\t')).Select(p => new { sha = p[0], author = p[1], date = p[2], merge = p[3].Contains(' '), subject = p[4] });
        });

        api.MapPost("/pairs/{pair}/sync", (string pair, OfflineBody b) => jobs.Start($"sync {pair}", async ct =>
        {
            foreach (var alias in e.Config.Pairs[pair].Products.Values) await e.Mirrors.SyncAsync(alias, b.Offline, ct);
            return new { pair };
        }));

        api.MapPost("/discover", (DiscoverBody b) => jobs.Start("discover", async ct =>
        {
            var (report, json, md, _) = await e.DiscoverAsync(b.Fetch, Math.Clamp(b.Depth, 1, 4), null, ct);
            return new { report, markdown = await File.ReadAllTextAsync(md, ct) };
        }));

        api.MapPost("/drift", (DriftBody b) => jobs.Start($"drift {b.Pair}", async ct => (object)await e.DriftAsync(b.Pair, b.From, b.To, b.Offline, b.AllowProposed, ct)));

        api.MapPost("/plans", (PlanRequestBody b) => jobs.Start($"plan {b.Pair} {b.From}->{b.To}", async ct =>
        {
            var mode = b.Mode switch
            {
                "reconcile" => SelectionMode.Reconcile, "merge" => SelectionMode.Merge, "range" => SelectionMode.Range, "commits" => SelectionMode.Commits,
                "feature" => SelectionMode.Feature, _ => throw new ArgumentException("mode must be feature | range | commits | reconcile | merge"),
            };
            var plan = await e.Planner.PlanAsync(new PlanRequest
            {
                Pair = b.Pair, From = b.From, To = b.To, Mode = mode, SourceRef = Blank(b.SourceRef), SourceBase = Blank(b.SourceBase), TargetBase = Blank(b.TargetBase),
                Range = Blank(b.Range), Commits = b.Commits ?? [], Components = b.Components ?? [], AllowProposed = b.AllowProposed, Offline = b.Offline,
                PreferLocal = b.PreferLocal, Baseline = Blank(b.Baseline), Reapply = b.Reapply ?? [],
            }, ct);
            await File.WriteAllTextAsync(Path.Combine(e.Store.PlanDir(plan.Id), "plan.md"), ReportWriter.PlanMarkdown(plan), ct);
            return new { planId = plan.Id, status = plan.PreliminaryStatus };
        }));

        api.MapGet("/plans", () => e.Store.ListPlans().Take(100).Select(p => new { p.Id, p.Pair, p.FromProduct, p.ToProduct, p.Mode, p.PreliminaryStatus, p.CreatedAt, p.SourceRef, commits = p.Commits.Count }));
        api.MapGet("/plans/{id}", (string id) => e.Store.LoadPlan(Id(id)));
        api.MapGet("/plans/{id}/markdown", (string id) => Results.Text(ReportWriter.PlanMarkdown(e.Store.LoadPlan(Id(id))), "text/markdown"));

        api.MapPost("/plans/{id}/simulate", (string id, SimulateBody b) => jobs.Start($"simulate {id}", async ct =>
        {
            var run = await e.Simulator.SimulateAsync(Id(id), new SimulateOptions { TrustValidation = b.TrustValidation, Profile = Blank(b.Profile), PreserveWorkspace = b.PreserveWorkspace }, ct);
            return new { runId = run.Id, status = run.Status };
        }));

        api.MapGet("/runs", () => e.Store.ListRuns().Take(200).Select(r => new { r.Id, r.PlanId, r.Status, r.CandidateSha, r.Branch, r.StartedAt, r.FinishedAt, r.BundleId, published = r.Publications.Any(p => p.Pushed), workspace = Directory.Exists(r.CandidateWorktree) }));
        api.MapGet("/runs/{id}", (string id) =>
        {
            var run = e.Store.LoadRun(Id(id));
            return Results.Ok(new { run, plan = e.Store.LoadPlan(run.PlanId), workspaceExists = Directory.Exists(run.CandidateWorktree) });
        });
        api.MapGet("/runs/{id}/diff", (string id) =>
        {
            var run = e.Store.LoadRun(Id(id));
            if (run.CandidateDiffFile is null || !File.Exists(run.CandidateDiffFile)) return Results.Text("");
            var bytes = File.ReadAllBytes(run.CandidateDiffFile);
            var text = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 3_000_000));
            return Results.Text(bytes.Length > 3_000_000 ? text + "\n… (truncated; full diff in " + run.CandidateDiffFile + ")" : text, "text/plain");
        });
        api.MapGet("/runs/{id}/report.md", (string id) => Results.Text(File.ReadAllText(ReportWriter.MarkdownPath(e.Store, Id(id))), "text/markdown"));
        api.MapGet("/runs/{id}/evidence", (string id) =>
        {
            var run = e.Store.LoadRun(Id(id));
            var dir = run.Conflict?.EvidenceDir;
            if (dir is null || !Directory.Exists(dir)) return Results.Ok(Array.Empty<object>());
            return Results.Ok(Directory.GetFiles(dir).Select(f => new { name = Path.GetFileName(f), content = File.ReadAllText(f) }));
        });
        api.MapPost("/runs/{id}/resume", (string id, SimulateBody b) => jobs.Start($"resume {id}", async ct =>
        {
            var run = await e.Simulator.ResumeAsync(Id(id), new SimulateOptions { TrustValidation = b.TrustValidation }, ct);
            return new { runId = run.Id, status = run.Status };
        }));
        api.MapPost("/runs/{id}/abort", (string id, EmptyBody _) => { var r = e.Abort(Id(id)); return new { r.Id, r.Status }; });
        api.MapPost("/runs/{id}/cleanup", (string id, EmptyBody _) => { e.Cleanup(Id(id)); return new { id, removed = true }; });
        api.MapPost("/runs/{id}/approve", async (string id, ApproveBody b) =>
            await e.Publisher.ApproveAsync(Id(id), b.Candidate, b.Accept ?? [], Environment.UserName + " (local UI)"));
        api.MapPost("/runs/{id}/publish", (string id, PublishBody b) => jobs.Start($"publish {id}", async ct => (object)await e.Publisher.PublishAsync(Id(id), b.CreatePr, ct)));

        api.MapGet("/bundles/{id}", (string id) => e.Store.LoadBundle(Id(id)));
        api.MapPost("/bundles", (BundleBody b) => e.CreateBundle(b.Plans.Select(Id), b.Name));
        api.MapPost("/bundles/{id}/simulate", (string id, SimulateBody b) => jobs.Start($"bundle {id}", async ct => (object)await e.SimulateBundleAsync(Id(id), new SimulateOptions { TrustValidation = b.TrustValidation }, Blank(b.Profile), ct)));
        api.MapPost("/bundles/{id}/publish", (string id, PublishBody b) => jobs.Start($"bundle publish {id}", async ct => (object)await e.PublishBundleAsync(Id(id), b.CreatePr, ct)));

        api.MapGet("/jobs", () => jobs.All());
        api.MapGet("/jobs/{id}", (string id) => jobs.Get(id) is { } j ? Results.Ok(j) : Results.NotFound());
        api.MapPost("/jobs/{id}/cancel", (string id, EmptyBody _) => jobs.Cancel(id) ? Results.Ok() : Results.NotFound());
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

public sealed record EmptyBody;
public sealed record OfflineBody(bool Offline);
public sealed record DiscoverBody(bool Fetch, int Depth = 2);
public sealed record DriftBody(string Pair, string From, string To, bool Offline, bool AllowProposed);
public sealed record PlanRequestBody(string Pair, string From, string To, string Mode, string? SourceRef, string? SourceBase, string? TargetBase,
    string? Range, List<string>? Commits, List<string>? Components, bool AllowProposed, bool Offline, bool PreferLocal, string? Baseline, List<string>? Reapply);
public sealed record SimulateBody(bool TrustValidation, string? Profile, bool PreserveWorkspace);
public sealed record ApproveBody(string Candidate, List<string>? Accept);
public sealed record PublishBody(bool CreatePr);
public sealed record BundleBody(List<string> Plans, string? Name);

/// <summary>Background jobs with cancellation. Engine operations are serialized: one git-mutating
/// operation at a time against the harness mirrors.</summary>
public sealed class JobService
{
    public sealed record Job
    {
        public required string Id { get; init; }
        public required string Kind { get; init; }
        public string State { get; set; } = "running"; // running | succeeded | failed | cancelled
        public object? Result { get; set; }
        public string? Error { get; set; }
        public int? ExitCode { get; set; }
        public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? FinishedAt { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public CancellationTokenSource Cts { get; } = new();
    }

    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public object Start(string kind, Func<CancellationToken, Task<object>> work)
    {
        var job = new Job { Id = StateStore.NewId("job"), Kind = kind };
        _jobs[job.Id] = job;
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try { job.Result = await work(job.Cts.Token); job.State = "succeeded"; }
            catch (OperationCanceledException) { job.State = "cancelled"; job.ExitCode = ExitCodes.Cancelled; }
            catch (Exception ex)
            {
                job.State = "failed"; job.Error = Redactor.Redact(ex.Message);
                job.ExitCode = ex switch
                {
                    PlanningException p => p.ExitCode, StaleException or PlanIntegrityException => ExitCodes.Stale,
                    PublishRefusedException p => p.ExitCode, FreshnessException => ExitCodes.FetchFailed, HarnessSafetyException => ExitCodes.Safety, _ => ExitCodes.Internal,
                };
            }
            finally { job.FinishedAt = DateTimeOffset.UtcNow; _gate.Release(); }
        });
        return new { jobId = job.Id };
    }

    public Job? Get(string id) => _jobs.GetValueOrDefault(id);
    public IEnumerable<Job> All() => _jobs.Values.OrderByDescending(j => j.StartedAt).Take(50);
    public bool Cancel(string id) { if (!_jobs.TryGetValue(id, out var j)) return false; j.Cts.Cancel(); return true; }
}
