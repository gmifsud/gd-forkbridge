using ForkBridge.Alignment.Model;
using ForkBridge.Alignment.Planning;
using ForkBridge.Alignment.Storage;
using ForkBridge.Alignment.Workspace;
using ForkBridge.Core.Storage;

namespace ForkBridge.Alignment.Tests;

public class PlannerTests
{
    private static async Task<(AlignFixture Fx, AlignmentPlan Plan, AlignmentPlanner Planner)> PlanBackendAsync()
    {
        var fx = new AlignFixture("backend");
        fx.WriteAlignConfig(AlignFixture.BackendProfileYaml.Replace("@SYNC@", fx.SyncBase));
        var planner = new AlignmentPlanner(fx.Profile(), fx.Git);
        var plan = await planner.PlanAsync(new AlignmentPlanRequest(new SideSelection { Rev = fx.PrimaryTip }, new SideSelection { Rev = fx.ForkTip }));
        return (fx, plan, planner);
    }

    private static AlignmentRow Row(AlignmentPlan p, string path) => p.Rows.Single(r => r.Path == path);

    [Fact]
    public async Task Backend_fixture_classifies_every_category_like_the_PS_suite()
    {
        var (fx, plan, _) = await PlanBackendAsync();
        using var _ = fx;
        void Expect(string path, Category c, Decision d)
        {
            var r = Row(plan, path);
            Assert.True(r.Category == c && r.Decision == d, $"{path}: expected {c}/{d}, got {r.Category}/{r.Decision} ({r.Reason})");
        }
        Expect("Shared/A.cs", Category.PrimaryChanged, Decision.TakePrimary);
        Expect("Shared/New.cs", Category.PrimaryAdded, Decision.TakePrimary);
        Expect("Shared/B.cs", Category.ForkChanged, Decision.TakeFork);
        Expect("Shared/C.cs", Category.BothChanged, Decision.Merged);
        Expect("Shared/E.cs", Category.Conflict, Decision.Manual);
        Expect("Shared/D.cs", Category.ForkDeleted, Decision.TakePrimary);
        Expect("Shared/Fmt.cs", Category.FormatOnly, Decision.TakePrimary);
        Expect("Shared/Renamed.cs", Category.ForkRenameSource, Decision.TakePrimary);
        Expect("Shared/Renamed2.cs", Category.ForkRenamed, Decision.DeleteBoth);
        Expect("Shared/IslOnly.cs", Category.ForkOnly, Decision.KeepForkOnly);
        Expect("Shared/_Fork/Own.cs", Category.ForkOwned, Decision.KeepForkOnly);
        Expect("Shared/Stale.cs", Category.ForkStale, Decision.DeleteBoth);
        Expect("Shared/Stale2.cs", Category.ForkStale, Decision.KeepForkOnly);          // still referenced by IslOnly.cs
        Expect("Directory.Packages.props", Category.Packages, Decision.ManifestUnion);
        Assert.DoesNotContain(plan.Rows, r => r.Path.StartsWith("Shared/Host/"));      // protected
        Assert.DoesNotContain(plan.Rows, r => r.Path.StartsWith("Product/"));          // out of scope
        Assert.Equal(1, plan.Identical);                                                 // Shared/Same.cs
        Assert.Contains("IslOnly.cs", Row(plan, "Shared/Stale2.cs").Refs);
        Assert.Contains("[", Row(plan, "Shared/B.cs").Reason);                          // reference-ref tag appended
        var merged = Row(plan, "Shared/C.cs").Merged!;
        Assert.NotNull(merged);
    }

