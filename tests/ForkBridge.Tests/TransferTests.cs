using System.Text;
using ForkBridge.Core.Model;
using ForkBridge.Core.Planning;
using ForkBridge.Core.Transfer;
using ForkBridge.Tests.Fixtures;

namespace ForkBridge.Tests;

public class TransferTests
{
    [Fact]
    public async Task Backend_ISL_to_QiSL_feature_branch()
    {
        using var fx = new RepoFixture(frontend: false);
        T.Branch(fx.IslBackend, "24300-shared-fix", "Fix service description",
            ("Fabric/Platform/Platform.Core/Service.cs", "namespace Platform.Core;\n\npublic class Service\n{\n    public string Describe(Kernel.Domain.Entity e) => e.Name.Trim();\n}\n"));
        var before = await fx.SnapshotUserReposAsync();
        var e = fx.Engine();
        var plan = await T.Feature(e, "backend", "isl", "qisl", "24300-shared-fix");
        Assert.Equal(RunStatus.PLANNED, plan.PreliminaryStatus);
        Assert.Contains("merge-base", plan.MergeBaseExplanation);
        var run = await T.Simulate(e, plan);
        Assert.Equal(RunStatus.READY, run.Status);
        Assert.Contains("Trim()", T.MirrorFile(e, "qisl-backend", run.CandidateSha!, "Fabric/Platform/Platform.Core/Service.cs"));
        Assert.Equal(before, await fx.SnapshotUserReposAsync());
    }

    [Fact]
    public async Task Frontend_both_directions_with_different_component_paths()
    {
        using var fx = new RepoFixture();
        // QiSL -> ISL: libs/shared-ui -> projects/common/shared-ui
        T.Branch(fx.QislFrontend, "feat/money", "Money with currency",
            ("libs/shared-ui/src/format.ts", "export function formatMoney(v: number, c = 'EUR'): string {\n  return `${v.toFixed(2)} ${c}`;\n}\n"));
        var e = fx.Engine();
        var p1 = await T.Feature(e, "frontend", "qisl", "isl", "feat/money");
        var d = p1.Commits.Single().Paths.Single();
        Assert.Equal("libs/shared-ui/src/format.ts", d.SourcePath);
        Assert.Equal("projects/common/shared-ui/src/format.ts", d.TargetPath);
        var r1 = await T.Simulate(e, p1);
        Assert.Equal(RunStatus.READY, r1.Status);
        Assert.Contains("EUR", T.MirrorFile(e, "isl-frontend", r1.CandidateSha!, "projects/common/shared-ui/src/format.ts"));

        // ISL -> QiSL: reverse mapping
        T.Branch(fx.IslFrontend, "feat/button-label", "Button label input",
            ("projects/common/shared-ui/src/button.component.ts", "import { Component, Input } from '@angular/core';\n\n@Component({ selector: 'fb-button', template: '<button>{{label}}</button>' })\nexport class ButtonComponent {\n  @Input() label = 'OK';\n}\n"));
        var p2 = await T.Feature(e, "frontend", "isl", "qisl", "feat/button-label");
        Assert.Equal("libs/shared-ui/src/button.component.ts", p2.Commits.Single().Paths.Single().TargetPath);
        var r2 = await T.Simulate(e, p2);
        Assert.Equal(RunStatus.READY, r2.Status);
        Assert.Contains("@Input()", T.MirrorFile(e, "qisl-frontend", r2.CandidateSha!, "libs/shared-ui/src/button.component.ts"));
    }

