using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForkBridge.Alignment.Manifests;

public sealed record ManifestMergeResult(string Text, IReadOnlyList<string> Report);

/// <summary>Add-only union of two package manifests: each side gains the other side's missing entries, removals
/// never propagate, version differences are reported only (except overrides). Untouched bytes are preserved.</summary>
public interface IManifestMerger
{
    ManifestMergeResult Merge(string text, string otherText, string otherName, IReadOnlyDictionary<string, string> overrides);
}

/// <summary>PowerShell [ordered]@{} semantics: insertion order kept, keys case-insensitive, last write wins.</summary>
internal sealed class OrderedMap
{
    private readonly List<string> _keys = new();
    private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);
    public void Set(string k, string v) { if (!_map.ContainsKey(k)) _keys.Add(k); _map[k] = v; }
    public bool Contains(string k) => _map.ContainsKey(k);
    public string this[string k] => _map[k];
    public IReadOnlyList<string> Keys => _keys;
}

internal static class Ps
{
    /// <summary>PowerShell -ne/-eq on strings: case-insensitive.</summary>
    public static bool Eq(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Directory.Packages.props (port of Merge-PackageProps).</summary>
public sealed class DirectoryPackagesPropsMerger : IManifestMerger
{
    private static readonly Regex Pv = new(@"<PackageVersion\s+Include=""(?<id>[^""]+)""\s+Version=""(?<v>[^""]+)""\s*/>", RegexOptions.IgnoreCase);

    internal static OrderedMap Versions(string text)
    {
        var d = new OrderedMap();
        foreach (Match m in Pv.Matches(text)) d.Set(m.Groups["id"].Value, m.Groups["v"].Value);
        return d;
    }

    public ManifestMergeResult Merge(string text, string otherText, string otherName, IReadOnlyDictionary<string, string> overrides)
    {
        var ov = new Dictionary<string, string>(overrides, StringComparer.OrdinalIgnoreCase);
        var mine = Versions(text); var theirs = Versions(otherText);
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var report = new List<string>();
        foreach (var (id, v) in overrides)
        {
            if (mine.Contains(id) && !Ps.Eq(mine[id], v))
            {
                text = Regex.Replace(text, @"<PackageVersion\s+Include=""" + Regex.Escape(id) + @"""\s+Version=""[^""]+""\s*/>",
                    $"<PackageVersion Include=\"{id}\" Version=\"{v}\" />".Replace("$", "$$"), RegexOptions.IgnoreCase);
                report.Add($"SET    {id} {mine[id]} -> {v} (override)");
            }
        }
        var adds = new List<string>();
        foreach (var id in theirs.Keys)
        {
            if (mine.Contains(id)) continue;
            var v = ov.TryGetValue(id, out var o) ? o : theirs[id];
            report.Add($"ADD    {id} {v} (from {otherName})");
            adds.Add($"    <PackageVersion Include=\"{id}\" Version=\"{v}\" />");
        }
        foreach (var id in mine.Keys)
            if (theirs.Contains(id) && !Ps.Eq(theirs[id], mine[id]) && !ov.ContainsKey(id))
                report.Add($"DIFFER {id} here={mine[id]} {otherName}={theirs[id]} (left as is)");
        if (adds.Count > 0)
        {
            var i = text.LastIndexOf("</ItemGroup>", StringComparison.Ordinal);
            if (i < 0) throw new InvalidOperationException("Directory.Packages.props has no </ItemGroup>");
            var lineStart = text.LastIndexOf('\n', i) + 1;
            var block = $"    <!-- Added by branch alignment: present in {otherName} -->{nl}" + string.Join(nl, adds) + nl;
            text = text.Insert(lineStart, block);
        }
        return new ManifestMergeResult(text, report);
    }
}

/// <summary>package.json dependency sections (port of Merge-PackageJson / Get-JsonSection).</summary>
public sealed class PackageJsonMerger(IReadOnlyList<string> sections) : IManifestMerger
{
    private static readonly Regex Entry = new(@"""(?<k>[^""]+)""\s*:\s*""(?<v>[^""]*)""");

    internal sealed record Section(int Open, int Close, string Body, OrderedMap Entries);

    internal static Section? GetSection(string text, string section)
    {
        var m = Regex.Match(text, "\"" + Regex.Escape(section) + "\"\\s*:\\s*\\{");
        if (!m.Success) return null;
        var open = m.Index + m.Length;
        var close = text.IndexOf('}', open);
        if (close < 0) throw new InvalidOperationException($"package.json: unterminated '{section}'");
        var body = text[open..close];
        var d = new OrderedMap();
        foreach (Match e in Entry.Matches(body)) d.Set(e.Groups["k"].Value, e.Groups["v"].Value);
        return new Section(open, close, body, d);
    }

    public ManifestMergeResult Merge(string text, string otherText, string otherName, IReadOnlyDictionary<string, string> overrides)
    {
        var ov = new Dictionary<string, string>(overrides, StringComparer.OrdinalIgnoreCase);
        var report = new List<string>();
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var mineAll = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var theirsAll = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var mineAllOrder = new List<string>();
        foreach (var sec in sections)
        {
            var a = GetSection(text, sec);
            if (a is not null) foreach (var k in a.Entries.Keys) { if (!mineAll.ContainsKey(k)) mineAllOrder.Add(k); mineAll[k] = a.Entries[k]; }
            var b = GetSection(otherText, sec);
            if (b is not null) foreach (var k in b.Entries.Keys) theirsAll[k] = b.Entries[k];
        }
        foreach (var sec in sections)
        {
            var theirs = GetSection(otherText, sec);
            if (theirs is null) continue;
            var mine = GetSection(text, sec);
            foreach (var (id, v) in overrides)
            {
                if (mine is not null && mine.Entries.Contains(id) && !Ps.Eq(mine.Entries[id], v))
                {
                    var body = Regex.Replace(mine.Body, "(\"" + Regex.Escape(id) + "\"\\s*:\\s*\")[^\"]*(\")", "${1}" + v.Replace("$", "$$") + "${2}");
                    text = text[..mine.Open] + body + text[mine.Close..];
                    report.Add($"SET    {sec}/{id} {mine.Entries[id]} -> {v} (override)");
                    mine = GetSection(text, sec);
                }
            }
            var adds = new List<string>();
            foreach (var id in theirs.Entries.Keys)
            {
                if (mineAll.ContainsKey(id))
                {
                    if (mine is not null && !mine.Entries.Contains(id)) report.Add($"MOVED  {sec}/{id} is in another section here (left as is)");
                    continue;
                }
                var v = ov.TryGetValue(id, out var o) ? o : theirs.Entries[id];
                adds.Add($"\"{id}\": \"{v}\"");
                report.Add($"ADD    {sec}/{id} {v} (from {otherName})");
            }
            if (adds.Count > 0)
            {
                if (mine is null) throw new InvalidOperationException($"package.json has no '{sec}' section to receive {adds.Count} entr(y/ies) from {otherName}; set KEEP_BOTH or edit by hand.");
                var im = Regex.Match(mine.Body, "(?m)^([ \\t]+)\"");
                var indent = im.Success ? im.Groups[1].Value : "    ";
                if (mine.Body.Trim().Length > 0)
                {
                    var ins = nl + string.Join(nl, adds.Select(x => indent + x + ","));
                    var lead = Regex.Match(mine.Body, "^[ \\t]*").Value;
                    text = text[..(mine.Open + lead.Length)] + ins + text[(mine.Open + lead.Length)..];
                }
                else
                {
                    var closeIndent = Regex.Match(text[..mine.Open], "(?m)^([ \\t]*).*$\\z").Groups[1].Value;
                    var ins = nl + string.Join("," + nl, adds.Select(x => indent + x)) + nl + closeIndent;
                    text = text[..mine.Open] + ins + text[mine.Close..];
                }
            }
        }
        foreach (var id in mineAllOrder)
            if (theirsAll.TryGetValue(id, out var tv) && !Ps.Eq(tv, mineAll[id]) && !ov.ContainsKey(id))
                report.Add($"DIFFER {id} here={mineAll[id]} {otherName}={tv} (left as is)");
        using (JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })) { }
        return new ManifestMergeResult(text, report);
    }
}
