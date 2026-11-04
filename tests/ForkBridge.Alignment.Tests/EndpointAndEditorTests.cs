using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ForkBridge.Alignment.Config;
using ForkBridge.Alignment.Model;
using ForkBridge.Web;

namespace ForkBridge.Alignment.Tests;

public class ProfileEditorTests
{
    private const string Yaml = """
        # header comment
        version: 1
        stateDir: "state"
        profiles:
          # ---- BACKEND ----
          backend:
            primary: { repo: qisl, label: QiSL, legacyName: Backend }
            fork:    { repo: isl,  label: ISL,  legacyName: Isl }
            # the base
            syncBase: "1111111"   # keep me
            scope: [Shared]
            overrides:
              - { pattern: "*/packages.lock.json", decision: KEEP_BOTH, reason: "derived" }
              - pattern: "a/b.cs"
                decision: KEEP_BOTH
                reason: "multi-line item"
            neverPush: [main]

          # ---- FRONTEND ----
          frontend:
            primary: { repo: qisl, label: QiSL, legacyName: Frontend }
            fork:    { repo: isl,  label: ISL }
            scope: [app]
            overrides: []
        """;

    private static string Write(string text) { var f = Path.Combine(Path.GetTempPath(), "fbedit-" + Guid.NewGuid().ToString("N")[..8] + ".yml"); File.WriteAllText(f, text); return f; }

    [Fact]
    public void SetSyncBase_replaces_only_that_profile_line_and_keeps_comments_and_a_backup()
    {
        var f = Write(Yaml);
        AlignmentProfileEditor.SetSyncBase(f, "backend", "8910728b3f", "Advisor");
        var t = File.ReadAllText(f);
        Assert.Contains("syncBase: \"8910728b3f\"   # set via Harmony", t);
        Assert.Contains("was 1111111", t);
        Assert.Contains("# header comment", t);
        Assert.Contains("# ---- FRONTEND ----", t);
        Assert.Equal(Yaml, File.ReadAllText(f + ".bak"));
        Assert.Equal("8910728b3f", AlignmentConfigLoader.Load(f).Profiles["backend"].SyncBase);
        AlignmentProfileEditor.SetSyncBase(f, "frontend", "c56a755");          // absent -> inserted inside the profile
        var cfg = AlignmentConfigLoader.Load(f);
        Assert.Equal("c56a755", cfg.Profiles["frontend"].SyncBase);
        Assert.Equal("8910728b3f", cfg.Profiles["backend"].SyncBase);
    }

    [Fact]
    public void AddOverride_appends_after_multi_line_items_and_expands_empty_lists()
    {
        var f = Write(Yaml);
        AlignmentProfileEditor.AddOverride(f, "backend", "Shared/X \"q\".cs", Decision.KeepBoth, "per product", "BE");
        AlignmentProfileEditor.AddOverride(f, "frontend", "app/env/*", Decision.TakeFork, "", "FE");
        var cfg = AlignmentConfigLoader.Load(f);
        Assert.Equal(["*/packages.lock.json", "a/b.cs", "Shared/X \"q\".cs"], cfg.Profiles["backend"].Overrides.Select(o => o.Pattern));
        Assert.Equal("KEEP_BOTH", cfg.Profiles["backend"].Overrides[2].Decision);
        Assert.Equal(["main"], cfg.Profiles["backend"].NeverPush);
        Assert.Equal("TAKE_ISL", cfg.Profiles["frontend"].Overrides.Single().Decision);
    }

    [Fact]
    public void Unknown_profile_or_bad_sha_is_refused_without_touching_the_file()
    {
        var f = Write(Yaml);
        Assert.Throws<ForkBridge.Core.Config.ConfigException>(() => AlignmentProfileEditor.SetSyncBase(f, "mobile", "1234567"));
        Assert.Throws<ArgumentException>(() => AlignmentProfileEditor.SetSyncBase(f, "backend", "HEAD~1"));
        Assert.Equal(Yaml, File.ReadAllText(f));
        Assert.False(File.Exists(f + ".bak"));
    }
}

public class AlignmentEndpointTests
{
    private static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