    [Fact]
    public async Task Conflicting_edit_blocks_with_evidence_then_resume_after_manual_resolution()
    {
        using var fx = new RepoFixture(frontend: false);
        // Target diverged on the same line.
        T.OnDevelopment(fx.IslBackend, "ISL tweak", ("Fabric/Kernel/Kernel.Domain/Entity.cs",
            "namespace Kernel.Domain;\n\npublic class Entity\n{\n    public long Id { get; set; }\n    public string Name { get; set; } = \"\";\n}\n"));
        T.Branch(fx.QislBackend, "feat/guid", "Guid ids", ("Fabric/Kernel/Kernel.Domain/Entity.cs",
            "namespace Kernel.Domain;\n\npublic class Entity\n{\n    public System.Guid Id { get; set; }\n    public string Name { get; set; } = \"\";\n}\n"));
        var before = await fx.SnapshotUserReposAsync();
        var e = fx.Engine();
        var plan = await T.Feature(e, "backend", "qisl", "isl", "feat/guid");
        Assert.True(plan.Findings.Has("PREEXISTING_DIVERGENCE"));
        var run = await T.Simulate(e, plan);
        Assert.Equal(RunStatus.BLOCKED, run.Status);
        Assert.NotNull(run.Conflict);
        Assert.Contains("Fabric/Kernel/Kernel.Domain/Entity.cs", run.Conflict!.Files);
        var ev = Directory.GetFiles(run.Conflict.EvidenceDir!);
        Assert.Contains(ev, f => f.EndsWith(".base"));
        Assert.Contains(ev, f => f.EndsWith(".target"));
        Assert.Contains(ev, f => f.EndsWith(".source"));
        Assert.Contains(run.Conflict.Instructions, i => i.Contains("forkbridge resume"));

        // Resume refuses while markers remain.
        await Assert.ThrowsAsync<PlanningException>(() => e.Simulator.ResumeAsync(run.Id, new SimulateOptions { TrustValidation = true }));
        // Resolve in the HARNESS workspace.
        File.WriteAllText(Path.Combine(run.CandidateWorktree, "Fabric/Kernel/Kernel.Domain/Entity.cs"),
            "namespace Kernel.Domain;\n\npublic class Entity\n{\n    public System.Guid Id { get; set; }\n    public long LegacyId { get; set; }\n    public string Name { get; set; } = \"\";\n}\n");
        var resumed = await e.Simulator.ResumeAsync(run.Id, new SimulateOptions { TrustValidation = true });
        Assert.Equal(RunStatus.REVIEW_REQUIRED, resumed.Status); // manual resolution must be reviewed
        Assert.True(resumed.Findings.Has("MANUAL_RESOLUTION"));
        Assert.Contains("ForkBridge-Resolved", RepoFixture.Git(e.Store.MirrorDir("isl-backend"), "log", "-1", "--format=%B", resumed.CandidateSha!));
        Assert.Equal(before, await fx.SnapshotUserReposAsync());
    }

    [Fact]
    public async Task Abort_removes_workspace_and_branch_but_keeps_reports()
    {
        using var fx = new RepoFixture(frontend: false);
        T.OnDevelopment(fx.IslBackend, "ISL tweak", ("Fabric/Kernel/Kernel.Domain/Audit.cs", "namespace Kernel.Domain;\npublic class Audit { public int Who; }\n"));
        T.Branch(fx.QislBackend, "feat/x", "x", ("Fabric/Kernel/Kernel.Domain/Audit.cs", "namespace Kernel.Domain;\npublic class Audit { public long Who; }\n"));
        var e = fx.Engine();
        var run = await T.Simulate(e, await T.Feature(e, "backend", "qisl", "isl", "feat/x"));
        Assert.Equal(RunStatus.BLOCKED, run.Status);
        var aborted = e.Abort(run.Id);
        Assert.Equal(RunStatus.ABORTED, aborted.Status);
        Assert.False(Directory.Exists(run.CandidateWorktree));
        Assert.DoesNotContain(run.Branch, RepoFixture.Git(e.Store.MirrorDir("isl-backend"), "branch", "--list", "forkbridge/*"));
        Assert.True(File.Exists(Path.Combine(run.WorkspaceDir, "report.md")));
    }

