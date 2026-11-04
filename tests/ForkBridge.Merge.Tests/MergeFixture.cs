using System.Diagnostics;
using System.Text;
using ForkBridge.Core;
using ForkBridge.Core.Config;
using ForkBridge.Core.Validation;
using ForkBridge.Merge.Model;
using ForkBridge.Merge.Resolution;

namespace ForkBridge.Merge.Tests;

/// <summary>One real repository ("app") with a bare origin, a user checkout and a ForkBridge config whose state
/// directory lives beside (never inside) the checkout. Default branch: develop.</summary>
public sealed class MergeFixture : IDisposable
{
    private static int _seq;
    public string Root { get; }
    public string Checkout => Path.Combine(Root, "work", "app");
    public string Origin => Path.Combine(Root, "remotes", "app.git");
    public string ConfigPath => Path.Combine(Root, "forkbridge.yml");
    public string StateDir => Path.Combine(Root, "state");

    public MergeFixture(long maxEditable = 1024 * 1024, string validationRun = "[git, --version]", bool trusted = true)
    {
        Root = Path.Combine(Path.GetTempPath(), "fbmerge-" + Interlocked.Increment(ref _seq) + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(Root, "remotes"));
        Directory.CreateDirectory(Path.Combine(Root, "work"));
        Git(Path.Combine(Root, "remotes"), "init", "-q", "--bare", "-b", "develop", Origin);
        Git(Path.Combine(Root, "work"), "init", "-q", "-b", "develop", Checkout);
        Git(Checkout, "remote", "add", "origin", Origin);
        Write("README.md", "# app\n");
        Write("src/shared.txt", "one\ntwo\nthree\nfour\nfive\n");
        Write("src/keep.txt", "keep\n");
        Commit("base");
        Push("develop");
        File.WriteAllText(ConfigPath, $$"""
            version: 1
            stateDir: state
            repositories:
              app:
                path: work/app
                remote: origin
            merge:
              maxEditableBytes: {{maxEditable}}
              repositories:
                app:
                  validation:
                    trusted: {{(trusted ? "true" : "false")}}
                    steps:
                      - id: smoke
                        kind: build
                        run: {{validationRun}}
                        timeoutSeconds: 60
            """);
    }

    public (ForkBridgeEngine Core, MergeEngine Merge) Engines()
    {
        var core = ForkBridgeEngine.Load(ConfigPath);
        return (core, new MergeEngine(core));
    }

    // ---- git helpers ----------------------------------------------------------------------------
    public static string Git(string dir, params string[] args) => GitRaw(dir, null, args).Trim();

    public static string GitRaw(string dir, byte[]? stdin, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin is not null, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_AUTHOR_NAME"] = "Fixture"; psi.Environment["GIT_AUTHOR_EMAIL"] = "fixture@example.com";
        psi.Environment["GIT_COMMITTER_NAME"] = "Fixture"; psi.Environment["GIT_COMMITTER_EMAIL"] = "fixture@example.com";
        using var p = Process.Start(psi)!;
        if (stdin is not null) { p.StandardInput.BaseStream.Write(stdin); p.StandardInput.Close(); }
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} in {dir} failed: {e}");
        return o.Result;
    }

    public void Write(string rel, string content) => WriteBytes(rel, Encoding.UTF8.GetBytes(content));
    public void WriteBytes(string rel, byte[] content)
    {
        var p = Path.Combine(Checkout, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, content);
    }
    public string Commit(string msg) { Git(Checkout, "add", "-A"); Git(Checkout, "commit", "-q", "--allow-empty", "-m", msg); return Git(Checkout, "rev-parse", "HEAD"); }
    public void Push(string branch) => Git(Checkout, "push", "-q", "origin", branch);
    public void Switch(string branch, bool create = false) { if (create) Git(Checkout, "switch", "-q", "-c", branch); else Git(Checkout, "switch", "-q", branch); }
    public string Head => Git(Checkout, "rev-parse", "HEAD");

    /// <summary>Creates a branch from develop, applies edits (null = delete), commits and pushes.</summary>
    public string OnBranch(string branch, string from, string msg, params (string Path, string? Content)[] edits)
    {
        var exists = Git(Checkout, "branch", "--list", branch).Length > 0;
        if (exists) Switch(branch); else { Switch(from); Switch(branch, create: true); }
        foreach (var (p, c) in edits) { if (c is null) Git(Checkout, "rm", "-q", "--", p); else Write(p, c); }
        var sha = Commit(msg);
        Push(branch);
        return sha;
    }

    public static RefSelection Remote(string name) => new(RefNamespace.Remote, name);

    /// <summary>Refresh + analyze source INTO target (remote namespace).</summary>
    public async Task<MergePlan> AnalyzeAsync(MergeEngine m, string source, string target)
    {
        await m.Refs.RefreshAsync("app", offline: false, CancellationToken.None);
        return await m.Analyzer.AnalyzeAsync("app", Remote(source), Remote(target));
    }

    public static async Task<FileView> FileAsync(MergeEngine m, string sessionId, MergePlan plan, string path)
        => await m.FileAsync(sessionId, plan.Entries.Single(e => e.Path == path).Id, CancellationToken.None);

    public string Snapshot()
    {
        var guard = new UserRepoGuard(new Core.Git.GitClient(new Core.Infrastructure.ProcessRunner()));
        var sb = new StringBuilder(guard.SnapshotAsync(Checkout).GetAwaiter().GetResult());
        foreach (var f in Directory.EnumerateFiles(Checkout, "*", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar)).Order(StringComparer.Ordinal))
            sb.Append('\n').Append(Path.GetRelativePath(Checkout, f)).Append('=').Append(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))));
        sb.Append("\nconfig=").Append(File.ReadAllText(Path.Combine(Checkout, ".git", "config")));
        return sb.ToString();
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
        catch { /* best effort */ }
    }
}
