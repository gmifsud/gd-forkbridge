using ForkBridge.Core;
using ForkBridge.Core.Git;
using ForkBridge.Core.Model;
using ForkBridge.Core.Planning;
using ForkBridge.Core.Transfer;
using ForkBridge.Tests.Fixtures;

namespace ForkBridge.Tests;

public class HistoryAndIdempotencyTests
{
    private const string Audit2 = "namespace Kernel.Domain;\n\npublic class Audit\n{\n    public string Who { get; set; } = \"\";\n    public string Reason { get; set; } = \"\";\n}\n";

    /// <summary>Simulates a merged PR: the candidate becomes the target's development branch.</summary>
    private static void Land(RepoFixture fx, ForkBridgeEngine e, Run run, string alias, string remoteName)
    {
        RepoFixture.Git(e.Store.MirrorDir(alias), "push", "-q", fx.Remote(remoteName), $"{run.CandidateSha}:refs/heads/development");
    }

    [Fact]
    public async Task Development_branch_is_never_turned_into_an_invented_feature_range()
    {
        using var fx = new RepoFixture(frontend: false);
        var e = fx.Engine();
        var ex = await Assert.ThrowsAsync<PlanningException>(() => T.Feature(e, "backend", "qisl", "isl", "development"));
        Assert.Contains("will not invent a feature range", ex.Message);
    }

    [Fact]
    public async Task Explicit_range_and_ordered_commit_list_are_distinct_selections()
    {
        using var fx = new RepoFixture(frontend: false);
        var a = T.OnDevelopment(fx.QislBackend, "A", ("Fabric/Kernel/Kernel.Domain/Audit.cs", Audit2));
        var b = T.OnDevelopment(fx.QislBackend, "B", ("Fabric/Platform/Platform.Core/Service.cs", "namespace Platform.Core;\npublic class Service { public string Describe(Kernel.Domain.Entity e) => e.Name + e.Id; }\n"));
        var e = fx.Engine();
        var range = await e.Planner.PlanAsync(new PlanRequest { Pair = "backend", From = "qisl", To = "isl", Mode = SelectionMode.Range, Range = $"{a}~1..{b}" });
        Assert.Equal(new[] { a, b }, range.Commits.Select(c => c.Sha));
        var list = await T.Commits(e, "backend", "qisl", "isl", b);
        Assert.Equal(b, list.Commits.Single().Sha);
        Assert.Equal(SelectionMode.Commits, list.Mode);
        await Assert.ThrowsAsync<PlanningException>(() => e.Planner.PlanAsync(new PlanRequest { Pair = "backend", From = "qisl", To = "isl", Mode = SelectionMode.Range, Range = $"{b}..{a}" }));
    }

    [Fact]
    public async Task Merge_only_conflict_resolution_changes_are_detected_and_block()
    {
        using var fx = new RepoFixture(frontend: false);
        var q = fx.QislBackend;
        T.Branch(q, "feat/merge", "Feature edit", ("Fabric/Kernel/Kernel.Domain/Audit.cs", "namespace Kernel.Domain;\npublic class Audit { public string Who { get; set; } = \"feature\"; }\n"));
        T.OnDevelopment(q, "Dev edit", ("Fabric/Kernel/Kernel.Domain/Audit.cs", "namespace Kernel.Domain;\npublic class Audit { public string Who { get; set; } = \"dev\"; }\n"));
        RepoFixture.Git(q, "switch", "-q", "feat/merge");
        try { RepoFixture.Git(q, "merge", "-q", "development"); } catch (InvalidOperationException) { }
        RepoFixture.Write(q, "Fabric/Kernel/Kernel.Domain/Audit.cs", "namespace Kernel.Domain;\npublic class Audit { public string Who { get; set; } = \"resolved-by-hand\"; }\n");
        RepoFixture.Git(q, "add", "-A");
        RepoFixture.Git(q, "commit", "-q", "--no-edit");
        RepoFixture.Push(q, "feat/merge");
        var e = fx.Engine();
        var plan = await e.Planner.PlanAsync(new PlanRequest { Pair = "backend", From = "qisl", To = "isl", Mode = SelectionMode.Range, Range = "development~1..feat/merge" });
        Assert.Equal(RunStatus.BLOCKED, plan.PreliminaryStatus);
        var f = plan.Findings.Single(x => x.Code == "MERGE_RESOLUTION_CHANGES");
        Assert.Contains("Fabric/Kernel/Kernel.Domain/Audit.cs", f.Files);
        Assert.Contains("reconcile", f.SuggestedAction);
    }