    [Fact]
    public async Task Mixed_shared_and_product_commit_is_partial_and_requires_review()
    {
        using var fx = new RepoFixture(frontend: false);
        var sha = T.Branch(fx.QislBackend, "feat/mixed", "Shared tweak plus VAM change",
            ("Fabric/Kernel/Kernel.Domain/Audit.cs", "namespace Kernel.Domain;\n\npublic class Audit\n{\n    public string Who { get; set; } = \"system\";\n}\n"),
            ("App.VAM/Vam/Vehicle.cs", "namespace Vam;\npublic class Vehicle : Kernel.Domain.Entity { public string Vin { get; set; } = \"\"; }\n"));
        var e = fx.Engine();
        var plan = await T.Feature(e, "backend", "qisl", "isl", "feat/mixed");
        var pc = plan.Commits.Single();
        Assert.True(pc.Mixed);
        Assert.Contains(pc.Paths, p => p.SourcePath.StartsWith("App.VAM") && p.Decision == Decision.Exclude);
        Assert.Equal(RunStatus.REVIEW_REQUIRED, plan.PreliminaryStatus);
        var mixed = plan.Findings.Single(f => f.Code == "MIXED_COMMIT");
        Assert.Contains(mixed.Evidence, x => x.StartsWith("INCLUDED"));
        Assert.Contains(mixed.Evidence, x => x.StartsWith("EXCLUDED"));

        var run = await T.Simulate(e, plan);
        Assert.Equal(RunStatus.REVIEW_REQUIRED, run.Status);
        var msg = RepoFixture.Git(e.Store.MirrorDir("isl-backend"), "log", "-1", "--format=%B", run.CandidateSha!);
        Assert.Contains("ForkBridge-Partial", msg);
        Assert.Throws<InvalidOperationException>(() => T.MirrorFile(e, "isl-backend", run.CandidateSha!, "App.VAM/Vam/Vehicle.cs"));
        // Approval requires explicitly accepting the review finding.
        await Assert.ThrowsAsync<ForkBridge.Core.Publishing.PublishRefusedException>(() => e.Publisher.ApproveAsync(run.Id, run.CandidateSha![..10], [], "tester"));
        var appr = await e.Publisher.ApproveAsync(run.Id, run.CandidateSha![..10], [mixed.Id], "tester");
        Assert.Contains(mixed.Id, appr.AcceptedFindings);
    }

    [Fact]
    public async Task Mixed_commit_whose_shared_part_uses_excluded_code_is_blocked()
    {
        using var fx = new RepoFixture(frontend: false);
        T.Branch(fx.QislBackend, "feat/cross", "Shared uses VAM helper",
            ("App.VAM/Vam/VinHelper.cs", "namespace Vam;\npublic class VinHelper { public static string Normalize(string v) => v; }\n"),
            ("Fabric/Platform/Platform.Core/Service.cs", "namespace Platform.Core;\n\npublic class Service\n{\n    public string Describe(Kernel.Domain.Entity e) => Vam.VinHelper.Normalize(e.Name);\n}\n"));
        var e = fx.Engine();
        var plan = await T.Feature(e, "backend", "qisl", "isl", "feat/cross");
        Assert.Equal(RunStatus.BLOCKED, plan.PreliminaryStatus);
        Assert.True(plan.Findings.Has("CROSS_BOUNDARY_DEPENDENCY"));
        await Assert.ThrowsAsync<PlanningException>(() => T.Simulate(e, plan));
    }

    [Fact]
    public async Task Excluded_declaration_of_a_type_the_target_already_has_is_not_cross_boundary()
    {
        using var fx = new RepoFixture(frontend: false);
        // The excluded VAM file declares a type named like an existing shared type (Entity); the shared change
        // references the target's existing Kernel.Domain.Entity. Name overlap alone must not block.
        T.Branch(fx.QislBackend, "feat/overlap", "Shared uses existing Entity",
            ("App.VAM/Vam/Entity.cs", "namespace Vam;\npublic class Entity { }\n"),
            ("Fabric/Platform/Platform.Core/Describer.cs", "namespace Platform.Core;\n\npublic class Describer\n{\n    public string Describe(Kernel.Domain.Entity e) => e.Name;\n}\n"));
        var e = fx.Engine();
        var plan = await T.Feature(e, "backend", "qisl", "isl", "feat/overlap");
        Assert.False(plan.Findings.Has("CROSS_BOUNDARY_DEPENDENCY"));
        Assert.NotEqual(RunStatus.BLOCKED, plan.PreliminaryStatus);
    }

