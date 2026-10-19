using System.Text.RegularExpressions;

namespace ForkBridge.Core.Analysis;

/// <summary>Lightweight, deterministic extraction of declared public surface and imports. This is
/// EVIDENCE for review, not proof of compatibility: the consumer build in validation is the gate.</summary>
public static partial class CodeSurface
{
    [GeneratedRegex(@"^\s*public\s+(?:(?:static|abstract|sealed|partial|virtual|override|readonly|async|new|unsafe|extern|required)\s+)*(?<kind>class|interface|struct|record|enum|delegate)?\s*(?<sig>[^{;=]+?)\s*(?:\{|;|=>|=|$)", RegexOptions.Multiline)]
    private static partial Regex CsPublic();

    [GeneratedRegex(@"^\s*namespace\s+(?<ns>[\w.]+)", RegexOptions.Multiline)]
    private static partial Regex CsNamespace();

    [GeneratedRegex(@"^\s*export\s+(?:default\s+)?(?:declare\s+)?(?:abstract\s+)?(?<kind>class|interface|function|const|let|type|enum)\s+(?<name>[A-Za-z_$][\w$]*)", RegexOptions.Multiline)]
    private static partial Regex TsExport();

    [GeneratedRegex(@"^\s*export\s*\{(?<names>[^}]*)\}", RegexOptions.Multiline)]
    private static partial Regex TsExportList();

    [GeneratedRegex(@"(?:import|export)\s[^'""]*?from\s*['""](?<spec>[^'""]+)['""]|import\s*\(\s*['""](?<spec>[^'""]+)['""]\s*\)|^\s*import\s+['""](?<spec>[^'""]+)['""]", RegexOptions.Multiline)]
    private static partial Regex TsImport();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Ws();

    public static HashSet<string> CSharpPublicMembers(string code)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var ns = CsNamespace().Match(code) is { Success: true } m ? m.Groups["ns"].Value + "." : "";
        foreach (Match x in CsPublic().Matches(StripComments(code)))
        {
            var sig = Ws().Replace(x.Groups["sig"].Value, " ").Trim();
            if (sig.Length == 0 || sig.Length > 300) continue;
            var kind = x.Groups["kind"].Value;
            set.Add(ns + (kind.Length > 0 ? kind + " " : "") + sig);
        }
        return set;
    }

    /// <summary>Type names declared in the code (for consumer lookup).</summary>
    public static IEnumerable<string> CSharpTypeNames(IEnumerable<string> members)
        => members.Select(m => Regex.Match(m, @"(?:class|interface|struct|record|enum)\s+(?<n>\w+)")).Where(m => m.Success).Select(m => m.Groups["n"].Value).Distinct();

    public static HashSet<string> TsExports(string code)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var stripped = StripComments(code);
        foreach (Match x in TsExport().Matches(stripped)) set.Add(x.Groups["name"].Value);
        foreach (Match x in TsExportList().Matches(stripped))
            foreach (var n in x.Groups["names"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                set.Add(n.Split(" as ").Last().Trim());
        return set;
    }

    public static HashSet<string> TsImports(string code)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match x in TsImport().Matches(code))
            foreach (Group g in x.Groups) if (g.Name == "spec" && g.Success) set.Add(g.Value);
        return set;
    }

    /// <summary>Code with comments and string/char literals removed (literal delimiters kept), so that
    /// identifier scans do not match text such as "Abp.Application.Features.FeatureSetting".</summary>
    internal static string CodeOnly(string code)
        => Regex.Replace(code,
            @"/\*.*?\*/|//[^\n]*|@""(?:[^""]|"""")*""|""(?:\\.|[^""\\\n])*""|'(?:\\.|[^'\\\n])*'|`(?:\\.|[^`\\])*`",
            m => m.Value.StartsWith("/") ? "" : "\"\"", RegexOptions.Singleline);

    internal static string StripComments(string code)
        => Regex.Replace(code, @"/\*.*?\*/|//[^\n]*", "", RegexOptions.Singleline);
}
