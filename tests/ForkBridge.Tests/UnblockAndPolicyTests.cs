using System.Text;
using ForkBridge.Core;
using ForkBridge.Core.Config;
using ForkBridge.Core.Model;
using ForkBridge.Core.Planning;
using ForkBridge.Core.Publishing;
using ForkBridge.Core.Transfer;
using ForkBridge.Tests.Fixtures;

namespace ForkBridge.Tests;

public class UnblockAndPolicyTests
{
    [Fact]
    public void PolicyAction_Preserves_Comments_And_Yaml_Anchors_And_Creates_Orig_Backup()
    {
        using var fx = new RepoFixture(frontend: false);
        var cfgPath = fx.ConfigPath;
        var originalYaml = File.ReadAllText(cfgPath).Replace("\r\n", "\n");
        var commentedYaml = "# >>> FILL IN header comment\n" +
            originalYaml.Replace(
                "    targetPinned:\n      qisl: [\"Fabric/Platform/Web.Host/appsettings*.json\"]\n      isl: [\"Fabric/Platform/Web.Host/appsettings*.json\"]",
                "    # VERIFIED pinned section\n    targetPinned:\n      qisl: &backendPinned\n        - \"Fabric/Platform/Web.Host/appsettings*.json\"\n      isl: *backendPinned");
        File.WriteAllText(cfgPath, commentedYaml);

        var e = fx.Engine();
        e.ApplyPolicyAction(new PolicyActionRequest
        {
            Pair = "backend",
            Action = "add-product-specific",
            Product = "both",
            Patterns = ["**/QiSL.Platform.Web.ISL.Host/**"],
        });
        e.ApplyPolicyAction(new PolicyActionRequest
        {
            Pair = "backend",
            Action = "add-target-pinned",
            Product = "both",
            Patterns = ["**/launchSettings.json"],
        });
        e.ApplyPolicyAction(new PolicyActionRequest
        {
            Pair = "backend",
            Action = "add-ignored",
            Patterns = ["**/legacy.bak"],
        });

        var afterYaml = File.ReadAllText(cfgPath);
        Assert.Contains("# >>> FILL IN header comment", afterYaml);
        Assert.Contains("# VERIFIED pinned section", afterYaml);
        Assert.Contains("&backendPinned", afterYaml);
        Assert.Contains("*backendPinned", afterYaml);
        Assert.Contains("**/QiSL.Platform.Web.ISL.Host/**", afterYaml);
        Assert.Contains("**/launchSettings.json", afterYaml);
        Assert.Contains("**/legacy.bak", afterYaml);
        Assert.True(File.Exists(cfgPath + ".orig"));
        Assert.True(File.Exists(cfgPath + ".bak"));
        Assert.Contains("**/QiSL.Platform.Web.ISL.Host/**", e.Config.Pairs["backend"].ProductSpecific["qisl"]);
        Assert.Contains("**/QiSL.Platform.Web.ISL.Host/**", e.Config.Pairs["backend"].ProductSpecific["isl"]);
        Assert.Contains("**/launchSettings.json", e.Config.Pairs["backend"].TargetPinned["qisl"]);
        Assert.Contains("**/launchSettings.json", e.Config.Pairs["backend"].TargetPinned["isl"]);
        Assert.Contains("**/legacy.bak", e.Config.Pairs["backend"].Ignored);
    }

    [Fact]
    public async Task ResolvePlan_Creates_Derived_Immutable_Plan_And_Unblocks_Blocked_Paths()
    {
        using var fx = new RepoFixture(frontend: false);
        T.Branch(fx.QislBackend, "feat/unblock-me", "Shared change plus unclassified file",
            ("Fabric/Platform/Platform.Core/Service.cs", "namespace Platform.Core;\npublic class Service { public string Describe(Kernel.Domain.Entity e) => e.Name.ToUpperInvariant(); }\n"),
            ("UnmappedFolder/Mystery.cs", "public class Mystery {}\n"));

        var e = fx.Engine();
        var blockedPlan = await T.Feature(e, "backend", "qisl", "isl", "feat/unblock-me");
        Assert.Equal(RunStatus.BLOCKED, blockedPlan.PreliminaryStatus);
        Assert.True(blockedPlan.Findings.Has("UNKNOWN_PATH"));

        var derivedPlan = await e.Planner.ResolvePlanAsync(blockedPlan.Id, new PlanResolutionRequest
        {
            UnblockAll = true,
            DefaultFileDecision = Decision.Exclude,
        });

        Assert.NotEqual(blockedPlan.Id, derivedPlan.Id);
        Assert.Equal(blockedPlan.Id, derivedPlan.ParentPlanId);
        Assert.Equal(RunStatus.REVIEW_REQUIRED, derivedPlan.PreliminaryStatus);
        Assert.Equal(Decision.Exclude, derivedPlan.PathOverrides!["UnmappedFolder/Mystery.cs"]);

        // Original plan on disk remains strictly immutable and BLOCKED
        var reloadedOriginal = e.Store.LoadPlan(blockedPlan.Id);
        Assert.Equal(RunStatus.BLOCKED, reloadedOriginal.PreliminaryStatus);
        Assert.Null(reloadedOriginal.ParentPlanId);

        // Derived plan simulates cleanly
        var run = await T.Simulate(e, derivedPlan);
        Assert.Equal(RunStatus.REVIEW_REQUIRED, run.Status);
        Assert.Contains("ToUpperInvariant()", T.MirrorFile(e, "isl-backend", run.CandidateSha!, "Fabric/Platform/Platform.Core/Service.cs"));
    }

