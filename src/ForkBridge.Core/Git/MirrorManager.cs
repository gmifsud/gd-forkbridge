using ForkBridge.Core.Config;
using ForkBridge.Core.Infrastructure;
using ForkBridge.Core.Model;
using ForkBridge.Core.Storage;

namespace ForkBridge.Core.Git;

/// <summary>Maintains harness-owned bare mirrors. User checkouts are only ever READ (git fetch from
/// the checkout path runs upload-pack against it, which does not modify it). The mirror holds a
/// full copy of the objects (no alternates), so garbage collection in a user repo can never break
/// a plan or workspace.
///
/// Ref namespaces inside a mirror:
///   refs/remotes/origin/*            fetched directly from the checkout's remote URL (fresh)
///   refs/fb/local/heads/*            the checkout's local branches
///   refs/fb/local/remote/*           the checkout's remote-tracking refs (possibly stale)
///   refs/fb/peer/&lt;alias&gt;/...           objects/refs of the other repo in the pair
///   refs/fb/runs/&lt;runId&gt;             run candidate branches</summary>
public sealed class MirrorManager
{
    private readonly GitClient _git;
    private readonly StateStore _store;
    private readonly ForkBridgeConfig _cfg;

    public MirrorManager(GitClient git, StateStore store, ForkBridgeConfig cfg)
    {
        _git = git; _store = store; _cfg = cfg;
    }

    public sealed record SyncResult(string Alias, string MirrorDir, Freshness Freshness, string? RemoteUrlRedacted, string ObjectFormat);

    public string UserRepoPath(string alias) => _cfg.ResolvePath(_cfg.Repositories[alias].Path);

    public async Task<SyncResult> SyncAsync(string alias, bool offline, CancellationToken ct = default)
    {
        var repo = _cfg.Repositories[alias];
        if (repo.Placeholder) throw new ConfigException($"Repository '{alias}' is a placeholder ({repo.Path}). Set its real path first.");
        var userPath = UserRepoPath(alias);
        var probe = await _git.RunAsync(userPath, ["rev-parse", "--git-dir"], ct: ct);
        if (!probe.Success) throw new GitException($"'{userPath}' (repository '{alias}') is not a git repository.");
        var fmtR = await _git.RunAsync(userPath, ["rev-parse", "--show-object-format"], ct: ct);
        var objectFormat = fmtR.Success ? fmtR.StdOut.Trim() : "sha1";

        var mirror = _store.MirrorDir(alias);
        using var _ = _store.Lock("mirror-" + alias);
        if (!Directory.Exists(Path.Combine(mirror, "objects")))
        {
            _store.Fs.CreateOwnedDirectory(mirror, "mirror:" + alias);
            await _git.CheckedAsync(mirror, ["init", "--bare", "--quiet", $"--object-format={objectFormat}", mirror], ct: ct);
            foreach (var (k, v) in new[] { ("core.autocrlf", "false"), ("core.safecrlf", "false"), ("gc.auto", "0"),
                         ("fetch.prune", "false"), ("advice.detachedHead", "false"), ("core.longpaths", "true") })
                await _git.CheckedAsync(mirror, ["config", k, v], ct: ct);
        }

        RepairWorktreeRegistrations(mirror);

        // 1. Local checkout (read-only for the user's repo).
        await _git.CheckedAsync(mirror, ["fetch", "--no-tags", "--prune", "--quiet", "--no-write-fetch-head", userPath,
            "+refs/heads/*:refs/fb/local/heads/*", $"+refs/remotes/{repo.Remote}/*:refs/fb/local/remote/*"], ct: ct);

        // 2. The checkout's remote, fetched straight into the mirror (the user's remote-tracking refs are untouched).
        var url = await _git.ConfigGetAsync(userPath, $"remote.{repo.Remote}.url");
        var redacted = url is null ? null : Redactor.Redact(url);
        if (offline)
        {
            var fh = Path.Combine(userPath, ".git", "FETCH_HEAD");
            DateTimeOffset? last = File.Exists(fh) ? File.GetLastWriteTimeUtc(fh) : null;
            return new SyncResult(alias, mirror, new Freshness
            {
                Offline = true, RemoteFetched = false, FetchedAt = last,
                Note = $"OFFLINE: remote not contacted; using the checkout's remote-tracking refs as last fetched{(last is null ? " (time unknown)" : $" at {last:u}")}."
            }, redacted, objectFormat);
        }
        if (url is null)
            throw new FreshnessException($"Repository '{alias}' has no remote '{repo.Remote}'. Use --offline to plan from local refs only.");
        var r = await _git.RunAsync(mirror, ["fetch", "--no-tags", "--prune", "--quiet", "--no-write-fetch-head", url,
            "+refs/heads/*:refs/remotes/origin/*"], timeout: TimeSpan.FromMinutes(30), ct: ct);
        if (!r.Success)
            throw new FreshnessException($"Fetching '{alias}' from {redacted} failed: {Redactor.Redact(r.StdErr.Trim())}\n" +
                                         "Fix network/credentials, or re-run with --offline to accept stale refs (recorded in the plan).");
        return new SyncResult(alias, mirror, new Freshness { Offline = false, RemoteFetched = true, FetchedAt = DateTimeOffset.UtcNow }, redacted, objectFormat);
    }

