using ForkBridge.Merge.Git;
using ForkBridge.Merge.Model;
using ForkBridge.Merge.Resolution;

namespace ForkBridge.Merge.Tests;

public class PublicationAndIsolationTests
{
    private static async Task<(MergeEngine M, MergePlan Plan, SessionView V)> Finalized(MergeFixture fx, bool validate = true)
    {
        fx.OnBranch("feature/x", "develop", "src", ("src/shared.txt", "one\nSRC\nthree\nfour\nfive\n"));
        fx.OnBranch("develop", "develop", "tgt", ("src/shared.txt", "one\nTGT\nthree\nfour\nfive\n"));
        var (_, m) = fx.Engines();
        var plan = await fx.AnalyzeAsync(m, "feature/x", "develop");
        var s = await m.CreateSessionAsync(plan.Id, CancellationToken.None);
        var f = await MergeFixture.FileAsync(m, s.Id, plan, "src/shared.txt");
        var r = await m.ResolveAsync(s.Id, new ResolveRequest(f.Entry.Id, ResolutionAction.TakeSource, f.SessionRevision, f.ResultContentHash), CancellationToken.None);
        var v = await m.FinalizeAsync(s.Id, r.Session.Revision, CancellationToken.None);
        if (validate) v = await m.ValidateAsync(s.Id, v.Session.Revision, true, CancellationToken.None);
        return (m, plan, v);
    }

    [Fact]
    public async Task Target_movement_after_approval_refuses_publication()
    {
        using var fx = new MergeFixture();
        var (m, _, v) = await Finalized(fx);
        var a = await m.ApproveAsync(v.Session.Id, v.Session.Revision, v.Session.CandidateSha!, false, false, "t");
        fx.OnBranch("develop", "develop", "moved", ("src/other.txt", "x\n"));
        var ex = await Assert.ThrowsAsync<MergeException>(() => m.PublishAsync(v.Session.Id, a.Session.Revision, CancellationToken.None));
        Assert.Equal(MergeErrorKind.Stale, ex.Kind);
        Assert.Contains("moved", ex.Message);
        Assert.DoesNotContain("forkbridge/merge", MergeFixture.Git(fx.Origin, "for-each-ref", "--format=%(refname)"));
    }

    [Fact]
    public async Task Source_movement_is_reported_but_the_pinned_candidate_is_published()
    {
        using var fx = new MergeFixture();
        var (m, plan, v) = await Finalized(fx);
        var a = await m.ApproveAsync(v.Session.Id, v.Session.Revision, v.Session.CandidateSha!, false, false, "t");
        var moved = fx.OnBranch("feature/x", "feature/x", "more source", ("src/late.txt", "late\n"));
        var p = await m.PublishAsync(v.Session.Id, a.Session.Revision, CancellationToken.None);
        var pub = p.Session.Publications.Single();
        Assert.Equal(moved, pub.SourceMovedTo);
        Assert.Equal(v.Session.CandidateSha, MergeFixture.Git(fx.Origin, "rev-parse", "refs/heads/" + pub.Branch));
        Assert.Equal(plan.Source.Sha, MergeFixture.Git(fx.Origin, "rev-parse", pub.Branch + "^2"));
    }

    [Fact]
    public async Task Existing_integration_branch_at_another_commit_is_never_overwritten()
    {
        using var fx = new MergeFixture();
        var (m, plan, v) = await Finalized(fx);
        var a = await m.ApproveAsync(v.Session.Id, v.Session.Revision, v.Session.CandidateSha!, false, false, "t");
        MergeFixture.Git(fx.Origin, "update-ref", "refs/heads/" + a.Session.Approval!.IntegrationBranch, plan.Target.Sha);
        var ex = await Assert.ThrowsAsync<MergeException>(() => m.PublishAsync(v.Session.Id, a.Session.Revision, CancellationToken.None));
        Assert.Contains("never force-pushes", ex.Message);
        Assert.Equal(plan.Target.Sha, MergeFixture.Git(fx.Origin, "rev-parse", "refs/heads/" + a.Session.Approval.IntegrationBranch));
    }

    [Fact]
    public async Task Publication_retry_after_a_lost_record_is_idempotent()
    {
        using var fx = new MergeFixture();
        var (m, _, v) = await Finalized(fx);
        var a = await m.ApproveAsync(v.Session.Id, v.Session.Revision, v.Session.CandidateSha!, false, false, "t");
        // Simulate: the push reached the remote, then the process died before recording it.
        MergeFixture.Git(m.Store.Core.MirrorDir("app"), "push", "-q", fx.Origin, $"{v.Session.CandidateSha}:refs/heads/{a.Session.Approval!.IntegrationBranch}");
        var p = await m.PublishAsync(v.Session.Id, a.Session.Revision, CancellationToken.None);
        Assert.True(p.Session.Publications.Single().AlreadyPresent);
        Assert.Equal(SessionState.Published, p.Session.State);
    }

    [Fact]
    public async Task Validation_gates_approval_and_failed_checks_cannot_be_waived()
    {
        using var fx = new MergeFixture(validationRun: "[git, definitely-not-a-git-command]");
        var (m, _, v) = await Finalized(fx);
        Assert.Equal("FAILED", v.CurrentValidation!.Verdict);
        var ex = await Assert.ThrowsAsync<MergeException>(() => m.ApproveAsync(v.Session.Id, v.Session.Revision, v.Session.CandidateSha!, false, acceptUnvalidated: true, "t"));
        Assert.Contains("FAILED", ex.Message);
    }

