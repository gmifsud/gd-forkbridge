using System.Text;
using ForkBridge.Core.Config;
using ForkBridge.Core.Git;
using ForkBridge.Core.Infrastructure;
using ForkBridge.Core.Storage;
using ForkBridge.Merge.Git;
using ForkBridge.Merge.Model;
using ForkBridge.Merge.Planning;
using ForkBridge.Merge.Storage;

namespace ForkBridge.Merge.Workspace;

/// <summary>The executable side of a merge: a detached harness worktree of the alias mirror at the pinned target,
/// into which the pinned source is merged with `git merge --no-ff --no-commit` (pinned configuration, isolated
/// environment, no hooks). The unmerged index entries Git leaves behind are the source of truth for conflicts.
/// Resolutions are recorded as blobs, so a missing or tampered workspace is rebuilt exactly by replaying them.</summary>
public sealed class SessionWorkspace
{
    private readonly ForkBridgeConfig _cfg;
    private readonly MergeGit _git;
    private readonly MergeStore _store;

    public SessionWorkspace(ForkBridgeConfig cfg, MergeGit git, MergeStore store)
    {
        _cfg = cfg; _git = git; _store = store;
    }

    public string Mirror(MergeSession s) => _store.Core.MirrorDir(s.RepositoryAlias);

    public async Task<MergeSession> CreateAsync(string planId, CancellationToken ct)
    {
        await _git.EnsureSupportedVersionAsync();
        var plan = _store.LoadPlan(planId);
        switch (plan.Outcome)
        {
            case MergeOutcome.UpToDate: throw new MergeException("Nothing to merge: the source is already contained in the target.", MergeErrorKind.Refused);
            case MergeOutcome.Blocked: throw new MergeException("The analysis is blocked: " + plan.BlockedReason, MergeErrorKind.Refused);
        }
        var mirror = _store.Core.MirrorDir(plan.RepositoryAlias);
        foreach (var sha in new[] { plan.Source.Sha, plan.Target.Sha })
            if (!(await _git.RunAsync(mirror, ["cat-file", "-e", sha + "^{commit}"])).Success)
                throw new MergeException($"Commit {sha[..10]} is no longer in the harness mirror. Analyze again.", MergeErrorKind.Stale);

        var id = StateStore.NewId("msess");
        var dir = _store.SessionDir(id);
        _store.Core.Fs.CreateOwnedDirectory(dir, id);
        var user = _cfg.ResolvePath(_cfg.Repositories[plan.RepositoryAlias].Path);
        var name = await _git.Git.ConfigGetAsync(user, "user.name");
        var email = await _git.Git.ConfigGetAsync(user, "user.email");
        var s = new MergeSession
        {
            Id = id, PlanId = plan.Id, RepositoryAlias = plan.RepositoryAlias, WorkspaceDir = dir, Worktree = Path.Combine(dir, "workspace"),
            CreatedAt = DateTimeOffset.UtcNow, State = SessionState.Creating,
            CommitterName = string.IsNullOrWhiteSpace(name) ? "ForkBridge Merge" : name.Trim(),
            CommitterEmail = string.IsNullOrWhiteSpace(email) ? "forkbridge@localhost" : email.Trim(),
        };
        _store.SaveSession(s);
        _store.Audit(s, $"created from plan {plan.Id}: {plan.Source.Name}@{plan.Source.Sha[..10]} into {plan.Target.Name}@{plan.Target.Sha[..10]}");
        using (_store.LockSession(id))
        {
            try
            {
                await BuildAsync(s, plan, replay: false, ct);
            }
            catch (OperationCanceledException)
            {
                s.StateReason = "Creation was cancelled; use Recover to rebuild the workspace.";
                _store.Audit(s, "cancelled during workspace creation");
                _store.SaveSession(s);
                throw;
            }
            catch (MergeException ex) when (ex.Kind != MergeErrorKind.GitFailure)
            {
                s.State = SessionState.Damaged; s.StateReason = ex.Message;
                _store.Audit(s, "creation failed: " + ex.Message);
                _store.SaveSession(s);
                throw;
            }
        }
        return s;
    }