    [Fact]
    public async Task Planning_is_deterministic_and_read_only_for_both_repositories()
    {
        var fx = new AlignFixture("backend");
        using var _ = fx;
        fx.WriteAlignConfig(AlignFixture.BackendProfileYaml.Replace("@SYNC@", fx.SyncBase));
        string Snapshot(string repo) => string.Join("|",
            AlignFixture.G(repo, "rev-parse", "HEAD"), AlignFixture.G(repo, "status", "--porcelain=v1", "--untracked-files=all"),
            AlignFixture.G(repo, "for-each-ref", "--format=%(refname) %(objectname)"), AlignFixture.G(repo, "count-objects", "-v"),
            File.ReadAllText(Path.Combine(repo, ".git", "config")));
        var before = (Snapshot(fx.Primary), Snapshot(fx.Fork));
        var planner = new AlignmentPlanner(fx.Profile(), fx.Git);
        var req = new AlignmentPlanRequest(new SideSelection { Rev = fx.PrimaryTip }, new SideSelection { Rev = fx.ForkTip });
        var a = await planner.PlanAsync(req);
        var b = await planner.PlanAsync(req);
        Assert.Equal(a.ContentHash, b.ContentHash);
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(before, (Snapshot(fx.Primary), Snapshot(fx.Fork)));
    }

    [Fact]
    public async Task Merged_row_holds_the_3way_result()
    {
        var (fx, plan, _) = await PlanBackendAsync();
        using var _ = fx;
        var sha = Row(plan, "Shared/C.cs").Merged!.Sha;
        var text = AlignFixture.G(Path.Combine(plan.Profile == "backend" ? fx.StateDir : fx.StateDir, "backend", "xrepo.git"), "cat-file", "blob", sha);
        Assert.StartsWith("line1-primary", text);
        Assert.EndsWith("line7-fork", text);
    }

    [Fact]
    public async Task Frontend_flags_detect_a_fork_behind_the_primary_and_union_package_json()
    {
        var fx = new AlignFixture("frontend");
        using var _ = fx;
        fx.WriteAlignConfig(AlignFixture.FrontendProfileYaml.Replace("@SYNC@", fx.SyncBase));
        var plan = await new AlignmentPlanner(fx.Profile("frontend"), fx.Git)
            .PlanAsync(new AlignmentPlanRequest(new SideSelection { Rev = fx.PrimaryTip }, new SideSelection { Rev = fx.ForkTip }));
        Assert.Equal((Category.ForkBehind, Decision.TakePrimary), (Row(plan, "app/src/a.service.ts").Category, Row(plan, "app/src/a.service.ts").Decision));
        Assert.Equal((Category.PrimaryChanged, Decision.TakePrimary), (Row(plan, "app/src/b.service.ts").Category, Row(plan, "app/src/b.service.ts").Decision));
        Assert.Equal((Category.Packages, Decision.ManifestUnion), (Row(plan, "app/package.json").Category, Row(plan, "app/package.json").Decision));
    }

    [Fact]
    public async Task Store_saves_immutable_plans_and_detects_tampering()
    {
        var (fx, plan, planner) = await PlanBackendAsync();
        using var _ = fx;
        var store = new AlignmentStore(fx.Profile().StateDir);
        var dir = store.Save(plan, planner.PrimaryCode);
        Assert.True(File.Exists(Path.Combine(dir, "plan.tsv")) && File.Exists(Path.Combine(dir, "plan.md")));
        Assert.Equal(plan.ContentHash, store.Load(plan.Id).ContentHash);
        Assert.Throws<PlanIntegrityException>(() => store.Save(plan, planner.PrimaryCode));
        var json = Path.Combine(dir, "plan.json");
        File.SetAttributes(json, FileAttributes.Normal);
        File.WriteAllText(json, File.ReadAllText(json).Replace("\"TakeFork\"", "\"TakePrimary\""));
        Assert.Throws<PlanIntegrityException>(() => store.Load(plan.Id));
    }

