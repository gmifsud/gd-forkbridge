using System.Text;
using ForkBridge.Core.Git;
using ForkBridge.Core.Model;

namespace ForkBridge.Core.Transfer;

/// <summary>Builds binary-safe patches for individual files and rewrites their paths from the
/// source layout to the target layout. One `git diff` per file keeps header rewriting
/// deterministic even for paths with spaces, quotes or non-ASCII characters.</summary>
public sealed class PatchBuilder
{
    private readonly GitClient _git;
    private readonly string _mirror;

    public PatchBuilder(GitClient git, string mirror) { _git = git; _mirror = mirror; }

    private static readonly string[] DiffOpts =
        ["-c", "core.quotePath=false", "-c", "diff.noprefix=false", "-c", "diff.mnemonicPrefix=false", "-c", "diff.relative=false",
         "diff", "--binary", "--full-index", "--no-color", "--no-ext-diff", "--no-textconv", "--src-prefix=a/", "--dst-prefix=b/"];

    /// <summary>Patch for one decision (handles add/modify/delete and renames as a pair).</summary>
    public async Task<byte[]> FilePatchAsync(string from, string to, PathDecision d)
    {
        var args = new List<string>(DiffOpts);
        if (d.SourceOldPath is not null && d.Status == 'R')
        {
            args.AddRange(["-M", "--find-renames=1%", from, to, "--", d.SourceOldPath, d.SourcePath]);
        }
        else
        {
            args.AddRange(["--no-renames", from, to, "--", d.SourcePath]);
        }
        var r = await _git.CheckedAsync(_mirror, args);
        var bytes = r.StdOutBytes;
        if (bytes.Length == 0) return bytes;
        if (d.Status == 'R' && CountSections(bytes) > 1)
        {
            // Rename not detected for this exact pair: express it as delete(old) + add(new).
            var del = await FilePatchAsync(from, to, d with { Status = 'D', SourcePath = d.SourceOldPath!, SourceOldPath = null, TargetPath = d.TargetOldPath, TargetOldPath = null });
            var add = await FilePatchAsync(from, to, d with { Status = 'A', SourceOldPath = null, TargetOldPath = null });
            return Concat([del, add]);
        }
        return RewritePaths(bytes, d.SourceOldPath ?? d.SourcePath, d.SourcePath, d.TargetOldPath ?? d.TargetPath!, d.TargetPath!);
    }

    /// <summary>Rewrites the header lines of a single-file git patch. Hunk content is untouched.</summary>
    public static byte[] RewritePaths(byte[] patch, string srcOld, string srcNew, string tgtOld, string tgtNew)
    {
        if (srcOld == tgtOld && srcNew == tgtNew) return patch;
        // Latin1 round-trips every byte, so binary hunks and non-UTF8 text survive unchanged.
        var text = Encoding.Latin1.GetString(patch);
        var sb = new StringBuilder(text.Length + 256);
        int pos = 0;
        bool inHeader = false;
        while (pos < text.Length)
        {
            int nl = text.IndexOf('\n', pos);
            int end = nl < 0 ? text.Length : nl + 1;
            var line = text[pos..end];
            var bare = line.TrimEnd('\n', '\r');
            var eol = line[bare.Length..];
            if (bare.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                inHeader = true;
                sb.Append("diff --git ").Append(Quote("a/" + tgtOld)).Append(' ').Append(Quote("b/" + tgtNew)).Append(eol);
            }
            else if (inHeader && bare.StartsWith("--- ", StringComparison.Ordinal))
                sb.Append(bare == "--- /dev/null" ? bare : "--- " + Quote("a/" + tgtOld)).Append(eol);
            else if (inHeader && bare.StartsWith("+++ ", StringComparison.Ordinal))
            {
                sb.Append(bare == "+++ /dev/null" ? bare : "+++ " + Quote("b/" + tgtNew)).Append(eol);
                inHeader = false;
            }
            else if (inHeader && bare.StartsWith("rename from ", StringComparison.Ordinal)) sb.Append("rename from ").Append(Quote(tgtOld)).Append(eol);
            else if (inHeader && bare.StartsWith("rename to ", StringComparison.Ordinal)) sb.Append("rename to ").Append(Quote(tgtNew)).Append(eol);
            else if (inHeader && bare.StartsWith("copy from ", StringComparison.Ordinal)) sb.Append("copy from ").Append(Quote(tgtOld)).Append(eol);
            else if (inHeader && bare.StartsWith("copy to ", StringComparison.Ordinal)) sb.Append("copy to ").Append(Quote(tgtNew)).Append(eol);
            else
            {
                if (inHeader && (bare.StartsWith("GIT binary patch") || bare.StartsWith("Binary files"))) inHeader = false;
                sb.Append(line);
            }
            pos = end;
        }
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>Paths arrive as UTF-8 strings; header bytes must be UTF-8 encoded then mapped to the
    /// Latin1 working string. C-style quoting is used only when git itself would require it.</summary>
    public static string Quote(string path)
    {
        bool needs = path.Any(c => c is '"' or '\\' or '\t' or '\n' or '\r' || c < 0x20);
        var utf8 = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(path));
        if (!needs) return utf8;
        var sb = new StringBuilder("\"");
        foreach (var c in utf8)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                default:
                    if (c < 0x20) sb.Append('\\').Append(Convert.ToString(c, 8).PadLeft(3, '0'));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    private static int CountSections(byte[] patch)
    {
        var t = Encoding.Latin1.GetString(patch);
        int n = t.StartsWith("diff --git ") ? 1 : 0, i = 0;
        while ((i = t.IndexOf("\ndiff --git ", i, StringComparison.Ordinal)) >= 0) { n++; i++; }
        return n;
    }

    public static byte[] Concat(IEnumerable<byte[]> parts)
    {
        using var ms = new MemoryStream();
        foreach (var p in parts) ms.Write(p);
        return ms.ToArray();
    }

    public async Task<string?> PatchIdAsync(byte[] patch)
    {
        if (patch.Length == 0) return null;
        var r = await _git.RunAsync(_mirror, ["patch-id", "--stable"], stdin: patch);
        var first = r.StdOut.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrEmpty(first) ? null : first;
    }

    /// <summary>Checks a patch against a tree using a throwaway index (never a user index).</summary>
    public async Task<(bool Ok, string Output)> CheckAgainstTreeAsync(string treeish, byte[] patch, bool reverse, string scratchDir)
    {
        Directory.CreateDirectory(scratchDir);
        var idx = Path.Combine(scratchDir, $"idx-{Guid.NewGuid():N}");
        try
        {
            var env = new Dictionary<string, string?> { ["GIT_INDEX_FILE"] = idx };
            await _git.CheckedAsync(_mirror, ["read-tree", treeish], env);
            var args = new List<string> { "apply", "--cached", "--check", "--binary" };
            if (reverse) args.Add("--reverse");
            var r = await _git.RunAsync(_mirror, args, env, stdin: patch);
            return (r.Success, r.StdErr.Trim());
        }
        finally { try { File.Delete(idx); } catch { } }
    }
}