    /// <summary>(Re)creates the worktree, merges, verifies the unmerged set matches the analysis and replays recorded
    /// resolutions. Caller holds the session lock.</summary>
    internal async Task BuildAsync(MergeSession s, MergePlan plan, bool replay, CancellationToken ct)
    {
        var mirror = Mirror(s);
        await RemoveWorktreeAsync(s);
        using (_store.Core.Lock("mirror-" + s.RepositoryAlias))
        {
            await _git.CheckedAsync(mirror, ["worktree", "add", "--quiet", "--detach", s.Worktree, plan.Target.Sha], ct: ct);
            await _git.CheckedAsync(mirror, ["update-ref", $"refs/fb/merge/sessions/{s.Id}/target", plan.Target.Sha], ct: ct);
            await _git.CheckedAsync(mirror, ["update-ref", $"refs/fb/merge/sessions/{s.Id}/source", plan.Source.Sha], ct: ct);
        }
        var merge = await _git.RunAsync(s.Worktree, ["merge", "--no-ff", "--no-commit", "--no-verify", "--no-edit", "--no-log",
            "--strategy=ort", "--no-allow-unrelated-histories", plan.Source.Sha], extraEnv: IdentityEnv(s), ct: ct);
        if (merge.ExitCode is not (0 or 1))
            throw new MergeException("git merge failed in the session workspace (not a conflict): " + Redactor.Redact(MergeGit.Trim(merge.StdErr + "\n" + merge.StdOut)), MergeErrorKind.GitFailure);
        var mh = await _git.TryRevParseAsync(s.Worktree, "MERGE_HEAD");
        if (mh != plan.Source.Sha) throw new MergeException("The workspace is not in the expected merge state (MERGE_HEAD missing or different).", MergeErrorKind.GitFailure);

        var unmerged = (await UnmergedAsync(s)).Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
        var expected = plan.Entries.Where(e => e.Status == EntryStatus.Conflicted).Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
        if (!unmerged.SetEquals(expected))
            throw new MergeException("The session merge does not match the analysis (different conflicted paths: " +
                string.Join(", ", unmerged.Except(expected).Concat(expected.Except(unmerged)).Take(6)) + "). Analyze again.", MergeErrorKind.Stale);

        if (replay)
            foreach (var r in s.Resolutions.Values.OrderBy(r => r.Revision))
                await ApplyIndexResultAsync(s, r.Path, r.Result, ct);
        await _git.RunAsync(s.Worktree, ["update-index", "-q", "--refresh"], ct: ct);
        s.State = SessionState.Resolving;
        s.StateReason = null;
        _store.Audit(s, replay ? $"workspace rebuilt; {s.Resolutions.Count} resolution(s) replayed" : $"workspace ready; {expected.Count} conflict(s)");
        _store.SaveSession(s);
    }

    /// <summary>The isolated environment has no user identity; the session's (read from the checkout at creation) is explicit.</summary>
    public static IReadOnlyDictionary<string, string?> IdentityEnv(MergeSession s) => new Dictionary<string, string?>
    {
        ["GIT_AUTHOR_NAME"] = s.CommitterName, ["GIT_AUTHOR_EMAIL"] = s.CommitterEmail,
        ["GIT_COMMITTER_NAME"] = s.CommitterName, ["GIT_COMMITTER_EMAIL"] = s.CommitterEmail,
    };

    public async Task<List<StageEntry>> UnmergedAsync(MergeSession s)
        => MergeTreeParser.ParseIndex((await _git.CheckedAsync(s.Worktree, ["ls-files", "-u", "-z"])).StdOutBytes);

    public async Task<List<StageEntry>> IndexEntriesAsync(MergeSession s, string path)
        => MergeTreeParser.ParseIndex((await _git.CheckedAsync(s.Worktree, ["ls-files", "-s", "-z", "--", path])).StdOutBytes);

    /// <summary>Sets the merged (stage 0) index entry of one path, or removes it, then materializes the worktree file
    /// through git itself (checkout-index / rm) so no client-influenced filesystem path is ever written directly.</summary>
    internal async Task ApplyIndexResultAsync(MergeSession s, string path, SideBlob? result, CancellationToken ct)
    {
        if (EntryClassifier.PathProblem(path) is { } p) throw new HarnessSafetyException($"Refusing to write '{path}': {p}.");
        _store.Core.Fs.AssertNoLinksBetweenRootAndPath(Path.Combine(s.Worktree, path.Replace('/', Path.DirectorySeparatorChar)));
        if (result is null)
        {
            await _git.CheckedAsync(s.Worktree, ["rm", "--quiet", "--force", "--ignore-unmatch", "--", path], ct: ct);
            // A conflicted path may exist only in the index; make sure nothing is left at stage 0..3.
            await _git.CheckedAsync(s.Worktree, ["update-index", "--force-remove", "--", path], ct: ct);
        }
        else
        {
            if (result.Mode is not ("100644" or "100755")) throw new MergeException($"Refusing to stage mode {result.Mode} for '{path}'.", MergeErrorKind.Refused);
            var info = Encoding.UTF8.GetBytes($"0 {new string('0', result.Sha.Length)}\t{path}\n{result.Mode} {result.Sha} 0\t{path}\n");
            await _git.CheckedAsync(s.Worktree, ["update-index", "--index-info"], info, ct: ct);
            await _git.CheckedAsync(s.Worktree, ["checkout-index", "--force", "--quiet", "--", path], ct: ct);
        }
        var now = await IndexEntriesAsync(s, path);
        var ok = result is null ? now.Count == 0 : now.Count == 1 && now[0].Stage == 0 && now[0].Sha == result.Sha && now[0].Mode == result.Mode;
        if (!ok) throw new MergeException($"The index entry for '{path}' is not what was requested after staging; the workspace is inconsistent.", MergeErrorKind.GitFailure);
    }