    /// <summary>Copies the source mirror's refs/objects into the target mirror under refs/fb/peer/&lt;alias&gt;/.</summary>
    public async Task FetchPeerAsync(string targetMirror, string sourceAlias, string sourceMirror, CancellationToken ct = default)
    {
        RepairWorktreeRegistrations(targetMirror);
        await _git.CheckedAsync(targetMirror, ["fetch", "--no-tags", "--prune", "--quiet", "--no-write-fetch-head", sourceMirror,
            $"+refs/remotes/origin/*:refs/fb/peer/{sourceAlias}/remote/*",
            $"+refs/fb/local/heads/*:refs/fb/peer/{sourceAlias}/local/*",
            $"+refs/fb/local/remote/*:refs/fb/peer/{sourceAlias}/localremote/*"], ct: ct);
    }

    /// <summary>Deletes worktree registrations (mirror/worktrees/&lt;name&gt;) that a killed `git worktree add`
    /// left unreadable - empty or missing gitdir/commondir/HEAD. git treats every registered worktree's
    /// HEAD as a reachability root, so one such entry makes every later fetch/branch/gc in the mirror fail
    /// ("bad object worktrees/x/HEAD", "failed to read worktrees/x/commondir"). Only harness-owned mirrors
    /// are touched, and git-mutating engine operations are serialized, so no legitimate add is in flight.
    /// Returns the names removed.</summary>
    public List<string> RepairWorktreeRegistrations(string mirror)
    {
        var removed = new List<string>();
        var admin = Path.Combine(mirror, "worktrees");
        if (!Directory.Exists(admin)) return removed;
        static string ReadTrim(string f) => File.Exists(f) ? File.ReadAllText(f).Trim() : "";
        foreach (var dir in Directory.GetDirectories(admin))
        {
            // `worktree add` writes HEAD as the null object id and a `locked` file reading "initializing"
            // until it completes; either surviving means the add was interrupted.
            var head = ReadTrim(Path.Combine(dir, "HEAD"));
            var broken = ReadTrim(Path.Combine(dir, "gitdir")).Length == 0
                      || ReadTrim(Path.Combine(dir, "commondir")).Length == 0
                      || head.Length == 0 || head.Trim('0').Length == 0
                      || ReadTrim(Path.Combine(dir, "locked")).StartsWith("initializing", StringComparison.Ordinal);
            if (broken && _store.Fs.IsInsideRoot(dir))
            {
                Directory.Delete(dir, recursive: true);
                removed.Add(Path.GetFileName(dir));
            }
        }
        return removed;
    }

    public sealed record ResolvedRef(string Input, string Ref, string Sha, string? Warning);

    /// <summary>Resolves a branch name or SHA to an exact SHA inside a mirror.
    /// <paramref name="peerAlias"/> set = resolve the source repo's refs through the peer namespace.</summary>
    public async Task<ResolvedRef> ResolveAsync(string mirror, string name, bool offline, bool preferLocal, string? peerAlias = null)
    {
        // Revision suffixes (e.g. "development~2", "abc123^"): resolve the base name, then apply the suffix.
        var sfx = System.Text.RegularExpressions.Regex.Match(name, @"^(?<base>[^~^]+)(?<suffix>[~^][~^0-9]*)$");
        if (sfx.Success)
        {
            var b = await ResolveAsync(mirror, sfx.Groups["base"].Value, offline, preferLocal, peerAlias);
            var full = await _git.TryRevParseAsync(mirror, b.Sha + sfx.Groups["suffix"].Value)
                       ?? throw new GitException($"Ref '{name}' not found.");
            return new ResolvedRef(name, b.Ref + sfx.Groups["suffix"].Value, full, b.Warning);
        }
        if (System.Text.RegularExpressions.Regex.IsMatch(name, "^[0-9a-fA-F]{7,64}$"))
        {
            var full = await _git.TryRevParseAsync(mirror, name);
            if (full is not null) return new ResolvedRef(name, full, full, null);
        }
        string ns = peerAlias is null ? "" : $"refs/fb/peer/{peerAlias}/";
        string remote = peerAlias is null ? (offline ? $"refs/fb/local/remote/{name}" : $"refs/remotes/origin/{name}")
                                          : (offline ? $"{ns}localremote/{name}" : $"{ns}remote/{name}");
        string local = peerAlias is null ? $"refs/fb/local/heads/{name}" : $"{ns}local/{name}";
        var rs = await _git.TryRevParseAsync(mirror, remote);
        var ls = await _git.TryRevParseAsync(mirror, local);
        string? warn = null;
        if (rs is not null && ls is not null && rs != ls)
            warn = $"'{name}': local branch ({ls[..10]}) differs from {(offline ? "last-fetched" : "remote")} ({rs[..10]}); using {(preferLocal ? "local" : "remote")}.";
        if (rs is null && ls is not null) warn = $"'{name}' exists only as a local (unpushed) branch.";
        var pick = preferLocal ? (ls ?? rs) : (rs ?? ls);
        if (pick is null) throw new GitException($"Ref '{name}' not found (looked for {remote} and {local}).");
        return new ResolvedRef(name, pick == rs ? remote : local, pick, warn);
    }

    public async Task<string> EmptyTreeAsync(string mirror)
        => (await _git.CheckedAsync(mirror, ["hash-object", "-t", "tree", "--stdin"], stdin: [])).StdOut.Trim();
}

public sealed class FreshnessException(string message) : Exception(message);