    [Fact]
    public async Task Harmony_api_plans_derives_advises_and_edits_the_profile()
    {
        using var fx = new AlignFixture("backend");
        fx.WriteAlignConfig(AlignFixture.BackendProfileYaml.Replace("@SYNC@", fx.SyncBase));
        var port = FreePort();
        await using var app = WebHost.Build(new WebOptions { ConfigPath = fx.FbConfig, Port = port, Token = "t0k3n", WebRoot = Path.Combine(fx.Root, "www") });
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        HttpRequestMessage Req(HttpMethod m, string url, object? body = null)
        {
            var r = new HttpRequestMessage(m, url);
            r.Headers.Add("X-ForkBridge-Token", "t0k3n");
            if (body is not null) r.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            return r;
        }
        async Task<JsonElement> J(HttpMethod m, string url, object? body = null, HttpStatusCode expect = HttpStatusCode.OK)
        {
            var resp = await http.SendAsync(Req(m, url, body));
            var text = await resp.Content.ReadAsStringAsync();
            Assert.True(resp.StatusCode == expect, $"{m} {url} -> {(int)resp.StatusCode}: {text}");
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        async Task<JsonElement> Job(JsonElement start)
        {
            var id = start.GetProperty("jobId").GetString();
            for (var i = 0; i < 200; i++)
            {
                var j = await J(HttpMethod.Get, $"/api/jobs/{id}");
                if (j.GetProperty("state").GetString() != "running") { Assert.Equal("succeeded", j.GetProperty("state").GetString()); return j.GetProperty("result"); }
                await Task.Delay(100);
            }
            throw new TimeoutException();
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/align/profiles")).StatusCode);
        var profiles = await J(HttpMethod.Get, "/api/align/profiles");
        Assert.True(profiles.GetProperty("found").GetBoolean());
        Assert.Equal("backend", profiles.GetProperty("profiles")[0].GetProperty("name").GetString());

        var wts = await J(HttpMethod.Get, "/api/align/backend/worktrees");
        var pid = wts.EnumerateArray().First(w => w.GetProperty("side").GetString() == "Primary").GetProperty("id").GetString();
        var fid = wts.EnumerateArray().First(w => w.GetProperty("side").GetString() == "Fork").GetProperty("id").GetString();

        var planned = await Job(await J(HttpMethod.Post, "/api/align/backend/plan", new { primaryWorktreeId = pid, forkWorktreeId = fid }));
        var planId = planned.GetProperty("id").GetString()!;
        var plan = await J(HttpMethod.Get, $"/api/align/backend/plans/{planId}");
        var b = plan.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("path").GetString() == "Shared/B.cs");
        Assert.Equal("TakeFork", b.GetProperty("decision").GetString());

        var blob = await J(HttpMethod.Get, $"/api/align/backend/plans/{planId}/blob?sha={b.GetProperty("fork").GetProperty("sha").GetString()}");
        Assert.Contains("int y = 2", blob.GetProperty("text").GetString());
        await J(HttpMethod.Get, $"/api/align/backend/plans/{planId}/blob?sha={new string('a', 40)}", expect: HttpStatusCode.BadRequest);

        var derived = await J(HttpMethod.Post, $"/api/align/backend/plans/{planId}/decisions", new { edits = new Dictionary<string, string> { ["Shared/B.cs"] = "KeepBoth" } });
        Assert.Equal(planId, derived.GetProperty("parentPlanId").GetString());
        await J(HttpMethod.Post, $"/api/align/backend/plans/{planId}/decisions", new { edits = new Dictionary<string, string> { ["../../etc"] = "KeepBoth" } }, HttpStatusCode.BadRequest);
        Assert.Equal(2, (await J(HttpMethod.Get, "/api/align/backend/plans")).GetArrayLength());

        var adv = await Job(await J(HttpMethod.Post, "/api/align/backend/syncbase/advise", new { planId }));
        Assert.Equal(fx.SyncBase, adv.GetProperty("recommended").GetString());
        Assert.True(adv.GetProperty("lines").GetArrayLength() >= 1);

        await J(HttpMethod.Post, "/api/align/backend/profile/syncbase", new { sha = fx.SyncBase[..10], note = "test" });
        await J(HttpMethod.Post, "/api/align/backend/profile/override", new { pattern = "Shared/B.cs", decision = "KEEP_BOTH", reason = "per product" });
        var cfg = AlignmentConfigLoader.Load(fx.AlignConfig).Profiles["backend"];
        Assert.Equal(fx.SyncBase[..10], cfg.SyncBase);
        Assert.Contains(cfg.Overrides, o => o.Pattern == "Shared/B.cs" && o.Decision == "KEEP_BOTH");
        await J(HttpMethod.Post, "/api/align/backend/profile/syncbase", new { sha = "HEAD~1" }, HttpStatusCode.BadRequest);
        await J(HttpMethod.Get, "/api/align/nope/plans", expect: HttpStatusCode.BadRequest);
    }
}
