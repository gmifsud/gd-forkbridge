using System.Text.Json;
using ForkBridge.Core.Infrastructure;
using ForkBridge.Core.Model;
using ForkBridge.Core.Storage;
using ForkBridge.Core.Transfer;
using ForkBridge.Tests.Fixtures;

namespace ForkBridge.Tests;

[Collection("env")]
public class ValidationAndSafetyTests
{
    private const string Audit2 = "namespace Kernel.Domain;\n\npublic class Audit\n{\n    public string Who { get; set; } = \"\";\n    public string Reason { get; set; } = \"\";\n}\n";

    [Fact]
    public async Task New_failure_vs_baseline_failure_are_distinguished_and_neither_is_ready()
    {
        using var fx = new RepoFixture(frontend: false);
        // New failure: the transfer introduces the "compile error".
        T.Branch(fx.QislBackend, "feat/break", "break", ("Fabric/Kernel/Kernel.Domain/Audit.cs", "// BUILD_BREAK\n" + Audit2));
        var e = fx.Engine();
        var r1 = await T.Simulate(e, await T.Feature(e, "backend", "qisl", "isl", "feat/break"));
        Assert.Equal(RunStatus.VALIDATION_FAILED, r1.Status);
        Assert.Equal("NEW_FAILURE", r1.ValidationComparison.Single(v => v.StepId == "build-shared-and-consumers").Verdict);
        Assert.True(r1.Findings.Has("VALIDATION_NEW_FAILURE"));

        // Baseline failure: the target is already broken; a clean transfer still cannot be READY.
        T.OnDevelopment(fx.IslBackend, "pre-existing break", ("App.MTL/Mtl/Broken.cs", "// BUILD_BREAK\n"));
        T.Branch(fx.QislBackend, "feat/ok", "ok", ("Fabric/Kernel/Kernel.Domain/Entity.cs", "namespace Kernel.Domain;\npublic class Entity { public int Id { get; set; } public string Name { get; set; } = \"\"; public int Rev { get; set; } }\n"));
        var r2 = await T.Simulate(e, await T.Feature(e, "backend", "qisl", "isl", "feat/ok"));
        Assert.Equal(RunStatus.VALIDATION_FAILED, r2.Status);
        Assert.Equal("BASELINE_FAILURE", r2.ValidationComparison.Single().Verdict);
        Assert.Equal(Origin.PreExisting, r2.Findings.Single(f => f.Code == "VALIDATION_BASELINE_FAILURE").Origin);
    }

    [Fact]
    public async Task Untrusted_or_unavailable_checks_are_never_a_pass()
    {
        using var fx = new RepoFixture(frontend: false);
        fx.BackendExtraValidation.Add(("integration-tests", "test", true, ["pass"]));
        fx.WriteConfig();
        File.WriteAllText(fx.ConfigPath, File.ReadAllText(fx.ConfigPath).Replace("- id: integration-tests\n", "- id: integration-tests\n            requiresEnv: [FORKBRIDGE_TEST_DB]\n"));
        T.Branch(fx.QislBackend, "feat/v", "v", ("Fabric/Kernel/Kernel.Domain/Audit.cs", Audit2));
        var e = fx.Engine();
        var plan = await T.Feature(e, "backend", "qisl", "isl", "feat/v");
        var untrusted = await T.Simulate(e, plan, trust: false);
        Assert.Equal(RunStatus.VALIDATION_FAILED, untrusted.Status);
        Assert.All(untrusted.CandidateValidation, v => Assert.Equal("UNAVAILABLE", v.Outcome));

        var plan2 = await T.Feature(e, "backend", "qisl", "isl", "feat/v");
        var noInfra = await T.Simulate(e, plan2);
        var it = noInfra.CandidateValidation.Single(v => v.StepId == "integration-tests");
        Assert.Equal("UNAVAILABLE", it.Outcome);
        Assert.Contains("FORKBRIDGE_TEST_DB", it.Reason);
        Assert.Equal(RunStatus.VALIDATION_FAILED, noInfra.Status);
    }

