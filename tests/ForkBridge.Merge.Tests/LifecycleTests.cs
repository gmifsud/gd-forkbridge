using ForkBridge.Merge.Git;
using ForkBridge.Merge.Model;
using ForkBridge.Merge.Resolution;

namespace ForkBridge.Merge.Tests;

public class LifecycleTests
{
    private static async Task<(MergeEngine M, MergePlan Plan, MergeSession S)> TwoConflicts(MergeFixture fx)
    {
        fx.OnBranch("feature/x", "develop", "src", ("src/shared.txt", "one\nSRC\nthree\nfour\nfive\n"), ("src/keep.txt", "keep SRC\n"));
        fx.OnBranch("develop", "develop", "tgt", ("src/shared.txt", "one\nTGT\nthree\nfour\nfive\n"), ("src/keep.txt", "keep TGT\n"));
        var (_, m) = fx.Engines();
        var plan = await fx.AnalyzeAsync(m, "feature/x", "develop");
        return (m, plan, await m.CreateSessionAsync(plan.Id, CancellationToken.None));
    }

    private static async Task<SessionView> Edit(MergeEngine m, MergePlan plan, string sid, string path, string text)
    {
        var v = await MergeFixture.FileAsync(m, sid, plan, path);
        return await m.ResolveAsync(sid, new ResolveRequest(v.Entry.Id, ResolutionAction.EditResult, v.SessionRevision, v.ResultContentHash, text), CancellationToken.None);
    }

    [Fact]
    public async Task Tampered_workspace_is_detected_then_rebuilt_by_replaying_resolutions()
    {
        using var fx = new MergeFixture();
        var (m, plan, s) = await TwoConflicts(fx);
        var v = await Edit(m, plan, s.Id, "src/shared.txt", "one\nMERGED\nthree\nfour\nfive\n");
        // Someone edits a resolved file and drops an untracked file in the harness workspace.
        File.WriteAllText(Path.Combine(s.Worktree, "src", "shared.txt"), "tampered\n");
        File.WriteAllText(Path.Combine(s.Worktree, "stray.txt"), "x\n");
        var (ok, problems) = await m.CheckAsync(s.Id);
        Assert.False(ok);
        Assert.Contains(problems, p => p.Contains("unstaged"));
        Assert.Contains(problems, p => p.Contains("untracked"));
        var view = await MergeFixture.FileAsync(m, s.Id, plan, "src/keep.txt");
        var ex = await Assert.ThrowsAsync<MergeException>(() => m.ResolveAsync(s.Id,
            new ResolveRequest(view.Entry.Id, ResolutionAction.TakeSource, view.SessionRevision, view.ResultContentHash), CancellationToken.None));
        Assert.Equal(MergeErrorKind.Stale, ex.Kind);
        var damaged = m.View(s.Id);
        Assert.Equal(SessionState.Damaged, damaged.Session.State);

        var rec = await m.RecoverAsync(s.Id, damaged.Session.Revision, CancellationToken.None);
        Assert.Equal(SessionState.Resolving, rec.Session.State);
        Assert.Equal("one\nMERGED\nthree\nfour\nfive\n", File.ReadAllText(Path.Combine(s.Worktree, "src", "shared.txt")));
        Assert.False(File.Exists(Path.Combine(s.Worktree, "stray.txt")));
        Assert.True((await m.CheckAsync(s.Id)).Ok);
        Assert.Equal(1, rec.Resolved);
    }

    [Fact]
    public async Task Missing_workspace_with_saved_drafts_recovers_after_restart()
    {
        using var fx = new MergeFixture();
        var (m, plan, s) = await TwoConflicts(fx);
        await Edit(m, plan, s.Id, "src/keep.txt", "keep BOTH\n");
        var d = await MergeFixture.FileAsync(m, s.Id, plan, "src/shared.txt");
        await m.SaveDraftAsync(s.Id, new SaveDraftRequest(d.Entry.Id, d.SessionRevision, d.ResultContentHash, "draft text\n"), CancellationToken.None);
        // The workspace disappears (disk cleanup, crash) and the process restarts.
        Directory.Delete(s.Worktree, recursive: true);
        var (_, m2) = fx.Engines();
        Assert.False((await m2.CheckAsync(s.Id)).Ok);
        var rec = await m2.RecoverAsync(s.Id, m2.View(s.Id).Session.Revision, CancellationToken.None);
        Assert.Equal("keep BOTH\n", File.ReadAllText(Path.Combine(s.Worktree, "src", "keep.txt")));
        var draft = await MergeFixture.FileAsync(m2, s.Id, plan, "src/shared.txt");
        Assert.Equal(("draft", "draft text\n"), (draft.State, draft.Result.Text));
        Assert.Contains("src/shared.txt", (await m2.Workspace.UnmergedAsync(rec.Session)).Select(u => u.Path));
    }

