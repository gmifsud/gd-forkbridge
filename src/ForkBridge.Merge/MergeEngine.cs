using ForkBridge.Core;
using ForkBridge.Core.Config;
using ForkBridge.Core.Git;
using ForkBridge.Core.Infrastructure;
using ForkBridge.Core.Publishing;
using ForkBridge.Merge.Git;
using ForkBridge.Merge.Model;
using ForkBridge.Merge.Planning;
using ForkBridge.Merge.Publishing;
using ForkBridge.Merge.Resolution;
using ForkBridge.Merge.Storage;
using ForkBridge.Merge.Workspace;

namespace ForkBridge.Merge;

public sealed record RepositorySummary(string Alias, string Kind, bool Placeholder, bool MirrorExists, RefFreshness Freshness,
    bool ValidationConfigured, bool ValidationTrusted, string Remote);

public sealed record SessionEntryState(string EntryId, string Path, EntryStatus Status, ConflictKind Conflict, bool Supported,
    string Resolution, ResolutionAction? Action, bool HasDraft);

/// <summary>A session with its plan and the per-entry resolution state, as the UI needs it.</summary>
public sealed record SessionView(MergeSession Session, MergePlan Plan, List<SessionEntryState> Entries, int Unresolved, int Resolved,
    int Unsupported, bool CanFinalize, string? IntegrationBranch, bool PullRequestProviderConfigured, ValidationRecord? CurrentValidation);

/// <summary>Façade of the branch merge workspace, shared by the web host (and a future CLI). Every session mutation
/// takes the per-session file lock and reloads the session inside it, so concurrent tabs or processes see each
/// other's revisions; the web host additionally runs mutations through its single job gate.</summary>
public sealed class MergeEngine
{
    public ForkBridgeConfig Config { get; }
    public MergeGit Git { get; }
    public MergeStore Store { get; }
    public RefCatalog Refs { get; }
    public MergeAnalyzer Analyzer { get; }
    public SessionWorkspace Workspace { get; }
    public ResolutionService Resolutions { get; }
    public MergeLifecycle Lifecycle { get; }
    private readonly IPullRequestProvider? _pr;

    public MergeEngine(ForkBridgeEngine core, IProcessRunner? runner = null, IPullRequestProvider? pr = null)
    {
        Config = core.Config;
        Git = new MergeGit(core.Git, core.Store);
        Store = new MergeStore(core.Store);
        Refs = new RefCatalog(Config, Git, core.Mirrors, Store);
        Analyzer = new MergeAnalyzer(Config, Git, Store, Refs);
        Workspace = new SessionWorkspace(Config, Git, Store);
        Resolutions = new ResolutionService(Config, Git, Store, Workspace);
        _pr = pr ?? (Config.Publish.Provider == "azure-devops" ? new AzureDevOpsPullRequestProvider(core.Git, core.Store.Root) : null);
        Lifecycle = new MergeLifecycle(Config, Git, Store, Workspace, runner ?? new ProcessRunner(), _pr);
    }

    public List<RepositorySummary> Repositories() => Config.Repositories.Select(kv =>
    {
        var v = Config.Merge.Repositories.GetValueOrDefault(kv.Key)?.Validation;
        return new RepositorySummary(kv.Key, kv.Value.Kind, kv.Value.Placeholder,
            Directory.Exists(Path.Combine(Store.Core.MirrorDir(kv.Key), "objects")),
            Store.LoadFreshness(kv.Key, RefCatalog.StaleAfter), v is { Steps.Count: > 0 }, v?.Trusted == true, kv.Value.Remote);
    }).OrderBy(r => r.Alias, StringComparer.Ordinal).ToList();

    public Task<MergeSession> CreateSessionAsync(string planId, CancellationToken ct) => Workspace.CreateAsync(planId, ct);

    public SessionView View(string sessionId)
    {
        var s = Store.LoadSession(sessionId);
        return View(s, Store.LoadPlan(s.PlanId));
    }

    private SessionView View(MergeSession s, MergePlan plan)
    {
        var entries = plan.Entries.Select(e =>
        {
            var r = s.Resolutions.GetValueOrDefault(e.Id);
            var draft = s.Drafts.ContainsKey(e.Id);
            var state = e.Status != EntryStatus.Conflicted ? "clean" : !e.Capabilities.Supported ? "unsupported" : r is not null ? "resolved" : draft ? "draft" : "unresolved";
            return new SessionEntryState(e.Id, e.Path, e.Status, e.Conflict, e.Capabilities.Supported, state, r?.Action, draft);
        }).ToList();
        int unresolved = entries.Count(e => e.Resolution is "unresolved" or "draft");
        int unsupported = entries.Count(e => e.Resolution == "unsupported");
        var current = s.CandidateSha is null ? null : s.Validations.LastOrDefault(v => v.CandidateSha == s.CandidateSha);
        return new SessionView(s, plan, entries, unresolved, entries.Count(e => e.Resolution == "resolved"), unsupported,
            s.State == SessionState.Resolving && unresolved == 0 && unsupported == 0 && s.Drafts.Count == 0,
            s.State is SessionState.Aborted ? null : Lifecycle.IntegrationBranch(s, plan), _pr is not null, current);
    }

