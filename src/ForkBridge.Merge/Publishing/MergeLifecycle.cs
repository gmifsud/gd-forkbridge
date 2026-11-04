using System.Text;
using ForkBridge.Core.Config;
using ForkBridge.Core.Infrastructure;
using ForkBridge.Core.Policy;
using ForkBridge.Core.Publishing;
using ForkBridge.Core.Storage;
using ForkBridge.Core.Validation;
using ForkBridge.Merge.Git;
using ForkBridge.Merge.Model;
using ForkBridge.Merge.Planning;
using ForkBridge.Merge.Storage;
using ForkBridge.Merge.Workspace;

namespace ForkBridge.Merge.Publishing;

/// <summary>Finalize → validate → approve → publish for one session. Every step re-verifies what it depends on;
/// nothing is carried forward on trust.</summary>
public sealed class MergeLifecycle
{
    private readonly ForkBridgeConfig _cfg;
    private readonly MergeGit _git;
    private readonly MergeStore _store;
    private readonly SessionWorkspace _ws;
    private readonly IProcessRunner _runner;
    private readonly IPullRequestProvider? _pr;

    public MergeLifecycle(ForkBridgeConfig cfg, MergeGit git, MergeStore store, SessionWorkspace ws, IProcessRunner runner, IPullRequestProvider? pr)
    {
        _cfg = cfg; _git = git; _store = store; _ws = ws; _runner = runner; _pr = pr;
    }

    private static void CheckRevision(MergeSession s, int expected)
    {
        if (s.Revision != expected)
            throw new MergeException($"The session changed since you loaded it (revision {expected} → {s.Revision}). Reload first.", MergeErrorKind.Stale);
    }

    // ---- finalize -----------------------------------------------------------------------------
    public async Task<MergeSession> FinalizeAsync(MergeSession s, MergePlan plan, int expectedRevision, CancellationToken ct)
    {
        if (s.State != SessionState.Resolving) throw new MergeException($"Only a session that is resolving can be finalized (state {s.State}).", MergeErrorKind.Refused);
        CheckRevision(s, expectedRevision);
        var unsupported = plan.Entries.Where(e => !e.Capabilities.Supported).Select(e => e.Path).ToList();
        if (unsupported.Count > 0) throw new MergeException("Unsupported conflicts block finalization: " + string.Join(", ", unsupported.Take(8)), MergeErrorKind.Unsupported);
        var integrity = await _ws.VerifyAsync(s, plan);
        if (!integrity.Ok)
        {
            s.State = SessionState.Damaged; s.StateReason = string.Join("; ", integrity.Problems);
            _store.Audit(s, "finalize refused, integrity: " + s.StateReason); _store.SaveSession(s);
            throw new MergeException("Workspace integrity check failed: " + s.StateReason, MergeErrorKind.Stale);
        }
        var unresolved = plan.Entries.Where(e => e.Status == EntryStatus.Conflicted && !s.Resolutions.ContainsKey(e.Id)).Select(e => e.Path).ToList();
        if (unresolved.Count > 0) throw new MergeException($"{unresolved.Count} conflict(s) are not resolved: " + string.Join(", ", unresolved.Take(8)), MergeErrorKind.Refused);
        if ((await _ws.UnmergedAsync(s)).Count > 0) throw new MergeException("The index still has unmerged entries.", MergeErrorKind.GitFailure);
        var pendingDrafts = s.Drafts.Keys.Select(id => plan.Entries.First(e => e.Id == id).Path).ToList();
        if (pendingDrafts.Count > 0)
            throw new MergeException("Saved drafts are newer than their resolution (mark them resolved or discard them by choosing a version): " + string.Join(", ", pendingDrafts.Take(8)), MergeErrorKind.Refused);

        var tree = (await _git.CheckedAsync(s.Worktree, ["write-tree"], ct: ct)).StdOut.Trim();
        // Integrity of everything Git merged cleanly: the candidate may differ from Git's own merge result only in conflicted paths.
        var conflictPaths = plan.Entries.Where(e => e.Status == EntryStatus.Conflicted).Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
        var diff = Encoding.UTF8.GetString((await _git.CheckedAsync(s.Worktree, ["diff-tree", "-r", "-z", "--name-only", "--no-renames", plan.ResultTree!, tree], ct: ct)).StdOutBytes)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(p => !conflictPaths.Contains(p)).ToList();
        if (diff.Count > 0) throw new MergeException("The candidate tree differs from Git's merge outside the conflicted paths: " + string.Join(", ", diff.Take(8)), MergeErrorKind.GitFailure);

        var msg = $"Merge {plan.Source.Name} into {plan.Target.Name}\n\n" +
                  $"ForkBridge-Merge-Session: {s.Id}\nForkBridge-Merge-Plan: {plan.Id}\n" +
                  $"ForkBridge-Merge-Source: {plan.Source.FullRef}@{plan.Source.Sha}\nForkBridge-Merge-Target: {plan.Target.FullRef}@{plan.Target.Sha}\n" +
                  (s.Resolutions.Count > 0 ? $"ForkBridge-Merge-Resolved: {s.Resolutions.Count} conflict(s) resolved in ForkBridge\n" : "");
        var env = SessionWorkspace.IdentityEnv(s);
        // commit-tree runs no hooks; parent order is explicit: first = target, second = source.
        var candidate = (await _git.CheckedAsync(s.Worktree, ["commit-tree", tree, "-p", plan.Target.Sha, "-p", plan.Source.Sha, "-F", "-"],
            Encoding.UTF8.GetBytes(msg), env, ct)).StdOut.Trim();
        var parents = (await _git.CheckedAsync(s.Worktree, ["rev-list", "--parents", "-n", "1", candidate], ct: ct)).StdOut.Trim().Split(' ');
        if (parents.Length != 3 || parents[0] != candidate || parents[1] != plan.Target.Sha || parents[2] != plan.Source.Sha)
            throw new MergeException("Candidate ancestry is not [target, source].", MergeErrorKind.GitFailure);
        if ((await _git.OutAsync(s.Worktree, "rev-parse", candidate + "^{tree}")) != tree)
            throw new MergeException("Candidate tree mismatch.", MergeErrorKind.GitFailure);
        using (_store.Core.Lock("mirror-" + s.RepositoryAlias))
            await _git.CheckedAsync(_ws.Mirror(s), ["update-ref", $"refs/fb/merge/sessions/{s.Id}/candidate", candidate], ct: ct);
        s.Revision++;
        s.CandidateSha = candidate; s.CandidateTree = tree; s.State = SessionState.Finalized; s.Approval = null;
        _store.Audit(s, $"finalized candidate {candidate} (tree {tree[..10]}, parents {plan.Target.Sha[..10]} {plan.Source.Sha[..10]})");
        _store.SaveSession(s);
        return s;
    }

