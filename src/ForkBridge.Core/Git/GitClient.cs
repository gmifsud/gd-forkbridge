using System.Text;
using ForkBridge.Core.Infrastructure;

namespace ForkBridge.Core.Git;

public sealed class GitException(string message, ProcessResult? result = null) : Exception(message)
{
    public ProcessResult? Result { get; } = result;
}

/// <summary>Typed wrapper over the installed git CLI. Every call passes an argument array.</summary>
public sealed class GitClient
{
    private readonly IProcessRunner _runner;
    public string GitExecutable { get; }
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromMinutes(20);

    public GitClient(IProcessRunner runner, string gitExecutable = "git")
    {
        _runner = runner;
        GitExecutable = gitExecutable;
    }

    private static readonly Dictionary<string, string?> BaseEnv = new()
    {
        ["GIT_TERMINAL_PROMPT"] = "0",   // never block on an interactive credential prompt
        ["LC_ALL"] = "C",
        ["LANG"] = "C",
        ["GIT_PAGER"] = "cat",
        ["GIT_EDITOR"] = "true",
        ["GIT_OPTIONAL_LOCKS"] = "0",    // read-only commands must not refresh the index of user repos
    };

    public async Task<ProcessResult> RunAsync(string repoDir, IEnumerable<string> args,
        IReadOnlyDictionary<string, string?>? env = null, byte[]? stdin = null,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var e = new Dictionary<string, string?>(BaseEnv);
        if (env is not null) foreach (var (k, v) in env) e[k] = v;
        var argv = new List<string> { "-C", repoDir };
        argv.AddRange(args);
        var r = await _runner.RunAsync(new ProcessSpec
        {
            FileName = GitExecutable,
            Arguments = argv,
            WorkingDirectory = Directory.Exists(repoDir) ? repoDir : Path.GetTempPath(),
            Environment = e,
            StandardInput = stdin,
            Timeout = timeout ?? DefaultTimeout,
        }, ct);
        // A killed git process is a cancellation, never a git failure or a "conflict".
        if (r.Cancelled) throw new OperationCanceledException($"git {string.Join(' ', args)} was cancelled.", ct);
        return r;
    }

    public async Task<string> OutAsync(string repoDir, params string[] args)
        => (await CheckedAsync(repoDir, args)).StdOut;

    public async Task<ProcessResult> CheckedAsync(string repoDir, IEnumerable<string> args,
        IReadOnlyDictionary<string, string?>? env = null, byte[]? stdin = null, CancellationToken ct = default)
    {
        var list = args.ToList();
        var r = await RunAsync(repoDir, list, env, stdin, ct: ct);
        if (!r.Success)
            throw new GitException(Redactor.Redact(
                $"git {string.Join(' ', list)} failed (exit {r.ExitCode}{(r.TimedOut ? ", timed out" : "")}) in {repoDir}: {r.StdErr.Trim()}"), r);
        return r;
    }

    public async Task<IReadOnlyList<string>> LinesAsync(string repoDir, params string[] args)
        => SplitLines(await OutAsync(repoDir, args));

    public static IReadOnlyList<string> SplitLines(string s)
        => s.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    public async Task<string?> TryRevParseAsync(string repoDir, string rev)
    {
        var r = await RunAsync(repoDir, ["rev-parse", "--verify", "--quiet", "--end-of-options", rev + "^{commit}"]);
        return r.Success ? r.StdOut.Trim() : null;
    }

    public async Task<bool> ObjectExistsAsync(string repoDir, string sha)
        => (await RunAsync(repoDir, ["cat-file", "-e", sha])).Success;

    public async Task<bool> IsAncestorAsync(string repoDir, string ancestor, string descendant)
        => (await RunAsync(repoDir, ["merge-base", "--is-ancestor", ancestor, descendant])).ExitCode == 0;

    public async Task<string?> MergeBaseAsync(string repoDir, string a, string b)
    {
        var r = await RunAsync(repoDir, ["merge-base", a, b]);
        return r.Success ? r.StdOut.Trim() : null;
    }

    public async Task<Version> VersionAsync()
    {
        var r = await _runner.RunAsync(new ProcessSpec
        {
            FileName = GitExecutable, Arguments = ["--version"], WorkingDirectory = Path.GetTempPath(),
            Timeout = TimeSpan.FromSeconds(30)
        });
        var m = System.Text.RegularExpressions.Regex.Match(r.StdOut, @"(\d+)\.(\d+)\.(\d+)");
        return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : new Version(0, 0);
    }

    public async Task<string?> ConfigGetAsync(string repoDir, string key)
    {
        var r = await RunAsync(repoDir, ["config", "--get", key]);
        return r.Success ? r.StdOut.Trim() : null;
    }

    public async Task<CommitInfo> GetCommitAsync(string repoDir, string sha)
    {
        // NUL-separated to survive any message content.
        var raw = await OutAsync(repoDir, "show", "-s", "--format=%H%x00%P%x00%an%x00%ae%x00%aI%x00%cn%x00%ce%x00%cI%x00%B", sha);
        var p = raw.Split('\0');
        return new CommitInfo(p[0], p[1].Split(' ', StringSplitOptions.RemoveEmptyEntries), p[2], p[3], p[4], p[5], p[6], p[7],
            p.Length > 8 ? p[8].TrimEnd('\n') : string.Empty);
    }

