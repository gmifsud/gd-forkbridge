using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ForkBridge.Core.Config;

public sealed class ForkBridgeConfig
{
    public int Version { get; set; } = 1;
    /// <summary>Harness-owned directory for mirrors, plans, runs and reports. Relative paths are
    /// resolved against the config file directory.</summary>
    public string StateDir { get; set; } = ".forkbridge-state";
    public Dictionary<string, RepositoryConfig> Repositories { get; set; } = new();
    public Dictionary<string, PairConfig> Pairs { get; set; } = new();
    public Dictionary<string, BundleValidationConfig> Bundles { get; set; } = new();
    public PublishConfig Publish { get; set; } = new();
    public string? LegacyScript { get; set; }

    [YamlIgnore] public string SourceFile { get; set; } = "";
    [YamlIgnore] public string BaseDirectory => Path.GetDirectoryName(Path.GetFullPath(SourceFile)) ?? ".";

    public string ResolvePath(string p) => Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(BaseDirectory, p));
    public string StateRoot => ResolvePath(StateDir);
}

public sealed class RepositoryConfig
{
    public string Path { get; set; } = "";
    /// <summary>Name of the remote in the user's checkout whose URL is used for fetch/publish. The
    /// user's remote configuration is read, never modified.</summary>
    public string Remote { get; set; } = "origin";
    public string Kind { get; set; } = "unknown"; // dotnet | angular | unknown
    /// <summary>True when the path is an unverified placeholder.</summary>
    public bool Placeholder { get; set; }
}

public sealed class PairConfig
{
    /// <summary>Product key -> repository alias, e.g. qisl: qisl-backend, isl: isl-backend.</summary>
    public Dictionary<string, string> Products { get; set; } = new();
    public PairDefaults Defaults { get; set; } = new();
    public List<DirectionConfig> Directions { get; set; } = new();
    public List<ComponentConfig> Components { get; set; } = new();
    /// <summary>Product key -> glob patterns that belong only to that product.</summary>
    public Dictionary<string, List<string>> ProductSpecific { get; set; } = new();
    /// <summary>Product key -> glob patterns whose content in that product must never be
    /// overwritten when it is the transfer target.</summary>
    public Dictionary<string, List<string>> TargetPinned { get; set; } = new();
    public List<GeneratedRule> Generated { get; set; } = new();
    /// <summary>Patterns that may change without affecting transfers (e.g. docs). Changes are
    /// excluded and reported. Must be explicit - nothing is ignored implicitly.</summary>
    public List<string> Ignored { get; set; } = new();
    public Dictionary<string, ValidationProfile> Validation { get; set; } = new();
    public List<string> ProtectedBranches { get; set; } = ["main", "master", "development", "develop", "release/*", "hotfix/*"];
}

public sealed class PairDefaults
{
    public string TargetBase { get; set; } = "development";
    public string SourceBase { get; set; } = "development";
    public string ValidationProfile { get; set; } = "default";
    public string BranchPrefix { get; set; } = "forkbridge";
}

public sealed class DirectionConfig
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    /// <summary>Allowed modes: pick (commit-preserving, default), reconcile, merge (advanced).</summary>
    public List<string> Modes { get; set; } = ["pick"];
}

public sealed class ComponentConfig
{
    public string Id { get; set; } = "";
    /// <summary>proposed | confirmed | placeholder. Only confirmed components can be transferred
    /// unless --allow-proposed is passed.</summary>
    public string Status { get; set; } = "proposed";
    public string? Description { get; set; }
    /// <summary>Product key -> path (directory or file) relative to that repo root. A product key
    /// set to null/empty means the component is intentionally absent from that product.</summary>
    public Dictionary<string, string?> Paths { get; set; } = new();
    /// <summary>Product key -> reason the component is intentionally absent there.</summary>
    public Dictionary<string, string> Absent { get; set; } = new();
    public List<string> DependsOn { get; set; } = new();
    public string? Evidence { get; set; }
}

public sealed class GeneratedRule
{
    public string Pattern { get; set; } = "";
    /// <summary>block (default): changes stop the transfer. exclude: drop from the transfer and
    /// require regeneration via the named validation step.</summary>
    public string Action { get; set; } = "block";
    public string? Regenerate { get; set; }
    public string? Note { get; set; }
}

