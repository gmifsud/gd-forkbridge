using System.Text;
using ForkBridge.Alignment.Model;

namespace ForkBridge.Alignment.Git;

/// <summary>Harness-owned bare repository whose object store borrows both repositories' objects
/// (objects/info/alternates). Neither repository receives refs, remotes or foreign objects: all cross-repo
/// computation (scoped trees, merges, projected trees) happens here.</summary>
public sealed class ScratchRepo : IDisposable
{
    public const string ZeroSha = "0000000000000000000000000000000000000000";
    private static readonly IReadOnlyDictionary<string, string?> FixedIdentity = new Dictionary<string, string?>
    {
        ["GIT_AUTHOR_NAME"] = "forkbridge-align", ["GIT_AUTHOR_EMAIL"] = "align@forkbridge.local", ["GIT_AUTHOR_DATE"] = "@0 +0000",
        ["GIT_COMMITTER_NAME"] = "forkbridge-align", ["GIT_COMMITTER_EMAIL"] = "align@forkbridge.local", ["GIT_COMMITTER_DATE"] = "@0 +0000",
    };

    private readonly AlignGit _git;
    private readonly IReadOnlyList<string> _scope;
    private readonly IReadOnlyList<string> _protected;
    private BlobReader? _blobs;

    public string Path { get; }

    private ScratchRepo(AlignGit git, string path, IReadOnlyList<string> scope, IReadOnlyList<string> prot)
    {
        _git = git; Path = path; _scope = scope; _protected = prot;
    }

    public static async Task<ScratchRepo> OpenAsync(AlignGit git, string stateDir, string primaryRoot, string forkRoot,
        IReadOnlyList<string> scope, IReadOnlyList<string> prot, CancellationToken ct = default)
    {
        var x = System.IO.Path.Combine(stateDir, "xrepo.git");
        if (!File.Exists(System.IO.Path.Combine(x, "HEAD")))
        {
            Directory.CreateDirectory(x);
            await git.RunAsync(null, ["init", "--quiet", "--bare", x], ct: ct);
        }
        var alts = new List<string>();
        foreach (var root in new[] { primaryRoot, forkRoot })
        {
            var common = (await git.RunAsync(root, ["rev-parse", "--path-format=absolute", "--git-common-dir"], ct: ct)).StdOut.Trim();
            alts.Add(System.IO.Path.Combine(common, "objects").Replace('\\', '/'));
        }
        Directory.CreateDirectory(System.IO.Path.Combine(x, "objects", "info"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(x, "objects", "info", "alternates"), string.Join("\n", alts) + "\n", ct);
        return new ScratchRepo(git, x, scope, prot);
    }

    public BlobReader Blobs => _blobs ??= new BlobReader(_git.GitExe, Path);
    public byte[] ReadBlob(string? sha) => Blobs.Read(sha);

    public bool InScope(string path)
    {
        foreach (var p in _protected) if (path == p || path.StartsWith(p + "/", StringComparison.Ordinal)) return false;
        foreach (var s in _scope) if (path == s || path.StartsWith(s + "/", StringComparison.Ordinal)) return true;
        return false;
    }

    public IReadOnlyList<string> ScopePathspecs()
        => _scope.Select(s => ":(top,literal)" + s).Concat(_protected.Select(p => ":(top,exclude,literal)" + p)).ToList();

    public async Task<string> TextAsync(params string[] args) => await _git.TextAsync(Path, args);
    public async Task<bool> TestAsync(params string[] args) => await _git.TestAsync(Path, args);
    public Task<Core.Infrastructure.ProcessResult> RunAsync(IReadOnlyList<string> args, byte[]? stdin = null, IReadOnlyDictionary<string, string?>? env = null, int[]? okExit = null, CancellationToken ct = default)
        => _git.RunAsync(Path, args, stdin, env, okExit, ct);

    /// <summary>path → blob for the scoped part of a commit or tree (port of Get-ScopedEntries), ordinal order.</summary>
    public async Task<SortedDictionary<string, BlobRef>> ScopedEntriesAsync(string rev, CancellationToken ct = default)
    {
        var args = new List<string> { "-c", "core.quotePath=false", "ls-tree", "-r", "-z", "--full-tree", rev, "--" };
        args.AddRange(_scope);
        var r = await _git.RunAsync(Path, args, ct: ct);
        var d = new SortedDictionary<string, BlobRef>(StringComparer.Ordinal);
        foreach (var rec in r.StdOut.Split('\0'))
        {
            if (rec.Length == 0) continue;
            var tab = rec.IndexOf('\t');
            var meta = rec[..tab].Split(' ');
            var path = rec[(tab + 1)..];
            if (meta[1] != "blob" || !InScope(path)) continue;
            d[path] = new BlobRef(meta[2], meta[0]);
        }
        return d;
    }

    /// <summary>Writes a tree object (port of New-TreeFromEntries) using a throw-away index file.</summary>
    public async Task<string> WriteTreeAsync(IEnumerable<KeyValuePair<string, BlobRef>> entries, string? fromTree = null, IEnumerable<string>? deletes = null, CancellationToken ct = default)
    {
        var idx = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"align-{Guid.NewGuid():N}.index");
        var env = new Dictionary<string, string?> { ["GIT_INDEX_FILE"] = idx };
        try
        {
            await _git.RunAsync(Path, fromTree is null ? ["read-tree", "--empty"] : ["read-tree", fromTree], env: env, ct: ct);
            var sb = new StringBuilder();
            foreach (var (k, v) in entries) sb.Append(v.Mode).Append(' ').Append(v.Sha).Append('\t').Append(k).Append('\0');
            foreach (var k in deletes ?? []) if (!string.IsNullOrEmpty(k)) sb.Append("0 ").Append(ZeroSha).Append('\t').Append(k).Append('\0');
            if (sb.Length > 0) await _git.RunAsync(Path, ["update-index", "-z", "--index-info"], Encoding.UTF8.GetBytes(sb.ToString()), env, ct: ct);
            return (await _git.RunAsync(Path, ["write-tree"], env: env, ct: ct)).StdOut.Trim();
        }
        finally { try { File.Delete(idx); } catch (IOException) { } }
    }

