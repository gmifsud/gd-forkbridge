using System.Text.RegularExpressions;
using ForkBridge.Alignment.Text;

namespace ForkBridge.Alignment.Analysis;

/// <summary>Finds what a file to be deleted is referenced by (stale-file reference fixpoint). Regex-based ports
/// of the PS functions; deterministic, nothing is executed and no source leaves the machine.</summary>
public interface IReferenceAnalyzer
{
    /// <summary>Symbols the file declares (C#: top-level types; TS: exports). C# non-code files fall back to the file name.</summary>
    IReadOnlyList<string> Names(string path, string text);
    /// <summary>Plain token the file is referenced by (TS import stem / file name); null when not applicable.</summary>
    string? Token(string path);
    /// <summary>git grep argument lists (without the leading 'grep' tree and pathspec tail handled by the caller).</summary>
    IEnumerable<List<string>> GrepQueries(string path, IReadOnlyList<string> names, string? token, string tree);
    /// <summary>True if <paramref name="text"/> references the names/token (used between stale files).</summary>
    bool References(string text, IReadOnlyList<string> names, string? token);
}

public static class ReferenceAnalyzers
{
    public static IReferenceAnalyzer For(string kind) => kind switch
    {
        "typescript" => new TypeScriptReferenceAnalyzer(),
        _ => new CSharpReferenceAnalyzer(),
    };

    internal static string Alt(IEnumerable<string> names) => string.Join("|", names.Select(Regex.Escape));
}

public sealed class CSharpReferenceAnalyzer : IReferenceAnalyzer
{
    private static readonly Regex CodeOnlyRx = new(@"(?<c>/\*.*?\*/|//[^\r\n]*)|(?<s>@""(?:[^""]|"""")*""|""(?:\\.|[^""\\\r\n])*""|'(?:\\.|[^'\\\r\n])*')", RegexOptions.Singleline);
    private static readonly Regex TopLevelRx = new(@"(?m)^(?:\t| {0,4})(?:(?:public|internal|static|sealed|abstract|partial|readonly|unsafe|new|file|ref)\s+)*(?:record\s+(?:class|struct)|class|interface|enum|struct|record)\s+([A-Za-z_]\w*)");
    private static readonly string[] Globs = ["*.cs", "*.csproj", "*.sln", "*.props", "*.targets", "*.tt", "*.cshtml"];

    /// <summary>C# text with comments removed and string/char literals emptied.</summary>
    public static string CodeOnly(string text) => CodeOnlyRx.Replace(text, m => m.Groups["c"].Success ? "" : "\"\"");

    public static IReadOnlyList<string> TopLevelTypeNames(string text)
        => TopLevelRx.Matches(CodeOnly(text)).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();

    public IReadOnlyList<string> Names(string path, string text)
        => Wildcard.IsMatch(path, "*.cs", ignoreCase: true) ? TopLevelTypeNames(text) : [Path.GetFileName(path)];

    public string? Token(string path) => null;

    public IEnumerable<List<string>> GrepQueries(string path, IReadOnlyList<string> names, string? token, string tree)
    {
        if (names.Count == 0) yield break;
        var ga = new List<string>();
        if (Wildcard.IsMatch(path, "*.cs", ignoreCase: true))
        {
            var alt = ReferenceAnalyzers.Alt(names);
            ga.AddRange(["grep", "-l", "-w", "-E", "("]);
            foreach (var n in names) { ga.Add("-e"); ga.Add(Regex.Escape(n)); }
            ga.AddRange([")", "--and", "--not", "-e", $@"(class|interface|enum|struct|record)\s+({alt})\b"]);
        }
        else
        {
            ga.AddRange(["grep", "-l", "-w", "-F"]);
            foreach (var n in names) { ga.Add("-e"); ga.Add(n); }
        }
        ga.Add(tree); ga.Add("--"); ga.AddRange(Globs);
        yield return ga;
    }

    public bool References(string text, IReadOnlyList<string> names, string? token)
    {
        if (names.Count == 0) return false;
        var alt = ReferenceAnalyzers.Alt(names);
        var code = Regex.Replace(CodeOnly(text), $@"\b(?:class|interface|enum|struct|record)\s+(?:{alt})\b", "");
        return Regex.IsMatch(code, $@"\b(?:{alt})\b");
    }
}

public sealed class TypeScriptReferenceAnalyzer : IReferenceAnalyzer
{
    private static readonly Regex CodeOnlyRx = new(@"(?<c>/\*.*?\*/|//[^\r\n]*)|(?<s>`(?:\\.|[^`\\])*`|""(?:\\.|[^""\\\r\n])*""|'(?:\\.|[^'\\\r\n])*')", RegexOptions.Singleline);
    private static readonly Regex ExportRx = new(@"(?m)^\s*export\s+(?:default\s+)?(?:declare\s+)?(?:abstract\s+)?(?:const\s+enum|class|interface|enum|type|function\*?|const|let|var)\s+([A-Za-z_$][\w$]*)");
    private static readonly string[] GenericStems = ["index", "main", "public-api", "public_api", "polyfills", "test", "module", "models", "types", "enums", "constants"];
    private static readonly string[] RefGlobs = ["*.ts", "*.html", "*.json", "*.scss", "*.css", "*.less", "*.js"];

    public static string CodeOnly(string text) => CodeOnlyRx.Replace(text, m => m.Groups["c"].Success ? "" : "\"\"");

    public static IReadOnlyList<string> ExportNames(string text)
        => ExportRx.Matches(CodeOnly(text)).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();

    public IReadOnlyList<string> Names(string path, string text)
        => Wildcard.IsMatch(path, "*.ts", ignoreCase: true) ? ExportNames(text) : [];

    /// <summary>A .ts file is referenced through its import specifier (stem), anything else by file name.</summary>
    public string? Token(string path)
    {
        var leaf = Path.GetFileName(path);
        if (Wildcard.IsMatch(path, "*.ts", ignoreCase: true))
        {
            var stem = Path.GetFileNameWithoutExtension(leaf);
            if (stem.Length < 4 || GenericStems.Contains(stem.ToLowerInvariant())) return null;
            return stem;
        }
        return leaf;
    }

    public IEnumerable<List<string>> GrepQueries(string path, IReadOnlyList<string> names, string? token, string tree)
    {
        if (names.Count > 0)
        {
            var alt = ReferenceAnalyzers.Alt(names);
            var ga = new List<string> { "grep", "-l", "-w", "-E", "(" };
            foreach (var n in names) { ga.Add("-e"); ga.Add(Regex.Escape(n)); }
            ga.AddRange([")", "--and", "--not", "-e", $@"(class|interface|enum|type|function|const|let|var)\s+({alt})\b", tree, "--", "*.ts", "*.html"]);
            yield return ga;
        }
        if (!string.IsNullOrEmpty(token))
        {
            var ga = new List<string> { "grep", "-l", "-w", "-F", "-e", token, tree, "--" };
            ga.AddRange(RefGlobs);
            yield return ga;
        }
    }

    public bool References(string text, IReadOnlyList<string> names, string? token)
    {
        if (!string.IsNullOrEmpty(token) && Regex.IsMatch(text, @"(?<![\w.-])" + Regex.Escape(token) + @"(?![\w-])")) return true;
        if (names.Count == 0) return false;
        var alt = ReferenceAnalyzers.Alt(names);
        var code = Regex.Replace(CodeOnly(text), $@"\b(?:class|interface|enum|type|function|const|let|var)\s+(?:{alt})\b", "");
        return Regex.IsMatch(code, $@"(?<![\w$])(?:{alt})(?![\w$])");
    }
}