public sealed class ValidationProfile
{
    /// <summary>Repository build scripts execute arbitrary code. Profiles only run when the profile
    /// is marked trusted here AND the caller passes --trust-validation (or the run is in CI with
    /// FORKBRIDGE_TRUST_VALIDATION=1).</summary>
    public bool Trusted { get; set; }
    public string Runner { get; set; } = "process"; // process | docker
    public string? DockerImage { get; set; }
    public List<ValidationStep> Steps { get; set; } = new();
}

public sealed class ValidationStep
{
    public string Id { get; set; } = "";
    /// <summary>restore | build | test | lint | typecheck | contract | other</summary>
    public string Kind { get; set; } = "build";
    /// <summary>Executable followed by arguments. Never interpreted by a shell.</summary>
    public List<string> Run { get; set; } = new();
    public string Cwd { get; set; } = ".";
    public int TimeoutSeconds { get; set; } = 1800;
    public bool Required { get; set; } = true;
    public long MaxOutputKb { get; set; } = 2048;
    /// <summary>Only run when the transfer touches one of these components (empty = always).</summary>
    public List<string> WhenComponents { get; set; } = new();
    public Dictionary<string, string> Env { get; set; } = new();
    /// <summary>Environment variables that must exist for the step (e.g. a test DB connection).
    /// When missing, the step is reported UNAVAILABLE - never as a pass.</summary>
    public List<string> RequiresEnv { get; set; } = new();
}

public sealed class BundleValidationConfig
{
    public List<string> Pairs { get; set; } = new();
    public ValidationProfile Validation { get; set; } = new();
}

public sealed class PublishConfig
{
    public string Provider { get; set; } = "none"; // none | azure-devops
    public bool Draft { get; set; } = true;
}

public static class ConfigLoader
{
    public static ForkBridgeConfig Load(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new ConfigException($"Config file not found: {full}");
        return LoadFromYaml(File.ReadAllText(full), full);
    }

    public static ForkBridgeConfig LoadFromYaml(string text, string sourceFile)
    {
        var full = Path.GetFullPath(sourceFile);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        ForkBridgeConfig cfg;
        try { cfg = deserializer.Deserialize<ForkBridgeConfig>(text) ?? new ForkBridgeConfig(); }
        catch (YamlDotNet.Core.YamlException ex) { throw new ConfigException($"Invalid YAML in {full}: {ex.Message}"); }
        cfg.SourceFile = full;
        var errors = ConfigValidator.Validate(cfg);
        if (errors.Count > 0) throw new ConfigException("Configuration is invalid:\n  - " + string.Join("\n  - ", errors));
        return cfg;
    }