    [Fact]
    public async Task Filtered_merge_ancestry_hiding_missing_shared_content_is_detected_and_reconcile_repairs_it()
    {
        using var fx = new RepoFixture(frontend: false);
        var q = fx.QislBackend; var i = fx.IslBackend;
        var before = T.OnDevelopment(q, "Shared change", ("Fabric/Kernel/Kernel.Domain/Audit.cs", Audit2));
        // Legacy-style fork-over: merge everything, then discard a shared file ("pin") before committing.
        RepoFixture.Git(i, "remote", "add", "qisl", fx.Remote("Backend"));
        RepoFixture.Git(i, "fetch", "-q", "qisl");
        RepoFixture.Git(i, "merge", "-q", "--no-ff", "--no-commit", "qisl/development");
        RepoFixture.Git(i, "checkout", "HEAD", "--", "Fabric/Kernel/Kernel.Domain/Audit.cs");
        RepoFixture.Git(i, "commit", "-q", "-m", "Fork-over (filtered)\n\nFork-Over-Source: qisl@" + before);
        RepoFixture.Git(i, "remote", "remove", "qisl");
        RepoFixture.Push(i);

        var e = fx.Engine();
        var plain = await e.Planner.PlanAsync(new PlanRequest { Pair = "backend", From = "qisl", To = "isl", Mode = SelectionMode.Reconcile });
        Assert.True(plain.Findings.Has("ANCESTRY_CONTENT_MISMATCH"));
        Assert.NotEqual(CommitState.Pending, plain.Reconciliation!.State);

        // Same change again via pick: ancestry says merged, content is missing -> review, not silently skipped.
        var pick = await T.Commits(e, "backend", "qisl", "isl", before);
        Assert.Equal(CommitState.PreviouslyTransferredMissing, pick.Commits.Single().State);
        Assert.True(pick.Findings.Has("TRANSFERRED_BUT_MISSING"));

        // Reconcile from an explicit baseline before the filtered merge restores the content.
        var rec = await e.Planner.PlanAsync(new PlanRequest { Pair = "backend", From = "qisl", To = "isl", Mode = SelectionMode.Reconcile, Baseline = before + "~1" });
        Assert.Equal(CommitState.Pending, rec.Reconciliation!.State);
        var run = await e.Simulator.SimulateAsync(rec.Id, new SimulateOptions { TrustValidation = true });
        Assert.Contains("Reason", T.MirrorFile(e, "isl-backend", run.CandidateSha!, "Fabric/Kernel/Kernel.Domain/Audit.cs"));
        var msg = RepoFixture.Git(e.Store.MirrorDir("isl-backend"), "log", "-1", "--format=%an|%B", run.CandidateSha!);
        Assert.Contains("ForkBridge-Reconcile-Source: qisl@", msg);
        Assert.DoesNotContain("Fork-Over-Source", msg); // never misrepresented as the original commits
    }

