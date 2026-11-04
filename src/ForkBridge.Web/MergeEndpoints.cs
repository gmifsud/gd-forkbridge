using ForkBridge.Core;
using ForkBridge.Core.Storage;
using ForkBridge.Merge;
using ForkBridge.Merge.Git;
using ForkBridge.Merge.Model;
using ForkBridge.Merge.Resolution;

namespace ForkBridge.Web;

public sealed record MergeRefreshBody(bool Offline);
public sealed record MergePlanBody(string Alias, RefSelection Source, RefSelection Target);
public sealed record MergeRevisionBody(int ExpectedSessionRevision);
public sealed record MergeValidateBody(int ExpectedSessionRevision, bool TrustValidation);
public sealed record MergeApproveBody(int ExpectedSessionRevision, string Candidate, bool CreatePullRequest, bool AcceptUnvalidated);

/// <summary>/api/merge/* - the branch merge workspace. Same security model as the rest of the API (loopback,
/// per-launch token, JSON-only POSTs). Inputs are configured repository aliases, a ref namespace + branch name that
/// must exist exactly in the harness mirror, and harness ids (plans, sessions, entries) - never filesystem paths or
/// git arguments. Long operations run as jobs; every session mutation runs through the same job gate as the
/// background jobs AND takes the per-session lock inside the engine, so concurrent tabs and processes are serialized.</summary>
public static class MergeEndpoints
{
    public static void Map(WebApplication app, ForkBridgeEngine core, JobService jobs)
    {
        var api = app.MapGroup("/api/merge");
        MergeEngine M() => new(core);
        static string Id(string s) => StateStore.Sanitize(s);
        string By() => Environment.UserName + " (local UI)";

        api.MapGet("/repositories", () => M().Repositories());
        api.MapGet("/repositories/{alias}/refs", async (string alias) => await M().Refs.ListAsync(alias));
        api.MapPost("/repositories/{alias}/refresh", (string alias, MergeRefreshBody b) =>
        {
            M().Refs.Repo(alias); // unknown alias -> 404 before a job starts
            return jobs.Start($"refresh refs {alias}", async ct => (object)await M().Refs.RefreshAsync(alias, b.Offline, ct));
        });

        api.MapPost("/plans", (MergePlanBody b) =>
        {
            if (b.Source is null || b.Target is null) throw new MergeException("source and target are required.", MergeErrorKind.Usage);
            if (b.Source.Namespace == b.Target.Namespace && b.Source.Name == b.Target.Name)
                throw new MergeException("Source and target are the same branch.", MergeErrorKind.Usage);
            M().Refs.Repo(b.Alias);
            return jobs.Start($"analyze {b.Source.Name} into {b.Target.Name}", async ct =>
            {
                var p = await M().Analyzer.AnalyzeAsync(b.Alias, b.Source, b.Target, ct);
                return new { planId = p.Id, outcome = p.Outcome };
            });
        });
        api.MapGet("/plans", (string? alias) => M().Store.ListPlans(alias).Take(50).Select(p => new
        {
            p.Id, p.CreatedAt, p.RepositoryAlias, source = p.Source, target = p.Target, p.Outcome, p.Counts,
        }));
        api.MapGet("/plans/{planId}", (string planId) => M().Store.LoadPlan(Id(planId)));
        api.MapGet("/plans/{planId}/sessions", (string planId) => M().Store.ListSessions().Where(s => s.PlanId == planId)
            .Select(s => new { s.Id, s.State, s.Revision, s.CreatedAt, s.CandidateSha }));
        api.MapPost("/plans/{planId}/sessions", (string planId, EmptyBody _) =>
        {
            var id = Id(planId);
            M().Store.LoadPlan(id);
            return jobs.Start($"create merge session {id}", async ct => new { sessionId = (await M().CreateSessionAsync(id, ct)).Id });
        });

        api.MapGet("/sessions", () => M().Store.ListSessions().Take(50).Select(s => new { s.Id, s.PlanId, s.RepositoryAlias, s.State, s.Revision, s.CreatedAt, s.CandidateSha }));
        api.MapGet("/sessions/{sessionId}", async (string sessionId) =>
        {
            var m = M();
            var id = Id(sessionId);
            var (ok, problems) = await m.CheckAsync(id);
            return new { view = m.View(id), integrity = new { ok, problems } };
        });
        api.MapGet("/sessions/{sessionId}/files/{fileId}", async (string sessionId, string fileId, CancellationToken ct)
            => await M().FileAsync(Id(sessionId), Id(fileId), ct));

        api.MapPost("/sessions/{sessionId}/drafts", async (string sessionId, SaveDraftRequest b, CancellationToken ct)
            => await jobs.RunExclusiveAsync(t => M().SaveDraftAsync(Id(sessionId), b with { EntryId = Id(b.EntryId) }, t), ct));
        api.MapPost("/sessions/{sessionId}/resolutions", async (string sessionId, ResolveRequest b, CancellationToken ct)
            => await jobs.RunExclusiveAsync(t => M().ResolveAsync(Id(sessionId), b with { EntryId = Id(b.EntryId) }, t), ct));
        api.MapPost("/sessions/{sessionId}/finalize", async (string sessionId, MergeRevisionBody b, CancellationToken ct)
            => await jobs.RunExclusiveAsync(t => M().FinalizeAsync(Id(sessionId), b.ExpectedSessionRevision, t), ct));
        api.MapPost("/sessions/{sessionId}/reopen", async (string sessionId, MergeRevisionBody b, CancellationToken ct)
            => await jobs.RunExclusiveAsync(_ => M().ReopenAsync(Id(sessionId), b.ExpectedSessionRevision), ct));
        api.MapPost("/sessions/{sessionId}/validate", (string sessionId, MergeValidateBody b)
            => jobs.Start($"validate merge {sessionId}", async ct => (object)await M().ValidateAsync(Id(sessionId), b.ExpectedSessionRevision, b.TrustValidation, ct)));
        api.MapPost("/sessions/{sessionId}/approve", async (string sessionId, MergeApproveBody b, CancellationToken ct)
            => await jobs.RunExclusiveAsync(_ => M().ApproveAsync(Id(sessionId), b.ExpectedSessionRevision, b.Candidate ?? "", b.CreatePullRequest, b.AcceptUnvalidated, By()), ct));
        api.MapPost("/sessions/{sessionId}/publish", (string sessionId, MergeRevisionBody b)
            => jobs.Start($"publish merge {sessionId}", async ct => (object)await M().PublishAsync(Id(sessionId), b.ExpectedSessionRevision, ct)));
        api.MapPost("/sessions/{sessionId}/recover", (string sessionId, MergeRevisionBody b)
            => jobs.Start($"recover merge {sessionId}", async ct => (object)await M().RecoverAsync(Id(sessionId), b.ExpectedSessionRevision, ct)));
        api.MapPost("/sessions/{sessionId}/abort", async (string sessionId, MergeRevisionBody b, CancellationToken ct)
            => await jobs.RunExclusiveAsync(_ => M().AbortAsync(Id(sessionId), b.ExpectedSessionRevision), ct));
    }

    public static (int Exit, int Status) Map(MergeException ex) => ex.Kind switch
    {
        MergeErrorKind.Usage => (Core.Planning.ExitCodes.UsageError, 400),
        MergeErrorKind.NotFound => (Core.Planning.ExitCodes.UsageError, 404),
        MergeErrorKind.Stale => (Core.Planning.ExitCodes.Stale, 409),
        MergeErrorKind.Safety => (Core.Planning.ExitCodes.Safety, 403),
        MergeErrorKind.Unsupported => (Core.Planning.ExitCodes.Blocked, 422),
        MergeErrorKind.GitFailure => (Core.Planning.ExitCodes.Internal, 500),
        _ => (Core.Planning.ExitCodes.Blocked, 409),
    };
}
