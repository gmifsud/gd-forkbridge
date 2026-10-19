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
        var text = File.ReadAllText(full);
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

public sealed class ConfigException(string message) : Exception(message);
