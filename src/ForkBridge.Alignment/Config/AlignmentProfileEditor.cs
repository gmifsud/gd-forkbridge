using System.Text;
using System.Text.RegularExpressions;
using ForkBridge.Alignment.Model;
using ForkBridge.Core.Config;

namespace ForkBridge.Alignment.Config;

/// <summary>Surgical, comment-preserving edits to forkbridge.alignment.yml (the YAML is never re-serialised).
/// Every edit keeps a .bak (and a one-time .orig), is re-parsed and validated, and is rolled back if the result
/// does not load. Each changed line carries an audit comment.</summary>
public static class AlignmentProfileEditor
{
    public static void SetSyncBase(string configPath, string profile, string sha, string? note = null)
    {
        if (!Regex.IsMatch(sha, "^[0-9a-f]{7,40}$")) throw new ArgumentException($"'{sha}' is not a commit id.");
        Edit(configPath, profile, (lines, start, end, indent) =>
        {
            var key = new Regex("^" + Regex.Escape(indent) + @"syncBase\s*:(.*)$");
            var audit = $"# set via Harmony {DateTime.UtcNow:yyyy-MM-dd}" + (note is null ? "" : $" ({note})");
            for (var i = start; i < end; i++)
            {
                var m = key.Match(lines[i]);
                if (!m.Success) continue;
                var old = Regex.Match(m.Groups[1].Value, "[0-9a-f]{7,40}").Value;
                lines[i] = $"{indent}syncBase: \"{sha}\"   {audit}{(old.Length > 0 && old != sha ? $"; was {old[..Math.Min(10, old.Length)]}" : "")}";
                return;
            }
            lines.Insert(end, $"{indent}syncBase: \"{sha}\"   {audit}");
        });
    }

    public static void AddOverride(string configPath, string profile, string pattern, Decision decision, string reason, string primaryCode)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Contains('\n')) throw new ArgumentException("A single-line pattern is required.");
        var entry = $"{{ pattern: {Q(pattern)}, decision: {DecisionNames.Legacy(decision, primaryCode)}, reason: {Q(string.IsNullOrWhiteSpace(reason) ? "added via Harmony" : reason.Replace('\n', ' '))} }}   # added via Harmony {DateTime.UtcNow:yyyy-MM-dd}";
        Edit(configPath, profile, (lines, start, end, indent) =>
        {
            var key = new Regex("^" + Regex.Escape(indent) + @"overrides\s*:\s*(.*)$");
            for (var i = start; i < end; i++)
            {
                var m = key.Match(lines[i]);
                if (!m.Success) continue;
                var rest = m.Groups[1].Value.Trim();
                if (rest.StartsWith("[]")) { lines[i] = $"{indent}overrides:"; lines.Insert(i + 1, $"{indent}  - {entry}"); return; }
                if (rest.StartsWith('[')) throw new ArgumentException("overrides is written as a flow sequence; edit it by hand once to a block list.");
                // Block list: insert after the last item (items start with "<indent>  - ", continuation lines are deeper).
                var last = i;
                var itemIndent = indent + "  ";
                for (var j = i + 1; j < end; j++)
                {
                    var l = lines[j];
                    if (l.Trim().Length == 0 || l.TrimStart().StartsWith('#')) { continue; }
                    var lead = l.Length - l.TrimStart().Length;
                    if (lead > indent.Length && (l.StartsWith(itemIndent + "- ") || lead > itemIndent.Length)) last = j; else break;
                }
                lines.Insert(last + 1, $"{itemIndent}- {entry}");
                return;
            }
            lines.Insert(end, $"{indent}overrides:");
            lines.Insert(end + 1, $"{indent}  - {entry}");
        });
    }

    private static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static void Edit(string configPath, string profile, Action<List<string>, int, int, string> change)
    {
        var full = Path.GetFullPath(configPath);
        var bytes = File.ReadAllBytes(full);
        var text = Encoding.UTF8.GetString(bytes);
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var (start, end, indent) = FindProfile(lines, profile);
        change(lines, start, end, indent);
        var updated = string.Join(nl, lines);
        AlignmentConfigLoader.Parse(updated, full);                         // must still be valid YAML of the right shape
        var orig = full + ".orig";
        if (!File.Exists(orig)) File.WriteAllBytes(orig, bytes);
        File.WriteAllBytes(full + ".bak", bytes);
        File.WriteAllText(full, updated, new UTF8Encoding(false));
    }

    /// <summary>Returns the line range of a profile's body (start inclusive, end exclusive) and the body indent.</summary>
    internal static (int Start, int End, string Indent) FindProfile(List<string> lines, string profile)
    {
        var p = lines.FindIndex(l => Regex.IsMatch(l, @"^profiles\s*:\s*(#.*)?$"));
        if (p < 0) throw new ConfigException("No 'profiles:' section in the alignment config.");
        var header = new Regex(@"^(\s+)" + Regex.Escape(profile) + @"\s*:\s*(#.*)?$");
        for (var i = p + 1; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.Length > 0 && !char.IsWhiteSpace(l[0]) && !l.StartsWith('#')) break;   // left the profiles section
            var m = header.Match(l);
            if (!m.Success) continue;
            var hIndent = m.Groups[1].Value.Length;
            string? bodyIndent = null;
            var j = i + 1;
            for (; j < lines.Count; j++)
            {
                var b = lines[j];
                if (b.Trim().Length == 0 || b.TrimStart().StartsWith('#')) continue;
                var lead = b.Length - b.TrimStart().Length;
                if (lead <= hIndent) break;
                bodyIndent ??= b[..lead];
            }
            // trim trailing blank/comment lines so inserts land inside the profile
            var end = j;
            while (end > i + 1 && (lines[end - 1].Trim().Length == 0 || lines[end - 1].TrimStart().StartsWith('#'))) end--;
            return (i + 1, end, bodyIndent ?? new string(' ', hIndent + 2));
        }
        throw new ConfigException($"Profile '{profile}' not found in the alignment config.");
    }
}
