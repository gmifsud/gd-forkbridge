using System.Text.Json;
using ForkBridge.Core.Model;
using ForkBridge.Core.Planning;
using ForkBridge.Core.Reporting;
using ForkBridge.Core.Transfer;
using ForkBridge.Tests.Fixtures;

namespace ForkBridge.Tests;

/// <summary>Phase A/B vertical slice: one source commit -> classification -> isolated transfer ->
/// consumer validation -> JSON/Markdown report.</summary>
public class VerticalSliceTests
{
    [Fact]
    public async Task One_shared_commit_flows_from_QiSL_to_ISL_and_is_READY()
    {
        using var fx = new RepoFixture();
        var q = fx.QislBackend;
        RepoFixture.Git(q, "switch", "-q", "-c", "feature/audit-timestamp");
        RepoFixture.Write(q, "Fabric/Kernel/Kernel.Domain/Audit.cs", "namespace Kernel.Domain;\n\npublic class Audit\n{\n    public string Who { get; set; } = \"\";\n    public System.DateTime When { get; set; }\n}\n");
        var src = RepoFixture.Commit(q, "Add audit timestamp", author: "Jane Source");
        RepoFixture.Push(q, "feature/audit-timestamp");

        var before = await fx.SnapshotUserReposAsync();
        var e = fx.Engine();
        var plan = await e.Planner.PlanAsync(new PlanRequest { Pair = "backend", From = "qisl", To = "isl", Mode = SelectionMode.Feature, SourceRef = "feature/audit-timestamp" });

        Assert.Equal(RunStatus.PLANNED, plan.PreliminaryStatus);
        var pc = Assert.Single(plan.Commits);
        Assert.Equal(src, pc.Sha);
        Assert.Equal(CommitState.Pending, pc.State);
        var path = Assert.Single(pc.Paths);
        Assert.Equal(Decision.Include, path.Decision);
        Assert.Equal("kernel", path.Component);
        Assert.Equal(RepoFixture.Git(fx.IslBackend, "rev-parse", "origin/development"), plan.TargetBaseSha);

        var run = await e.Simulator.SimulateAsync(plan.Id, new SimulateOptions { TrustValidation = true });
        Assert.Equal(RunStatus.READY, run.Status);
        Assert.NotNull(run.CandidateSha);
        Assert.All(run.ValidationComparison, v => Assert.Equal("OK", v.Verdict));

        // Provenance and authorship preserved in the candidate commit (harness mirror).
        var mirror = e.Store.MirrorDir("isl-backend");
        var msg = RepoFixture.Git(mirror, "log", "-1", "--format=%an|%B", run.CandidateSha!);
        Assert.StartsWith("Jane Source|Add audit timestamp", msg);
        Assert.Contains($"Fork-Over-Source: qisl@{src}", msg);
        Assert.Contains("ForkBridge-Scope: kernel", msg);

        // Reports.
        var json = JsonDocument.Parse(File.ReadAllText(ReportWriter.JsonPath(e.Store, run.Id)));
        Assert.Equal("READY", json.RootElement.GetProperty("run").GetProperty("status").GetString());
        Assert.Contains("READY", File.ReadAllText(ReportWriter.MarkdownPath(e.Store, run.Id)));

        // User checkouts untouched.
        Assert.Equal(before, await fx.SnapshotUserReposAsync());
        Assert.DoesNotContain("Fork-Over", RepoFixture.Git(fx.IslBackend, "log", "--all", "--format=%B"));
    }
}