    [Fact]
    public async Task Cancelled_creation_leaves_a_recoverable_session()
    {
        using var fx = new MergeFixture();
        fx.OnBranch("feature/x", "develop", "src", ("src/shared.txt", "one\nSRC\nthree\nfour\nfive\n"));
        fx.OnBranch("develop", "develop", "tgt", ("src/shared.txt", "one\nTGT\nthree\nfour\nfive\n"));
        var (_, m) = fx.Engines();
        var plan = await fx.AnalyzeAsync(m, "feature/x", "develop");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => m.CreateSessionAsync(plan.Id, cts.Token));
        var s = m.Store.ListSessions().Single();
        Assert.Equal(SessionState.Creating, s.State);
        var (ok, problems) = await m.CheckAsync(s.Id);
        Assert.False(ok);
        Assert.Contains("did not complete", problems.Single());
        var rec = await m.RecoverAsync(s.Id, s.Revision, CancellationToken.None);
        Assert.Equal(SessionState.Resolving, rec.Session.State);
        Assert.Equal(1, rec.Unresolved);
    }

    [Fact]
    public async Task Unexpected_head_or_merge_state_is_refused_at_finalize()
    {
        using var fx = new MergeFixture();
        var (m, plan, s) = await TwoConflicts(fx);
        await Edit(m, plan, s.Id, "src/shared.txt", "a\n");
        var v = await Edit(m, plan, s.Id, "src/keep.txt", "b\n");
        var gitDir = (await m.Git.Git.RunAsync(s.Worktree, ["rev-parse", "--absolute-git-dir"])).StdOut.Trim();
        File.Delete(Path.Combine(gitDir, "MERGE_HEAD"));
        var ex = await Assert.ThrowsAsync<MergeException>(() => m.FinalizeAsync(s.Id, v.Session.Revision, CancellationToken.None));
        Assert.Equal(MergeErrorKind.Stale, ex.Kind);
        Assert.Contains("MERGE_HEAD", ex.Message);
    }

    [Fact]
    public async Task Abort_removes_only_the_owned_workspace_and_refs()
    {
        using var fx = new MergeFixture();
        var (m, plan, s) = await TwoConflicts(fx);
        var mirror = m.Store.Core.MirrorDir("app");
        Assert.Contains($"refs/fb/merge/sessions/{s.Id}/source", MergeFixture.Git(mirror, "for-each-ref", "--format=%(refname)"));
        var v = await m.AbortAsync(s.Id, m.View(s.Id).Session.Revision);
        Assert.Equal(SessionState.Aborted, v.Session.State);
        Assert.False(Directory.Exists(s.Worktree));
        Assert.True(File.Exists(Path.Combine(s.WorkspaceDir, "session.json")));
        Assert.DoesNotContain($"refs/fb/merge/sessions/{s.Id}/", MergeFixture.Git(mirror, "for-each-ref", "--format=%(refname)"));
        Assert.DoesNotContain(s.Worktree, MergeFixture.Git(mirror, "worktree", "list"));
        Assert.True(Directory.Exists(fx.Checkout));
        await Assert.ThrowsAsync<MergeException>(() => m.RecoverAsync(s.Id, v.Session.Revision, CancellationToken.None));
    }

    [Fact]
    public async Task Abort_refuses_a_directory_whose_ownership_marker_was_changed()
    {
        using var fx = new MergeFixture();
        var (m, _, s) = await TwoConflicts(fx);
        File.WriteAllText(Path.Combine(s.WorkspaceDir, Core.Infrastructure.SafeFileSystem.OwnershipMarker), "someone-else");
        await Assert.ThrowsAnyAsync<Exception>(() => m.AbortAsync(s.Id, s.Revision + 0));
        Assert.True(Directory.Exists(s.Worktree));
    }
}