    [Fact]
    public async Task Member_access_and_string_literals_are_not_type_references()
    {
        using var fx = new RepoFixture(frontend: false);
        T.Branch(fx.QislBackend, "feat/members", "Shared uses a property and a string named like excluded types",
            ("App.VAM/Vam/Classification.cs", "namespace Vam;\npublic class Classification { }\npublic class Features { }\n"),
            ("Fabric/Platform/Platform.Core/LogEntry.cs",
             "namespace Platform.Core;\n\npublic class LogEntry\n{\n    public int? Classification { get; set; }\n" +
             "    public void Mark(LogEntry entry) { entry.Classification ??= 1; this.Classification = Get()?.Classification; }\n" +
             "    private LogEntry? Get() => null;\n    public const string Name = \"Abp.Application.Features.FeatureSetting\";\n}\n"));
        var e = fx.Engine();
        var plan = await T.Feature(e, "backend", "qisl", "isl", "feat/members");
        Assert.False(plan.Findings.Has("CROSS_BOUNDARY_DEPENDENCY"),
            string.Join("\n", plan.Findings.Where(f => f.Code == "CROSS_BOUNDARY_DEPENDENCY").SelectMany(f => f.Evidence)));
    }

    [Fact]
    public async Task Generated_file_in_unselected_component_is_excluded_not_blocking()
    {
        using var fx = new RepoFixture(frontend: false);
        T.Branch(fx.QislBackend, "feat/gen", "Kernel change plus platform generated file",
            ("Fabric/Platform/Platform.Core/Model.g.cs", "// generated\n"),
            ("Fabric/Kernel/Kernel.Domain/Extra.cs", "namespace Kernel.Domain;\npublic class Extra { }\n"));
        var e = fx.Engine();
        var kernelOnly = await T.Feature(e, "backend", "qisl", "isl", "feat/gen", "kernel");
        Assert.NotEqual(RunStatus.BLOCKED, kernelOnly.PreliminaryStatus);
        Assert.Contains(kernelOnly.Commits.Single().Paths, p => p.SourcePath.EndsWith("Model.g.cs") && p.Decision == Decision.Exclude);
        var withPlatform = await T.Feature(e, "backend", "qisl", "isl", "feat/gen", "kernel", "platform");
        Assert.Equal(RunStatus.BLOCKED, withPlatform.PreliminaryStatus);
        Assert.Contains(withPlatform.Commits.Single().Paths, p => p.SourcePath.EndsWith("Model.g.cs") && p.Decision == Decision.Block);
    }

    [Fact]
    public async Task Nested_product_specific_path_under_shared_component_is_excluded()
    {
        using var fx = new RepoFixture();
        T.Branch(fx.QislFrontend, "feat/brand", "Brand and format",
            ("libs/shared-ui/src/branding/theme.ts", "export const brand = 'qisl-2026';\n"),
            ("libs/shared-ui/src/format.ts", "export function formatMoney(v: number): string {\n  return v.toFixed(3);\n}\n"));
        var e = fx.Engine();
        var plan = await T.Feature(e, "frontend", "qisl", "isl", "feat/brand");
        var brand = plan.Commits.Single().Paths.Single(p => p.SourcePath.Contains("branding"));
        Assert.Equal(Decision.Exclude, brand.Decision);
        Assert.StartsWith("ProductSpecific", brand.SourceClass);
        var run = await T.Simulate(e, plan);
        Assert.Contains("'isl'", T.MirrorFile(e, "isl-frontend", run.CandidateSha!, "projects/common/shared-ui/src/branding/theme.ts"));
    }