    /// <summary>Back to resolving: the candidate, its validation binding and any approval are discarded.</summary>
    public MergeSession Reopen(MergeSession s, int expectedRevision)
    {
        if (s.State is not (SessionState.Finalized or SessionState.Approved))
            throw new MergeException($"Only a finalized or approved (unpublished) session can be reopened (state {s.State}).", MergeErrorKind.Refused);
        CheckRevision(s, expectedRevision);
        s.Revision++;
        var old = s.CandidateSha;
        s.CandidateSha = null; s.CandidateTree = null; s.Approval = null; s.State = SessionState.Resolving;
        _store.Audit(s, $"reopened; candidate {old} discarded (validation and approval invalidated)");
        _store.SaveSession(s);
        return s;
    }

    // ---- validate -----------------------------------------------------------------------------
    public async Task<MergeSession> ValidateAsync(MergeSession s, MergePlan plan, int expectedRevision, bool trustValidation, CancellationToken ct)
    {
        if (s.State != SessionState.Finalized || s.CandidateSha is null) throw new MergeException("Finalize the session before validating.", MergeErrorKind.Refused);
        CheckRevision(s, expectedRevision);
        var profile = _cfg.Merge.Repositories.GetValueOrDefault(s.RepositoryAlias)?.Validation;
        var id = StateStore.NewId("mval");
        ValidationRecord rec;
        if (profile is null || profile.Steps.Count == 0)
            rec = new ValidationRecord { Id = id, CandidateSha = s.CandidateSha, SessionRevision = s.Revision, At = DateTimeOffset.UtcNow, Verdict = "NOT_CONFIGURED", Steps = [],
                Note = $"No checks are configured for '{s.RepositoryAlias}' (merge.repositories.{s.RepositoryAlias}.validation)." };
        else if (!profile.Trusted || !trustValidation)
        {
            var results = await new ValidationRunner(_runner).RunAsync(profile, s.WorkspaceDir, "candidate", false, [], null, ct);
            rec = new ValidationRecord { Id = id, CandidateSha = s.CandidateSha, SessionRevision = s.Revision, At = DateTimeOffset.UtcNow, Verdict = "UNTRUSTED",
                Steps = results.Select(Map).ToList(),
                Note = !profile.Trusted ? "The validation profile is not marked trusted in the config; nothing was executed." : "Validation was not trusted for this request; nothing was executed." };
        }
        else
        {
            var wt = Path.Combine(s.WorkspaceDir, "validate");
            var userRepo = _cfg.ResolvePath(_cfg.Repositories[s.RepositoryAlias].Path);
            var guard = new UserRepoGuard(_git.Git);
            var before = await guard.SnapshotAsync(userRepo);
            await _ws.RemoveWorktreeAsync(s, wt);
            try
            {
                using (_store.Core.Lock("mirror-" + s.RepositoryAlias))
                    await _git.CheckedAsync(_ws.Mirror(s), ["worktree", "add", "--quiet", "--detach", wt, s.CandidateSha], ct: ct);
                var results = await new ValidationRunner(_runner).RunAsync(profile, wt, "candidate", true, [],
                    new Dictionary<string, string> { ["FORKBRIDGE_MERGE_SESSION"] = s.Id, ["FORKBRIDGE_CANDIDATE"] = s.CandidateSha }, ct);
                var after = await guard.SnapshotAsync(userRepo);
                var steps = results.Select(Map).ToList();
                var verdict = before != after ? "USER_REPO_MODIFIED"
                    : steps.Where(x => x.Required).All(x => x.Outcome is "PASSED" or "SKIPPED") ? "PASSED" : "FAILED";
                rec = new ValidationRecord { Id = id, CandidateSha = s.CandidateSha, SessionRevision = s.Revision, At = DateTimeOffset.UtcNow, Verdict = verdict, Steps = steps,
                    Note = verdict == "USER_REPO_MODIFIED" ? "Your checkout changed while validation ran. Inspect it; the candidate cannot be approved on this result." : null };
            }
            finally { await _ws.RemoveWorktreeAsync(s, wt); }
        }
        s.Validations.Add(rec);
        s.Revision++;
        _store.Audit(s, $"validation {rec.Id} on {s.CandidateSha[..10]}: {rec.Verdict}");
        _store.SaveSession(s);
        return s;
    }

