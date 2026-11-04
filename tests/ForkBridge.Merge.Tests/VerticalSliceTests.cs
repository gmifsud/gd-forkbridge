using ForkBridge.Merge.Git;
using ForkBridge.Merge.Model;
using ForkBridge.Merge.Resolution;

namespace ForkBridge.Merge.Tests;

/// <summary>The brief's acceptance scenario: select two branches of one repository, inspect, resolve a text conflict,
/// reload/restart and recover, finalize, validate, approve and publish an integration branch - without changing the
/// user's checkout.</summary>
public class VerticalSliceTests
{
    [Fact]
    public async Task Text_conflict_end_to_end_with_restart_and_publication()
    {
        using var fx = new MergeFixture();
        var src = fx.OnBranch("feature/greeting", "develop", "src", ("src/shared.txt", "one\nSOURCE\nthree\nfour\nfive\n"), ("src/src-only.txt", "s\n"));
        var tgt = fx.OnBranch("develop", "develop", "tgt", ("src/shared.txt", "one\nTARGET\nthree\nfour\nfive\n"));
        fx.Switch("feature/greeting");
        var before = fx.Snapshot();

        var (_, m) = fx.Engines();
        var plan = await fx.AnalyzeAsync(m, "feature/greeting", "develop");
        Assert.Equal((MergeOutcome.Conflicts, src, tgt), (plan.Outcome, plan.Source.Sha, plan.Target.Sha));
        var session = await m.CreateSessionAsync(plan.Id, CancellationToken.None);
        Assert.Equal(SessionState.Resolving, session.State);

        var view = await MergeFixture.FileAsync(m, session.Id, plan, "src/shared.txt");
        Assert.Equal("unresolved", view.State);
        Assert.True(view.Editable);
        Assert.Contains("<<<<<<<", view.Result.Text);
        Assert.Contains("|||||||", view.Result.Text); // diff3 conflict style is pinned
        Assert.Equal(4, view.MarkerLines); // <<<<<<< ||||||| ======= >>>>>>>
        Assert.Contains("+TARGET", view.TargetChanges.Diff);
        Assert.Contains("+SOURCE", view.SourceChanges.Diff);
        Assert.Equal("feature/greeting", view.SourceLabel);

        // Draft: saved server-side, index still unmerged.
        var v1 = await m.SaveDraftAsync(session.Id, new SaveDraftRequest(view.Entry.Id, view.SessionRevision, view.ResultContentHash, "one\nTARGET and SOURCE\nthree\nfour\nfive\n"), CancellationToken.None);
        Assert.Equal("draft", v1.Entries.Single(e => e.Path == "src/shared.txt").Resolution);
        Assert.False(v1.CanFinalize);
        Assert.Contains("src/shared.txt", (await m.Workspace.UnmergedAsync(v1.Session)).Select(u => u.Path)); // a draft does not touch the index

        // "Restart": a brand-new engine instance reads only persisted state.
        var (_, m2) = fx.Engines();
        var reloaded = await MergeFixture.FileAsync(m2, session.Id, plan, "src/shared.txt");
        Assert.Equal("draft", reloaded.State);
        Assert.Equal("one\nTARGET and SOURCE\nthree\nfour\nfive\n", reloaded.Result.Text);

        // Resolve with the edited content.
        var v2 = await m2.ResolveAsync(session.Id, new ResolveRequest(reloaded.Entry.Id, ResolutionAction.EditResult, reloaded.SessionRevision,
            reloaded.ResultContentHash, reloaded.Result.Text), CancellationToken.None);
        Assert.True(v2.CanFinalize);
        Assert.Empty(await m2.Workspace.UnmergedAsync(v2.Session));
        var resolved = await MergeFixture.FileAsync(m2, session.Id, plan, "src/shared.txt");
        Assert.Contains("+TARGET and SOURCE", resolved.TargetToResult.Diff);

        // Finalize: ordered parents, exact tree.
        var v3 = await m2.FinalizeAsync(session.Id, v2.Session.Revision, CancellationToken.None);
        var cand = v3.Session.CandidateSha!;
        var mirror = m2.Store.Core.MirrorDir("app");
        Assert.Equal($"{cand} {tgt} {src}", MergeFixture.Git(mirror, "rev-list", "--parents", "-n", "1", cand));
        Assert.Equal("one\nTARGET and SOURCE\nthree\nfour\nfive\n", MergeFixture.GitRaw(mirror, null, "show", cand + ":src/shared.txt"));
        Assert.Equal("s\n", MergeFixture.GitRaw(mirror, null, "show", cand + ":src/src-only.txt"));

        // Validate exactly this candidate (trusted profile + trusted request).
        var v4 = await m2.ValidateAsync(session.Id, v3.Session.Revision, trust: true, CancellationToken.None);
        Assert.Equal("PASSED", v4.CurrentValidation!.Verdict);
        Assert.Equal(cand, v4.CurrentValidation.CandidateSha);
        Assert.False(Directory.Exists(Path.Combine(v4.Session.WorkspaceDir, "validate")));

        // Approve exact candidate, then publish an integration branch.
        await Assert.ThrowsAsync<MergeException>(() => m2.ApproveAsync(session.Id, v4.Session.Revision, "deadbeef", false, false, "test"));
        var v5 = await m2.ApproveAsync(session.Id, v4.Session.Revision, cand[..12], createPr: false, acceptUnvalidated: false, "test");
        Assert.Equal(SessionState.Approved, v5.Session.State);
        var v6 = await m2.PublishAsync(session.Id, v5.Session.Revision, CancellationToken.None);
        Assert.Equal(SessionState.Published, v6.Session.State);
        var branch = v6.Session.Approval!.IntegrationBranch;
        Assert.Equal($"forkbridge/merge/develop/{session.Id}", branch);
        Assert.Equal(cand, MergeFixture.Git(fx.Origin, "rev-parse", "refs/heads/" + branch));
        Assert.Equal(tgt, MergeFixture.Git(fx.Origin, "rev-parse", "refs/heads/develop")); // target untouched

        // Retry is idempotent.
        var again = await m2.PublishAsync(session.Id, v6.Session.Revision, CancellationToken.None);
        Assert.Single(again.Session.Publications);

        Assert.Equal(before, fx.Snapshot());
    }