    [Fact]
    public async Task Parity_checker_passes_against_its_own_tsv_and_reports_deltas()
    {
        var (fx, plan, planner) = await PlanBackendAsync();
        using var _ = fx;
        var tsv = Path.Combine(fx.Root, "golden.tsv");
        File.WriteAllText(tsv, PlanTsv.Write(plan, planner.PrimaryCode));
        var ok = ParityChecker.Compare(tsv, plan, planner.PrimaryCode);
        Assert.True(ok.Pass, ok.ToMarkdown());
        File.WriteAllText(tsv, File.ReadAllText(tsv).Replace("\tISL_CHANGED\tTAKE_ISL\t", "\tISL_CHANGED\tKEEP_BOTH\t"));
        var bad = ParityChecker.Compare(tsv, plan, planner.PrimaryCode);
        Assert.False(bad.Pass);
        Assert.Contains(bad.Deltas, d => d.Path == "Shared/B.cs" && d.Field == "Decision" && d.Reference == "KEEP_BOTH" && d.Port == "TAKE_ISL");
        var inputs = ParityChecker.GoldenInputs(tsv, planner.PrimaryCode);
        Assert.Equal((fx.PrimaryTip, fx.ForkTip, fx.SyncBase), (inputs.PrimaryRev, inputs.ForkRev, inputs.SyncBase));
    }

    [Fact]
    public async Task Worktree_catalogue_lists_linked_worktrees_and_worktree_planning_refuses_a_dirty_scope()
    {
        var fx = new AlignFixture("backend");
        using var _ = fx;
        fx.WriteAlignConfig(AlignFixture.BackendProfileYaml.Replace("@SYNC@", fx.SyncBase));
        var wt = Path.Combine(fx.Root, "Primary.worktrees", "align");
        AlignFixture.G(fx.Primary, "worktree", "add", "-q", "-b", "migrations/align", wt);
        var catalog = new WorktreeCatalog(fx.Profile(), fx.Git);
        var list = await catalog.ListAsync();
        var prim = list.Where(w => w.Side == Side.Primary).ToList();
        Assert.Equal(2, prim.Count);
        Assert.True(prim[0].IsMain && prim[0].Protected);                                 // Development is in neverPush
        var linked = prim.Single(w => w.Branch == "migrations/align");
        Assert.Equal(false, linked.ScopeDirty);
        Assert.Equal(linked.Id, (await catalog.ResolveAsync(linked.Id)).Id);
        Assert.Equal(linked.Id, WorktreeCatalog.IdFor(Side.Primary, linked.Path));

        var forkMain = list.Single(w => w.Side == Side.Fork && w.IsMain);
        var planner = new AlignmentPlanner(fx.Profile(), fx.Git);
        var clean = await planner.PlanAsync(new AlignmentPlanRequest(new SideSelection { WorktreePath = linked.Path }, new SideSelection { WorktreePath = forkMain.Path }));
        Assert.Equal("migrations/align", clean.Primary.Branch);

        AlignFixture.W(wt, "Shared/A.cs", "dirty\n");
        Assert.True((await catalog.ListAsync()).Single(w => w.Id == linked.Id).ScopeDirty);
        var ex = await Assert.ThrowsAsync<AlignmentException>(() => planner.PlanAsync(new AlignmentPlanRequest(new SideSelection { WorktreePath = linked.Path }, new SideSelection { WorktreePath = forkMain.Path })));
        Assert.Contains("uncommitted changes in the aligned scope", ex.Message);

        // Explicit opt-in: plan the committed HEAD; the uncommitted edit is not part of the plan.
        var head = AlignFixture.G(wt, "rev-parse", "HEAD");
        var committed = await planner.PlanAsync(new AlignmentPlanRequest(new SideSelection { WorktreePath = linked.Path, IgnoreUncommitted = true }, new SideSelection { WorktreePath = forkMain.Path }));
        Assert.Equal(head, committed.Primary.Rev);
        Assert.Equal("migrations/align", committed.Primary.Branch);
        Assert.Equal(linked.Path, committed.Primary.WorktreePath);
        Assert.Equal(clean.ContentHash, committed.ContentHash);                            // same commits → same plan; the dirty edit changed nothing
    }
}