    [Fact]
    public async Task ResolvePlan_ForceInclude_Restores_ModifyDelete_And_Emits_Review_Finding()
    {
        using var fx = new RepoFixture(frontend: false);
        // Target deletes Audit.cs, while source modifies Audit.cs (MODIFY_DELETE) and adds an unknown path (UNKNOWN_PATH)
        T.OnDevelopment(fx.IslBackend, "Remove Audit in ISL", ("Fabric/Kernel/Kernel.Domain/Audit.cs", null));
        T.Branch(fx.QislBackend, "feat/audit-update", "Update Audit in QiSL",
            ("Fabric/Kernel/Kernel.Domain/Audit.cs", "namespace Kernel.Domain;\npublic class Audit { public string Actor { get; set; } = \"admin\"; }\n"),
            ("CustomTools/ExtraNote.txt", "force-included note\n"));

        var e = fx.Engine();
        var blocked = await T.Feature(e, "backend", "qisl", "isl", "feat/audit-update");
        Assert.Equal(RunStatus.BLOCKED, blocked.PreliminaryStatus);
        Assert.True(blocked.Findings.Has("MODIFY_DELETE"));
        Assert.True(blocked.Findings.Has("UNKNOWN_PATH"));

        var derived = await e.Planner.ResolvePlanAsync(blocked.Id, new PlanResolutionRequest
        {
            PathOverrides = new Dictionary<string, Decision>
            {
                ["Fabric/Kernel/Kernel.Domain/Audit.cs"] = Decision.Include,
                ["CustomTools/ExtraNote.txt"] = Decision.Include,
            },
        });

        Assert.NotEqual(RunStatus.BLOCKED, derived.PreliminaryStatus);
        Assert.False(derived.Findings.Has("MODIFY_DELETE"));
        Assert.True(derived.Findings.Has("MODIFY_DELETE_RESTORED"));
        Assert.True(derived.Findings.Has("PATH_OVERRIDE_INCLUDED"));

        var run = await T.Simulate(e, derived);
        Assert.Contains("Actor", T.MirrorFile(e, "isl-backend", run.CandidateSha!, "Fabric/Kernel/Kernel.Domain/Audit.cs"));
        Assert.Contains("force-included note", T.MirrorFile(e, "isl-backend", run.CandidateSha!, "CustomTools/ExtraNote.txt"));
    }

    [Fact]
    public async Task ConflictResolver_CustomResolution_Preserves_Bom_And_Crlf_And_Rejects_Markers()
    {
        using var fx = new RepoFixture(frontend: false);
        // Write file with UTF-8 BOM and CRLF in ISL target
        RepoFixture.Git(fx.IslBackend, "switch", "-q", "development");
        byte[] bomCrlf = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("namespace Kernel.Domain;\r\n\r\npublic class Entity\r\n{\r\n    public long Id { get; set; }\r\n    public string Name { get; set; } = \"\";\r\n}\r\n")];
        File.WriteAllBytes(Path.Combine(fx.IslBackend, "Fabric/Kernel/Kernel.Domain/Entity.cs"), bomCrlf);
        RepoFixture.Commit(fx.IslBackend, "ISL BOM+CRLF tweak");
        RepoFixture.Push(fx.IslBackend, "development");

        T.Branch(fx.QislBackend, "feat/bom-conflict", "QiSL Guid Id",
            ("Fabric/Kernel/Kernel.Domain/Entity.cs", "namespace Kernel.Domain;\n\npublic class Entity\n{\n    public System.Guid Id { get; set; }\n    public string Name { get; set; } = \"\";\n}\n"));

        var e = fx.Engine();
        var plan = await T.Feature(e, "backend", "qisl", "isl", "feat/bom-conflict");
        var run = await T.Simulate(e, plan);
        Assert.Equal(RunStatus.BLOCKED, run.Status);
        Assert.NotNull(run.Conflict);

        var details = await e.Simulator.GetConflictDetailsAsync(run.Id);
        var cf = Assert.Single(details);
        Assert.Equal("Fabric/Kernel/Kernel.Domain/Entity.cs", cf.File);
        Assert.False(cf.IsBinary);

        // Reject custom resolution that still has conflict markers
        await Assert.ThrowsAsync<PlanningException>(() => e.Simulator.ResolveConflictFileAsync(run.Id, new ConflictResolutionRequest
        {
            File = cf.File,
            Resolution = "custom",
            Content = "<<<<<<< ours\npublic long Id;\n=======\npublic Guid Id;\n>>>>>>> theirs\n",
        }));