    [Fact]
    public async Task Fast_forward_creates_a_no_ff_candidate_with_the_source_tree()
    {
        using var fx = new MergeFixture();
        var src = fx.OnBranch("feature/ff", "develop", "ff", ("src/new.txt", "new\n"));
        var tgt = MergeFixture.Git(fx.Checkout, "rev-parse", "develop");
        var (_, m) = fx.Engines();
        var plan = await fx.AnalyzeAsync(m, "feature/ff", "develop");
        var s = await m.CreateSessionAsync(plan.Id, CancellationToken.None);
        var v = m.View(s.Id);
        Assert.True(v.CanFinalize);
        var f = await m.FinalizeAsync(s.Id, v.Session.Revision, CancellationToken.None);
        var mirror = m.Store.Core.MirrorDir("app");
        Assert.Equal($"{f.Session.CandidateSha} {tgt} {src}", MergeFixture.Git(mirror, "rev-list", "--parents", "-n", "1", f.Session.CandidateSha!));
        Assert.Equal(MergeFixture.Git(mirror, "rev-parse", src + "^{tree}"), f.Session.CandidateTree);
    }

    [Fact]
    public async Task Concurrent_browser_writes_are_rejected_not_overwritten()
    {
        using var fx = new MergeFixture();
        fx.OnBranch("feature/x", "develop", "src", ("src/shared.txt", "one\nSRC\nthree\nfour\nfive\n"));
        fx.OnBranch("develop", "develop", "tgt", ("src/shared.txt", "one\nTGT\nthree\nfour\nfive\n"));
        var (_, m) = fx.Engines();
        var plan = await fx.AnalyzeAsync(m, "feature/x", "develop");
        var s = await m.CreateSessionAsync(plan.Id, CancellationToken.None);
        var tabA = await MergeFixture.FileAsync(m, s.Id, plan, "src/shared.txt");
        var tabB = await MergeFixture.FileAsync(m, s.Id, plan, "src/shared.txt");
        await m.SaveDraftAsync(s.Id, new SaveDraftRequest(tabA.Entry.Id, tabA.SessionRevision, tabA.ResultContentHash, "A\n"), CancellationToken.None);
        var stale = await Assert.ThrowsAsync<MergeException>(() =>
            m.SaveDraftAsync(s.Id, new SaveDraftRequest(tabB.Entry.Id, tabB.SessionRevision, tabB.ResultContentHash, "B\n"), CancellationToken.None));
        Assert.Equal(MergeErrorKind.Stale, stale.Kind);
        // Right revision, stale content hash: still refused.
        var cur = m.View(s.Id).Session.Revision;
        var stale2 = await Assert.ThrowsAsync<MergeException>(() =>
            m.ResolveAsync(s.Id, new ResolveRequest(tabB.Entry.Id, ResolutionAction.EditResult, cur, tabB.ResultContentHash, "B\n"), CancellationToken.None));
        Assert.Equal(MergeErrorKind.Stale, stale2.Kind);
        Assert.Equal("A\n", (await MergeFixture.FileAsync(m, s.Id, plan, "src/shared.txt")).Result.Text);
    }

    [Fact]
    public async Task Conflict_markers_require_explicit_acknowledgement()
    {
        using var fx = new MergeFixture();
        fx.OnBranch("feature/x", "develop", "src", ("src/shared.txt", "one\nSRC\nthree\nfour\nfive\n"));
        fx.OnBranch("develop", "develop", "tgt", ("src/shared.txt", "one\nTGT\nthree\nfour\nfive\n"));
        var (_, m) = fx.Engines();
        var plan = await fx.AnalyzeAsync(m, "feature/x", "develop");
        var s = await m.CreateSessionAsync(plan.Id, CancellationToken.None);
        var v = await MergeFixture.FileAsync(m, s.Id, plan, "src/shared.txt");
        var ex = await Assert.ThrowsAsync<MergeException>(() => m.ResolveAsync(s.Id,
            new ResolveRequest(v.Entry.Id, ResolutionAction.EditResult, v.SessionRevision, v.ResultContentHash, v.Result.Text), CancellationToken.None));
        Assert.Contains("conflict-marker", ex.Message);
        var ok = await m.ResolveAsync(s.Id, new ResolveRequest(v.Entry.Id, ResolutionAction.EditResult, v.SessionRevision, v.ResultContentHash, v.Result.Text, AcknowledgeMarkers: true), CancellationToken.None);
        Assert.True(ok.Session.Resolutions[v.Entry.Id].MarkersAcknowledged);
    }
}