    [Fact]
    public async Task Unknown_paths_block_and_new_shared_dependency_is_not_silently_excluded()
    {
        using var fx = new RepoFixture(frontend: false);
        // New top-level folder nobody classified.
        T.Branch(fx.QislBackend, "feat/unknown", "New gateway", ("Gateway/Gateway.cs", "class G {}\n"));
        var e = fx.Engine();
        var p1 = await T.Feature(e, "backend", "qisl", "isl", "feat/unknown");
        Assert.Equal(RunStatus.BLOCKED, p1.PreliminaryStatus);
        Assert.True(p1.Findings.Has("UNKNOWN_PATH"));

        // A shared project gains a reference to a project in a component that is not selected / does not exist in target.
        RepoFixture.Git(fx.QislBackend, "switch", "-q", "development");
        T.Branch(fx.QislBackend, "feat/reporting", "Reporting dependency",
            ("Fabric/Reporting/Reporting/Reporting.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>\n"),
            ("Fabric/Platform/Platform.Core/Platform.Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>\n  <ItemGroup>\n    <ProjectReference Include=\"../../Kernel/Kernel.Domain/Kernel.Domain.csproj\" />\n    <ProjectReference Include=\"../../Reporting/Reporting/Reporting.csproj\" />\n  </ItemGroup>\n</Project>\n"));
        var p2 = await T.Feature(e, "backend", "qisl", "isl", "feat/reporting");
        Assert.Equal(RunStatus.BLOCKED, p2.PreliminaryStatus);
        Assert.True(p2.Findings.Has("MISSING_PROJECT"));
        Assert.Contains("reporting", p2.Findings.Single(f => f.Code == "MISSING_PROJECT").SuggestedAction!);

        // Explicitly including the (proposed) component resolves it - with a review finding for the proposed mapping.
        var p3 = await e.Planner.PlanAsync(new PlanRequest
        {
            Pair = "backend", From = "qisl", To = "isl", Mode = SelectionMode.Feature, SourceRef = "feat/reporting",
            Components = ["kernel", "platform", "reporting"], AllowProposed = true,
        });
        Assert.False(p3.Findings.Has("MISSING_PROJECT"));
        Assert.True(p3.Findings.Has("PROPOSED_COMPONENTS"));
        Assert.True(p3.Findings.Has("COMPONENT_MISSING_IN_TARGET"));
        Assert.Equal(RunStatus.REVIEW_REQUIRED, p3.PreliminaryStatus);
    }

    [Fact]
    public async Task Deletion_and_rename_inside_component_transfer_with_mapping()
    {
        using var fx = new RepoFixture();
        T.Branch(fx.QislFrontend, "feat/rename", "Rename format -> money, drop button",
            ("libs/shared-ui/src/format.ts", null), ("libs/shared-ui/src/money.ts", "export function formatMoney(v: number): string {\n  return v.toFixed(2);\n}\n"),
            ("libs/shared-ui/src/button.component.ts", null));
        var e = fx.Engine();
        var plan = await T.Feature(e, "frontend", "qisl", "isl", "feat/rename");
        var ren = plan.Commits.Single().Paths.Single(p => p.Status == 'R');
        Assert.Equal("projects/common/shared-ui/src/format.ts", ren.TargetOldPath);
        Assert.Equal("projects/common/shared-ui/src/money.ts", ren.TargetPath);
        Assert.True(plan.Findings.Has("EXPORT_REMOVED") || plan.Findings.Has("ANGULAR_SELECTOR_CHANGED"));
        var run = await T.Simulate(e, plan);
        var tree = RepoFixture.Git(e.Store.MirrorDir("isl-frontend"), "ls-tree", "-r", "--name-only", run.CandidateSha!);
        Assert.Contains("projects/common/shared-ui/src/money.ts", tree);
        Assert.DoesNotContain("projects/common/shared-ui/src/format.ts", tree);
        Assert.DoesNotContain("projects/common/shared-ui/src/button.component.ts", tree);
    }