    [Fact]
    public async Task Credentials_are_scrubbed_from_validation_environment()
    {
        using var fx = new RepoFixture(frontend: false);
        fx.BackendExtraValidation.Add(("no-token-visible", "test", true, ["env", "FB_TEST_ACCESS_TOKEN"]));
        fx.WriteConfig();
        Environment.SetEnvironmentVariable("FB_TEST_ACCESS_TOKEN", "ghp_abcdefghijklmnopqrstuvwxyz0123456789");
        try
        {
            T.Branch(fx.QislBackend, "feat/env", "env", ("Fabric/Kernel/Kernel.Domain/Audit.cs", Audit2));
            var e = fx.Engine();
            var run = await T.Simulate(e, await T.Feature(e, "backend", "qisl", "isl", "feat/env"));
            Assert.Equal("PASSED", run.CandidateValidation.Single(v => v.StepId == "no-token-visible").Outcome);
            foreach (var f in Directory.EnumerateFiles(fx.StateDir, "*.json", SearchOption.AllDirectories))
                Assert.DoesNotContain("ghp_abcdefghijklmnopqrstuvwxyz0123456789", File.ReadAllText(f));
        }
        finally { Environment.SetEnvironmentVariable("FB_TEST_ACCESS_TOKEN", null); }
    }