    [Fact]
    public async Task Untrusted_validation_executes_nothing_and_needs_explicit_acceptance()
    {
        using var fx = new MergeFixture(trusted: false);
        var (m, _, v) = await Finalized(fx);
        Assert.Equal("UNTRUSTED", v.CurrentValidation!.Verdict);
        Assert.All(v.CurrentValidation.Steps, s => Assert.Equal("UNAVAILABLE", s.Outcome));
        await Assert.ThrowsAsync<MergeException>(() => m.ApproveAsync(v.Session.Id, v.Session.Revision, v.Session.CandidateSha!, false, false, "t"));
        var a = await m.ApproveAsync(v.Session.Id, v.Session.Revision, v.Session.CandidateSha!, false, acceptUnvalidated: true, "t");
        Assert.True(a.Session.Approval!.ValidationWaived);
        Assert.Contains($"candidate={v.Session.CandidateSha}", a.Session.Approval.Binding);
    }

    [Fact]
    public async Task Approval_without_validation_and_pr_without_provider_are_refused()
    {
        using var fx = new MergeFixture();
        var (m, _, v) = await Finalized(fx, validate: false);
        await Assert.ThrowsAsync<MergeException>(() => m.ApproveAsync(v.Session.Id, v.Session.Revision, v.Session.CandidateSha!, false, true, "t"));
        var val = await m.ValidateAsync(v.Session.Id, v.Session.Revision, true, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<MergeException>(() => m.ApproveAsync(v.Session.Id, val.Session.Revision, val.Session.CandidateSha!, createPr: true, false, "t"));
        Assert.Contains("provider", ex.Message);
    }

    [Fact]
    public async Task User_checkout_in_mid_merge_with_staged_and_untracked_changes_is_untouched()
    {
        using var fx = new MergeFixture();
        fx.OnBranch("feature/x", "develop", "src", ("src/shared.txt", "one\nSRC\nthree\nfour\nfive\n"));
        fx.OnBranch("develop", "develop", "tgt", ("src/shared.txt", "one\nTGT\nthree\nfour\nfive\n"));
        // The user is in the middle of their own conflicted merge, with extra staged and untracked files.
        fx.Switch("develop");
        try { MergeFixture.Git(fx.Checkout, "merge", "--no-ff", "feature/x"); } catch (InvalidOperationException) { }
        fx.Write("staged.txt", "staged\n"); MergeFixture.Git(fx.Checkout, "add", "staged.txt");
        fx.Write("untracked.txt", "u\n");
        Assert.True(File.Exists(Path.Combine(fx.Checkout, ".git", "MERGE_HEAD")));
        var before = fx.Snapshot();

        var (_, m) = fx.Engines();
        var plan = await fx.AnalyzeAsync(m, "feature/x", "develop");
        var s = await m.CreateSessionAsync(plan.Id, CancellationToken.None);
        var f = await MergeFixture.FileAsync(m, s.Id, plan, "src/shared.txt");
        await m.SaveDraftAsync(s.Id, new SaveDraftRequest(f.Entry.Id, f.SessionRevision, f.ResultContentHash, "d\n"), CancellationToken.None);
        var f2 = await MergeFixture.FileAsync(m, s.Id, plan, "src/shared.txt");
        var r = await m.ResolveAsync(s.Id, new ResolveRequest(f2.Entry.Id, ResolutionAction.EditResult, f2.SessionRevision, f2.ResultContentHash, "merged\n"), CancellationToken.None);
        var fin = await m.FinalizeAsync(s.Id, r.Session.Revision, CancellationToken.None);
        var val = await m.ValidateAsync(s.Id, fin.Session.Revision, true, CancellationToken.None);
        Assert.Equal("PASSED", val.CurrentValidation!.Verdict);
        var ap = await m.ApproveAsync(s.Id, val.Session.Revision, fin.Session.CandidateSha!, false, false, "t");
        await m.PublishAsync(s.Id, ap.Session.Revision, CancellationToken.None);
        await m.AbortAsync(s.Id, m.View(s.Id).Session.Revision);

        Assert.Equal(before, fx.Snapshot()); // files, index, HEAD, branch, MERGE_HEAD, stash, refs, local config (remotes)
    }

    [Fact]
    public async Task Global_filters_merge_drivers_and_harness_hooks_never_run()
    {
        using var fx = new MergeFixture();
        // Attributes name the filter and merge driver that the test "global" config defines.
        fx.Write(".gitattributes", "*.txt merge=fbtest\n*.dat filter=fbtest\n");
        fx.Switch("develop"); fx.Commit("attrs"); fx.Push("develop");
        fx.OnBranch("feature/x", "develop", "src", ("src/shared.txt", "one\nSRC\nthree\nfour\nfive\n"));
        fx.OnBranch("develop", "develop", "tgt", ("src/shared.txt", "one\nTGT\nthree\nfour\nfive\n"));
        var (_, m) = fx.Engines();
        await m.Refs.RefreshAsync("app", false, CancellationToken.None);
        // A hook planted in the mirror (e.g. by a careless tool) must not run either.
        var mirror = m.Store.Core.MirrorDir("app");
        var marker = Path.Combine(fx.Root, "hook-ran");
        var hook = Path.Combine(mirror, "hooks", "post-checkout");
        File.WriteAllText(hook, $"#!/bin/sh\ntouch '{marker}'\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var visible = await m.Git.RunAsync(mirror, ["config", "--get-regexp", @"^(filter|merge)\.fbtest\."]);
        Assert.Equal("", visible.StdOut.Trim());
        var plan = await m.Analyzer.AnalyzeAsync("app", MergeFixture.Remote("feature/x"), MergeFixture.Remote("develop"));
        Assert.Equal(MergeOutcome.Conflicts, plan.Outcome); // built-in text merge, not the (undefined) driver
        var s = await m.CreateSessionAsync(plan.Id, CancellationToken.None);
        Assert.Equal(SessionState.Resolving, s.State);
        Assert.False(File.Exists(marker));
    }
}
