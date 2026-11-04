using ForkBridge.Alignment.Config;
using ForkBridge.Alignment.Git;
using ForkBridge.Alignment.Model;

namespace ForkBridge.Alignment.Workspace;

public sealed record RemoteBranch(string Name, string Sha, string Date, string Author, string Subject, bool LocalExists, string? WorktreeId);

public sealed record FetchResult(Side Side, string Remote, int Branches, string FetchedAt);

public sealed record CreateWorktreeResult(WorktreeInfo Worktree, bool Created, bool TrackingBranchCreated);

public sealed record FastForwardResult(string WorktreeId, string Branch, string Upstream, string Before, string After, int Commits, bool AlreadyUpToDate);

/// <summary>The only repository writes Harmony makes before phase 4, all additive or fast-forward only:
/// fetch a remote (remote-tracking refs only, no prune), add a worktree for a remote branch, and fast-forward a
/// clean worktree to its upstream. Inputs are a side, a branch name validated by git itself and present on the
/// remote, or a worktree id from <see cref="WorktreeCatalog"/>; paths are always derived here, never supplied.</summary>
public sealed class WorktreeOps(ResolvedProfile profile, AlignGit git, Func<Side, string> remoteOf)
{
    private readonly WorktreeCatalog _catalog = new(profile, git);

    public async Task<FetchResult> FetchAsync(Side side, CancellationToken ct = default)
    {
        var root = profile.RootOf(side); var remote = Remote(side);
        await git.RunAsync(root, ["fetch", "--no-tags", "--quiet", remote], ct: ct);
        var n = (await ListRemoteBranchesAsync(side, ct)).Count;
        return new FetchResult(side, remote, n, DateTimeOffset.UtcNow.ToString("O"));
    }

    /// <summary>Branches on the remote as of the last fetch, newest first, with the worktree that has each checked out.</summary>
    public async Task<IReadOnlyList<RemoteBranch>> ListRemoteBranchesAsync(Side side, CancellationToken ct = default)
    {
        var root = profile.RootOf(side); var remote = Remote(side); var prefix = $"refs/remotes/{remote}/";
        var refs = (await git.RunAsync(root, ["for-each-ref", "--sort=-committerdate",
            "--format=%(refname)%09%(objectname)%09%(committerdate:short)%09%(authorname)%09%(subject)", prefix], ct: ct)).StdOut;
        var locals = (await git.RunAsync(root, ["for-each-ref", "--format=%(refname:short)", "refs/heads/"], ct: ct)).StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        var byBranch = (await _catalog.ListAsync(inspect: false, ct)).Where(w => w.Side == side && w.Branch is not null)
            .GroupBy(w => w.Branch!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);
        var list = new List<RemoteBranch>();
        foreach (var line in refs.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split('\t');
            if (f.Length < 5) continue;
            var name = f[0][prefix.Length..];
            if (name == "HEAD") continue;
            list.Add(new RemoteBranch(name, f[1], f[2], f[3], f[4], locals.Contains(name), byBranch.GetValueOrDefault(name)));
        }
        return list;
    }

    /// <summary>Adds a worktree for <paramref name="branch"/> under the side's worktreeRoot. Idempotent: if a worktree
    /// already has the branch checked out it is returned unchanged. An existing local branch is reused as-is
    /// (use <see cref="FastForwardAsync"/> to bring it up to date); otherwise a tracking branch is created.</summary>
    public async Task<CreateWorktreeResult> CreateWorktreeAsync(Side side, string branch, CancellationToken ct = default)
    {
        var root = profile.RootOf(side); var remote = Remote(side);
        await ValidateBranchNameAsync(branch, ct);
        var existing = (await _catalog.ListAsync(inspect: false, ct)).FirstOrDefault(w => w.Side == side && w.Branch == branch);
        if (existing is not null) return new CreateWorktreeResult(existing, Created: false, TrackingBranchCreated: false);

        var remoteRef = $"refs/remotes/{remote}/{branch}";
        var localExists = await git.TestAsync(root, "show-ref", "--verify", "--quiet", $"refs/heads/{branch}");
        if (!localExists && !await git.TestAsync(root, "show-ref", "--verify", "--quiet", remoteRef))
            throw new AlignmentException($"'{branch}' is not a branch on {remote} (as of the last fetch). Fetch {remote} and try again.", 2);

        var target = TargetPath(side, branch);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new AlignmentException($"Cannot create the worktree: {target} already exists and is not empty.", 9);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        if (localExists) await git.RunAsync(root, ["worktree", "add", target, branch], ct: ct);
        else await git.RunAsync(root, ["worktree", "add", "--track", "-b", branch, target, $"{remote}/{branch}"], ct: ct);

        var canon = PathCanon.Canonical(target);
        var created = (await _catalog.ListAsync(inspect: true, ct)).First(w => w.Side == side && w.Path.Equals(canon, StringComparison.OrdinalIgnoreCase));
        return new CreateWorktreeResult(created, Created: true, TrackingBranchCreated: !localExists);
    }