    public static string ToYaml(ForkBridgeConfig cfg)
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
        return serializer.Serialize(cfg);
    }

    public static void BackupFile(string target)
    {
        if (!File.Exists(target)) return;
        var orig = target + ".orig";
        if (!File.Exists(orig)) File.Copy(target, orig, overwrite: false);
        File.Copy(target, target + ".bak", overwrite: true);
    }

    public static void Save(ForkBridgeConfig cfg, string? path = null)
    {
        var errors = ConfigValidator.Validate(cfg);
        if (errors.Count > 0) throw new ConfigException("Configuration is invalid:\n  - " + string.Join("\n  - ", errors));
        var target = Path.GetFullPath(path ?? cfg.SourceFile);
        BackupFile(target);
        File.WriteAllText(target, ToYaml(cfg), new UTF8Encoding(false));
        cfg.SourceFile = target;
    }

    public static ForkBridgeConfig SaveRawYaml(string yaml, string path)
    {
        var target = Path.GetFullPath(path);
        var cfg = LoadFromYaml(yaml, target);
        BackupFile(target);
        File.WriteAllText(target, yaml, new UTF8Encoding(false));
        return cfg;
    }

    /// <summary>Applies a policy action directly to the raw YAML text so comments, blank lines,
    /// and YAML anchors (&amp;anchor / *alias) are preserved intact.</summary>
    public static bool TryApplyPolicyActionInPlace(string rawYaml, PolicyActionRequest req, PairConfig pair, out string updatedYaml)
    {
        updatedYaml = rawYaml;
        var eol = rawYaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = rawYaml.Replace("\r\n", "\n").Split('\n').ToList();

        var (pairsStart, pairsEnd) = FindBlock(lines, 0, lines.Count - 1, 0, "pairs");
        if (pairsStart < 0) return false;
        var (pairStart, pairEnd) = FindBlock(lines, pairsStart + 1, pairsEnd, 2, req.Pair);
        if (pairStart < 0) return false;

        var products = string.IsNullOrWhiteSpace(req.Product) || req.Product.Equals("both", StringComparison.OrdinalIgnoreCase)
            ? pair.Products.Keys.ToList()
            : [req.Product];
        var cleanPatterns = req.Patterns.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct(StringComparer.Ordinal).ToList();

        switch (req.Action)
        {
            case "add-ignored":
            case "remove-ignored":
            {
                var (secStart, secEnd) = FindBlock(lines, pairStart + 1, pairEnd, 4, "ignored");
                if (secStart < 0) return false;
                if (req.Action == "add-ignored") AddListItems(lines, secStart, secEnd, 6, cleanPatterns);
                else RemoveListItems(lines, secStart, secEnd, 6, cleanPatterns);
                break;
            }
            case "add-product-specific":
            case "remove-product-specific":
            case "add-target-pinned":
            case "remove-target-pinned":
            {
                var key = req.Action.EndsWith("product-specific", StringComparison.Ordinal) ? "productSpecific" : "targetPinned";
                var (secStart, secEnd) = FindBlock(lines, pairStart + 1, pairEnd, 4, key);
                if (secStart < 0) return false;
                var handledRanges = new HashSet<int>();
                foreach (var prod in products)
                {
                    (_, secEnd) = FindBlock(lines, pairStart + 1, lines.Count - 1, 4, key);
                    var (pStart, pEnd) = FindBlock(lines, secStart + 1, secEnd, 6, prod);
                    if (pStart < 0) return false;
                    var aliasMatch = System.Text.RegularExpressions.Regex.Match(StripComment(lines[pStart]), @"^\s*" + System.Text.RegularExpressions.Regex.Escape(prod) + @":\s*\*(\S+)\s*$");
                    if (aliasMatch.Success)
                    {
                        var anchorName = aliasMatch.Groups[1].Value;
                        var anchorLine = FindAnchorLine(lines, secStart + 1, secEnd, 6, anchorName);
                        if (anchorLine < 0) return false;
                        (pStart, pEnd) = GetBlockAt(lines, anchorLine, secEnd, 6);
                    }
                    if (!handledRanges.Add(pStart)) continue;
                    if (req.Action.StartsWith("add-", StringComparison.Ordinal)) AddListItems(lines, pStart, pEnd, 8, cleanPatterns);
                    else RemoveListItems(lines, pStart, pEnd, 8, cleanPatterns);
                    (_, pairEnd) = FindBlock(lines, pairsStart + 1, lines.Count - 1, 2, req.Pair);
                }
                break;
            }
            case "set-component-status":
            {
                var (secStart, secEnd) = FindBlock(lines, pairStart + 1, pairEnd, 4, "components");
                if (secStart < 0) return false;
                var newStatus = req.Status ?? "confirmed";
                bool anyMatched = false;
                for (int i = secStart + 1; i <= secEnd; i++)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(StripComment(lines[i]), @"^(\s*)-\s+id:\s*(\S+)\s*$");
                    if (!m.Success || m.Groups[1].Value.Length != 6) continue;
                    var id = m.Groups[2].Value.Trim('"', '\'');
                    if (req.ComponentId != "*" && !string.Equals(id, req.ComponentId, StringComparison.Ordinal)) continue;
                    for (int j = i + 1; j <= secEnd; j++)
                    {
                        var trimmed = lines[j].TrimStart();
                        if (trimmed.StartsWith("- id:", StringComparison.Ordinal) && LeadingSpaces(lines[j]) <= 6) break;
                        var sm = System.Text.RegularExpressions.Regex.Match(lines[j], @"^(\s*status:\s*)(\S+)(\s*(?:#.*)?)$");
                        if (sm.Success)
                        {
                            var curVal = sm.Groups[2].Value.Trim('"', '\'');
                            if (req.ComponentId == "*" && curVal != "proposed") break;
                            lines[j] = $"{sm.Groups[1].Value}{newStatus}{sm.Groups[3].Value}";
                            anyMatched = true;
                            break;
                        }
                    }
                }
                if (!anyMatched && req.ComponentId != "*") return false;
                break;
            }
            case "set-validation-trusted":
            {
                var profName = string.IsNullOrWhiteSpace(req.Profile) ? pair.Defaults.ValidationProfile : req.Profile;
                var (secStart, secEnd) = FindBlock(lines, pairStart + 1, pairEnd, 4, "validation");
                if (secStart < 0) return false;
                var (pStart, pEnd) = FindBlock(lines, secStart + 1, secEnd, 6, profName);
                if (pStart < 0) return false;
                var newVal = (req.Trusted ?? true) ? "true" : "false";
                bool found = false;
                for (int i = pStart + 1; i <= pEnd; i++)
                {
                    var tm = System.Text.RegularExpressions.Regex.Match(lines[i], @"^(\s*trusted:\s*)(true|false)(\s*(?:#.*)?)$");
                    if (tm.Success && LeadingSpaces(lines[i]) == 8)
                    {
                        lines[i] = $"{tm.Groups[1].Value}{newVal}{tm.Groups[3].Value}";
                        found = true;
                        break;
                    }
                }
                if (!found) return false;
                break;
            }
            default:
                return false;
        }

        updatedYaml = string.Join(eol, lines);
        return true;
    }

    private static int LeadingSpaces(string line)
    {
        int i = 0;
        while (i < line.Length && line[i] == ' ') i++;
        return i;
    }

    private static string StripComment(string line)
    {
        bool inSingle = false, inDouble = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '"' && !inSingle && (i == 0 || line[i - 1] != '\\')) inDouble = !inDouble;
            else if (c == '#' && !inSingle && !inDouble && (i == 0 || char.IsWhiteSpace(line[i - 1])))
                return line[..i];
        }
        return line;
    }

    private static (int start, int end) FindBlock(List<string> lines, int from, int to, int indent, string key)
    {
        var prefix = new string(' ', indent) + key + ":";
        for (int i = from; i <= to && i < lines.Count; i++)
        {
            var code = StripComment(lines[i]);
            if (code.StartsWith(prefix, StringComparison.Ordinal) &&
                (code.Length == prefix.Length || char.IsWhiteSpace(code[prefix.Length])))
            {
                return GetBlockAt(lines, i, to, indent);
            }
        }
        return (-1, -1);
    }

    private static (int start, int end) GetBlockAt(List<string> lines, int startLine, int maxEnd, int indent)
    {
        int end = startLine;
        for (int j = startLine + 1; j <= maxEnd && j < lines.Count; j++)
        {
            var t = lines[j].Trim();
            if (t.Length == 0 || t.StartsWith('#')) { end = j; continue; }
            if (LeadingSpaces(lines[j]) <= indent) break;
            end = j;
        }
        return (startLine, end);
    }

    private static int FindAnchorLine(List<string> lines, int from, int to, int indent, string anchorName)
    {
        for (int i = from; i <= to && i < lines.Count; i++)
        {
            if (LeadingSpaces(lines[i]) != indent) continue;
            var code = StripComment(lines[i]);
            if (System.Text.RegularExpressions.Regex.IsMatch(code, @"^\s*\S+:\s*&" + System.Text.RegularExpressions.Regex.Escape(anchorName) + @"\b"))
                return i;
        }
        return -1;
    }

    private static string? TryExtractListItemScalar(string line, int itemIndent)
    {
        if (LeadingSpaces(line) != itemIndent) return null;
        var code = StripComment(line).Trim();
        if (!code.StartsWith("- ", StringComparison.Ordinal)) return null;
        var rest = code[2..].Trim();
        if (rest.Length >= 2 && ((rest[0] == '"' && rest[^1] == '"') || (rest[0] == '\'' && rest[^1] == '\'')))
            return rest[1..^1];
        return rest;
    }

    private static void AddListItems(List<string> lines, int secStart, int secEnd, int itemIndent, List<string> patterns)
    {
        var headCode = StripComment(lines[secStart]);
        var flowMatch = System.Text.RegularExpressions.Regex.Match(headCode, @"^(\s*\S+:\s*(?:&\S+\s*)?)\[(.*)\]\s*$");
        if (flowMatch.Success)
        {
            var commentSuffix = lines[secStart][headCode.Length..];
            var items = ParseFlowItems(flowMatch.Groups[2].Value);
            foreach (var pat in patterns)
                if (!items.Contains(pat, StringComparer.Ordinal)) items.Add(pat);
            var rendered = string.Join(", ", items.Select(FormatScalar));
            lines[secStart] = $"{flowMatch.Groups[1].Value}[{rendered}]{commentSuffix}";
            return;
        }

        var existing = new HashSet<string>(StringComparer.Ordinal);
        int lastItemLine = secStart;
        for (int i = secStart + 1; i <= secEnd && i < lines.Count; i++)
        {
            if (TryExtractListItemScalar(lines[i], itemIndent) is { } val)
            {
                existing.Add(val);
                lastItemLine = i;
            }
        }
        var prefix = new string(' ', itemIndent) + "- ";
        int insertAt = lastItemLine + 1;
        foreach (var pat in patterns)
        {
            if (!existing.Add(pat)) continue;
            lines.Insert(insertAt++, $"{prefix}{FormatScalar(pat)}");
        }
    }

    private static void RemoveListItems(List<string> lines, int secStart, int secEnd, int itemIndent, List<string> patterns)
    {
        var toRemove = patterns.ToHashSet(StringComparer.Ordinal);
        var headCode = StripComment(lines[secStart]);
        var flowMatch = System.Text.RegularExpressions.Regex.Match(headCode, @"^(\s*\S+:\s*(?:&\S+\s*)?)\[(.*)\]\s*$");
        if (flowMatch.Success)
        {
            var commentSuffix = lines[secStart][headCode.Length..];
            var items = ParseFlowItems(flowMatch.Groups[2].Value).Where(x => !toRemove.Contains(x)).ToList();
            var rendered = string.Join(", ", items.Select(FormatScalar));
            lines[secStart] = $"{flowMatch.Groups[1].Value}[{rendered}]{commentSuffix}";
            return;
        }

        for (int i = Math.Min(secEnd, lines.Count - 1); i > secStart; i--)
        {
            if (TryExtractListItemScalar(lines[i], itemIndent) is { } val && toRemove.Contains(val))
                lines.RemoveAt(i);
        }
    }

    private static List<string> ParseFlowItems(string inner)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(inner)) return list;
        foreach (var part in inner.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var s = part.Trim();
            if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
                s = s[1..^1];
            list.Add(s);
        }
        return list;
    }

    private static string FormatScalar(string pat)
        => "\"" + pat.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>Stable hash of the policy that governs a pair. Any change to the pair section or to
    /// the repositories it uses invalidates plans made under the old policy.</summary>
    public static string PolicyHash(ForkBridgeConfig cfg, string pairName)
    {
        var pair = cfg.Pairs[pairName];
        var repos = pair.Products.Values.Distinct().OrderBy(x => x, StringComparer.Ordinal)
            .ToDictionary(a => a, a => cfg.Repositories[a]);
        var canonical = JsonSerializer.Serialize(new { version = cfg.Version, pair = pairName, policy = pair, repos },
            new JsonSerializerOptions { WriteIndented = false });
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

public sealed record PolicyActionRequest
{
    public required string Pair { get; init; }
    /// <summary>add-ignored | remove-ignored | add-product-specific | remove-product-specific |
    /// add-target-pinned | remove-target-pinned | set-component-status | upsert-component |
    /// set-validation-trusted | set-generated-rule</summary>
    public required string Action { get; init; }
    public string? Product { get; init; } // product key or "both"
    public List<string> Patterns { get; set; } = new();
    public string? ComponentId { get; set; }
    public string? Status { get; set; }
    public Dictionary<string, string?>? ComponentPaths { get; set; }
    public string? Profile { get; set; }
    public bool? Trusted { get; set; }
    public string? GeneratedAction { get; set; }
}

public sealed class ConfigException(string message) : Exception(message);
