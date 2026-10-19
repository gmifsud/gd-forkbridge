using System.Net;
using System.Text;
using ForkBridge.Cli;
using ForkBridge.Core.Config;
using ForkBridge.Core.Git;
using ForkBridge.Core.Infrastructure;
using ForkBridge.Core.Model;
using ForkBridge.Core.Planning;
using ForkBridge.Core.Policy;
using ForkBridge.Core.Publishing;
using ForkBridge.Core.Reporting;
using ForkBridge.Core.Transfer;
using ForkBridge.Tests.Fixtures;

namespace ForkBridge.Tests;

[Collection("env")]
public class PublishCliBundleTests
{
    private const string Audit2 = "namespace Kernel.Domain;\n\npublic class Audit\n{\n    public string Who { get; set; } = \"\";\n    public string Reason { get; set; } = \"\";\n}\n";

    [Fact]
    public async Task Publish_is_approval_bound_never_forced_and_detects_tampering()
    {
        using var fx = new RepoFixture(frontend: false);
        T.Branch(fx.QislBackend, "feat/p", "p", ("Fabric/Kernel/Kernel.Domain/Audit.cs", Audit2));
        var e = fx.Engine();
        var run = await T.Simulate(e, await T.Feature(e, "backend", "qisl", "isl", "feat/p"));

        await Assert.ThrowsAsync<PublishRefusedException>(() => e.Publisher.PublishAsync(run.Id, false));            // not approved
        await Assert.ThrowsAsync<PublishRefusedException>(() => e.Publisher.ApproveAsync(run.Id, "0000000000", [], "t")); // wrong candidate
        var appr = await e.Publisher.ApproveAsync(run.Id, run.CandidateSha![..10], [], "tester");
        Assert.Contains(run.CandidateSha, appr.Binding);

        // Tampered report invalidates approval.
        var rp = ReportWriter.JsonPath(e.Store, run.Id);
        var original = File.ReadAllText(rp);
        File.WriteAllText(rp, original + " ");
        var stale = await Assert.ThrowsAsync<PublishRefusedException>(() => e.Publisher.PublishAsync(run.Id, false));
        Assert.Contains("report changed", stale.Message);
        File.WriteAllText(rp, original);

        var rec = await e.Publisher.PublishAsync(run.Id, false);
        Assert.True(rec.Pushed);
        Assert.Equal(run.CandidateSha, RepoFixture.Git(fx.Remote("ISL-Backend"), "rev-parse", "refs/heads/" + run.Branch));
        // development on the remote is untouched.
        Assert.NotEqual(run.CandidateSha, RepoFixture.Git(fx.Remote("ISL-Backend"), "rev-parse", "refs/heads/development"));
        // Second publish of the same branch is refused (no force).
        var again = await Assert.ThrowsAsync<PublishRefusedException>(() => e.Publisher.PublishAsync(run.Id, false));
        Assert.Contains("never force-pushes", again.Message);
    }

    [Fact]
    public async Task Publish_refuses_protected_branches_and_moved_target_base()
    {
        using var fx = new RepoFixture(frontend: false) { BackendProtected = "[development, \"forkbridge/**\"]" };
        fx.WriteConfig();
        T.Branch(fx.QislBackend, "feat/q", "q", ("Fabric/Kernel/Kernel.Domain/Audit.cs", Audit2));
        var e = fx.Engine();
        var run = await T.Simulate(e, await T.Feature(e, "backend", "qisl", "isl", "feat/q"));
        await e.Publisher.ApproveAsync(run.Id, run.CandidateSha![..10], [], "t");
        var ex = await Assert.ThrowsAsync<PublishRefusedException>(() => e.Publisher.PublishAsync(run.Id, false));
        Assert.Contains("protected", ex.Message);

        using var fx2 = new RepoFixture(frontend: false);
        T.Branch(fx2.QislBackend, "feat/q", "q", ("Fabric/Kernel/Kernel.Domain/Audit.cs", Audit2));
        var e2 = fx2.Engine();
        var run2 = await T.Simulate(e2, await T.Feature(e2, "backend", "qisl", "isl", "feat/q"));
        await e2.Publisher.ApproveAsync(run2.Id, run2.CandidateSha![..10], [], "t");
        T.OnDevelopment(fx2.IslBackend, "moved", ("App.MTL/Mtl/X.cs", "class X {}\n"));
        await Assert.ThrowsAsync<StaleException>(() => e2.Publisher.PublishAsync(run2.Id, false));
    }