    /// <summary>Cancels at different points of a simulation (fetch, worktree creation, apply, analysis,
    /// validation) and proves the outcome is always CANCELLED-or-not-started, never a bogus git error or
    /// conflict, and that the run can then be completed.</summary>
    [Theory]
    [InlineData(150)]
    [InlineData(600)]
    [InlineData(1500)]
    [InlineData(3000)]
    public async Task Cancellation_is_recorded_and_the_run_can_be_resumed(int cancelAfterMs)
    {
        using var fx = new RepoFixture(frontend: false);
        fx.BackendExtraValidation.Add(("slow-tests", "test", true, ["sleep", "4000"]));
        fx.WriteConfig();
        T.Branch(fx.QislBackend, "feat/slow", "slow", ("Fabric/Kernel/Kernel.Domain/Audit.cs", Audit2));
        var e = fx.Engine();
        var plan = await T.Feature(e, "backend", "qisl", "isl", "feat/slow");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(cancelAfterMs));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => e.Simulator.SimulateAsync(plan.Id, new SimulateOptions { TrustValidation = true }, cts.Token));
        var runs = e.Store.ListRuns().ToList();
        Run finished;
        if (runs.Count == 0) finished = await T.Simulate(e, plan);   // cancelled before a run existed
        else
        {
            var run = Assert.Single(runs);
            Assert.Equal(RunStatus.CANCELLED, run.Status);
            Assert.Null(run.Conflict);
            Assert.DoesNotContain(run.Findings, f => f.Code == "CONFLICT");
            finished = await e.Simulator.ResumeAsync(run.Id, new SimulateOptions { TrustValidation = true });
        }
        Assert.Equal(RunStatus.READY, finished.Status);
    }

    [Fact]
    public async Task Crash_recovery_resets_the_harness_workspace_to_the_last_recorded_commit()
    {
        using var fx = new RepoFixture(frontend: false);
        T.Branch(fx.QislBackend, "feat/two", "one", ("Fabric/Kernel/Kernel.Domain/Audit.cs", Audit2));
        T.Branch(fx.QislBackend, "feat/two", "two", ("Fabric/Platform/Platform.Core/Service.cs", "namespace Platform.Core;\npublic class Service { public string Describe(Kernel.Domain.Entity e) => \"two\"; }\n"));
        var e = fx.Engine();
        var plan = await T.Feature(e, "backend", "qisl", "isl", "feat/two");
        var done = await T.Simulate(e, plan, preserve: true);
        Assert.Equal(RunStatus.READY, done.Status);
        // Simulate a crash after the first commit: metadata says 1 applied, workspace is half-written.
        var first = done.ResultCommits[plan.Commits[0].Sha];
        RepoFixture.Git(done.CandidateWorktree, "reset", "-q", "--hard", first);
        File.WriteAllText(Path.Combine(done.CandidateWorktree, "Fabric/Kernel/Kernel.Domain/Audit.cs"), "half-written garbage");
        var crashed = done with { Status = RunStatus.IN_PROGRESS, NextCommitIndex = 1, CandidateSha = null, AppliedCommits = [plan.Commits[0].Sha],
            ResultCommits = new() { [plan.Commits[0].Sha] = first } };
        e.Store.SaveRun(crashed);
        var resumed = await e.Simulator.ResumeAsync(done.Id, new SimulateOptions { TrustValidation = true });
        Assert.Equal(RunStatus.READY, resumed.Status);
        Assert.Contains("crash recovery", string.Join("\n", resumed.Log));
        Assert.Contains("Reason", T.MirrorFile(e, "isl-backend", resumed.CandidateSha!, "Fabric/Kernel/Kernel.Domain/Audit.cs"));
        Assert.Contains("two", T.MirrorFile(e, "isl-backend", resumed.CandidateSha!, "Fabric/Platform/Platform.Core/Service.cs"));
    }

    [Fact]
    public async Task Dirty_developer_checkouts_with_in_progress_operations_stay_byte_identical()
    {
        using var fx = new RepoFixture(frontend: false);
        T.Branch(fx.QislBackend, "feat/d", "d", ("Fabric/Kernel/Kernel.Domain/Audit.cs", Audit2));
        // ISL developer is mid-merge with a conflict, has staged and unstaged edits and untracked files.
        var i = fx.IslBackend;
        RepoFixture.Git(i, "switch", "-q", "-c", "wip");
        RepoFixture.Write(i, "README.md", "# wip-a\n"); RepoFixture.Commit(i, "wip a");
        RepoFixture.Git(i, "switch", "-q", "development");
        RepoFixture.Write(i, "README.md", "# wip-b\n"); RepoFixture.Commit(i, "wip b");
        try { RepoFixture.Git(i, "merge", "wip"); } catch (InvalidOperationException) { }
        RepoFixture.Write(i, "App.MTL/Mtl/Untracked.cs", "untracked\n");
        RepoFixture.Write(i, "Fabric/Kernel/Kernel.Domain/Entity.cs", "local edit\n");
        RepoFixture.Git(i, "add", "Fabric/Kernel/Kernel.Domain/Entity.cs");
        RepoFixture.Write(i, "Fabric/Kernel/Kernel.Domain/Entity.cs", "unstaged on top\n");
        var before = await fx.SnapshotUserReposAsync();
        var head = RepoFixture.Git(i, "rev-parse", "HEAD");
        var remotes = RepoFixture.Git(i, "config", "--get-regexp", "^remote\\.");

        var e = fx.Engine();
        var run = await T.Simulate(e, await T.Feature(e, "backend", "qisl", "isl", "feat/d"));
        Assert.Equal(RunStatus.READY, run.Status);
        await e.Publisher.ApproveAsync(run.Id, run.CandidateSha![..12], [], "tester");
        await e.Publisher.PublishAsync(run.Id, createPr: false);
        e.Cleanup(run.Id);

        Assert.Equal(before, await fx.SnapshotUserReposAsync());
        Assert.Equal(head, RepoFixture.Git(i, "rev-parse", "HEAD"));
        Assert.Equal(remotes, RepoFixture.Git(i, "config", "--get-regexp", "^remote\\."));
        Assert.True(File.Exists(Path.Combine(i, ".git", "MERGE_HEAD")));
    }

    [Fact]
    public async Task Validation_script_modifying_a_user_checkout_is_detected_and_blocks()
    {
        using var fx = new RepoFixture(frontend: false);
        var victim = Path.Combine(fx.IslBackend, "pwned.txt").Replace("\\", "/");
        fx.BackendExtraValidation.Add(("evil-build", "build", true, ["write-outside", victim]));
        fx.WriteConfig();
        T.Branch(fx.QislBackend, "feat/evil", "e", ("Fabric/Kernel/Kernel.Domain/Audit.cs", Audit2));
        var e = fx.Engine();
        var run = await T.Simulate(e, await T.Feature(e, "backend", "qisl", "isl", "feat/evil"));
        Assert.Equal(RunStatus.BLOCKED, run.Status);
        var f = run.Findings.Single(x => x.Code == "USER_REPO_MODIFIED");
        Assert.Equal(Severity.Critical, f.Severity);
    }

    [Fact]
    public async Task Cleanup_never_follows_symlinks_planted_in_a_workspace()
    {
        using var fx = new RepoFixture(frontend: false);
        var outside = Path.Combine(fx.Root, "precious");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        fx.BackendExtraValidation.Add(("plant-link", "build", false, ["link-outside", "escape", outside.Replace("\\", "/")]));
        fx.WriteConfig();
        T.Branch(fx.QislBackend, "feat/l", "l", ("Fabric/Kernel/Kernel.Domain/Audit.cs", Audit2));
        var e = fx.Engine();
        var run = await T.Simulate(e, await T.Feature(e, "backend", "qisl", "isl", "feat/l"), preserve: true);
        Assert.True(SafeFileSystem.IsLink(Path.Combine(run.CandidateWorktree, "escape")));
        e.Cleanup(run.Id);
        Assert.False(Directory.Exists(run.WorkspaceDir));
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
        // Deletion outside the harness root is refused outright.
        Assert.Throws<HarnessSafetyException>(() => e.Store.Fs.DeleteOwnedDirectory(outside, "x"));
        Assert.Throws<HarnessSafetyException>(() => e.Store.Fs.DeleteOwnedDirectory(Path.Combine(e.Store.Root, "..", "precious"), "x"));
    }

    [Fact]
    public async Task Cleanup_requires_ownership_marker()
    {
        using var fx = new RepoFixture(frontend: false);
        T.Branch(fx.QislBackend, "feat/o", "o", ("Fabric/Kernel/Kernel.Domain/Audit.cs", Audit2));
        var e = fx.Engine();
        var run = await T.Simulate(e, await T.Feature(e, "backend", "qisl", "isl", "feat/o"));
        File.WriteAllText(Path.Combine(run.WorkspaceDir, SafeFileSystem.OwnershipMarker), "someone-else");
        Assert.Throws<HarnessSafetyException>(() => e.Cleanup(run.Id));
        Assert.True(Directory.Exists(run.WorkspaceDir));
    }

    [Fact]
    public void State_directory_inside_a_repository_is_refused()
    {
        using var fx = new RepoFixture(frontend: false);
        File.WriteAllText(fx.ConfigPath, File.ReadAllText(fx.ConfigPath).Replace($"stateDir: \"{fx.StateDir.Replace("\\", "/")}\"", $"stateDir: \"{Path.Combine(fx.IslBackend, ".fb").Replace("\\", "/")}\""));
        Assert.Throws<HarnessSafetyException>(() => fx.Engine());
    }
}

