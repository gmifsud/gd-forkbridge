using ForkBridge.Alignment.Model;
using ForkBridge.Alignment.Workspace;
using static ForkBridge.Alignment.Tests.AlignFixture;

namespace ForkBridge.Alignment.Tests;

public class WorktreeOpsTests
{
    /// <summary>Primary gets a bare "origin" with an extra branch that has no local branch or worktree, and a
    /// second clone ("colleague") that can push new commits to origin.</summary>
    private static (AlignFixture Fx, WorktreeOps Ops, string Origin, string Colleague) Build()
    {
        var fx = new AlignFixture("backend");
        var origin = Path.Combine(fx.Root, "origin.git");
        G(fx.Root, "init", "-q", "--bare", origin);
        G(fx.Primary, "remote", "add", "origin", origin);
        G(fx.Primary, "push", "-q", "origin", "Development");
        G(fx.Primary, "branch", "-q", "--set-upstream-to=origin/Development", "Development");
        var colleague = Path.Combine(fx.Root, "colleague");
        G(fx.Root, "clone", "-q", origin, colleague);
        G(colleague, "checkout", "-q", "-b", "feature/24500-external-services-groups");
        W(colleague, "Shared/Remote.cs", "namespace S; class Remote { }\n");
        Commit(colleague, "work on the remote branch");
        G(colleague, "push", "-q", "origin", "feature/24500-external-services-groups");
        fx.WriteAlignConfig(BackendProfileYaml.Replace("@SYNC@", fx.SyncBase)
            .Replace("primary: { repo: qisl,", $"primary: {{ repo: qisl, worktreeRoot: \"{fx.Root.Replace('\\', '/')}/P.worktrees\","));
        return (fx, new WorktreeOps(fx.Profile(), fx.Git, _ => "origin"), origin, colleague);
    }

    [Fact]
    public async Task Fetch_lists_remote_branches_and_creates_an_idempotent_tracking_worktree_under_worktreeRoot()
    {
        var (fx, ops, _, _) = Build();
        using var _f = fx;
        await ops.FetchAsync(Side.Primary);
        var branches = await ops.ListRemoteBranchesAsync(Side.Primary);
        var b = branches.Single(x => x.Name == "feature/24500-external-services-groups");
        Assert.False(b.LocalExists);
        Assert.Null(b.WorktreeId);
        Assert.NotNull(branches.Single(x => x.Name == "Development").WorktreeId);                   // main checkout has it

        var r = await ops.CreateWorktreeAsync(Side.Primary, b.Name);
        Assert.True(r.Created); Assert.True(r.TrackingBranchCreated);
        Assert.Equal(Path.Combine(fx.Root, "P.worktrees", "feature", "24500-external-services-groups"), r.Worktree.Path);
        Assert.Equal(b.Sha, r.Worktree.Head);
        Assert.Equal("origin/feature/24500-external-services-groups", G(r.Worktree.Path, "rev-parse", "--abbrev-ref", "@{upstream}"));
        Assert.True(File.Exists(Path.Combine(r.Worktree.Path, "Shared", "Remote.cs")));

        var again = await ops.CreateWorktreeAsync(Side.Primary, b.Name);
        Assert.False(again.Created);
        Assert.Equal(r.Worktree.Id, again.Worktree.Id);
        Assert.Equal(r.Worktree.Id, (await ops.ListRemoteBranchesAsync(Side.Primary)).Single(x => x.Name == b.Name).WorktreeId);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("-x")]
    [InlineData("a..b")]
    [InlineData("feature/does-not-exist")]
    public async Task Bad_or_unknown_branch_names_are_refused_without_creating_anything(string name)
    {
        var (fx, ops, _, _) = Build();
        using var _f = fx;
        await ops.FetchAsync(Side.Primary);
        var before = G(fx.Primary, "worktree", "list", "--porcelain");
        await Assert.ThrowsAsync<AlignmentException>(() => ops.CreateWorktreeAsync(Side.Primary, name));
        Assert.Equal(before, G(fx.Primary, "worktree", "list", "--porcelain"));
        Assert.False(Directory.Exists(Path.Combine(fx.Root, "escape")));
    }

    [Fact]
    public async Task Fast_forward_moves_a_clean_worktree_to_origin_and_refuses_dirty_diverged_and_unconfirmed_main()
    {
        var (fx, ops, _, colleague) = Build();
        using var _f = fx;
        await ops.FetchAsync(Side.Primary);
        var wt = (await ops.CreateWorktreeAsync(Side.Primary, "feature/24500-external-services-groups")).Worktree;

        W(colleague, "Shared/Remote.cs", "namespace S; class Remote { int v = 2; }\n");
        var newTip = Commit(colleague, "more remote work");
        G(colleague, "push", "-q", "origin", "feature/24500-external-services-groups");

        var ff = await ops.FastForwardAsync(wt.Id, confirmMainCheckout: false);          // fetches by itself
        Assert.False(ff.AlreadyUpToDate);
        Assert.Equal(1, ff.Commits);
        Assert.Equal(newTip, ff.After);
        Assert.Equal(newTip, G(wt.Path, "rev-parse", "HEAD"));
        Assert.True((await ops.FastForwardAsync(wt.Id, false)).AlreadyUpToDate);

        W(wt.Path, "Shared/Remote.cs", "dirty\n");                                          // tracked change → refused
        await Assert.ThrowsAsync<AlignmentException>(() => ops.FastForwardAsync(wt.Id, false));
        G(wt.Path, "checkout", "-q", "--", "Shared/Remote.cs");

        W(wt.Path, "Shared/Local.cs", "namespace S; class Local { }\n");                  // local commit + remote commit → diverged
        var local = Commit(wt.Path, "local only");
        W(colleague, "Shared/Other2.cs", "namespace S; class Other2 { }\n");
        Commit(colleague, "remote moves on");
        G(colleague, "push", "-q", "origin", "feature/24500-external-services-groups");
        var ex = await Assert.ThrowsAsync<AlignmentException>(() => ops.FastForwardAsync(wt.Id, false));
        Assert.Contains("local commit", ex.Message);
        Assert.Equal(local, G(wt.Path, "rev-parse", "HEAD"));                              // untouched

        var main = (await new WorktreeCatalog(fx.Profile(), fx.Git).ListAsync(false)).Single(w => w.Side == Side.Primary && w.IsMain);
        await Assert.ThrowsAsync<AlignmentException>(() => ops.FastForwardAsync(main.Id, confirmMainCheckout: false));
        Assert.True((await ops.FastForwardAsync(main.Id, confirmMainCheckout: true)).AlreadyUpToDate);
    }
}