    /// <summary>Changes between two trees/commits with rename detection, -z safe.</summary>
    public async Task<IReadOnlyList<FileChange>> DiffTreeAsync(string repoDir, string from, string to,
        IEnumerable<string>? pathspec = null, bool detectRenames = true)
    {
        var args = new List<string> { "diff-tree", "-r", "-z", "--no-commit-id", "--raw", "--numstat" };
        if (detectRenames) args.Add("-M"); else args.Add("--no-renames");
        args.Add(from); args.Add(to);
        if (pathspec is not null) { args.Add("--"); args.AddRange(pathspec); }
        var r = await CheckedAsync(repoDir, args);
        return FileChange.ParseRawNumstatZ(r.StdOutBytes);
    }

    public async Task<IReadOnlyList<TreeEntry>> LsTreeAsync(string repoDir, string treeish, string? path = null, bool recursive = true)
    {
        var args = new List<string> { "ls-tree", "-z", "-l", "--full-tree" };
        if (recursive) args.Add("-r");
        args.Add(treeish);
        if (path is not null) { args.Add("--"); args.Add(path); }
        var r = await RunAsync(repoDir, args);
        if (!r.Success) return [];
        var list = new List<TreeEntry>();
        foreach (var rec in Encoding.UTF8.GetString(r.StdOutBytes).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = rec.IndexOf('\t');
            var meta = rec[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            long size = long.TryParse(meta[3], out var s) ? s : -1;
            list.Add(new TreeEntry(meta[0], meta[1], meta[2], size, rec[(tab + 1)..]));
        }
        return list;
    }

    public async Task<byte[]?> ReadBlobAsync(string repoDir, string treeish, string path)
    {
        var r = await RunAsync(repoDir, ["cat-file", "blob", $"{treeish}:{path}"]);
        return r.Success ? r.StdOutBytes : null;
    }
}

public sealed record CommitInfo(string Sha, string[] Parents, string AuthorName, string AuthorEmail, string AuthorDate,
    string CommitterName, string CommitterEmail, string CommitterDate, string Message)
{
    public bool IsMerge => Parents.Length > 1;
    public string Subject => Message.Split('\n')[0];
}

public sealed record TreeEntry(string Mode, string Type, string Sha, long Size, string Path);

public sealed record FileChange(char Status, string? OldPath, string Path, string OldMode, string NewMode,
    string OldBlob, string NewBlob, int? Similarity, bool IsBinary, int Added, int Deleted)
{
    public bool IsRename => Status == 'R';
    public bool IsCopy => Status == 'C';
    public bool IsAdd => Status == 'A';
    public bool IsDelete => Status == 'D';

    /// <summary>Parses `git diff-tree -z --raw --numstat` output (raw records first, then numstat).</summary>
    public static IReadOnlyList<FileChange> ParseRawNumstatZ(byte[] bytes)
    {
        var tokens = Encoding.UTF8.GetString(bytes).Split('\0');
        var raws = new List<(char st, string? oldp, string p, string om, string nm, string ob, string nb, int? sim)>();
        var numstat = new Dictionary<string, (bool bin, int a, int d)>();
        int i = 0;
        while (i < tokens.Length)
        {
            var t = tokens[i];
            if (t.Length == 0) { i++; continue; }
            if (t.StartsWith(':'))
            {
                var meta = t[1..].Split(' ');
                var statusTok = meta[4];
                char st = statusTok[0];
                int? sim = statusTok.Length > 1 && int.TryParse(statusTok[1..], out var sv) ? sv : null;
                if (st is 'R' or 'C')
                {
                    raws.Add((st, tokens[i + 1], tokens[i + 2], meta[0], meta[1], meta[2], meta[3], sim));
                    i += 3;
                }
                else
                {
                    raws.Add((st, null, tokens[i + 1], meta[0], meta[1], meta[2], meta[3], sim));
                    i += 2;
                }
                continue;
            }
            // numstat: "added\tdeleted\tpath" or for renames "added\tdeleted\t" followed by old, new
            var parts = t.Split('\t');
            if (parts.Length >= 3)
            {
                bool bin = parts[0] == "-";
                int a = bin ? 0 : int.Parse(parts[0]);
                int d = bin ? 0 : int.Parse(parts[1]);
                if (parts[2].Length == 0 && i + 2 < tokens.Length)
                {
                    numstat[tokens[i + 2]] = (bin, a, d);
                    i += 3;
                }
                else { numstat[parts[2]] = (bin, a, d); i++; }
                continue;
            }
            i++;
        }
        return raws.Select(r =>
        {
            numstat.TryGetValue(r.p, out var ns);
            return new FileChange(r.st, r.oldp, r.p, r.om, r.nm, r.ob, r.nb, r.sim, ns.bin, ns.a, ns.d);
        }).ToList();
    }
}
