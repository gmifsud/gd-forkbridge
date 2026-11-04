using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ForkBridge.Web;

namespace ForkBridge.Merge.Tests;

public class MergeWebTests
{
    private static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

    private sealed class Client(HttpClient http, string token)
    {
        public HttpRequestMessage Req(HttpMethod m, string url, object? body = null, string? tok = null)
        {
            var r = new HttpRequestMessage(m, url);
            r.Headers.Add("X-ForkBridge-Token", tok ?? token);
            if (body is not null) r.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            return r;
        }
        public async Task<(HttpStatusCode, JsonElement)> Send(HttpMethod m, string url, object? body = null)
        {
            var r = await http.SendAsync(Req(m, url, body));
            var text = await r.Content.ReadAsStringAsync();
            return (r.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
        }
        public async Task<JsonElement> Job(string url, object body)
        {
            var (st, start) = await Send(HttpMethod.Post, url, body);
            Assert.Equal(HttpStatusCode.OK, st);
            var id = start.GetProperty("jobId").GetString();
            for (int i = 0; i < 300; i++)
            {
                var (_, j) = await Send(HttpMethod.Get, $"/api/jobs/{id}");
                var state = j.GetProperty("state").GetString();
                if (state == "succeeded") return j.GetProperty("result");
                if (state != "running") throw new Xunit.Sdk.XunitException($"job {state}: {j}");
                await Task.Delay(100);
            }
            throw new TimeoutException();
        }
    }

    [Fact]
    public async Task Merge_api_is_token_protected_typed_and_rejects_stale_writes()
    {
        using var fx = new MergeFixture();
        fx.OnBranch("feature/x", "develop", "src", ("src/shared.txt", "one\nSRC\nthree\nfour\nfive\n"));
        fx.OnBranch("develop", "develop", "tgt", ("src/shared.txt", "one\nTGT\nthree\nfour\nfive\n"));
        var before = fx.Snapshot();
        var port = FreePort();
        await using var app = WebHost.Build(new WebOptions { ConfigPath = fx.ConfigPath, Port = port, Token = "t0k3n", WebRoot = Path.Combine(fx.Root, "www") });
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var c = new Client(http, "t0k3n");

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/merge/repositories")).StatusCode);
        var form = c.Req(HttpMethod.Post, "/api/merge/plans"); form.Content = new StringContent("alias=app", Encoding.UTF8, "application/x-www-form-urlencoded");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await http.SendAsync(form)).StatusCode);
        var cross = c.Req(HttpMethod.Post, "/api/merge/repositories/app/refresh", new { offline = false }); cross.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(cross)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.Send(HttpMethod.Get, "/api/merge/repositories/nope/refs")).Item1);
        Assert.NotEqual(HttpStatusCode.OK, (await c.Send(HttpMethod.Get, "/api/merge/sessions/..%2F..%2Fetc")).Item1);

        var (st, repos) = await c.Send(HttpMethod.Get, "/api/merge/repositories");
        Assert.Equal(HttpStatusCode.OK, st);
        Assert.Equal("app", repos[0].GetProperty("alias").GetString());
        await c.Job("/api/merge/repositories/app/refresh", new { offline = false });
        var (_, refs) = await c.Send(HttpMethod.Get, "/api/merge/repositories/app/refs");
        Assert.Contains(refs.GetProperty("refs").EnumerateArray(), r => r.GetProperty("name").GetString() == "feature/x" && r.GetProperty("namespace").GetString() == "Remote");

        var plan = await c.Job("/api/merge/plans", new { alias = "app", source = new { @namespace = "Remote", name = "feature/x" }, target = new { @namespace = "Remote", name = "develop" } });
        Assert.Equal("Conflicts", plan.GetProperty("outcome").GetString());
        var planId = plan.GetProperty("planId").GetString();
        var sess = await c.Job($"/api/merge/plans/{planId}/sessions", new { });
        var sid = sess.GetProperty("sessionId").GetString();
        var (_, sv) = await c.Send(HttpMethod.Get, $"/api/merge/sessions/{sid}");
        Assert.True(sv.GetProperty("integrity").GetProperty("ok").GetBoolean());
        var entry = sv.GetProperty("view").GetProperty("entries")[0];
        var eid = entry.GetProperty("entryId").GetString();
        Assert.Equal("unresolved", entry.GetProperty("resolution").GetString());

        var (_, file) = await c.Send(HttpMethod.Get, $"/api/merge/sessions/{sid}/files/{eid}");
        var rev = file.GetProperty("sessionRevision").GetInt32();
        var hash = file.GetProperty("resultContentHash").GetString();
        Assert.True(file.GetProperty("editable").GetBoolean());

        var (ok, _) = await c.Send(HttpMethod.Post, $"/api/merge/sessions/{sid}/drafts", new { entryId = eid, expectedSessionRevision = rev, expectedContentHash = hash, content = "draft\n" });
        Assert.Equal(HttpStatusCode.OK, ok);
        var (stale, err) = await c.Send(HttpMethod.Post, $"/api/merge/sessions/{sid}/drafts", new { entryId = eid, expectedSessionRevision = rev, expectedContentHash = hash, content = "other tab\n" });
        Assert.Equal(HttpStatusCode.Conflict, stale);
        Assert.Contains("another tab", err.GetProperty("error").GetString());
        Assert.Equal(13, err.GetProperty("exitCode").GetInt32());

        var (_, f2) = await c.Send(HttpMethod.Get, $"/api/merge/sessions/{sid}/files/{eid}");
        var (resolved, rv) = await c.Send(HttpMethod.Post, $"/api/merge/sessions/{sid}/resolutions", new
        {
            entryId = eid, action = "EditResult", expectedSessionRevision = f2.GetProperty("sessionRevision").GetInt32(),
            expectedContentHash = f2.GetProperty("resultContentHash").GetString(), content = "merged\n",
        });
        Assert.Equal(HttpStatusCode.OK, resolved);
        Assert.True(rv.GetProperty("canFinalize").GetBoolean());
        var (fin, fv) = await c.Send(HttpMethod.Post, $"/api/merge/sessions/{sid}/finalize", new { expectedSessionRevision = rv.GetProperty("session").GetProperty("revision").GetInt32() });
        Assert.Equal(HttpStatusCode.OK, fin);
        Assert.Equal("Finalized", fv.GetProperty("session").GetProperty("state").GetString());
        // Unsupported action → 422, wrong revision → 409.
        var (wrong, _) = await c.Send(HttpMethod.Post, $"/api/merge/sessions/{sid}/abort", new { expectedSessionRevision = 0 });
        Assert.Equal(HttpStatusCode.Conflict, wrong);
        await app.StopAsync();
        Assert.Equal(before, fx.Snapshot());
    }
}
