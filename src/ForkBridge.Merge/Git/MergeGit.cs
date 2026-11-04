using System.Text;
using ForkBridge.Core.Git;
using ForkBridge.Core.Infrastructure;
using ForkBridge.Core.Storage;

namespace ForkBridge.Merge.Git;

/// <summary>A merge-workspace failure that is not a merge conflict (bad input, refused operation, git invocation
/// failure). <see cref="Kind"/> drives the HTTP status: usage → 400, conflict/stale → 409, safety → 403.</summary>
public sealed class MergeException(string message, MergeErrorKind kind = MergeErrorKind.Refused) : Exception(message)
{
    public MergeErrorKind Kind { get; } = kind;
}

public enum MergeErrorKind { Usage, NotFound, Refused, Stale, Safety, GitFailure, Unsupported }

/// <summary>Git access for the merge workspace. Every analysis/workspace call runs in an isolated environment:
/// no system or global git configuration (so no user merge drivers, clean/smudge filters, LFS, hooks, fsmonitor or
/// credential helpers), an empty harness hooks directory, literal pathspecs and the pinned merge configuration below.
/// Network operations (refresh, publish) deliberately use <see cref="GitClient"/> with the user's environment so
/// credential helpers work; they never run merge machinery.</summary>
public sealed class MergeGit
{
    public const int MinMajor = 2, MinMinor = 38;

    /// <summary>The merge configuration used by both analysis (merge-tree) and the session (merge). It is part of
    /// every plan's options fingerprint and of the approval binding.</summary>
    public static readonly IReadOnlyDictionary<string, string> PinnedOptions = new SortedDictionary<string, string>(StringComparer.Ordinal)
    {
        ["merge.renames"] = "true",
        ["merge.directoryRenames"] = "false",
        ["merge.conflictStyle"] = "diff3",
        ["merge.renormalize"] = "false",
        ["merge.stat"] = "false",
        ["rerere.enabled"] = "false",
        ["core.autocrlf"] = "false",
        ["core.safecrlf"] = "false",
        ["core.quotePath"] = "false",
        ["core.fsmonitor"] = "false",
        ["commit.gpgsign"] = "false",
        ["strategy"] = "ort",
    };

    public GitClient Git { get; }
    private readonly string _envDir;
    private readonly string _hooksDir;
    private readonly string _globalConfig;

    public MergeGit(GitClient git, StateStore store)
    {
        Git = git;
        _envDir = Path.Combine(store.Root, "merge", "env");
        _hooksDir = Path.Combine(_envDir, "hooks-empty");
        _globalConfig = Path.Combine(_envDir, "gitconfig");
        store.Fs.AssertNoLinksBetweenRootAndPath(_envDir);
        Directory.CreateDirectory(_hooksDir);
        foreach (var f in Directory.GetFileSystemEntries(_hooksDir))
            throw new HarnessSafetyException($"The harness hooks directory '{_hooksDir}' must be empty (found '{Path.GetFileName(f)}').");
        // An explicit, harness-owned "global" config: nothing from ~/.gitconfig or the system config is read.
        const string body = "# ForkBridge merge workspace: isolated git configuration (do not add filters, drivers or hooks)\n" +
                            "[core]\n\tautocrlf = false\n\tsafecrlf = false\n[advice]\n\tdetachedHead = false\n";
        if (!File.Exists(_globalConfig) || File.ReadAllText(_globalConfig) != body) File.WriteAllText(_globalConfig, body);
    }

    public static string OptionsFingerprint(string gitVersion)
        => "sha256:" + StateStore.Sha256("git=" + gitVersion + "\n" + string.Join("\n", PinnedOptions.Select(kv => kv.Key + "=" + kv.Value)));

    public IReadOnlyDictionary<string, string?> IsolatedEnv => new Dictionary<string, string?>
    {
        ["GIT_CONFIG_NOSYSTEM"] = "1",
        ["GIT_CONFIG_GLOBAL"] = _globalConfig,
        ["GIT_ATTR_NOSYSTEM"] = "1",
        ["GIT_LITERAL_PATHSPECS"] = "1",
        ["GIT_NO_REPLACE_OBJECTS"] = "1",
        ["GIT_CONFIG_COUNT"] = null, ["GIT_CONFIG_PARAMETERS"] = null,
        ["GIT_DIR"] = null, ["GIT_WORK_TREE"] = null, ["GIT_INDEX_FILE"] = null, ["GIT_COMMON_DIR"] = null,
        ["GIT_OBJECT_DIRECTORY"] = null, ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = null, ["GIT_EXTERNAL_DIFF"] = null,
        ["GIT_ASKPASS"] = null, ["SSH_ASKPASS"] = null, ["GIT_SSH_COMMAND"] = null,
    };

    private IEnumerable<string> ConfigArgs()
    {
        foreach (var (k, v) in PinnedOptions)
            if (k != "strategy") { yield return "-c"; yield return $"{k}={v}"; }
        yield return "-c"; yield return "core.hooksPath=" + _hooksDir;
    }

    public string HooksDir => _hooksDir;

    /// <summary>Runs git isolated. Does not throw on a non-zero exit (callers interpret exit codes).</summary>
    public Task<ProcessResult> RunAsync(string dir, IEnumerable<string> args, byte[]? stdin = null,
        IReadOnlyDictionary<string, string?>? extraEnv = null, CancellationToken ct = default)
    {
        var env = new Dictionary<string, string?>(IsolatedEnv);
        if (extraEnv is not null) foreach (var (k, v) in extraEnv) env[k] = v;
        return Git.RunAsync(dir, ConfigArgs().Concat(args), env, stdin, ct: ct);
    }