    private static ValidationStepRecord Map(Core.Model.ValidationStepResult r)
        => new(r.StepId, r.Kind, r.Required, r.Outcome, r.ExitCode, r.DurationSeconds, r.Reason, r.OutputTail);

    // ---- approve / publish ------------------------------------------------------------------------
    public string IntegrationBranch(MergeSession s, MergePlan plan)
    {
        var sb = new StringBuilder();
        foreach (var c in plan.Target.Name) sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' ? c : '-');
        var safe = string.Join('/', sb.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries).Select(seg => seg.Trim('.')).Where(seg => seg.Length > 0));
        while (safe.Contains("..")) safe = safe.Replace("..", ".");
        if (safe.Length == 0) safe = "target";
        return $"{_cfg.Merge.BranchPrefix.Trim('/')}/{safe}/{s.Id}";
    }

    private async Task<string> RemoteUrlAsync(string alias)
    {
        var repo = _cfg.Repositories[alias];
        return await _git.Git.ConfigGetAsync(_cfg.ResolvePath(repo.Path), $"remote.{repo.Remote}.url")
               ?? throw new MergeException($"The checkout of '{alias}' has no remote '{repo.Remote}'.", MergeErrorKind.Refused);
    }

    private static string Binding(MergeSession s, int revision, MergePlan plan, string validationId, string remote, string branch, bool pr)
        => string.Join("\n",
            $"repository={s.RepositoryAlias}", $"remote={Redactor.Redact(remote)}", $"plan={plan.Id}", $"analysis={plan.AnalysisHash}",
            $"session={s.Id}", $"revision={revision}", $"source={plan.Source.FullRef}@{plan.Source.Sha}", $"target={plan.Target.FullRef}@{plan.Target.Sha}",
            $"candidate={s.CandidateSha}", $"validation={validationId}", $"options={plan.OptionsFingerprint}", $"branch={branch}",
            $"pullRequest={(pr ? "refs/heads/" + plan.Target.Name : "none")}");

    public async Task<MergeSession> ApproveAsync(MergeSession s, MergePlan plan, int expectedRevision, string candidatePrefix, bool createPr, bool acceptUnvalidated, string approvedBy)
    {
        if (s.State != SessionState.Finalized || s.CandidateSha is null) throw new MergeException($"Only a finalized session can be approved (state {s.State}).", MergeErrorKind.Refused);
        CheckRevision(s, expectedRevision);
        if (candidatePrefix.Length < 7 || !s.CandidateSha.StartsWith(candidatePrefix, StringComparison.OrdinalIgnoreCase))
            throw new MergeException($"Approval must name the exact candidate (at least 7 characters of {s.CandidateSha}).", MergeErrorKind.Usage);
        var v = s.Validations.LastOrDefault(x => x.CandidateSha == s.CandidateSha)
                ?? throw new MergeException("Validate this exact candidate before approving it.", MergeErrorKind.Refused);
        bool waived = false;
        if (v.Verdict != "PASSED")
        {
            if (v.Verdict is "NOT_CONFIGURED" or "UNTRUSTED" && acceptUnvalidated) waived = true;
            else throw new MergeException(v.Verdict is "FAILED" or "USER_REPO_MODIFIED"
                ? $"Validation {v.Verdict} on this candidate; it cannot be approved."
                : $"Validation result is {v.Verdict}. Confirm explicitly that you approve an unvalidated candidate.", MergeErrorKind.Refused);
        }
        var branch = IntegrationBranch(s, plan);
        RefuseProtected(branch);
        if (createPr && _pr is null) throw new MergeException("A pull request was requested but no provider is configured (publish.provider).", MergeErrorKind.Refused);
        var remote = await RemoteUrlAsync(s.RepositoryAlias);
        s.Revision++;
        var binding = Binding(s, s.Revision, plan, v.Id, remote, branch, createPr);
        s.Approval = new MergeApproval
        {
            Binding = binding, BindingHash = "sha256:" + StateStore.Sha256(binding), CandidateSha = s.CandidateSha, SessionRevision = s.Revision,
            ValidationId = v.Id, IntegrationBranch = branch, CreatePullRequest = createPr, ValidationWaived = waived, ApprovedBy = approvedBy, ApprovedAt = DateTimeOffset.UtcNow,
        };
        s.State = SessionState.Approved;
        _store.Audit(s, $"approved by {approvedBy}{(waived ? " (unvalidated, explicitly accepted)" : "")}: {s.Approval.BindingHash}");
        _store.SaveSession(s);
        return s;
    }

    private void RefuseProtected(string branch)
    {
        if (!branch.StartsWith(_cfg.Merge.BranchPrefix.Trim('/') + "/", StringComparison.Ordinal))
            throw new MergeException($"Refusing to publish '{branch}': not under the harness prefix.", MergeErrorKind.Safety);
        if (_cfg.Merge.ProtectedBranches.Any(p => Glob.HasWildcard(p) ? Glob.IsMatch(p, branch) : p == branch))
            throw new MergeException($"Refusing to publish to protected branch '{branch}'.", MergeErrorKind.Safety);
    }

    private async Task<string?> RemoteHeadAsync(string mirror, string url, string branch, CancellationToken ct)
    {
        var r = await _git.Git.RunAsync(mirror, ["ls-remote", "--heads", url, "refs/heads/" + branch], timeout: TimeSpan.FromMinutes(2), ct: ct);
        if (!r.Success) throw new MergeException("Could not query the remote: " + Redactor.Redact(MergeGit.Trim(r.StdErr)), MergeErrorKind.GitFailure);
        var line = r.StdOut.Split('\n').FirstOrDefault(l => l.EndsWith("\trefs/heads/" + branch, StringComparison.Ordinal));
        return line?.Split('\t')[0].Trim();
    }

    public async Task<MergeSession> PublishAsync(MergeSession s, MergePlan plan, int expectedRevision, CancellationToken ct)
    {
        if (s.State == SessionState.Published) return s; // idempotent: already recorded
        if (s.State != SessionState.Approved || s.Approval is null || s.CandidateSha is null)
            throw new MergeException($"Only an approved session can be published (state {s.State}).", MergeErrorKind.Refused);
        CheckRevision(s, expectedRevision);
        var a = s.Approval;
        var remote = await RemoteUrlAsync(s.RepositoryAlias);
        // Content can only change after a reopen, which discards the approval; publish attempts themselves bump the
        // revision, so the binding is recomputed at the approval's revision.
        var expected = Binding(s, a.SessionRevision, plan, a.ValidationId, remote, a.IntegrationBranch, a.CreatePullRequest);
        if (a.CandidateSha != s.CandidateSha || "sha256:" + StateStore.Sha256(expected) != a.BindingHash)
            throw new MergeException("The approval no longer matches the session (candidate, revision, remote, branch or options changed). Approve again.", MergeErrorKind.Stale);
        RefuseProtected(a.IntegrationBranch);
        var mirror = _ws.Mirror(s);

        var remoteTarget = await RemoteHeadAsync(mirror, remote, plan.Target.Name, ct);
        if (remoteTarget is null)
            throw new MergeException($"The target branch '{plan.Target.Name}' does not exist on the remote; cannot publish an integration branch for it.", MergeErrorKind.Refused);
        if (remoteTarget != plan.Target.Sha)
            throw new MergeException($"The target branch moved on the remote ({plan.Target.Sha[..10]} → {remoteTarget[..10]}). The approved merge is stale: analyze again.", MergeErrorKind.Stale);
        string? sourceMoved = null;
        if (plan.Source.Namespace != RefNamespace.Local)
        {
            var rs = await RemoteHeadAsync(mirror, remote, plan.Source.Name, ct);
            if (rs is not null && rs != plan.Source.Sha) sourceMoved = rs;
        }

        var existing = await RemoteHeadAsync(mirror, remote, a.IntegrationBranch, ct);
        MergePublication rec;
        if (existing is not null && existing != s.CandidateSha)
            throw new MergeException($"Remote branch '{a.IntegrationBranch}' already exists at a different commit. ForkBridge never force-pushes.", MergeErrorKind.Refused);
        if (existing == s.CandidateSha)
            rec = new MergePublication { At = DateTimeOffset.UtcNow, Branch = a.IntegrationBranch, CandidateSha = s.CandidateSha, Pushed = true, AlreadyPresent = true, SourceMovedTo = sourceMoved };
        else
        {
            // No '+' in the refspec: git itself rejects anything but creating the branch. No hooks run.
            var push = await _git.Git.RunAsync(mirror, ["-c", "core.hooksPath=" + _git.HooksDir, "push", "--porcelain", "--no-verify", remote,
                $"{s.CandidateSha}:refs/heads/{a.IntegrationBranch}"], timeout: TimeSpan.FromMinutes(15), ct: ct);
            rec = new MergePublication { At = DateTimeOffset.UtcNow, Branch = a.IntegrationBranch, CandidateSha = s.CandidateSha, Pushed = push.Success,
                SourceMovedTo = sourceMoved, Error = push.Success ? null : Redactor.Redact(MergeGit.Trim(push.StdErr)) };
        }
        if (rec.Pushed && a.CreatePullRequest && !s.Publications.Any(p => p.PullRequestUrl is not null))
        {
            try
            {
                var desc = $"ForkBridge merge of `{plan.Source.Name}` ({plan.Source.Sha[..10]}) into `{plan.Target.Name}` ({plan.Target.Sha[..10]}).\n\n" +
                           $"Session `{s.Id}` · Plan `{plan.Id}` · Candidate `{s.CandidateSha}`\n\n{s.Resolutions.Count} conflict(s) resolved; validation `{a.ValidationId}`{(a.ValidationWaived ? " (waived)" : "")}.";
                var url = await _pr!.CreateAsync(remote, a.IntegrationBranch, plan.Target.Name, $"[ForkBridge] Merge {plan.Source.Name} into {plan.Target.Name}", desc, _cfg.Publish.Draft, ct);
                rec = rec with { PullRequestUrl = url };
            }
            catch (Exception ex) { rec = rec with { Error = "Branch published; pull request creation failed: " + Redactor.Redact(ex.Message) }; }
        }
        s.Publications.Add(rec);
        s.Revision++;
        if (rec.Pushed) s.State = SessionState.Published;
        _store.Audit(s, rec.Pushed ? $"published {rec.Branch}{(rec.AlreadyPresent ? " (already present: retry)" : "")}{(rec.PullRequestUrl is null ? "" : " PR " + rec.PullRequestUrl)}" : "publish failed: " + rec.Error);
        _store.SaveSession(s);
        if (!rec.Pushed) throw new MergeException("Push failed: " + rec.Error, MergeErrorKind.GitFailure);
        return s;
    }
}