    [Fact]
    public async Task Partial_transfer_then_expanded_component_selection_transfers_the_rest()
    {
        using var fx = new RepoFixture(frontend: false);
        var sha = T.Branch(fx.QislBackend, "feat/two", "Kernel and platform",
            ("Fabric/Kernel/Kernel.Domain/Audit.cs", Audit2),
            ("Fabric/Platform/Platform.Core/Service.cs", "namespace Platform.Core;\npublic class Service { public string Describe(Kernel.Domain.Entity e) => \"v2:\" + e.Name; }\n"));
        var e = fx.Engine();
        var p1 = await T.Feature(e, "backend", "qisl", "isl", "feat/two", "kernel");
        Assert.True(p1.Findings.Has("MIXED_COMMIT"));
        var r1 = await T.Simulate(e, p1);
        Land(fx, e, r1, "isl-backend", "ISL-Backend");
        Assert.Contains("ForkBridge-Partial", RepoFixture.Git(e.Store.MirrorDir("isl-backend"), "log", "-1", "--format=%B", r1.CandidateSha!));

        var p2 = await T.Feature(e, "backend", "qisl", "isl", "feat/two", "kernel", "platform");
        var pc = p2.Commits.Single();
        Assert.Equal(CommitState.Pending, pc.State); // earlier partial provenance does NOT suppress the new component
        Assert.True(p2.Findings.Has("PARTIALLY_PRESENT"));
        var r2 = await T.Simulate(e, p2);
        Assert.Contains("v2:", T.MirrorFile(e, "isl-backend", r2.CandidateSha!, "Fabric/Platform/Platform.Core/Service.cs"));
    }

    [Fact]
    public async Task Repeated_runs_equivalents_reverts_and_edited_equivalents()
    {
        using var fx = new RepoFixture(frontend: false);
        var sha = T.Branch(fx.QislBackend, "feat/audit", "Audit reason", ("Fabric/Kernel/Kernel.Domain/Audit.cs", Audit2));
        var e = fx.Engine();
        var r1 = await T.Simulate(e, await T.Feature(e, "backend", "qisl", "isl", "feat/audit"));
        Assert.Equal(RunStatus.READY, r1.Status);
        Land(fx, e, r1, "isl-backend", "ISL-Backend");

        // 1. Repeat: content present -> NO_CHANGES.
        var again = await T.Feature(e, "backend", "qisl", "isl", "feat/audit");
        Assert.Equal(CommitState.EquivalentPresent, again.Commits.Single().State);
        Assert.Equal(RunStatus.NO_CHANGES, again.PreliminaryStatus);

        // 2. Reverted in the target -> provenance says transferred, content missing -> REVIEW.
        RepoFixture.Git(fx.IslBackend, "pull", "-q", "origin", "development");
        RepoFixture.Git(fx.IslBackend, "revert", "--no-edit", "HEAD");
        RepoFixture.Push(fx.IslBackend);
        var reverted = await T.Feature(e, "backend", "qisl", "isl", "feat/audit");
        Assert.Equal(CommitState.PreviouslyTransferredMissing, reverted.Commits.Single().State);
        Assert.Equal(RunStatus.REVIEW_REQUIRED, reverted.PreliminaryStatus);
        // Explicit re-apply is possible.
        var reapply = await e.Planner.PlanAsync(new PlanRequest { Pair = "backend", From = "qisl", To = "isl", Mode = SelectionMode.Feature, SourceRef = "feat/audit", Reapply = [sha[..10]] });
        Assert.Equal(CommitState.Pending, reapply.Commits.Single().State);

        // 3. Equivalent change made by hand in the target (no trailer) -> recognised by content.
        T.OnDevelopment(fx.IslBackend, "Same change by hand", ("Fabric/Kernel/Kernel.Domain/Audit.cs", Audit2));
        var manual = await T.Feature(e, "backend", "qisl", "isl", "feat/audit");
        Assert.Equal(CommitState.EquivalentPresent, manual.Commits.Single().State);

        // 4. Edited equivalent: target changed the transferred lines further -> conservative REVIEW, not silent skip.
        T.OnDevelopment(fx.IslBackend, "Edit", ("Fabric/Kernel/Kernel.Domain/Audit.cs", Audit2.Replace("Reason", "Cause")));
        var edited = await T.Feature(e, "backend", "qisl", "isl", "feat/audit");
        Assert.Equal(CommitState.PreviouslyTransferredMissing, edited.Commits.Single().State);
    }

