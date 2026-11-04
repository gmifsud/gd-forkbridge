using System.Text.Json.Serialization;

namespace ForkBridge.Alignment.Model;

public enum Side { Primary, Fork }

/// <summary>Per-path decision. Legacy PS tokens: TAKE_BE/TAKE_FE, TAKE_ISL, MERGED, DELETE_BOTH, KEEP_ISL_ONLY,
/// KEEP_BOTH, PACKAGES_UNION, MANUAL.</summary>
public enum Decision { TakePrimary, TakeFork, Merged, DeleteBoth, KeepForkOnly, KeepBoth, ManifestUnion, Manual }

public enum Category
{
    Packages, FormatOnly, PrimaryChanged, PrimaryAdded, PrimaryDeleted,
    ForkBehind, ForkStale, ForkDeleted, ForkAdded, ForkChanged, BothChanged, Conflict,
    ForkOwned, ForkRenamed, ForkRenameSource, ForkOnly,
}

public sealed record BlobRef(string Sha, string Mode);

/// <summary>One differing path of the shared scope. Mutable while the planner runs; a saved plan is immutable
/// (content-hashed, read-only file) and edits create a derived plan.</summary>
public sealed class AlignmentRow
{
    public required string Path { get; init; }
    public Category Category { get; set; }
    public Decision Decision { get; set; }
    public string Reason { get; set; } = "";
    /// <summary>"n: leaf, leaf…" - surviving references that keep or warn about a deletion.</summary>
    public string Refs { get; set; } = "";
    public BlobRef? Base { get; init; }
    public BlobRef? Primary { get; init; }
    public BlobRef? Fork { get; init; }
    public BlobRef? Merged { get; init; }
    /// <summary>Both sides present, merge-tree produced a result and reported no conflict for the path.</summary>
    public bool CleanMerge { get; init; }

    [JsonIgnore] internal List<string> ExtRefs { get; set; } = new();
    [JsonIgnore] internal List<string> KeptBy { get; set; } = new();

    public BlobRef? Of(Side s) => s == Side.Primary ? Primary : Fork;
}

public sealed record SideState(string RepoAlias, string Label, string? Branch, string Rev, string CommitRev, string ScopedTree, string? WorktreePath);

public sealed class AlignmentPlan
{
    public required string Id { get; init; }
    public string? ParentPlanId { get; init; }
    public required string Profile { get; init; }
    public required string ProfileHash { get; init; }
    public required string SyncBase { get; init; }
    public required IReadOnlyList<string> Scope { get; init; }
    public required SideState Primary { get; init; }
    public required SideState Fork { get; init; }
    public required string BaseTree { get; init; }
    public required string MergedTree { get; init; }
    public int Identical { get; init; }
    public bool Preview { get; init; }
    public required IReadOnlyList<AlignmentRow> Rows { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string ContentHash { get; set; } = "";
    public SideState SideOf(Side s) => s == Side.Primary ? Primary : Fork;
}

public static class DecisionNames
{
    private static readonly Dictionary<string, Decision> Map = new(StringComparer.Ordinal)
    {
        ["TakePrimary"] = Decision.TakePrimary, ["TAKE_PRIMARY"] = Decision.TakePrimary, ["TAKE_BE"] = Decision.TakePrimary, ["TAKE_FE"] = Decision.TakePrimary,
        ["TakeFork"] = Decision.TakeFork, ["TAKE_FORK"] = Decision.TakeFork, ["TAKE_ISL"] = Decision.TakeFork,
        ["Merged"] = Decision.Merged, ["MERGED"] = Decision.Merged,
        ["DeleteBoth"] = Decision.DeleteBoth, ["DELETE_BOTH"] = Decision.DeleteBoth,
        ["KeepForkOnly"] = Decision.KeepForkOnly, ["KEEP_FORK_ONLY"] = Decision.KeepForkOnly, ["KEEP_ISL_ONLY"] = Decision.KeepForkOnly,
        ["KeepBoth"] = Decision.KeepBoth, ["KEEP_BOTH"] = Decision.KeepBoth,
        ["ManifestUnion"] = Decision.ManifestUnion, ["MANIFEST_UNION"] = Decision.ManifestUnion, ["PACKAGES_UNION"] = Decision.ManifestUnion,
        ["Manual"] = Decision.Manual, ["MANUAL"] = Decision.Manual,
    };

    public static bool TryParse(string s, out Decision d) => Map.TryGetValue(s.Trim(), out d);
    public static Decision Parse(string s) => TryParse(s, out var d) ? d : throw new FormatException($"Unknown decision '{s}'.");

    /// <summary>PS token for a decision; <paramref name="primaryCode"/> is "BE" or "FE".</summary>
    public static string Legacy(Decision d, string primaryCode) => d switch
    {
        Decision.TakePrimary => "TAKE_" + primaryCode,
        Decision.TakeFork => "TAKE_ISL",
        Decision.Merged => "MERGED",
        Decision.DeleteBoth => "DELETE_BOTH",
        Decision.KeepForkOnly => "KEEP_ISL_ONLY",
        Decision.KeepBoth => "KEEP_BOTH",
        Decision.ManifestUnion => "PACKAGES_UNION",
        _ => "MANUAL",
    };

    /// <summary>PS category token; <paramref name="primaryCode"/> is "BE" or "FE".</summary>
    public static string Legacy(Category c, string primaryCode) => c switch
    {
        Category.Packages => "PACKAGES",
        Category.FormatOnly => "FORMAT_ONLY",
        Category.PrimaryChanged => primaryCode + "_CHANGED",
        Category.PrimaryAdded => primaryCode + "_ADDED",
        Category.PrimaryDeleted => primaryCode + "_DELETED",
        Category.ForkBehind => "ISL_BEHIND",
        Category.ForkStale => "ISL_STALE",
        Category.ForkDeleted => "ISL_DELETED",
        Category.ForkAdded => "ISL_ADDED",
        Category.ForkChanged => "ISL_CHANGED",
        Category.BothChanged => "BOTH_CHANGED",
        Category.Conflict => "CONFLICT",
        Category.ForkOwned => "FORK_OWNED",
        Category.ForkRenamed => "ISL_RENAMED",
        Category.ForkRenameSource => "ISL_RENAME_SOURCE",
        _ => "ISL_ONLY",
    };

    public static Category ParseLegacyCategory(string s) => s switch
    {
        "PACKAGES" => Category.Packages,
        "FORMAT_ONLY" => Category.FormatOnly,
        "BE_CHANGED" or "FE_CHANGED" => Category.PrimaryChanged,
        "BE_ADDED" or "FE_ADDED" => Category.PrimaryAdded,
        "BE_DELETED" or "FE_DELETED" => Category.PrimaryDeleted,
        "ISL_BEHIND" => Category.ForkBehind,
        "ISL_STALE" => Category.ForkStale,
        "ISL_DELETED" => Category.ForkDeleted,
        "ISL_ADDED" => Category.ForkAdded,
        "ISL_CHANGED" => Category.ForkChanged,
        "BOTH_CHANGED" => Category.BothChanged,
        "CONFLICT" => Category.Conflict,
        "FORK_OWNED" => Category.ForkOwned,
        "ISL_RENAMED" => Category.ForkRenamed,
        "ISL_RENAME_SOURCE" => Category.ForkRenameSource,
        "ISL_ONLY" => Category.ForkOnly,
        _ => throw new FormatException($"Unknown category '{s}'."),
    };
}

public sealed class AlignmentException(string message, int exitCode = 11) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}
