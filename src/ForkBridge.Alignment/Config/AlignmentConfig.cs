using YamlDotNet.Serialization;

namespace ForkBridge.Alignment.Config;

/// <summary>Root of <c>forkbridge.alignment.yml</c>. Repositories are referenced by the aliases declared in
/// <c>forkbridge.yml</c>, which stays the single source of truth for repository paths.</summary>
public sealed class AlignmentConfig
{
    public int Version { get; set; } = 1;
    /// <summary>Harness-owned state (scratch repos, plans, ledgers, logs). Must be outside every repository.</summary>
    public string StateDir { get; set; } = "";
    public Dictionary<string, AlignmentProfile> Profiles { get; set; } = new();

    [YamlIgnore] public string SourceFile { get; set; } = "";
}

/// <summary>One primary/fork pair (e.g. backend, frontend). Mirrors Align.Config.psd1 of the PS suites.</summary>
public sealed class AlignmentProfile
{
    public SideProfile Primary { get; set; } = new();
    public SideProfile Fork { get; set; } = new();

    /// <summary>Default 3-way merge base for the shared scope. A session or the ledger may supersede it.</summary>
    public string SyncBase { get; set; } = "";

    /// <summary>Shared paths reconciled (folders or single files, repo-relative, '/' separators).</summary>
    public List<string> Scope { get; set; } = new();
    /// <summary>Never read, never written (prefix match).</summary>
    public List<string> Protected { get; set; } = new();
    /// <summary>Fork-only paths inside the shared scope that are never promoted (PowerShell -clike patterns).</summary>
    public List<string> ForkOwned { get; set; } = new();
    /// <summary>Extensions (".cs") where whitespace/BOM-only differences carry no meaning.</summary>
    public List<string> FormatInsensitive { get; set; } = new();

    public RuleFlags Rules { get; set; } = new();
    public ManifestSpec Manifest { get; set; } = new();
    public ReferenceSpec References { get; set; } = new();
    public List<OverrideSpec> Overrides { get; set; } = new();
    public List<GuardSpec> Guards { get; set; } = new();
    public BuildSpec Builds { get; set; } = new();
    public List<string> NeverPush { get; set; } = new();
    /// <summary>Optional trailer appended to alignment commit messages.</summary>
    public string? CommitTrailer { get; set; }
    /// <summary>Optional sync ledger (JSON array with nextSyncBase entries, e.g. the PS suite's sync-ledger.json);
    /// its entries become SyncBase candidates.</summary>
    public string? Ledger { get; set; }
    public AdvisorSpec Advisor { get; set; } = new();
}

/// <summary>SyncBase Advisor tuning (Addendum A §3).</summary>
public sealed class AdvisorSpec
{
    public int RevertWeight { get; set; } = 5;
    public int MergeWeight { get; set; } = 1;
    public int ConflictWeight { get; set; } = 3;
    /// <summary>Fork commits touching at least this many scoped files are matched as bulk imports/overlays.</summary>
    public int OverlayThreshold { get; set; } = 50;
    public int MaxCommitsPerLine { get; set; } = 150;
    public int MaxWindowDays { get; set; } = 180;
    public int MaxCandidates { get; set; } = 10;
}

public sealed class SideProfile
{
    /// <summary>Repository alias from forkbridge.yml.</summary>
    public string Repo { get; set; } = "";
    /// <summary>Display label ("QiSL", "ISL").</summary>
    public string Label { get; set; } = "";
    /// <summary>Legacy name used by the PS suites in plan.tsv / meta ("Backend", "Frontend", "Isl").</summary>
    public string LegacyName { get; set; } = "";
    /// <summary>Where new alignment worktrees are created (WorktreeRoot\NewBranch).</summary>
    public string WorktreeRoot { get; set; } = "";
    /// <summary>Blobs reachable from these refs count as "primary once held this exact content".</summary>
    public List<string> HistoryRefs { get; set; } = new();
    /// <summary>Plan rows are tagged with which side matches this ref (QiSL is primary).</summary>
    public string? ReferenceRef { get; set; }
    public AzureRepoSpec? Azure { get; set; }
    /// <summary>Defaults offered when a session creates a new alignment branch.</summary>
    public SessionDefaults Session { get; set; } = new();
}

public sealed class SessionDefaults
{
    public string? BaseRef { get; set; }
    public string? NewBranch { get; set; }
    public string? TargetBranch { get; set; }
    public List<string> MergeRefs { get; set; } = new();
}

public sealed class AzureRepoSpec
{
    public string Organization { get; set; } = "";
    public string Project { get; set; } = "";
    public string Repo { get; set; } = "";
}

/// <summary>Rule-order switches that keep each profile at parity with its PS suite (ADR-0002 §8, D1–D3).</summary>
public sealed class RuleFlags
{
    /// <summary>"first": manifest rows are classified before any other rule (Backend suite).
    /// "afterBehind": one-sided manifest changes are taken, union only when both changed (Frontend suite).</summary>
    public string ManifestPrecedence { get; set; } = "first";
    /// <summary>When true, the fork-vs-primary format-only comparison is limited to FormatInsensitive extensions.</summary>
    public bool FormatGated { get; set; }
    /// <summary>When true, a fork blob equal to an earlier primary version of the same path is "behind"
    /// (take primary / delete both) instead of a fork change.</summary>
    public bool BehindDetection { get; set; }
}

public sealed class ManifestSpec
{
    /// <summary>directoryPackagesProps | packageJson</summary>
    public string Kind { get; set; } = "directoryPackagesProps";
    /// <summary>-clike patterns selecting manifest files (matched case-insensitively for directoryPackagesProps file names).</summary>
    public List<string> Files { get; set; } = new();
    /// <summary>packageJson only: dependency sections that are unioned.</summary>
    public List<string> Sections { get; set; } = new();
    /// <summary>Applied to both sides; other version differences are reported only.</summary>
    public Dictionary<string, string> VersionOverrides { get; set; } = new();
}

public sealed class ReferenceSpec
{
    /// <summary>csharp | typescript</summary>
    public string Analyzer { get; set; } = "csharp";
}

public sealed class OverrideSpec
{
    public string Pattern { get; set; } = "";
    public string Decision { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class GuardSpec
{
    public string Name { get; set; } = "";
    /// <summary>primary | fork | both</summary>
    public string Side { get; set; } = "both";
    public string Pattern { get; set; } = "";
    public List<string> Include { get; set; } = new();
}

public sealed class BuildSpec
{
    /// <summary>Build commands run user code; they run only when trusted here and on the invocation.</summary>
    public bool Trusted { get; set; }
    public List<BuildStep> Primary { get; set; } = new();
    public List<BuildStep> Fork { get; set; } = new();
    public List<string> RestoreAfterBuild { get; set; } = new();
    public List<string> WarnDirtyAfterBuild { get; set; } = new();
}

public sealed class BuildStep
{
    public string Name { get; set; } = "";
    public string Exe { get; set; } = "";
    public List<string> Args { get; set; } = new();
}