        // Apply clean custom resolution (passed with LF from browser textarea)
        var resolvedContent = "namespace Kernel.Domain;\n\npublic class Entity\n{\n    public System.Guid Id { get; set; }\n    public long LegacyId { get; set; }\n    public string Name { get; set; } = \"\";\n}\n";
        var afterDetails = await e.Simulator.ResolveConflictFileAsync(run.Id, new ConflictResolutionRequest
        {
            File = cf.File,
            Resolution = "custom",
            Content = resolvedContent,
        });
        Assert.False( Assert.Single(afterDetails).HasConflictMarkers );

        // Verify BOM and CRLF were preserved on disk in candidate worktree
        var writtenBytes = File.ReadAllBytes(Path.Combine(run.CandidateWorktree, cf.File));
        Assert.True(writtenBytes.Length >= 3 && writtenBytes[0] == 0xEF && writtenBytes[1] == 0xBB && writtenBytes[2] == 0xBF, "Expected UTF-8 BOM to be preserved");
        var writtenText = Encoding.UTF8.GetString(writtenBytes, 3, writtenBytes.Length - 3);
        Assert.Contains("\r\n", writtenText);

        var resumed = await e.Simulator.ResumeAsync(run.Id, new SimulateOptions { TrustValidation = true });
        Assert.Equal(RunStatus.REVIEW_REQUIRED, resumed.Status);
    }

    [Fact]
    public async Task Approve_With_OverrideValidation_Allows_Approval_When_Validation_Unavailable()
    {
        using var fx = new RepoFixture(frontend: false);
        T.Branch(fx.IslBackend, "feat/val-override", "Shared service change",
            ("Fabric/Platform/Platform.Core/Service.cs", "namespace Platform.Core;\npublic class Service { public string Describe(Kernel.Domain.Entity e) => e.Name; }\n"));

        var e = fx.Engine();
        var plan = await T.Feature(e, "backend", "isl", "qisl", "feat/val-override");
        // Simulate with trust: false -> required validation is UNAVAILABLE -> status is VALIDATION_FAILED
        var run = await T.Simulate(e, plan, trust: false);
        Assert.Equal(RunStatus.VALIDATION_FAILED, run.Status);

        // Without overrideValidation, approval is refused
        await Assert.ThrowsAsync<PublishRefusedException>(() =>
            e.Publisher.ApproveAsync(run.Id, run.CandidateSha![..10], [], "tester", overrideValidation: false));

        // With overrideValidation: true, approval succeeds and records ValidationOverridden
        var approval = await e.Publisher.ApproveAsync(run.Id, run.CandidateSha![..10], [], "tester", overrideValidation: true);
        Assert.True(approval.ValidationOverridden);
        Assert.Contains("OVERRIDE_VALIDATION", approval.AcceptedFindings);

        var reloadedRun = e.Store.LoadRun(run.Id);
        Assert.Equal(RunStatus.READY, reloadedRun.Status);
    }

    [Fact]
    public async Task FileDiff_LibraryExclusion_And_PlanMarkdown_Work()
    {
        using var fx = new RepoFixture(frontend: false);
        T.Branch(fx.IslBackend, "feat/lib-exclude", "Touch core and unused client library",
            ("Fabric/Platform/Platform.Core/Service.cs", "namespace Platform.Core;\npublic class Service { public string Describe(Kernel.Domain.Entity e) => e.Name + \"!\"; }\n"),
            ("Fabric/Platform/Platform.Core/src/Unused.Client/ApiClient.cs", "namespace Unused.Client;\npublic class ApiClient {}\n"));

        var e = fx.Engine();
        var plan = await T.Feature(e, "backend", "isl", "qisl", "feat/lib-exclude");

        // 1. Inspect file diff for Service.cs
        var diff = await e.Planner.GetFileDiffAsync(plan.Id, "Fabric/Platform/Platform.Core/Service.cs");
        Assert.True(diff.ExistsInSource);
        Assert.True(diff.ExistsInTarget);
        Assert.False(diff.Identical);
        Assert.Contains("+public class Service { public string Describe(Kernel.Domain.Entity e) => e.Name + \"!\"; }", diff.TargetToSourceDiff);
        Assert.NotEmpty(diff.CommitDiff);

        // 2. Exclude the library via glob in ResolvePlanAsync
        var derived = await e.Planner.ResolvePlanAsync(plan.Id, new PlanResolutionRequest
        {
            PathOverrides = new Dictionary<string, Decision>
            {
                ["Fabric/Platform/Platform.Core/src/Unused.Client/**"] = Decision.Exclude,
            },
        });
        var clientDecision = derived.Commits.SelectMany(c => c.Paths)
            .Single(p => p.SourcePath == "Fabric/Platform/Platform.Core/src/Unused.Client/ApiClient.cs");
        Assert.Equal(Decision.Exclude, clientDecision.Decision);

        // 3. Verify Markdown export contains the Active Overrides and Enumerated Classification tables
        var md = ForkBridge.Core.Reporting.ReportWriter.PlanMarkdown(derived);
        Assert.Contains("## Active Path & Library Overrides", md);
        Assert.Contains("Fabric/Platform/Platform.Core/src/Unused.Client/**", md);
        Assert.Contains("| # | Commit | St | Library / Subproject |", md);
    }
}
