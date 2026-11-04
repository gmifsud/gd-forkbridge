using ForkBridge.Alignment.Config;
using ForkBridge.Alignment.Git;
using ForkBridge.Alignment.Model;
using ForkBridge.Core.Storage;

namespace ForkBridge.Alignment.Workspace;

public sealed record WorktreeInfo(
    string Id, Side Side, string RepoAlias, string Path, string? Branch, string? Head,
    bool IsMain, bool Detached, bool Bare, bool Locked, bool Prunable, bool Exists,
    bool? ScopeDirty, int? Ahead, int? Behind, bool Protected);

/// <summary>Every worktree (main and linked) of the primary and fork repositories. The UI and API address
/// worktrees by <see cref="WorktreeInfo.Id"/> only - never by a path supplied from outside.</summary>
public sealed class WorktreeCatalog(ResolvedProfile profile, AlignGit git)
{
    public async Task<IReadOnlyList<WorktreeInfo>> ListAsync(bool inspect = true, CancellationToken ct = default)
    {
        var all = new List<WorktreeInfo>();
        foreach (var side in new[] { Side.Primary, Side.Fork })
            all.AddRange(await ListSideAsync(side, inspect, ct));
        return all;
    }

    public async Task<WorktreeInfo> ResolveAsync(string id, CancellationToken ct = default)
        => (await ListAsync(inspect: false, ct)).FirstOrDefault(w => w.Id == id)
           ?? throw new AlignmentException($"Unknown worktree id '{id}'. Run `forkbridge align worktrees` to list them.", 2);

    public static string IdFor(Side side, string canonicalPath)
        => StateStore.Sha256($"{side}|{canonicalPath.Replace('\\', '/').TrimEnd('/').ToLowerInvariant()}")[..12];

    private async Task<IReadOnlyList<WorktreeInfo>> ListSideAsync(Side side, bool inspect, CancellationToken ct)
    {
        var root = profile.RootOf(side);
        var sp = profile.SideOf(side);
        var porcelain = (await git.RunAsync(root, ["worktree", "list", "--porcelain"], ct: ct)).StdOut;
        var blocks = porcelain.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var result = new List<WorktreeInfo>();
        var first = true;
        foreach (var block in blocks)
        {
            string? path = null, head = null, branch = null; bool detached = false, bare = false, locked = false, prunable = false;
            foreach (var line in block.Split('\n'))
            {
                if (line.StartsWith("worktree ")) path = line[9..];
                else if (line.StartsWith("HEAD ")) head = line[5..];
                else if (line.StartsWith("branch ")) branch = line[7..].Replace("refs/heads/", "");
                else if (line == "detached") detached = true;
                else if (line == "bare") bare = true;
                else if (line.StartsWith("locked")) locked = true;
                else if (line.StartsWith("prunable")) prunable = true;
            }
            if (path is null) continue;
            var canon = PathCanon.Canonical(path);
            var exists = Directory.Exists(canon);
            bool? dirty = null; int? ahead = null, behind = null;
            if (inspect && exists && !bare)
            {
                var spec = profile.Profile.Scope.Select(s => ":(top,literal)" + s).Concat(profile.Profile.Protected.Select(p => ":(top,exclude,literal)" + p));
                var st = await git.RunAsync(canon, ["status", "--porcelain=v1", "--untracked-files=all", "--", .. spec], okExit: [0, 128], ct: ct);
                dirty = st.ExitCode == 0 ? st.StdOut.Trim().Length > 0 : null;
                var ab = await git.RunAsync(canon, ["rev-list", "--left-right", "--count", "HEAD...@{upstream}"], okExit: [0, 128], ct: ct);
                if (ab.ExitCode == 0)
                {
                    var parts = ab.StdOut.Trim().Split('\t', ' ');
                    if (parts.Length >= 2 && int.TryParse(parts[0], out var a) && int.TryParse(parts[^1], out var b)) { ahead = a; behind = b; }
                }
            }
            var isProtected = branch is not null && profile.Profile.NeverPush.Contains(branch, StringComparer.Ordinal);
            result.Add(new WorktreeInfo(IdFor(side, canon), side, sp.Repo, canon, branch, head, first, detached, bare, locked, prunable, exists, dirty, ahead, behind, isProtected));
            first = false;
        }
        return result;
    }
}
