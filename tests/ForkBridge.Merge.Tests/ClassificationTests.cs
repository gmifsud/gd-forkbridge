using ForkBridge.Merge.Git;
using ForkBridge.Merge.Model;

namespace ForkBridge.Merge.Tests;

public class ClassificationTests
{
    [Fact]
    public async Task Source_already_merged_and_identical_tips_are_up_to_date_and_refuse_sessions()
    {
        using var fx = new MergeFixture();
        fx.OnBranch("feature/a", "develop", "a", ("src/a.txt", "a\n"));
        fx.Switch("develop"); MergeFixture.Git(fx.Checkout, "merge", "-q", "--no-ff", "-m", "merge a", "feature/a"); fx.Push("develop");
        var (_, m) = fx.Engines();
        var plan = await fx.AnalyzeAsync(m, "feature/a", "develop");
        Assert.Equal(MergeOutcome.UpToDate, plan.Outcome);
        Assert.Equal(0, plan.Counts.SourceOnlyCommits);
        Assert.Empty(plan.Entries);
        var ex = await Assert.ThrowsAsync<MergeException>(() => m.CreateSessionAsync(plan.Id, CancellationToken.None));
        Assert.Contains("Nothing to merge", ex.Message);

        fx.Switch("develop"); fx.Switch("same", create: true); fx.Push("same");
        var same = await fx.AnalyzeAsync(m, "same", "develop");
        Assert.Equal(MergeOutcome.UpToDate, same.Outcome);
        Assert.Equal(same.Source.Sha, same.Target.Sha);
    }

    [Fact]
    public async Task Fast_forward_is_detected_and_lists_incoming_changes()
    {
        using var fx = new MergeFixture();
        fx.OnBranch("feature/ff", "develop", "ff", ("src/new.txt", "new\n"), ("src/keep.txt", "kept\n"));
        var (_, m) = fx.Engines();
        var plan = await fx.AnalyzeAsync(m, "feature/ff", "develop");
        Assert.Equal(MergeOutcome.FastForward, plan.Outcome);
        Assert.Equal((1, 0), (plan.Counts.SourceOnlyCommits, plan.Counts.TargetOnlyCommits));
        Assert.All(plan.Entries, e => Assert.Equal(EntryStatus.Incoming, e.Status));
        Assert.Equal(["src/keep.txt", "src/new.txt"], plan.Entries.Select(e => e.Path));
        Assert.Equal(MergeFixture.Git(fx.Checkout, "rev-parse", "feature/ff^{tree}"), plan.ResultTree);
    }

    [Fact]
    public async Task Clean_divergent_merge_distinguishes_incoming_and_auto_merged()
    {
        using var fx = new MergeFixture();
        fx.OnBranch("feature/c", "develop", "src side", ("src/shared.txt", "ONE\ntwo\nthree\nfour\nfive\n"), ("src/only-src.txt", "x\n"));
        fx.OnBranch("develop", "develop", "tgt side", ("src/shared.txt", "one\ntwo\nthree\nfour\nFIVE\n"));
        var (_, m) = fx.Engines();
        var plan = await fx.AnalyzeAsync(m, "feature/c", "develop");
        Assert.Equal(MergeOutcome.Clean, plan.Outcome);
        Assert.Equal(EntryStatus.AutoMerged, plan.Entries.Single(e => e.Path == "src/shared.txt").Status);
        Assert.Equal(EntryStatus.Incoming, plan.Entries.Single(e => e.Path == "src/only-src.txt").Status);
        Assert.Single(plan.MergeBases);
        Assert.StartsWith("sha256:", plan.AnalysisHash);
        Assert.Equal(MergeGit.OptionsFingerprint(plan.GitVersion), plan.OptionsFingerprint);
    }

    [Fact]
    public async Task Conflicts_are_reported_with_kinds_and_capabilities()
    {
        using var fx = new MergeFixture();
        fx.OnBranch("feature/x", "develop", "src", ("src/shared.txt", "one\nSRC\nthree\nfour\nfive\n"));
        fx.OnBranch("develop", "develop", "tgt", ("src/shared.txt", "one\nTGT\nthree\nfour\nfive\n"));
        var (_, m) = fx.Engines();
        var plan = await fx.AnalyzeAsync(m, "feature/x", "develop");
        Assert.Equal(MergeOutcome.Conflicts, plan.Outcome);
        var e = Assert.Single(plan.Entries);
        Assert.Equal((EntryStatus.Conflicted, ConflictKind.Content), (e.Status, e.Conflict));
        Assert.True(e.Capabilities.EditText && e.Capabilities.TakeSource && e.Capabilities.TakeTarget);
        Assert.NotNull(e.Base); Assert.NotNull(e.Target); Assert.NotNull(e.Source);
        Assert.Contains(e.Messages, x => x.Contains("Merge conflict in src/shared.txt"));
    }