    [Fact]
    public async Task Target_base_movement_after_planning_invalidates_the_plan()
    {
        using var fx = new RepoFixture(frontend: false);
        T.Branch(fx.QislBackend, "feat/a", "a", ("Fabric/Kernel/Kernel.Domain/Audit.cs", Audit2));
        var e = fx.Engine();
        var plan = await T.Feature(e, "backend", "qisl", "isl", "feat/a");
        T.OnDevelopment(fx.IslBackend, "moved", ("App.MTL/Mtl/Note.cs", "class Note {}\n"));
        var ex = await Assert.ThrowsAsync<StaleException>(() => T.Simulate(e, plan));
        Assert.Contains("moved", ex.Message);
        // Policy change also invalidates.
        var plan2 = await T.Feature(e, "backend", "qisl", "isl", "feat/a");
        File.AppendAllText(fx.ConfigPath, "\n# touch\n");
        fx.BackendTrusted = false; fx.WriteConfig();
        await Assert.ThrowsAsync<StaleException>(() => T.Simulate(fx.Engine(), plan2));
        // Tampered plan artifacts are refused.
        var plan3 = await T.Feature(fx.Engine(), "backend", "qisl", "isl", "feat/a");
        var planFile = Path.Combine(fx.Engine().Store.PlanDir(plan3.Id), "plan.json");
        File.SetAttributes(planFile, FileAttributes.Normal);
        File.WriteAllText(planFile, File.ReadAllText(planFile).Replace("\"pending\"", "\"Pending\"").Replace(plan3.TargetBaseSha, new string('0', 40)));
        Assert.Throws<ForkBridge.Core.Storage.PlanIntegrityException>(() => fx.Engine().Store.LoadPlan(plan3.Id));
    }

    [Fact]
    public async Task Remote_fetch_failure_fails_by_default_and_offline_mode_records_staleness()
    {
        using var fx = new RepoFixture(frontend: false);
        T.Branch(fx.QislBackend, "feat/o", "o", ("Fabric/Kernel/Kernel.Domain/Audit.cs", Audit2));
        // Test setup only: make the target's remote unreachable.
        RepoFixture.Git(fx.IslBackend, "remote", "set-url", "origin", Path.Combine(fx.Root, "does-not-exist.git"));
        var e = fx.Engine();
        await Assert.ThrowsAsync<FreshnessException>(() => T.Feature(e, "backend", "qisl", "isl", "feat/o"));
        var plan = await e.Planner.PlanAsync(new PlanRequest { Pair = "backend", From = "qisl", To = "isl", Mode = SelectionMode.Feature, SourceRef = "feat/o", Offline = true });
        Assert.True(plan.Freshness.Offline);
        Assert.True(plan.Findings.Has("OFFLINE_REFS"));
        Assert.Equal(RunStatus.REVIEW_REQUIRED, plan.PreliminaryStatus);
    }

    [Fact]
    public async Task Advanced_merge_mode_reports_ancestry_and_resets_excluded_paths()
    {
        using var fx = new RepoFixture(frontend: false) { BackendModes = "[pick, reconcile, merge]" };
        fx.WriteConfig();
        T.OnDevelopment(fx.QislBackend, "Shared + VAM", ("Fabric/Kernel/Kernel.Domain/Audit.cs", Audit2), ("App.VAM/Vam/New.cs", "class N {}\n"),
            ("Fabric/Platform/Web.Host/appsettings.json", "{ \"Env\": \"qisl-changed\" }\n"));
        var e = fx.Engine();
        var plan = await e.Planner.PlanAsync(new PlanRequest { Pair = "backend", From = "qisl", To = "isl", Mode = SelectionMode.Merge });
        Assert.True(plan.Findings.Has("MERGE_ANCESTRY"));
        var run = await T.Simulate(e, plan);
        Assert.True(run.CandidateSha is not null, $"{run.Status} {run.Error} {run.Conflict?.Operation} {run.Conflict?.Output} files={string.Join(",", run.Conflict?.Files ?? [])} log={string.Join(" | ", run.Log)}");
        var mirror = e.Store.MirrorDir("isl-backend");
        var tree = RepoFixture.Git(mirror, "ls-tree", "-r", "--name-only", run.CandidateSha!);
        Assert.DoesNotContain("App.VAM/Vam/New.cs", tree);
        Assert.Contains("isl-base", T.MirrorFile(e, "isl-backend", run.CandidateSha!, "Fabric/Platform/Web.Host/appsettings.json"));
        Assert.Contains("Reason", T.MirrorFile(e, "isl-backend", run.CandidateSha!, "Fabric/Kernel/Kernel.Domain/Audit.cs"));
        Assert.Equal(2, RepoFixture.Git(mirror, "log", "-1", "--format=%P", run.CandidateSha!).Split(' ').Length);
    }

