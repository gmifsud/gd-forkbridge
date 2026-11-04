using System.Text;
using ForkBridge.Alignment.Config;
using ForkBridge.Alignment.Git;
using ForkBridge.Alignment.Model;
using ForkBridge.Alignment.Text;

namespace ForkBridge.Alignment.Planning;

/// <summary>Unified diffs for one plan row, for the Harmony File Diff Inspector. Every diff is between blobs the plan
/// recorded (so it shows exactly what was classified, not whatever the worktrees hold now). An absent side diffs
/// against the empty blob. Read-only: blobs come from the scratch repository, history from the two repositories.</summary>
public sealed record RowDiffResult(
    string Path, Category Category, Decision Decision, string Reason, string Refs,
    BlobRef? Base, BlobRef? Primary, BlobRef? Fork, BlobRef? Merged, bool Binary,
    string ForkVsPrimary, string BaseToFork, string BaseToPrimary, string PrimaryToMerged, string ForkToMerged,
    string? PrimaryText, string? ForkText, string? PrimaryLastCommit, string? ForkLastCommit, bool Truncated);

public static class RowDiffs
{
    private const int MaxText = 2 * 1024 * 1024;

    public static async Task<RowDiffResult> ComputeAsync(ResolvedProfile p, AlignGit git, AlignmentPlan plan, string path, CancellationToken ct = default)
    {
        var row = plan.Rows.FirstOrDefault(r => r.Path == path) ?? throw new ArgumentException($"'{path}' is not a row of plan {plan.Id}.");
        using var x = await ScratchRepo.OpenAsync(git, p.StateDir, p.PrimaryRoot, p.ForkRoot, p.Profile.Scope, p.Profile.Protected, ct);
        var empty = await x.HashBlobAsync([], ct);

        byte[] Bytes(BlobRef? b) => b is null ? [] : x.ReadBlob(b.Sha);
        var blobs = new[] { row.Base, row.Primary, row.Fork, row.Merged };
        var binary = blobs.Any(b => b is not null && Bytes(b).Take(8000).Contains((byte)0));

        string pl = p.Profile.Primary.Label, fl = p.Profile.Fork.Label;
        async Task<string> Diff(BlobRef? a, BlobRef? b, string aName, string bName)
        {
            if (binary || (a is null && b is null) || a?.Sha == b?.Sha) return "";
            var r = await x.RunAsync(["diff", "--no-color", "--no-ext-diff", "-U3", a?.Sha ?? empty, b?.Sha ?? empty], okExit: [0, 1], ct: ct);
            var body = r.StdOut.Replace("\r\n", "\n");
            var hunk = body.IndexOf("\n@@", StringComparison.Ordinal);
            if (hunk < 0) return "";
            var header = new StringBuilder().Append("--- ").Append(a is null ? "/dev/null" : $"{aName}/{row.Path}").Append('\n')
                                            .Append("+++ ").Append(b is null ? "/dev/null" : $"{bName}/{row.Path}");
            return header + body[hunk..].TrimEnd('\n');
        }

        string? Text(BlobRef? b) { if (b is null || binary) return null; var bytes = Bytes(b); return TextOps.ToText(bytes.Length > MaxText ? bytes[..MaxText] : bytes); }
        var truncated = !binary && blobs.Any(b => b is not null && Bytes(b).Length > MaxText);

        async Task<string?> LastCommit(string root, string rev)
        {
            var r = await git.RunAsync(root, ["log", "-1", "--format=%h · %an · %ad · %s", "--date=short", rev, "--", row.Path], okExit: [0, 128], ct: ct);
            var s = r.StdOut.Trim();
            return r.ExitCode == 0 && s.Length > 0 ? s : null;
        }

        return new RowDiffResult(row.Path, row.Category, row.Decision, row.Reason, row.Refs, row.Base, row.Primary, row.Fork, row.Merged, binary,
            await Diff(row.Fork, row.Primary, fl, pl), await Diff(row.Base, row.Fork, "SyncBase", fl), await Diff(row.Base, row.Primary, "SyncBase", pl),
            await Diff(row.Primary, row.Merged, pl, "merged"), await Diff(row.Fork, row.Merged, fl, "merged"),
            Text(row.Primary), Text(row.Fork),
            await LastCommit(p.PrimaryRoot, plan.Primary.Rev), await LastCommit(p.ForkRoot, plan.Fork.Rev), truncated);
    }
}