    [Fact]
    public async Task Cli_end_to_end_with_documented_exit_codes()
    {
        using var fx = new RepoFixture(frontend: false);
        T.Branch(fx.QislBackend, "feat/cli", "cli", ("Fabric/Kernel/Kernel.Domain/Audit.cs", Audit2));
        var cfg = new[] { "--config", fx.ConfigPath };
        async Task<(int code, string outText, string err)> Cli(params string[] a)
        {
            var o = new StringWriter(); var er = new StringWriter();
            var code = await ForkBridge.Cli.Program.RunAsync(a.Concat(cfg).ToArray(), o, er, CancellationToken.None);
            return (code, o.ToString(), er.ToString());
        }
        Assert.Equal(0, (await Cli("validate-config")).code);
        var (pc, pout, _) = await Cli("plan", "--pair", "backend", "--from", "qisl", "--to", "isl", "--source-ref", "feat/cli", "--source-base", "development", "--target-base", "development");
        Assert.Equal(ExitCodes.Ok, pc);
        var planId = System.Text.RegularExpressions.Regex.Match(pout, @"Plan (pl-[\w-]+)").Groups[1].Value;
        var (sc, sout, _) = await Cli("simulate", "--plan", planId, "--trust-validation");
        Assert.Equal(ExitCodes.Ok, sc);
        var runId = System.Text.RegularExpressions.Regex.Match(sout, @"Run (run-[\w-]+)").Groups[1].Value;
        var (rc, rmd, _) = await Cli("report", "--run", runId, "--format", "markdown");
        Assert.Equal(0, rc);
        Assert.Contains("READY", rmd);
        var cand = System.Text.RegularExpressions.Regex.Match(sout, @"candidate ([0-9a-f]{40})").Groups[1].Value;
        Assert.Equal(ExitCodes.Ok, (await Cli("publish", "--run", runId, "--approve", "--candidate", cand[..12])).code);
        Assert.Equal(ExitCodes.Ok, (await Cli("cleanup", "--run", runId)).code);
        // Blocked plan, development-as-feature refusal, unknown pair, stale plan.
        T.Branch(fx.QislBackend, "feat/unk", "u", ("Gateway/G.cs", "x"));
        Assert.Equal(ExitCodes.Blocked, (await Cli("plan", "--pair", "backend", "--from", "qisl", "--to", "isl", "--source-ref", "feat/unk")).code);
        Assert.Equal(ExitCodes.UsageError, (await Cli("plan", "--pair", "backend", "--from", "qisl", "--to", "isl", "--source-ref", "development")).code);
        Assert.Equal(ExitCodes.UsageError, (await Cli("plan", "--pair", "nope", "--from", "qisl", "--to", "isl", "--source-ref", "x")).code);
        Assert.Equal(ExitCodes.UsageError, (await Cli("frobnicate")).code);
        var (_, help, _) = await Cli("help");
        Assert.Contains("Exit codes", help);
        File.WriteAllText(fx.ConfigPath, "version: 1\nrepositories: {}\n");
        Assert.Equal(ExitCodes.ConfigError, (await Cli("runs")).code);
    }

    [Fact]
    public async Task Coordinated_bundle_records_sha_matrix_and_publishes_per_repository()
    {
        using var fx = new RepoFixture();
        T.Branch(fx.QislBackend, "feat/api", "Audit DTO", ("Fabric/Platform/Platform.Core/AuditDto.cs", "namespace Platform.Core;\npublic class AuditDto { public string Who { get; set; } = \"\"; }\n"));
        T.Branch(fx.QislFrontend, "feat/api", "Audit model", ("libs/shared-ui/src/audit.ts", "export interface AuditDto { who: string; }\n"));
        var e = fx.Engine();
        var pb = await T.Feature(e, "backend", "qisl", "isl", "feat/api");
        var pf = await T.Feature(e, "frontend", "qisl", "isl", "feat/api");
        Assert.True(pb.Findings.Has("CONTRACT_CHANGED"));
        var b = e.CreateBundle([pb.Id, pf.Id], "audit-dto");
        Assert.True(b.Findings.Has("API_CLIENT_COUPLING"));
        var bs = await e.SimulateBundleAsync(b.Id, new SimulateOptions { TrustValidation = true }, "fullstack");
        Assert.Equal(2, bs.RunIds.Count);
        Assert.Equal("PASSED", bs.CombinedValidation.Single().Outcome);
        Assert.Equal(RunStatus.REVIEW_REQUIRED, bs.Status); // coupling must be reviewed
        foreach (var rid in bs.RunIds.Values)
        {
            var r = e.Store.LoadRun(rid);
            Assert.Equal(b.Id, r.BundleId);
            var plan = e.Store.LoadPlan(r.PlanId);
            await e.Publisher.ApproveAsync(rid, r.CandidateSha![..10], plan.Findings.Concat(r.Findings).Where(f => f.RequiresReview).Select(f => f.Id), "t");
        }
        // Make the frontend push fail -> partial publication is recorded with recovery steps.
        var firstRun = e.Store.LoadRun(bs.RunIds[pb.Id]);
        var secondRun = e.Store.LoadRun(bs.RunIds[pf.Id]);
        RepoFixture.Git(fx.Remote("ISL-Frontend"), "branch", secondRun.Branch, "development");
        var ex = await Assert.ThrowsAsync<PublishRefusedException>(() => e.PublishBundleAsync(b.Id, false));
        Assert.Contains("PARTIAL PUBLICATION", ex.Message);
        Assert.True(e.Store.LoadRun(firstRun.Id).Publications.Single().Pushed);
    }