[CollectionDefinition("env", DisableParallelization = true)]
public class EnvCollection { }

/// <summary>Deterministic cancellation: cancels exactly when a given git operation starts (the
/// failure seen on Windows, where cancellation landed inside `git worktree add`).</summary>
public class DeterministicCancellationTests
{
    private sealed class CancelOnRunner(Func<ForkBridge.Core.Infrastructure.ProcessSpec, bool> trigger, CancellationTokenSource cts)
        : ForkBridge.Core.Infrastructure.IProcessRunner
    {
        private readonly ForkBridge.Core.Infrastructure.ProcessRunner _inner = new();
        public bool Fired { get; private set; }
        public Task<ForkBridge.Core.Infrastructure.ProcessResult> RunAsync(ForkBridge.Core.Infrastructure.ProcessSpec spec, CancellationToken ct = default)
        {
            if (!Fired && trigger(spec)) { Fired = true; cts.Cancel(); }
            return _inner.RunAsync(spec, ct);
        }
    }

    [Theory]
    [InlineData("candidate-worktree")]
    [InlineData("apply")]
    [InlineData("baseline-worktree")]
    public async Task Cancellation_inside_a_git_operation_is_a_cancellation_and_resumes_cleanly(string where)
    {
        using var fx = new RepoFixture(frontend: false);
        T.Branch(fx.QislBackend, "feat/c", "c", ("Fabric/Kernel/Kernel.Domain/Audit.cs", "namespace Kernel.Domain;\npublic class Audit { public string Why { get; set; } = \"\"; }\n"));
        var plan = await T.Feature(fx.Engine(), "backend", "qisl", "isl", "feat/c");
        using var cts = new CancellationTokenSource();
        var runner = new CancelOnRunner(s =>
        {
            var a = s.Arguments;
            return where switch
            {
                "candidate-worktree" => a.Contains("worktree") && a.Contains("add") && a.Contains("-b"),
                "apply" => a.Contains("apply") && a.Contains("--3way"),
                _ => a.Contains("worktree") && a.Contains("add") && a.Contains("--detach"),
            };
        }, cts);
        var e = new ForkBridge.Core.ForkBridgeEngine(ForkBridge.Core.Config.ConfigLoader.Load(fx.ConfigPath), runner);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => e.Simulator.SimulateAsync(plan.Id, new SimulateOptions { TrustValidation = true }, cts.Token));
        Assert.True(runner.Fired);
        var run = Assert.Single(e.Store.ListRuns());
        Assert.Equal(RunStatus.CANCELLED, run.Status);
        Assert.Null(run.Conflict);
        var resumed = await fx.Engine().Simulator.ResumeAsync(run.Id, new SimulateOptions { TrustValidation = true });
        Assert.Equal(RunStatus.READY, resumed.Status);
        Assert.Contains("Why", T.MirrorFile(fx.Engine(), "isl-backend", resumed.CandidateSha!, "Fabric/Kernel/Kernel.Domain/Audit.cs"));
    }
}