    /// <summary>Wraps a tree in a parentless commit with a fixed identity and date (deterministic object id).
    /// Lets merge-tree run on git 2.40+ instead of requiring tree-ish merge bases (2.45+).</summary>
    public async Task<string> CommitTreeAsync(string tree, CancellationToken ct = default)
        => (await _git.RunAsync(Path, ["commit-tree", tree, "-m", "forkbridge-align scoped tree"], env: FixedIdentity, ct: ct)).StdOut.Trim();

    /// <summary>Scoped 3-way merge: base, ours = fork, theirs = primary (same orientation as the PS suite).</summary>
    public async Task<(string Tree, HashSet<string> Conflicts)> MergeAsync(string baseTree, string forkTree, string primaryTree, CancellationToken ct = default)
    {
        var b = await CommitTreeAsync(baseTree, ct);
        var o = await CommitTreeAsync(forkTree, ct);
        var t = await CommitTreeAsync(primaryTree, ct);
        var r = await _git.RunAsync(Path, ["-c", "core.quotePath=false", "merge-tree", "--write-tree", "-z", "--name-only", "--no-messages", $"--merge-base={b}", o, t], okExit: [0, 1], ct: ct);
        var tok = r.StdOut.Split('\0');
        var conflicts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var x in tok.Skip(1)) if (x.Length > 0) conflicts.Add(x);
        return (tok[0].Trim(), conflicts);
    }

    /// <summary>Merges extra refs into a commit in memory (preview of MergeRefs). Returns a tree.</summary>
    public async Task<string> MergeCommitsAsync(string a, string b, CancellationToken ct = default)
    {
        var r = await _git.RunAsync(Path, ["merge-tree", "--write-tree", "--no-messages", a, b], okExit: [0, 1], ct: ct);
        if (r.ExitCode != 0) throw new AlignmentException($"{b} does not merge cleanly into {a}");
        return r.StdOut.Split('\n')[0].Trim();
    }

    public async Task<string> HashBlobAsync(byte[] content, CancellationToken ct = default)
        => (await _git.RunAsync(Path, ["hash-object", "-w", "--stdin"], content, ct: ct)).StdOut.Trim();

    public void Dispose() => _blobs?.Dispose();
}