    [Fact]
    public async Task Change_to_a_file_the_target_renamed_is_readdressed_and_applies()
    {
        using var fx = new RepoFixture(frontend: false);
        RepoFixture.Git(fx.IslBackend, "switch", "-q", "development");
        RepoFixture.Git(fx.IslBackend, "mv", "Fabric/Kernel/Kernel.Domain/Audit.cs", "Fabric/Kernel/Kernel.Domain/AuditEntry.cs");
        RepoFixture.Commit(fx.IslBackend, "ISL renames Audit.cs");
        RepoFixture.Push(fx.IslBackend);
        T.Branch(fx.QislBackend, "feat/audit-when", "Audit gets When",
            ("Fabric/Kernel/Kernel.Domain/Audit.cs", "namespace Kernel.Domain;\n\npublic class Audit\n{\n    public string Who { get; set; } = \"\";\n    public System.DateTime When { get; set; }\n}\n"));
        var e = fx.Engine();

        var plan = await T.Feature(e, "backend", "qisl", "isl", "feat/audit-when");
        Assert.True(plan.Findings.Has("TARGET_RENAMED"));
        Assert.False(plan.Findings.Has("MODIFY_DELETE"));
        Assert.Contains(plan.Commits.Single().Paths, p => p.SourcePath.EndsWith("Audit.cs") && p.TargetPath!.EndsWith("AuditEntry.cs") && p.Decision == Decision.Include);
        var run = await T.Simulate(e, plan);
        Assert.Null(run.Conflict);
        Assert.Contains("When", T.MirrorFile(e, "isl-backend", run.CandidateSha!, "Fabric/Kernel/Kernel.Domain/AuditEntry.cs"));
        Assert.Throws<InvalidOperationException>(() => T.MirrorFile(e, "isl-backend", run.CandidateSha!, "Fabric/Kernel/Kernel.Domain/Audit.cs"));

        var rec = await e.Planner.PlanAsync(new PlanRequest { Pair = "backend", From = "qisl", To = "isl", Mode = SelectionMode.Reconcile, SourceRef = "feat/audit-when" });
        Assert.Contains(rec.Reconciliation!.Paths, p => p.SourcePath.EndsWith("Audit.cs") && p.TargetPath!.EndsWith("AuditEntry.cs"));
    }

    [Fact]
    public async Task Change_to_a_file_the_target_deleted_blocks_at_plan_time()
    {
        using var fx = new RepoFixture(frontend: false);
        T.OnDevelopment(fx.IslBackend, "ISL drops Audit.cs", ("Fabric/Kernel/Kernel.Domain/Audit.cs", null));
        T.Branch(fx.QislBackend, "feat/audit-edit", "Audit edit",
            ("Fabric/Kernel/Kernel.Domain/Audit.cs", "namespace Kernel.Domain;\n\npublic class Audit\n{\n    public string Who { get; set; } = \"x\";\n}\n"));
        var plan = await T.Feature(fx.Engine(), "backend", "qisl", "isl", "feat/audit-edit");
        Assert.Equal(RunStatus.BLOCKED, plan.PreliminaryStatus);
        Assert.True(plan.Findings.Has("MODIFY_DELETE"));
    }
}
