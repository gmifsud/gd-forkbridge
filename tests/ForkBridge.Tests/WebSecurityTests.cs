using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ForkBridge.Tests.Fixtures;
using ForkBridge.Web;

namespace ForkBridge.Tests;

public class WebSecurityTests
{
    private static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

    [Fact]
    public async Task Api_requires_token_loopback_host_same_origin_and_json()
    {
        using var fx = new RepoFixture(frontend: false);
        T.Branch(fx.QislBackend, "feat/web", "web", ("Fabric/Kernel/Kernel.Domain/Audit.cs", "namespace Kernel.Domain;\npublic class Audit { public string Who { get; set; } = \"w\"; }\n"));
        var port = FreePort();
        await using var app = WebHost.Build(new WebOptions { ConfigPath = fx.ConfigPath, Port = port, Token = "t0k3n", WebRoot = Path.Combine(fx.Root, "www") });
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/config")).StatusCode);
        HttpRequestMessage Req(HttpMethod m, string url, string? json = null, string token = "t0k3n")
        {
            var r = new HttpRequestMessage(m, url);
            r.Headers.Add("X-ForkBridge-Token", token);
            if (json is not null) r.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return r;
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Req(HttpMethod.Get, "/api/config", token: "wrong"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Req(HttpMethod.Get, "/api/config"))).StatusCode);

        var rebinding = Req(HttpMethod.Get, "/api/config"); rebinding.Headers.Host = "evil.example";
        Assert.Equal(HttpStatusCode.MisdirectedRequest, (await http.SendAsync(rebinding)).StatusCode);
        var cross = Req(HttpMethod.Post, "/api/discover", "{}"); cross.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(cross)).StatusCode);
        var form = Req(HttpMethod.Post, "/api/discover"); form.Content = new StringContent("fetch=true", Encoding.UTF8, "application/x-www-form-urlencoded");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await http.SendAsync(form)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await http.SendAsync(Req(HttpMethod.Delete, "/api/runs/x"))).StatusCode);
        // Ids are sanitized - no path traversal through the API.
        Assert.NotEqual(HttpStatusCode.OK, (await http.SendAsync(Req(HttpMethod.Get, "/api/plans/..%2F..%2Fetc"))).StatusCode);

        // A plan through the job API.
        var start = await http.SendAsync(Req(HttpMethod.Post, "/api/plans", JsonSerializer.Serialize(new { pair = "backend", from = "qisl", to = "isl", mode = "feature", sourceRef = "feat/web" })));
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var jobId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString();
        JsonElement job = default;
        for (int i = 0; i < 100; i++)
        {
            job = await (await http.SendAsync(Req(HttpMethod.Get, $"/api/jobs/{jobId}"))).Content.ReadFromJsonAsync<JsonElement>();
            if (job.GetProperty("state").GetString() != "running") break;
            await Task.Delay(200);
        }
        Assert.Equal("succeeded", job.GetProperty("state").GetString());
        var planId = job.GetProperty("result").GetProperty("planId").GetString();
        var plan = await (await http.SendAsync(Req(HttpMethod.Get, $"/api/plans/{planId}"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PLANNED", plan.GetProperty("preliminaryStatus").GetString());
        await app.StopAsync();
    }
}