    [Fact]
    public async Task Discovery_is_read_only_and_proposes_only_evidence_backed_mappings()
    {
        using var fx = new RepoFixture();
        // A same-named directory with unrelated content must NOT be proposed.
        T.OnDevelopment(fx.QislBackend, "Tools q", ("Tools/a.txt", "qisl tooling\n"));
        T.OnDevelopment(fx.IslBackend, "Tools i", ("Tools/b.txt", "isl tooling\n"));
        var before = await fx.SnapshotUserReposAsync();
        var e = fx.Engine();
        var draft = Path.Combine(fx.Root, "draft.yml");
        var (report, json, md, draftPath) = await e.DiscoverAsync(fetch: false, depth: 2, writeConfig: draft);
        Assert.Equal(before, await fx.SnapshotUserReposAsync());
        var backend = report.Pairs.Single(p => p.Pair == "backend");
        Assert.True(backend.SharesHistory);
        Assert.Equal("proposed-shared", backend.Directories.Single(d => d.Path == "Fabric/Kernel").Verdict);
        Assert.Equal("name-only", backend.Directories.Single(d => d.Path == "Tools").Verdict);
        Assert.Equal("only-in-qisl", backend.Directories.Single(d => d.Path == "App.VAM").Verdict);
        var isl = report.Repositories.Single(r => r.Alias == "isl-backend");
        Assert.True(isl.BinaryAssemblyReferences > 0);
        Assert.Contains("lib/**/*.dll (tracked binaries)", isl.GeneratedArtifacts.Keys);
        Assert.True(File.Exists(md));
        var draftText = File.ReadAllText(draftPath!);
        Assert.Contains("status: proposed", draftText);
        Assert.DoesNotContain("status: confirmed", draftText);
        Assert.DoesNotContain("id: tools", draftText);
        var frontend = report.Pairs.Single(p => p.Pair == "frontend");
        Assert.Contains(frontend.Directories, d => d.Path == "projects" && d.Verdict == "only-in-isl");
    }

    [Fact]
    public async Task Azure_DevOps_adapter_uses_the_credential_helper_and_never_persists_the_secret()
    {
        // Fake secret only. Git config is isolated by TestIsolation (no system/global helpers), and the
        // helper list is reset ("") before the fake helper so no real credential manager is ever asked.
        const string secret = "fake-test-secret-not-a-real-credential";
        var vars = new Dictionary<string, string?>
        {
            ["GIT_CONFIG_COUNT"] = "2",
            ["GIT_CONFIG_KEY_0"] = "credential.helper", ["GIT_CONFIG_VALUE_0"] = "",
            ["GIT_CONFIG_KEY_1"] = "credential.helper", ["GIT_CONFIG_VALUE_1"] = $"!f() {{ echo username=pat; echo password={secret}; }}; f",
        };
        foreach (var (k, v) in vars) Environment.SetEnvironmentVariable(k, v);
        try
        {
            var handler = new FakeHandler();
            var git = new GitClient(new ProcessRunner());
            var p = new AzureDevOpsPullRequestProvider(git, Path.GetTempPath(), handler);
            var url = await p.CreateAsync("https://ISLMalta@dev.azure.com/ISLMalta/EBS%20%28Platform%29/_git/Backend", "forkbridge/x", "development", "t", "d", true, CancellationToken.None);
            Assert.Equal("https://dev.azure.com/ISLMalta/EBS%20%28Platform%29/_git/Backend/pullrequest/42", url);
            Assert.Contains("/_apis/git/repositories/Backend/pullrequests", handler.LastUri!.ToString());
            // Never pass credential material to an assertion that prints values on failure.
            var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("pat:" + secret));
            Assert.True(handler.LastAuth == expected, "Authorization header did not come from the isolated fake credential helper (value withheld).");
            Assert.Contains("\"isDraft\":true", handler.LastBody);
        }
        finally { foreach (var k in vars.Keys) Environment.SetEnvironmentVariable(k, null); }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Uri? LastUri; public string? LastAuth; public string LastBody = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            LastUri = r.RequestUri; LastAuth = r.Headers.Authorization?.Parameter; LastBody = await r.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{\"pullRequestId\":42}") };
        }
    }
}

