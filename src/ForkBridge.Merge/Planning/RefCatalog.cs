using ForkBridge.Core.Config;
using ForkBridge.Core.Git;
using ForkBridge.Merge.Git;
using ForkBridge.Merge.Model;
using ForkBridge.Merge.Storage;

namespace ForkBridge.Merge.Planning;

public sealed record RefInfo(RefNamespace Namespace, string Name, string Sha, string? Date, string? Subject);

public sealed record RefListing(string Alias, bool MirrorExists, RefFreshness Freshness, List<RefInfo> Refs);

/// <summary>Branches of one repository as the harness mirror holds them, with explicit provenance. Refresh fetches
/// the user's checkout (read-only, upload-pack) and its remote into the harness mirror; the checkout's refs, index,
/// working tree and remote configuration are never written.</summary>
public sealed class RefCatalog
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);
    private readonly ForkBridgeConfig _cfg;
    private readonly MergeGit _git;
    private readonly MirrorManager _mirrors;
    private readonly MergeStore _store;

    public RefCatalog(ForkBridgeConfig cfg, MergeGit git, MirrorManager mirrors, MergeStore store)
    {
        _cfg = cfg; _git = git; _mirrors = mirrors; _store = store;
    }

    public static string Prefix(RefNamespace ns) => ns switch
    {
        RefNamespace.Remote => "refs/remotes/origin/",
        RefNamespace.Local => "refs/fb/local/heads/",
        RefNamespace.CheckoutRemote => "refs/fb/local/remote/",
        _ => throw new ArgumentOutOfRangeException(nameof(ns)),
    };

    public static string Describe(RefNamespace ns) => ns switch
    {
        RefNamespace.Remote => "remote branch (fetched by ForkBridge from the checkout's remote)",
        RefNamespace.Local => "local branch of your checkout (as of the last refresh)",
        RefNamespace.CheckoutRemote => "your checkout's remote-tracking ref (as you last fetched it)",
        _ => ns.ToString(),
    };

    public RepositoryConfig Repo(string alias)
    {
        if (!_cfg.Repositories.TryGetValue(alias, out var r)) throw new MergeException($"Unknown repository alias '{alias}'.", MergeErrorKind.NotFound);
        if (r.Placeholder) throw new MergeException($"Repository '{alias}' is a placeholder; set its real path first.", MergeErrorKind.Refused);
        return r;
    }

    public async Task<RefFreshness> RefreshAsync(string alias, bool offline, CancellationToken ct)
    {
        Repo(alias);
        var sync = await _mirrors.SyncAsync(alias, offline, ct);
        var f = new RefFreshness
        {
            RefreshedAt = DateTimeOffset.UtcNow, Offline = sync.Freshness.Offline, RemoteFetched = sync.Freshness.RemoteFetched,
            Note = sync.Freshness.Note, RemoteUrlRedacted = sync.RemoteUrlRedacted,
        };
        _store.SaveFreshness(alias, f);
        return _store.LoadFreshness(alias, StaleAfter);
    }

    public async Task<RefListing> ListAsync(string alias)
    {
        Repo(alias);
        var mirror = _store.Core.MirrorDir(alias);
        var fresh = _store.LoadFreshness(alias, StaleAfter);
        if (!Directory.Exists(Path.Combine(mirror, "objects"))) return new RefListing(alias, false, fresh, []);
        var r = await _git.CheckedAsync(mirror, ["for-each-ref", "--sort=-committerdate",
            "--format=%(refname)%00%(objectname)%00%(committerdate:iso-strict)%00%(subject)",
            "refs/remotes/origin/", "refs/fb/local/heads/", "refs/fb/local/remote/"]);
        var list = new List<RefInfo>();
        foreach (var line in GitClient.SplitLines(r.StdOut))
        {
            var p = line.Split('\0');
            if (p.Length < 2) continue;
            foreach (var ns in Enum.GetValues<RefNamespace>())
            {
                var pre = Prefix(ns);
                if (!p[0].StartsWith(pre, StringComparison.Ordinal)) continue;
                var name = p[0][pre.Length..];
                if (name == "HEAD") break;
                list.Add(new RefInfo(ns, name, p[1], p.Length > 2 ? p[2] : null, p.Length > 3 ? p[3] : null));
                break;
            }
        }
        return new RefListing(alias, true, fresh, list);
    }

    public static string? NameProblem(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255) return "empty or too long";
        if (name.StartsWith('-') || name.StartsWith('/') || name.EndsWith('/') || name.EndsWith(".lock", StringComparison.Ordinal) || name.Contains("..") || name.Contains("@{") || name.Contains("//"))
            return "not a valid branch name";
        if (name.Any(c => c < 0x20 || c == 0x7F || char.IsWhiteSpace(c) || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\'))
            return "not a valid branch name";
        return null;
    }

    /// <summary>Resolves a selection to an exact commit inside the harness mirror. Only exact refs in the chosen
    /// namespace are accepted - no revision expressions, abbreviations or fallbacks to another namespace.</summary>
    public async Task<PinnedRef> ResolveAsync(string alias, RefSelection sel)
    {
        var problem = NameProblem(sel.Name);
        if (problem is not null) throw new MergeException($"Branch '{sel.Name}': {problem}.", MergeErrorKind.Usage);
        var mirror = _store.Core.MirrorDir(alias);
        if (!Directory.Exists(Path.Combine(mirror, "objects")))
            throw new MergeException($"Refs of '{alias}' have not been loaded yet. Refresh refs first.", MergeErrorKind.Refused);
        var full = Prefix(sel.Namespace) + sel.Name;
        var show = await _git.RunAsync(mirror, ["show-ref", "--verify", "--quiet", full]);
        if (!show.Success)
            throw new MergeException($"'{sel.Name}' was not found as a {Describe(sel.Namespace)}. Refresh refs or pick another branch.", MergeErrorKind.NotFound);
        var sha = await _git.TryRevParseAsync(mirror, full + "^{commit}")
                  ?? throw new MergeException($"'{full}' does not point to a commit.", MergeErrorKind.Refused);
        var meta = (await _git.CheckedAsync(mirror, ["show", "-s", "--format=%s%x00%cI", sha])).StdOut.Split('\0');
        return new PinnedRef { Namespace = sel.Namespace, Name = sel.Name, FullRef = full, Sha = sha, Subject = meta[0], CommitDate = meta.Length > 1 ? meta[1].Trim() : null };
    }
}
