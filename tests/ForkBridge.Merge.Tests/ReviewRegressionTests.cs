using System.Text;
using ForkBridge.Core.Publishing;
using ForkBridge.Merge.Git;
using ForkBridge.Merge.Model;
using ForkBridge.Merge.Resolution;

namespace ForkBridge.Merge.Tests;

/// <summary>Regression tests for defects found by the independent safety review.</summary>
public class ReviewRegressionTests
{
    private static async Task<(MergeEngine M, MergePlan Plan, MergeSession S)> Conflict(MergeFixture fx, MergeEngine? engine = null)
    {
        fx.OnBranch("feature/x", "develop", "src", ("src/shared.txt", "one\nSRC\nthree\nfour\nfive\n"));
        fx.OnBranch("develop", "develop", "tgt", ("src/shared.txt", "one\nTGT\nthree\nfour\nfive\n"));
        var m = engine ?? fx.Engines().Merge;
        var plan = await fx.AnalyzeAsync(m, "feature/x", "develop");
        return (m, plan, await m.CreateSessionAsync(plan.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Checkout_conversions_never_leak_into_resolved_blobs()
    {
        using var fx = new MergeFixture();
        fx.Write(".gitattributes", "*.txt text eol=crlf\n");
        fx.Switch("develop"); fx.Commit("attrs"); fx.Push("develop");
        var (m, plan, s) = await Conflict(fx);
        // The worktree holds CRLF (checkout conversion); the repository form is LF.
        Assert.Contains("\r\n", File.ReadAllText(Path.Combine(s.Worktree, "src", "shared.txt")));
        var v = await MergeFixture.FileAsync(m, s.Id, plan, "src/shared.txt");
        Assert.Equal("lf", v.Result.Codec!.Eol);
        var r = await m.ResolveAsync(s.Id, new ResolveRequest(v.Entry.Id, ResolutionAction.EditResult, v.SessionRevision, v.ResultContentHash, "one\nMERGED\nthree\nfour\nfive\n"), CancellationToken.None);
        Assert.True((await m.CheckAsync(s.Id)).Ok); // not "dirty" after checkout-index + refresh
        var f = await m.FinalizeAsync(s.Id, r.Session.Revision, CancellationToken.None);
        var bytes = MergeFixture.GitRaw(m.Store.Core.MirrorDir("app"), null, "show", f.Session.CandidateSha + ":src/shared.txt");
        Assert.Equal("one\nMERGED\nthree\nfour\nfive\n", bytes);
    }

    [Fact]
    public async Task Analysis_and_session_read_the_same_gitattributes()
    {
        using var fx = new MergeFixture();
        fx.Write(".gitattributes", "*.txt merge=union\n");
        fx.Switch("develop"); fx.Commit("union"); fx.Push("develop");
        fx.OnBranch("feature/x", "develop", "src", ("src/shared.txt", "one\nSRC\nthree\nfour\nfive\n"));
        fx.OnBranch("develop", "develop", "tgt", ("src/shared.txt", "one\nTGT\nthree\nfour\nfive\n"));
        var (_, m) = fx.Engines();
        var plan = await fx.AnalyzeAsync(m, "feature/x", "develop");
        Assert.Equal(MergeOutcome.Clean, plan.Outcome); // the target tree's attributes apply in the bare mirror too
        var s = await m.CreateSessionAsync(plan.Id, CancellationToken.None);
        var f = await m.FinalizeAsync(s.Id, m.View(s.Id).Session.Revision, CancellationToken.None);
        Assert.Equal(plan.ResultTree, f.Session.CandidateTree);
    }

    [Fact]
    public async Task Global_attributes_file_does_not_reach_the_isolated_environment()
    {
        using var fx = new MergeFixture();
        fx.OnBranch("feature/x", "develop", "src", ("src/shared.txt", "one\nSRC\nthree\nfour\nfive\n"));
        fx.OnBranch("develop", "develop", "tgt", ("src/shared.txt", "one\nTGT\nthree\nfour\nfive\n"));
        var (_, m) = fx.Engines();
        await m.Refs.RefreshAsync("app", false, CancellationToken.None);
        var xdg = Path.Combine(fx.Root, "xdg");
        Directory.CreateDirectory(Path.Combine(xdg, "git"));
        File.WriteAllText(Path.Combine(xdg, "git", "attributes"), "* merge=union\n");
        var env = new Dictionary<string, string?> { ["XDG_CONFIG_HOME"] = xdg, ["HOME"] = fx.Root };
        var r = await m.Git.RunAsync(m.Store.Core.MirrorDir("app"), ["merge-tree", "--write-tree", "-z", "refs/remotes/origin/develop", "refs/remotes/origin/feature/x"], extraEnv: env);
        Assert.Equal(1, r.ExitCode); // still a conflict: the user's global `merge=union` was ignored
    }

    [Fact]
    public async Task Publish_retry_after_a_recorded_loss_survives_later_target_movement()
    {
        using var fx = new MergeFixture();
        var (m, plan, s) = await Conflict(fx);
        var v = await MergeFixture.FileAsync(m, s.Id, plan, "src/shared.txt");
        var r = await m.ResolveAsync(s.Id, new ResolveRequest(v.Entry.Id, ResolutionAction.TakeSource, v.SessionRevision, v.ResultContentHash), CancellationToken.None);
        var f = await m.FinalizeAsync(s.Id, r.Session.Revision, CancellationToken.None);
        var val = await m.ValidateAsync(s.Id, f.Session.Revision, true, CancellationToken.None);
        var a = await m.ApproveAsync(s.Id, val.Session.Revision, f.Session.CandidateSha!, false, false, "t");
        // The push landed but was never recorded; then someone moved the target.
        MergeFixture.Git(m.Store.Core.MirrorDir("app"), "push", "-q", fx.Origin, $"{f.Session.CandidateSha}:refs/heads/{a.Session.Approval!.IntegrationBranch}");
        fx.OnBranch("develop", "develop", "moved later", ("src/late.txt", "x\n"));
        var p = await m.PublishAsync(s.Id, a.Session.Revision, CancellationToken.None);
        Assert.Equal(SessionState.Published, p.Session.State);
        Assert.True(p.Session.Publications.Single().AlreadyPresent);
    }

    private sealed class FlakyPr : IPullRequestProvider
    {
        public int Calls;
        public string Name => "fake";
        public Task<string> CreateAsync(string remoteUrl, string sourceBranch, string targetBranch, string title, string description, bool draft, CancellationToken ct)
            => ++Calls == 1 ? throw new InvalidOperationException("service unavailable") : Task.FromResult("https://example.invalid/pr/1");
    }

    [Fact]
    public async Task A_failed_pull_request_can_be_retried_after_the_branch_is_published()
    {
        using var fx = new MergeFixture();
        var pr = new FlakyPr();
        var m = new MergeEngine(Core.ForkBridgeEngine.Load(fx.ConfigPath), pr: pr);
        var (_, plan, s) = await Conflict(fx, m);
        var v = await MergeFixture.FileAsync(m, s.Id, plan, "src/shared.txt");
        var r = await m.ResolveAsync(s.Id, new ResolveRequest(v.Entry.Id, ResolutionAction.TakeTarget, v.SessionRevision, v.ResultContentHash), CancellationToken.None);
        var f = await m.FinalizeAsync(s.Id, r.Session.Revision, CancellationToken.None);
        var val = await m.ValidateAsync(s.Id, f.Session.Revision, true, CancellationToken.None);
        var a = await m.ApproveAsync(s.Id, val.Session.Revision, f.Session.CandidateSha!, createPr: true, false, "t");
        var p1 = await m.PublishAsync(s.Id, a.Session.Revision, CancellationToken.None);
        Assert.Equal(SessionState.Published, p1.Session.State);
        Assert.Contains("pull request creation failed", p1.Session.Publications.Single().Error);
        var p2 = await m.PublishAsync(s.Id, p1.Session.Revision, CancellationToken.None);
        Assert.Equal("https://example.invalid/pr/1", p2.Session.Publications.Last().PullRequestUrl);
        Assert.True(p2.Session.Publications.Last().AlreadyPresent);
        var p3 = await m.PublishAsync(s.Id, p2.Session.Revision, CancellationToken.None);
        Assert.Equal(2, p3.Session.Publications.Count); // nothing left to do
        Assert.Equal(2, pr.Calls);
    }

    [Fact]
    public async Task A_busy_session_reports_409_and_read_checks_never_race_it()
    {
        using var fx = new MergeFixture();
        var (m, plan, s) = await Conflict(fx);
        m.Store.LockWait = TimeSpan.FromMilliseconds(300);
        var v = await MergeFixture.FileAsync(m, s.Id, plan, "src/shared.txt");
        using (m.Store.LockSession(s.Id))
        {
            var ex = await Assert.ThrowsAsync<MergeException>(() => m.SaveDraftAsync(s.Id, new SaveDraftRequest(v.Entry.Id, v.SessionRevision, v.ResultContentHash, "x\n"), CancellationToken.None));
            Assert.Equal(MergeErrorKind.Stale, ex.Kind);
            Assert.Contains("busy", ex.Message);
            Assert.True((await m.CheckAsync(s.Id)).Ok); // skipped, not raced
        }
        Assert.True(File.Exists(Path.Combine(m.Store.Core.Root, "locks", $"merge-session-{s.Id}.lock"))); // persistent lock file
        await m.SaveDraftAsync(s.Id, new SaveDraftRequest(v.Entry.Id, v.SessionRevision, v.ResultContentHash, "x\n"), CancellationToken.None);
    }
}