    public async Task<ProcessResult> CheckedAsync(string dir, IEnumerable<string> args, byte[]? stdin = null,
        IReadOnlyDictionary<string, string?>? extraEnv = null, CancellationToken ct = default)
    {
        var list = args.ToList();
        var r = await RunAsync(dir, list, stdin, extraEnv, ct);
        if (!r.Success)
            throw new MergeException(Redactor.Redact($"git {string.Join(' ', list.Take(4))} failed (exit {r.ExitCode}{(r.TimedOut ? ", timed out" : "")}): {Trim(r.StdErr)}"), MergeErrorKind.GitFailure);
        return r;
    }

    public async Task<string> OutAsync(string dir, params string[] args) => (await CheckedAsync(dir, args)).StdOut.Trim();

    public async Task<string?> TryRevParseAsync(string dir, string rev)
    {
        var r = await RunAsync(dir, ["rev-parse", "--verify", "--quiet", "--end-of-options", rev]);
        return r.Success ? r.StdOut.Trim() : null;
    }

    public async Task<byte[]> ReadBlobAsync(string dir, string sha, CancellationToken ct = default)
        => (await CheckedAsync(dir, ["cat-file", "blob", sha], ct: ct)).StdOutBytes;

    public async Task<long> BlobSizeAsync(string dir, string sha)
        => long.TryParse((await CheckedAsync(dir, ["cat-file", "-s", sha])).StdOut.Trim(), out var n) ? n : -1;

    /// <summary>Writes bytes as a blob without any filter (exact bytes are stored).</summary>
    public async Task<string> HashObjectAsync(string dir, byte[] bytes, CancellationToken ct = default)
        => (await CheckedAsync(dir, ["hash-object", "-w", "--no-filters", "--stdin"], bytes, ct: ct)).StdOut.Trim();

    public async Task<Dictionary<string, (string Mode, string Sha, string Type)>> LsTreeAsync(string dir, string treeish, CancellationToken ct = default)
    {
        var r = await CheckedAsync(dir, ["ls-tree", "-r", "-z", "--full-tree", treeish], ct: ct);
        var map = new Dictionary<string, (string, string, string)>(StringComparer.Ordinal);
        foreach (var rec in Encoding.UTF8.GetString(r.StdOutBytes).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = rec.IndexOf('\t');
            var meta = rec[..tab].Split(' ');
            map[rec[(tab + 1)..]] = (meta[0], meta[2], meta[1]);
        }
        return map;
    }

    public async Task<Version> VersionAsync() => await Git.VersionAsync();

    public async Task EnsureSupportedVersionAsync()
    {
        var v = await Git.VersionAsync();
        if (v.Major < MinMajor || (v.Major == MinMajor && v.Minor < MinMinor))
            throw new MergeException($"The merge workspace needs git {MinMajor}.{MinMinor} or newer (found {v}). " +
                                     "It relies on `git merge-tree --write-tree` for analysis. Upgrade git and retry.", MergeErrorKind.Unsupported);
    }

    public static string Trim(string s) { s = s.Trim(); return s.Length > 600 ? s[..600] + "…" : s; }
}

/// <summary>Parsed `git merge-tree --write-tree -z --messages` output.</summary>
public sealed record MergeTreeResult(string Tree, List<StageEntry> Stages, List<MergeMessage> Messages, bool Clean);

public sealed record StageEntry(string Mode, string Sha, int Stage, string Path);

public sealed record MergeMessage(List<string> Paths, string Type, string Text);

public static class MergeTreeParser
{
    /// <summary>Format (git ≥ 2.38, -z): &lt;tree&gt; NUL, then per conflicted stage "mode SP oid SP stage TAB path" NUL,
    /// then an empty record, then per message: &lt;count&gt; NUL &lt;path&gt; NUL × count, &lt;type&gt; NUL, &lt;text&gt; NUL.
    /// A clean merge prints only &lt;tree&gt; NUL.</summary>
    public static MergeTreeResult Parse(byte[] output, bool clean)
    {
        var tok = Encoding.UTF8.GetString(output).Split('\0');
        int i = 0;
        var tree = tok[i++].Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(tree, "^[0-9a-f]{40,64}$"))
            throw new MergeException("Unexpected merge-tree output (no tree id).", MergeErrorKind.GitFailure);
        var stages = new List<StageEntry>();
        var messages = new List<MergeMessage>();
        while (i < tok.Length && tok[i].Length > 0)
        {
            var rec = tok[i++];
            var tab = rec.IndexOf('\t');
            if (tab < 0) throw new MergeException("Unexpected merge-tree conflict record.", MergeErrorKind.GitFailure);
            var meta = rec[..tab].Split(' ');
            stages.Add(new StageEntry(meta[0], meta[1], int.Parse(meta[2]), rec[(tab + 1)..]));
        }
        i++; // the empty record separating conflicted entries from messages
        while (i < tok.Length && tok[i].Length > 0)
        {
            if (!int.TryParse(tok[i++], out var n) || n < 0 || i + n + 1 >= tok.Length + 1) break;
            var paths = tok.Skip(i).Take(n).ToList(); i += n;
            var type = i < tok.Length ? tok[i++] : "";
            var text = i < tok.Length ? tok[i++].TrimEnd('\n') : "";
            messages.Add(new MergeMessage(paths, type, text));
        }
        return new MergeTreeResult(tree, stages, messages, clean);
    }

    /// <summary>Parses `git ls-files -u -z` / `ls-files -s -z` records.</summary>
    public static List<StageEntry> ParseIndex(byte[] output)
    {
        var list = new List<StageEntry>();
        foreach (var rec in Encoding.UTF8.GetString(output).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = rec.IndexOf('\t');
            var meta = rec[..tab].Split(' ');
            list.Add(new StageEntry(meta[0], meta[1], int.Parse(meta[2]), rec[(tab + 1)..]));
        }
        return list;
    }
}
