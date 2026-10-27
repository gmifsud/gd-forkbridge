using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ForkBridge.Core.Infrastructure;

namespace ForkBridge.Alignment.Git;

public sealed class AlignGitException(string message) : Exception(message);

/// <summary>Binary-safe git runner (port of Invoke-GitX): argument arrays, optional stdin and environment,
/// no shell. Read-only commands never take optional locks, so a user's index is never refreshed.</summary>
public sealed class AlignGit(IProcessRunner runner, string gitExe = "git")
{
    public static readonly Version MinimumVersion = new(2, 40, 0);
    public string GitExe { get; } = gitExe;
    public IProcessRunner Runner { get; } = runner;

    public async Task<ProcessResult> RunAsync(string? repo, IReadOnlyList<string> args, byte[]? stdin = null,
        IReadOnlyDictionary<string, string?>? env = null, int[]? okExit = null, CancellationToken ct = default)
    {
        var argv = new List<string>();
        if (repo is not null) { argv.Add("-C"); argv.Add(repo); }
        argv.AddRange(args);
        var e = new Dictionary<string, string?> { ["GIT_OPTIONAL_LOCKS"] = "0", ["GIT_TERMINAL_PROMPT"] = "0", ["LC_ALL"] = "C" };
        if (env is not null) foreach (var (k, v) in env) e[k] = v;
        var r = await Runner.RunAsync(new ProcessSpec
        {
            FileName = GitExe, Arguments = argv, WorkingDirectory = repo ?? Environment.CurrentDirectory,
            Environment = e, StandardInput = stdin, Timeout = TimeSpan.FromMinutes(20), MaxOutputBytes = 512L * 1024 * 1024,
        }, ct);
        if (r.TimedOut) throw new AlignGitException($"git {Describe(args)} timed out");
        if (r.OutputTruncated) throw new AlignGitException($"git {Describe(args)} produced more output than allowed");
        if (!(okExit ?? [0]).Contains(r.ExitCode)) throw new AlignGitException($"git {Describe(args)} [in {repo}] exited {r.ExitCode}: {r.StdErr.Trim()}");
        return r;
    }

    public async Task<string> TextAsync(string? repo, params string[] args)
        => (await RunAsync(repo, args)).StdOut.TrimEnd('\n', '\r');

    public async Task<bool> TestAsync(string? repo, params string[] args)
        => (await RunAsync(repo, args, okExit: [0, 1, 128])).ExitCode == 0;

    public async Task<Version> VersionAsync()
    {
        var v = await TextAsync(null, "--version");
        var m = Regex.Match(v, @"(\d+)\.(\d+)\.(\d+)");
        if (!m.Success) throw new AlignGitException($"Cannot parse git version: {v}");
        return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
    }

    public async Task AssertVersionAsync()
    {
        var v = await VersionAsync();
        if (v < MinimumVersion) throw new AlignGitException($"Git {MinimumVersion}+ required (merge-tree --write-tree --merge-base). Found {v}.");
    }

    private static string Describe(IReadOnlyList<string> a) => string.Join(' ', a.Take(4));
}

/// <summary>Long-lived <c>git cat-file --batch</c> reader with a content cache. Not thread-safe.</summary>
public sealed class BlobReader : IDisposable
{
    private readonly Process _p;
    private readonly Dictionary<string, byte[]> _cache = new(StringComparer.Ordinal);

    public BlobReader(string gitExe, string repo)
    {
        var psi = new ProcessStartInfo(gitExe) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-C", repo, "cat-file", "--batch" }) psi.ArgumentList.Add(a);
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        _p = Process.Start(psi) ?? throw new AlignGitException("cannot start git cat-file");
        _ = _p.StandardError.ReadToEndAsync();
    }

    public byte[] Read(string? sha)
    {
        if (string.IsNullOrEmpty(sha)) return [];
        if (_cache.TryGetValue(sha, out var hit)) return hit;
        var input = _p.StandardInput.BaseStream;
        input.Write(Encoding.ASCII.GetBytes(sha + "\n"));
        input.Flush();
        var output = _p.StandardOutput.BaseStream;
        var header = ReadLine(output);
        var parts = header.Split(' ');
        if (parts.Length < 3 || parts[1] == "missing") throw new AlignGitException($"object {sha} is missing (re-plan, or fetch the repositories)");
        var size = int.Parse(parts[2]);
        var buf = new byte[size];
        var read = 0;
        while (read < size) { var n = output.Read(buf, read, size - read); if (n <= 0) throw new AlignGitException("cat-file ended early"); read += n; }
        output.ReadByte(); // trailing LF
        if (_cache.Count < 20000) _cache[sha] = buf;
        return buf;
    }

    private static string ReadLine(Stream s)
    {
        var sb = new StringBuilder();
        int b;
        while ((b = s.ReadByte()) >= 0 && b != '\n') sb.Append((char)b);
        return sb.ToString();
    }

    public void Dispose()
    {
        try { _p.StandardInput.Close(); if (!_p.WaitForExit(5000)) _p.Kill(true); } catch { /* best effort */ }
        _p.Dispose();
    }
}