    /// <summary>Fetches the worktree's upstream branch and fast-forwards to it. Refuses (nothing changed) when the
    /// worktree is missing, detached, has tracked changes, has local commits not on the remote, or is the main
    /// checkout without <paramref name="confirmMainCheckout"/>.</summary>
    public async Task<FastForwardResult> FastForwardAsync(string worktreeId, bool confirmMainCheckout, CancellationToken ct = default)
    {
        var w = await _catalog.ResolveAsync(worktreeId, ct);
        var remote = Remote(w.Side);
        if (!w.Exists) throw new AlignmentException($"Worktree {w.Path} is missing on disk.", 9);
        if (w.Bare || w.Detached || w.Branch is null) throw new AlignmentException($"Worktree {w.Path} is not on a branch (detached HEAD); nothing to fast-forward.", 9);
        if (w.IsMain && !confirmMainCheckout) throw new AlignmentException($"{w.Path} is the main checkout; confirm to fast-forward it.", 9);

        var up = (await git.RunAsync(w.Path, ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}"], okExit: [0, 128], ct: ct)).StdOut.Trim();
        if (string.IsNullOrEmpty(up) || up.Contains(' ')) up = $"{remote}/{w.Branch}";                       // no upstream configured: same-name branch on the remote
        if (!up.StartsWith(remote + "/", StringComparison.Ordinal)) throw new AlignmentException($"{w.Branch} tracks {up}, not a branch on {remote}.", 9);
        var upBranch = up[(remote.Length + 1)..];
        await ValidateBranchNameAsync(upBranch, ct);

        await git.RunAsync(w.Path, ["fetch", "--no-tags", "--quiet", remote, $"+refs/heads/{upBranch}:refs/remotes/{remote}/{upBranch}"], okExit: [0], ct: ct);

        var dirty = (await git.RunAsync(w.Path, ["status", "--porcelain=v1", "--untracked-files=no"], ct: ct)).StdOut.Trim();
        if (dirty.Length > 0) throw new AlignmentException($"{w.Path} has uncommitted changes to tracked files; commit or stash them first.", 9);

        var before = await git.TextAsync(w.Path, "rev-parse", "HEAD");
        var counts = (await git.TextAsync(w.Path, "rev-list", "--left-right", "--count", $"HEAD...{up}")).Split('\t', ' ', StringSplitOptions.RemoveEmptyEntries);
        int ahead = int.Parse(counts[0]), behind = int.Parse(counts[^1]);
        if (ahead > 0) throw new AlignmentException($"{w.Branch} has {ahead} local commit(s) not on {up} (and is {behind} behind); a fast-forward is impossible. Merge or rebase it yourself.", 9);
        if (behind == 0) return new FastForwardResult(w.Id, w.Branch, up, before, before, 0, AlreadyUpToDate: true);

        await git.RunAsync(w.Path, ["merge", "--ff-only", "--quiet", up], ct: ct);
        var after = await git.TextAsync(w.Path, "rev-parse", "HEAD");
        return new FastForwardResult(w.Id, w.Branch, up, before, after, behind, AlreadyUpToDate: false);
    }

    /// <summary>worktreeRoot/branch (branch slashes become folders, matching the existing layout); defaults to "&lt;repo&gt;.worktrees".</summary>
    public string TargetPath(Side side, string branch)
    {
        var sp = profile.SideOf(side);
        var baseDir = string.IsNullOrWhiteSpace(sp.WorktreeRoot) ? profile.RootOf(side).TrimEnd('/', '\\') + ".worktrees" : sp.WorktreeRoot;
        var full = Path.GetFullPath(Path.Combine(baseDir, branch.Replace('/', Path.DirectorySeparatorChar)));
        var rootFull = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) throw new AlignmentException($"Branch '{branch}' would escape the worktree root.", 2);
        return full;
    }

    private string Remote(Side side)
    {
        var r = remoteOf(side);
        if (string.IsNullOrWhiteSpace(r) || r.StartsWith('-') || r.Any(char.IsWhiteSpace)) throw new AlignmentException($"Invalid remote name '{r}' in forkbridge.yml.", 3);
        return r;
    }

    private async Task ValidateBranchNameAsync(string branch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(branch) || branch.StartsWith('-') || branch.Length > 250 || branch.Contains('\\'))
            throw new AlignmentException($"Invalid branch name '{branch}'.", 2);
        var r = await git.RunAsync(null, ["check-ref-format", "--branch", branch], okExit: [0, 1, 128], ct: ct);
        if (r.ExitCode != 0) throw new AlignmentException($"Invalid branch name '{branch}'.", 2);
    }
}