    public sealed record Integrity(bool Ok, List<string> Problems);

    /// <summary>Checks the workspace is exactly what the session recorded: ownership, HEAD = target, MERGE_HEAD = source,
    /// unmerged set = conflicts minus resolutions, every resolution in the index, no unstaged or untracked changes.</summary>
    public async Task<Integrity> VerifyAsync(MergeSession s, MergePlan plan)
    {
        var problems = new List<string>();
        if (_store.Core.Fs.ReadOwner(s.WorkspaceDir) != s.Id) problems.Add("session directory ownership marker missing or different");
        if (!Directory.Exists(s.Worktree) || !File.Exists(Path.Combine(s.Worktree, ".git"))) { problems.Add("workspace is missing"); return new(false, problems); }
        if (SafeFileSystem.IsLink(s.Worktree)) { problems.Add("workspace is a link"); return new(false, problems); }
        var head = await _git.TryRevParseAsync(s.Worktree, "HEAD");
        if (head != plan.Target.Sha) problems.Add($"HEAD is {head?[..10] ?? "missing"}, expected target {plan.Target.Sha[..10]}");
        var mh = await _git.TryRevParseAsync(s.Worktree, "MERGE_HEAD");
        if (mh != plan.Source.Sha) problems.Add("MERGE_HEAD is missing or not the pinned source");
        if (problems.Count > 0) return new(false, problems);

        var unmerged = (await UnmergedAsync(s)).Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
        var resolvedPaths = s.Resolutions.Values.Select(r => r.Path).ToHashSet(StringComparer.Ordinal);
        var expected = plan.Entries.Where(e => e.Status == EntryStatus.Conflicted && !resolvedPaths.Contains(e.Path)).Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
        if (!unmerged.SetEquals(expected)) problems.Add("unmerged entries differ from the session record: " + string.Join(", ", unmerged.Except(expected).Concat(expected.Except(unmerged)).Take(6)));
        foreach (var r in s.Resolutions.Values)
        {
            var e = await IndexEntriesAsync(s, r.Path);
            var ok = r.Result is null ? e.Count == 0 : e.Count == 1 && e[0].Stage == 0 && e[0].Sha == r.Result.Sha && e[0].Mode == r.Result.Mode;
            if (!ok) problems.Add($"index entry of resolved '{r.Path}' differs from the recorded resolution");
        }
        await _git.RunAsync(s.Worktree, ["update-index", "-q", "--refresh"]);
        var dirty = Encoding.UTF8.GetString((await _git.CheckedAsync(s.Worktree, ["diff-files", "--name-only", "-z"])).StdOutBytes)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(p => !unmerged.Contains(p)).ToList();
        if (dirty.Count > 0) problems.Add("unstaged changes in the workspace: " + string.Join(", ", dirty.Take(6)));
        var untracked = Encoding.UTF8.GetString((await _git.CheckedAsync(s.Worktree, ["ls-files", "--others", "-z", "--", "."])).StdOutBytes)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (untracked.Count > 0) problems.Add("untracked files in the workspace: " + string.Join(", ", untracked.Take(6)));
        return new(problems.Count == 0, problems);
    }

    /// <summary>Removes the session worktree (registration first, then the directory through SafeFileSystem with the
    /// session's ownership check). Caller holds the session lock.</summary>
    internal async Task RemoveWorktreeAsync(MergeSession s, string? path = null)
    {
        var wt = path ?? s.Worktree;
        var mirror = Mirror(s);
        if (_store.Core.Fs.ReadOwner(s.WorkspaceDir) != s.Id)
            throw new HarnessSafetyException($"Refusing to remove '{wt}': session directory is not owned by {s.Id}.");
        using (_store.Core.Lock("mirror-" + s.RepositoryAlias))
        {
            await _git.RunAsync(mirror, ["worktree", "unlock", wt]);
            await _git.RunAsync(mirror, ["worktree", "remove", "--force", "--force", wt]);
            _store.Core.Fs.DeleteInsideOwned(wt, s.WorkspaceDir, s.Id);
            await _git.RunAsync(mirror, ["worktree", "prune"]);
        }
    }

    public async Task DeleteSessionRefsAsync(MergeSession s, bool keepCandidate)
    {
        var mirror = Mirror(s);
        using var _ = _store.Core.Lock("mirror-" + s.RepositoryAlias);
        foreach (var r in new[] { "source", "target", "candidate" })
            if (!(keepCandidate && r == "candidate"))
                await _git.RunAsync(mirror, ["update-ref", "-d", $"refs/fb/merge/sessions/{s.Id}/{r}"]);
    }
}