public class UnitTests
{
    [Theory]
    [InlineData("App.VAM/**", "App.VAM/Vam/X.cs", true)]
    [InlineData("App.VAM/**", "App.VAMX/a.cs", false)]
    [InlineData("Fabric/Platform", "Fabric/Platform/x/y.cs", true)]
    [InlineData("Fabric/Platform", "Fabric/PlatformX/y.cs", false)]
    [InlineData("**/appsettings*.json", "a/b/appsettings.Development.json", true)]
    [InlineData("src/*.ts", "src/a/b.ts", false)]
    [InlineData("lib/**", "lib/Kernel.dll", true)]
    [InlineData("**/Resources/**/*.Designer.cs", "QiSL.Fabric/Platform/aspnet-core/src/QiSL.Platform.Mobile.Droid/Resources/Resource.Designer.cs", true)]
    [InlineData("**/Resources/**/*.Designer.cs", "QiSL.Fabric/Platform/aspnet-core/src/QiSL.Platform.Mobile.MAUI/Resources/Localization/AppResources.Designer.cs", true)]
    [InlineData("**/Resources/**/*.Designer.cs", "QiSL.Fabric/Kernel/QiSL.Platform.SharedKernel.EntityFrameworkCore/Migrations/20261007160000_X.Designer.cs", false)]
    public void Glob_matches_repository_paths(string pattern, string path, bool expected) => Assert.Equal(expected, Glob.IsMatch(pattern, path));

    [Theory]
    [InlineData("https://user:pa55word@dev.azure.com/org/p/_git/r", "https://<redacted>@dev.azure.com/org/p/_git/r")]
    [InlineData("https://ISLMalta@dev.azure.com/x", "https://<redacted>@dev.azure.com/x")]
    [InlineData("Authorization: Basic dXNlcjpwYXNzd29yZA==", "Authorization: <redacted>")]
    [InlineData("token=abcdef123456", "token=<redacted>")]
    public void Redactor_removes_credentials(string input, string expected) => Assert.Equal(expected, Redactor.Redact(input));

    [Fact]
    public void Path_rewriting_and_quoting_follow_git_rules()
    {
        var patch = Encoding.UTF8.GetBytes("diff --git a/libs/x/a b.ts b/libs/x/a b.ts\nindex 1..2 100644\n--- a/libs/x/a b.ts\n+++ b/libs/x/a b.ts\n@@ -1 +1 @@\n-a\n+b\n");
        var o = Encoding.UTF8.GetString(ForkBridge.Core.Transfer.PatchBuilder.RewritePaths(patch, "libs/x/a b.ts", "libs/x/a b.ts", "proj/x/a b.ts", "proj/x/a b.ts"));
        Assert.Contains("diff --git a/proj/x/a b.ts b/proj/x/a b.ts", o);
        Assert.Contains("+++ b/proj/x/a b.ts", o);
        Assert.Equal("\"a/q\\\"uote\"", ForkBridge.Core.Transfer.PatchBuilder.Quote("a/q\"uote"));
    }

    [Fact]
    public void Config_validation_reports_placeholders_and_missing_paths()
    {
        var cfg = new ForkBridgeConfig
        {
            Repositories = { ["a"] = new() { Path = "<PLACEHOLDER: path>" }, ["b"] = new() { Path = "/x" } },
            Pairs = { ["p"] = new() { Products = { ["x"] = "a", ["y"] = "b" }, Components = { new() { Id = "c", Paths = { ["x"] = "src" } } } } },
        };
        var errors = ConfigValidator.Validate(cfg);
        Assert.Contains(errors, e => e.Contains("placeholder"));
        Assert.Contains(errors, e => e.Contains("no path for 'y'"));
    }

    [Fact]
    public void Commit_trailers_append_to_existing_trailer_block()
    {
        var m = Simulator.AppendTrailers("Subject\n\nBody\n\nSigned-off-by: A <a@b>", ["Fork-Over-Source: qisl@abc"]);
        Assert.EndsWith("Signed-off-by: A <a@b>\nFork-Over-Source: qisl@abc\n", m);
        Assert.EndsWith("Subject\n\nX: 1\n", Simulator.AppendTrailers("Subject", ["X: 1"]));
    }
}