    [Fact]
    public async Task Rename_across_policy_boundary_reports_both_ends_and_blocks()
    {
        using var fx = new RepoFixture(frontend: false);
        RepoFixture.Git(fx.QislBackend, "switch", "-q", "-c", "feat/move");
        RepoFixture.Git(fx.QislBackend, "mv", "App.VAM/Vam/Vehicle.cs", "Fabric/Kernel/Kernel.Domain/Vehicle.cs");
        RepoFixture.Commit(fx.QislBackend, "Promote Vehicle to kernel");
        RepoFixture.Push(fx.QislBackend, "feat/move");
        var e = fx.Engine();
        var plan = await T.Feature(e, "backend", "qisl", "isl", "feat/move");
        Assert.Equal(RunStatus.BLOCKED, plan.PreliminaryStatus);
        var f = plan.Findings.Single(x => x.Code == "RENAME_ACROSS_BOUNDARY");
        Assert.Contains("App.VAM/Vam/Vehicle.cs", f.Files);
        Assert.Contains("Fabric/Kernel/Kernel.Domain/Vehicle.cs", f.Files);
    }

    [Fact]
    public async Task Binary_non_ascii_and_crlf_content_is_byte_exact()
    {
        using var fx = new RepoFixture();
        var bin = new byte[] { 0, 1, 2, 255, 254, 0x89, 0x50, 0x4E, 0x47, 0, 0, 13, 10, 13 };
        var crlf = Encoding.UTF8.GetBytes("line one\r\nline two ü\r\n");
        RepoFixture.Git(fx.QislFrontend, "switch", "-q", "-c", "feat/assets");
        RepoFixture.WriteBytes(fx.QislFrontend, "libs/shared-ui/src/assets/logo.png", bin);
        RepoFixture.WriteBytes(fx.QislFrontend, "libs/shared-ui/src/i18n/übersetzung de (CRLF).txt", crlf);
        RepoFixture.Commit(fx.QislFrontend, "Assets: binary, umlaut path, CRLF");
        RepoFixture.Push(fx.QislFrontend, "feat/assets");
        var e = fx.Engine();
        var plan = await T.Feature(e, "frontend", "qisl", "isl", "feat/assets");
        Assert.Contains(plan.Commits.Single().Paths, p => p.Binary);
        var run = await T.Simulate(e, plan);
        Assert.Equal(RunStatus.READY, run.Status);
        var mirror = e.Store.MirrorDir("isl-frontend");
        var git = new ForkBridge.Core.Git.GitClient(new ForkBridge.Core.Infrastructure.ProcessRunner());
        Assert.Equal(bin, await git.ReadBlobAsync(mirror, run.CandidateSha!, "projects/common/shared-ui/src/assets/logo.png"));
        Assert.Equal(crlf, await git.ReadBlobAsync(mirror, run.CandidateSha!, "projects/common/shared-ui/src/i18n/übersetzung de (CRLF).txt"));
    }

    [Fact]
    public async Task Generated_files_block_by_default()
    {
        using var fx = new RepoFixture(frontend: false);
        RepoFixture.Git(fx.QislBackend, "switch", "-q", "-c", "feat/lib");
        RepoFixture.WriteBytes(fx.QislBackend, "lib/Kernel.Domain.dll", [0x4D, 0x5A, 1, 2, 3]);
        RepoFixture.Write(fx.QislBackend, "Fabric/Kernel/Kernel.Domain/Entity.cs", "namespace Kernel.Domain;\npublic class Entity { public int Id { get; set; } public string Name { get; set; } = \"\"; public int V { get; set; } }\n");
        RepoFixture.Commit(fx.QislBackend, "Rebuild lib");
        RepoFixture.Push(fx.QislBackend, "feat/lib");
        var e = fx.Engine();
        var plan = await T.Feature(e, "backend", "qisl", "isl", "feat/lib");
        Assert.Equal(RunStatus.BLOCKED, plan.PreliminaryStatus);
        Assert.Contains(plan.Commits.Single().Paths, p => p.SourcePath == "lib/Kernel.Domain.dll" && p.Decision == Decision.Block);
    }

