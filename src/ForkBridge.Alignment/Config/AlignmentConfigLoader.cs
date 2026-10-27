using System.Text.Json;
using System.Text.RegularExpressions;
using ForkBridge.Alignment.Model;
using ForkBridge.Core.Config;
using ForkBridge.Core.Storage;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ForkBridge.Alignment.Config;

/// <summary>A profile with its repository aliases resolved to canonical paths.</summary>
public sealed record ResolvedProfile(string Name, AlignmentProfile Profile, string PrimaryRoot, string ForkRoot, string StateDir, string Hash)
{
    public string RootOf(Side side) => side == Side.Primary ? PrimaryRoot : ForkRoot;
    public SideProfile SideOf(Side side) => side == Side.Primary ? Profile.Primary : Profile.Fork;
}

public static class AlignmentConfigLoader
{
    public const string DefaultFileName = "forkbridge.alignment.yml";

    /// <summary>Alignment config path: explicit, then FORKBRIDGE_ALIGNMENT_CONFIG, then next to forkbridge.yml.</summary>
    public static string ResolvePath(string? explicitPath, string forkBridgeConfigPath)
        => explicitPath
           ?? Environment.GetEnvironmentVariable("FORKBRIDGE_ALIGNMENT_CONFIG")
           ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(forkBridgeConfigPath)) ?? ".", DefaultFileName);

    public static AlignmentConfig Load(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new ConfigException($"Alignment config not found: {full}");
        return Parse(File.ReadAllText(full), full);
    }

    public static AlignmentConfig Parse(string yaml, string sourceFile)
    {
        var d = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();
        AlignmentConfig cfg;
        try { cfg = d.Deserialize<AlignmentConfig>(yaml) ?? new AlignmentConfig(); }
        catch (YamlDotNet.Core.YamlException ex) { throw new ConfigException($"Invalid YAML in {sourceFile}: {ex.Message}"); }
        cfg.SourceFile = Path.GetFullPath(sourceFile);
        return cfg;
    }

    /// <summary>Validates a profile against forkbridge.yml and resolves repository paths.</summary>
    public static ResolvedProfile Resolve(AlignmentConfig cfg, ForkBridgeConfig fb, string profileName)
    {
        if (!cfg.Profiles.TryGetValue(profileName, out var p))
            throw new ConfigException($"Unknown alignment profile '{profileName}'. Known: {string.Join(", ", cfg.Profiles.Keys)}");
        var errors = Validate(cfg, fb, profileName, p);
        if (errors.Count > 0) throw new ConfigException($"Alignment profile '{profileName}' is invalid:\n  - " + string.Join("\n  - ", errors));
        var baseDir = Path.GetDirectoryName(cfg.SourceFile) ?? ".";
        var state = Path.GetFullPath(Path.IsPathRooted(cfg.StateDir) ? cfg.StateDir : Path.Combine(baseDir, cfg.StateDir));
        return new ResolvedProfile(profileName, p,
            PathCanon.Canonical(fb.ResolvePath(fb.Repositories[p.Primary.Repo].Path)),
            PathCanon.Canonical(fb.ResolvePath(fb.Repositories[p.Fork.Repo].Path)),
            Path.Combine(state, StateStore.Sanitize(profileName)), Hash(p));
    }

    public static List<string> Validate(AlignmentConfig cfg, ForkBridgeConfig fb, string name, AlignmentProfile p)
    {
        var e = new List<string>();
        if (cfg.Version != 1) e.Add($"version {cfg.Version} is not supported (expected 1)");
        if (string.IsNullOrWhiteSpace(cfg.StateDir)) e.Add("stateDir is required");
        foreach (var (label, side) in new[] { ("primary", p.Primary), ("fork", p.Fork) })
        {
            if (!fb.Repositories.TryGetValue(side.Repo, out var repo)) { e.Add($"{label}.repo '{side.Repo}' is not a repository alias in forkbridge.yml"); continue; }
            if (repo.Placeholder) e.Add($"{label}.repo '{side.Repo}' is a placeholder in forkbridge.yml");
            if (PathCanon.UsesJunction(repo.Path)) e.Add($"{label}.repo path {repo.Path} uses the C:\\Repos junction; use the canonical D:\\Repos path");
            if (!string.IsNullOrEmpty(side.WorktreeRoot) && PathCanon.UsesJunction(side.WorktreeRoot)) e.Add($"{label}.worktreeRoot uses the C:\\Repos junction");
            if (string.IsNullOrWhiteSpace(side.Label)) e.Add($"{label}.label is required");
        }
        if (p.Primary.Repo == p.Fork.Repo) e.Add("primary and fork must be different repositories");
        if (p.Scope.Count == 0) e.Add("scope must list at least one path");
        foreach (var s in p.Scope.Concat(p.Protected))
            if (s.Contains('\\') || s.StartsWith('/') || s.Contains("..") || s.EndsWith('/')) e.Add($"scope/protected entry '{s}' must be repo-relative with '/' separators and no trailing '/'");
        if (!string.IsNullOrEmpty(p.SyncBase) && !Regex.IsMatch(p.SyncBase, "^[0-9a-f]{7,40}$")) e.Add($"syncBase '{p.SyncBase}' is not a commit id");
        if (p.Rules.ManifestPrecedence is not ("first" or "afterBehind")) e.Add("rules.manifestPrecedence must be 'first' or 'afterBehind'");
        if (p.Manifest.Kind is not ("directoryPackagesProps" or "packageJson")) e.Add("manifest.kind must be directoryPackagesProps or packageJson");
        if (p.Manifest.Kind == "packageJson" && p.Manifest.Sections.Count == 0) e.Add("manifest.sections is required for packageJson");
        if (p.References.Analyzer is not ("csharp" or "typescript")) e.Add("references.analyzer must be csharp or typescript");
        foreach (var o in p.Overrides)
            if (!DecisionNames.TryParse(o.Decision, out _)) e.Add($"override '{o.Pattern}': unknown decision '{o.Decision}'");
        foreach (var g in p.Guards)
        {
            if (g.Side is not ("primary" or "fork" or "both")) e.Add($"guard '{g.Name}': side must be primary, fork or both");
            try { _ = new Regex(g.Pattern); } catch (ArgumentException ex) { e.Add($"guard '{g.Name}': invalid regex: {ex.Message}"); }
        }
        // State must not live inside a repository (same rule as ForkBridge.Core).
        var baseDir = Path.GetDirectoryName(cfg.SourceFile) ?? ".";
        if (!string.IsNullOrWhiteSpace(cfg.StateDir))
        {
            var state = Path.GetFullPath(Path.IsPathRooted(cfg.StateDir) ? cfg.StateDir : Path.Combine(baseDir, cfg.StateDir));
            foreach (var alias in new[] { p.Primary.Repo, p.Fork.Repo })
                if (fb.Repositories.TryGetValue(alias, out var r))
                {
                    var rp = fb.ResolvePath(r.Path);
                    if (PathCanon.IsInside(state, rp) || PathCanon.IsInside(rp, state)) e.Add($"stateDir {state} must not be inside or contain repository '{alias}' ({rp})");
                }
        }
        return e;
    }

    /// <summary>Content hash of a profile (canonical JSON). Recorded in every plan; a change makes plans stale.</summary>
    public static string Hash(AlignmentProfile p) => "sha256:" + StateStore.Sha256(JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = false }));
}

public static class PathCanon
{
    public static bool UsesJunction(string path) => Regex.IsMatch(path.Replace('/', '\\'), @"^[Cc]:\\Repos(\\|$)");

    /// <summary>Full path with any symlink/junction ancestor replaced by its target, so a worktree created
    /// through C:\Repos still resolves to D:\Repos.</summary>
    public static string Canonical(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (full.Length == 0) return path;
        var parts = new List<string>();
        var cur = full;
        while (!string.IsNullOrEmpty(cur))
        {
            try
            {
                var di = new DirectoryInfo(cur);
                if (di.Exists && di.LinkTarget is not null)
                {
                    var target = di.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                    if (target is not null)
                    {
                        parts.Reverse();
                        return parts.Aggregate(target, Path.Combine);
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            var parent = Path.GetDirectoryName(cur);
            if (parent is null || parent == cur) break;
            parts.Add(Path.GetFileName(cur));
            cur = parent;
        }
        return full;
    }

    public static bool IsInside(string parent, string child)
    {
        var p = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var c = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return c.StartsWith(p, cmp);
    }
}