    [Fact]
    public async Task Unrelated_histories_are_refused()
    {
        using var fx = new MergeFixture();
        fx.Switch("develop");
        MergeFixture.Git(fx.Checkout, "switch", "-q", "--orphan", "orphan");
        foreach (var f in Directory.GetFileSystemEntries(fx.Checkout).Where(f => !f.EndsWith(".git"))) { if (Directory.Exists(f)) Directory.Delete(f, true); else File.Delete(f); }
        fx.Write("other.txt", "other\n"); fx.Commit("orphan root"); fx.Push("orphan");
        var (_, m) = fx.Engines();
        var plan = await fx.AnalyzeAsync(m, "orphan", "develop");
        Assert.Equal(MergeOutcome.Blocked, plan.Outcome);
        Assert.Contains("unrelated histories", plan.BlockedReason);
        Assert.Empty(plan.MergeBases);
        await Assert.ThrowsAsync<MergeException>(() => m.CreateSessionAsync(plan.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Multiple_merge_bases_are_delegated_to_git_and_match_a_real_merge()
    {
        using var fx = new MergeFixture();
        // Criss-cross: A and B each merge the other's first commit.
        fx.OnBranch("a", "develop", "a1", ("a.txt", "a1\n"));
        fx.OnBranch("b", "develop", "b1", ("b.txt", "b1\n"));
        var a1 = MergeFixture.Git(fx.Checkout, "rev-parse", "a"); var b1 = MergeFixture.Git(fx.Checkout, "rev-parse", "b");
        fx.Switch("a"); MergeFixture.Git(fx.Checkout, "merge", "-q", "--no-ff", "-m", "a<-b1", b1);
        fx.Write("a.txt", "a2\n"); fx.Commit("a2"); fx.Push("a");
        fx.Switch("b"); MergeFixture.Git(fx.Checkout, "merge", "-q", "--no-ff", "-m", "b<-a1", a1);
        fx.Write("b.txt", "b2\n"); fx.Commit("b2"); fx.Push("b");
        var (_, m) = fx.Engines();
        var plan = await fx.AnalyzeAsync(m, "a", "b");
        Assert.Equal(2, plan.MergeBases.Count);
        Assert.Equal(MergeOutcome.Clean, plan.Outcome);
        Assert.Contains(plan.Findings, f => f.Code == "MULTIPLE_MERGE_BASES");
        // The analysis tree equals what `git merge` produces.
        fx.Switch("b"); MergeFixture.Git(fx.Checkout, "merge", "-q", "--no-ff", "-m", "check", "a");
        Assert.Equal(MergeFixture.Git(fx.Checkout, "rev-parse", "HEAD^{tree}"), plan.ResultTree);
    }

    [Fact]
    public async Task Plans_are_immutable_and_tamper_evident()
    {
        using var fx = new MergeFixture();
        fx.OnBranch("feature/t", "develop", "t", ("src/t.txt", "t\n"));
        var (_, m) = fx.Engines();
        var plan = await fx.AnalyzeAsync(m, "feature/t", "develop");
        var file = Path.Combine(m.Store.PlanDir(plan.Id), "plan.json");
        Assert.True(File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly));
        File.SetAttributes(file, FileAttributes.Normal);
        File.WriteAllText(file, File.ReadAllText(file).Replace(plan.Source.Sha, plan.Target.Sha));
        var ex = Assert.Throws<MergeException>(() => m.Store.LoadPlan(plan.Id));
        Assert.Equal(MergeErrorKind.Stale, ex.Kind);
    }

    [Fact]
    public async Task Ref_namespaces_are_explicit_and_selection_never_falls_back()
    {
        using var fx = new MergeFixture();
        fx.Switch("develop"); fx.Switch("local-only", create: true); fx.Write("l.txt", "l\n"); fx.Commit("local");
        var (_, m) = fx.Engines();
        await m.Refs.RefreshAsync("app", false, CancellationToken.None);
        var list = await m.Refs.ListAsync("app");
        Assert.Contains(list.Refs, r => r.Namespace == RefNamespace.Local && r.Name == "local-only");
        Assert.DoesNotContain(list.Refs, r => r.Namespace == RefNamespace.Remote && r.Name == "local-only");
        Assert.False(list.Freshness.Stale);
        var ex = await Assert.ThrowsAsync<MergeException>(() => m.Refs.ResolveAsync("app", new RefSelection(RefNamespace.Remote, "local-only")));
        Assert.Equal(MergeErrorKind.NotFound, ex.Kind);
        foreach (var bad in new[] { "../x", "-x", "a b", "HEAD~1", "x^{tree}", "a..b" })
            await Assert.ThrowsAsync<MergeException>(() => m.Refs.ResolveAsync("app", new RefSelection(RefNamespace.Remote, bad)));
        var pinned = await m.Refs.ResolveAsync("app", new RefSelection(RefNamespace.Local, "local-only"));
        Assert.Equal(fx.Head, pinned.Sha);
        Assert.Equal("refs/fb/local/heads/local-only", pinned.FullRef);
    }
}