    [Fact]
    public async Task Target_pinned_config_is_never_overwritten_and_is_reported()
    {
        using var fx = new RepoFixture(frontend: false);
        T.Branch(fx.QislBackend, "feat/cfg", "Config and code",
            ("Fabric/Platform/Web.Host/appsettings.json", "{ \"Env\": \"qisl-new\" }\n"),
            ("Fabric/Platform/Platform.Core/Service.cs", "namespace Platform.Core;\npublic class Service { public string Describe(Kernel.Domain.Entity e) => e.Name + \"!\"; }\n"));
        var e = fx.Engine();
        var plan = await T.Feature(e, "backend", "qisl", "isl", "feat/cfg");
        var pinned = plan.Commits.Single().Paths.Single(p => p.SourcePath.EndsWith("appsettings.json"));
        Assert.Equal(Decision.Exclude, pinned.Decision);
        Assert.Contains("target-pinned", pinned.Reason);
        var run = await T.Simulate(e, plan);
        Assert.Contains("isl-base", T.MirrorFile(e, "isl-backend", run.CandidateSha!, "Fabric/Platform/Web.Host/appsettings.json"));
    }
}

public class PrerequisiteAndGraphTests
{
    [Fact]
    public async Task Selecting_a_commit_without_its_prerequisite_blocks_with_a_missing_import()
    {
        using var fx = new RepoFixture();
        var first = T.Branch(fx.QislFrontend, "feat/chain", "Add currency module", ("libs/shared-ui/src/currency.ts", "export const EUR = 'EUR';\n"));
        var second = T.Branch(fx.QislFrontend, "feat/chain", "Use currency module",
            ("libs/shared-ui/src/format.ts", "import { EUR } from './currency';\nexport function formatMoney(v: number): string {\n  return `${v.toFixed(2)} ${EUR}`;\n}\n"));
        var e = fx.Engine();
        var onlySecond = await T.Commits(e, "frontend", "qisl", "isl", second);
        Assert.Equal(RunStatus.BLOCKED, onlySecond.PreliminaryStatus);
        Assert.Contains("projects/common/shared-ui/src/currency", onlySecond.Findings.Single(f => f.Code == "MISSING_IMPORT_TARGET").Evidence.Single());
        var both = await T.Commits(e, "frontend", "qisl", "isl", first, second);
        Assert.False(both.Findings.Has("MISSING_IMPORT_TARGET"));
        Assert.Equal(RunStatus.READY, (await T.Simulate(e, both)).Status);
    }

    [Fact]
    public void HintPath_assembly_references_map_back_to_producing_projects()
    {
        var g = new ForkBridge.Core.Analysis.DependencyGraph();
        var producer = ForkBridge.Core.Analysis.DependencyGraph.ParseCsproj("Fabric/Kernel/K/K.csproj",
            System.Text.Encoding.UTF8.GetBytes("<Project><PropertyGroup><AssemblyName>QiSL.Platform.SharedKernel.Domain</AssemblyName></PropertyGroup></Project>"))!;
        var consumer = ForkBridge.Core.Analysis.DependencyGraph.ParseCsproj("App.MTL/M/M.csproj",
            System.Text.Encoding.UTF8.GetBytes("<Project><ItemGroup><Reference Include=\"QiSL.Platform.SharedKernel.Domain, Version=1.0.0.0\"><HintPath>..\\..\\lib\\QiSL.Platform.SharedKernel.Domain.dll</HintPath></Reference></ItemGroup></Project>"))!;
        g.Projects[producer.Path] = producer; g.Projects[consumer.Path] = consumer;
        Assert.Equal("lib/QiSL.Platform.SharedKernel.Domain.dll", consumer.HintPaths.Single());
        Assert.Equal(consumer.Path, g.TransitiveConsumers([producer]).Single().Path);
    }
}