    /// <summary>Runs one mutation under the session lock on a freshly loaded session.</summary>
    private async Task<SessionView> MutateAsync(string sessionId, Func<MergeSession, MergePlan, Task<MergeSession>> f)
    {
        using var _ = Store.LockSession(sessionId);
        var s = Store.LoadSession(sessionId);
        var plan = Store.LoadPlan(s.PlanId);
        s = await f(s, plan);
        return View(s, plan);
    }

    public Task<FileView> FileAsync(string sessionId, string entryId, CancellationToken ct)
    {
        var s = Store.LoadSession(sessionId);
        return Resolutions.GetAsync(s, Store.LoadPlan(s.PlanId), entryId, ct);
    }

    public Task<SessionView> SaveDraftAsync(string sessionId, SaveDraftRequest r, CancellationToken ct)
        => MutateAsync(sessionId, (s, p) => Resolutions.SaveDraftAsync(s, p, r, ct));

    public Task<SessionView> ResolveAsync(string sessionId, ResolveRequest r, CancellationToken ct)
        => MutateAsync(sessionId, (s, p) => Resolutions.ResolveAsync(s, p, r, ct));

    public Task<SessionView> FinalizeAsync(string sessionId, int rev, CancellationToken ct)
        => MutateAsync(sessionId, (s, p) => Lifecycle.FinalizeAsync(s, p, rev, ct));

    public Task<SessionView> ReopenAsync(string sessionId, int rev)
        => MutateAsync(sessionId, (s, _) => Task.FromResult(Lifecycle.Reopen(s, rev)));

    public Task<SessionView> ValidateAsync(string sessionId, int rev, bool trust, CancellationToken ct)
        => MutateAsync(sessionId, (s, p) => Lifecycle.ValidateAsync(s, p, rev, trust, ct));

    public Task<SessionView> ApproveAsync(string sessionId, int rev, string candidate, bool createPr, bool acceptUnvalidated, string by)
        => MutateAsync(sessionId, (s, p) => Lifecycle.ApproveAsync(s, p, rev, candidate, createPr, acceptUnvalidated, by));

    public Task<SessionView> PublishAsync(string sessionId, int rev, CancellationToken ct)
        => MutateAsync(sessionId, (s, p) => Lifecycle.PublishAsync(s, p, rev, ct));

    /// <summary>Rebuilds a missing, half-created or tampered workspace: new worktree at the pinned target, the same
    /// merge, then every recorded resolution replayed from its blob. A finalized candidate survives only if the
    /// rebuilt tree is identical; otherwise the candidate, validation and approval are discarded.</summary>
    public Task<SessionView> RecoverAsync(string sessionId, int rev, CancellationToken ct) => MutateAsync(sessionId, async (s, plan) =>
    {
        if (s.State is SessionState.Aborted or SessionState.Published) throw new MergeException($"A {s.State} session cannot be recovered.", MergeErrorKind.Refused);
        if (s.Revision != rev) throw new MergeException($"The session changed since you loaded it (revision {rev} → {s.Revision}). Reload first.", MergeErrorKind.Stale);
        var (candidate, tree, approval, state) = (s.CandidateSha, s.CandidateTree, s.Approval, s.State);
        await Workspace.BuildAsync(s, plan, replay: true, ct);
        s.Revision++;
        if (candidate is not null && tree is not null && state is SessionState.Finalized or SessionState.Approved)
        {
            var rebuilt = (await Git.CheckedAsync(s.Worktree, ["write-tree"], ct: ct)).StdOut.Trim();
            if (rebuilt == tree) { s.CandidateSha = candidate; s.CandidateTree = tree; s.Approval = approval; s.State = state; }
            else { s.CandidateSha = null; s.CandidateTree = null; s.Approval = null; Store.Audit(s, "recovered tree differs from the candidate; candidate discarded"); }
        }
        Store.Audit(s, "recovered");
        Store.SaveSession(s);
        return s;
    });

    /// <summary>Aborts: removes the harness worktree(s) after an ownership check and drops the session refs. The
    /// session record, drafts and audit log are kept. A published session keeps its candidate ref.</summary>
    public Task<SessionView> AbortAsync(string sessionId, int rev) => MutateAsync(sessionId, async (s, _) =>
    {
        if (s.Revision != rev) throw new MergeException($"The session changed since you loaded it (revision {rev} → {s.Revision}). Reload first.", MergeErrorKind.Stale);
        await Workspace.RemoveWorktreeAsync(s);
        await Workspace.RemoveWorktreeAsync(s, Path.Combine(s.WorkspaceDir, "validate"));
        await Workspace.DeleteSessionRefsAsync(s, keepCandidate: s.State == SessionState.Published);
        s.Revision++;
        if (s.State != SessionState.Published) s.State = SessionState.Aborted;
        Store.Audit(s, s.State == SessionState.Published ? "workspace cleaned up (published session)" : "aborted; workspace removed");
        Store.SaveSession(s);
        return s;
    });

    /// <summary>Light integrity check for display (no state change). A session left in Creating by a crash or
    /// cancellation reports that it needs recovery.</summary>
    public async Task<(bool Ok, List<string> Problems)> CheckAsync(string sessionId)
    {
        var s = Store.LoadSession(sessionId);
        if (s.State is SessionState.Aborted or SessionState.Published) return (true, []);
        if (s.State == SessionState.Creating) return (false, ["workspace creation did not complete (interrupted or cancelled)"]);
        var r = await Workspace.VerifyAsync(s, Store.LoadPlan(s.PlanId));
        return (r.Ok, r.Problems);
    }
}
