using ForkBridge.Alignment.Config;
using ForkBridge.Alignment.Model;

namespace ForkBridge.Alignment.Planning;

/// <summary>Inputs for one path. Shas are blob ids (null = absent). Format comparisons are injected so the
/// classifier stays pure and table-testable.</summary>
public sealed record ClassifyInput(
    string Path, string? Base, string? Fork, string? Primary,
    bool FormatInsensitive, bool IsManifest, bool CleanMerge, bool Behind,
    Func<string, string, bool> FormatEqual);

/// <summary>Total classification function C(b, f, p) → (category, decision, reason), first match wins.
/// Port of the classification block of 02-New-AlignmentPlan.ps1 for both suites; the rule-order differences
/// are selected by <see cref="RuleFlags"/> (ADR-0002 §2.2, D1–D3).</summary>
public static class Classifier
{
    public static (Category Category, Decision Decision, string Reason)? Classify(ClassifyInput x, RuleFlags rules, string primaryLabel, string forkLabel)
    {
        string? b = x.Base, i = x.Fork, e = x.Primary;
        if (i == e) return null;
        var fi = x.FormatInsensitive;
        var fmtI = fi && b is not null && i is not null && b != i && x.FormatEqual(i, b);
        var fmtE = fi && b is not null && e is not null && b != e && x.FormatEqual(e, b);
        var iEff = fmtI ? b : i;
        var eEff = fmtE ? b : e;
        var manifestFirst = rules.ManifestPrecedence == "first";

        if (manifestFirst && x.IsManifest && i is not null && e is not null)
            return (Category.Packages, Decision.ManifestUnion, "add-only union of PackageVersion entries; version differences reported");
        if (iEff == eEff || (i is not null && e is not null && (!rules.FormatGated || fi) && x.FormatEqual(i, e)))
            return (Category.FormatOnly, Decision.TakePrimary, $"whitespace/BOM-only difference; {primaryLabel} formatting kept");
        if (iEff == b)
        {
            var cat = e is null ? Category.PrimaryDeleted : b is null ? Category.PrimaryAdded : Category.PrimaryChanged;
            return (cat, Decision.TakePrimary, $"{primaryLabel}-only change since sync" + (fmtI ? $"; {forkLabel} change was format-only" : ""));
        }
        if (rules.BehindDetection && x.Behind)
            return e is not null
                ? (Category.ForkBehind, Decision.TakePrimary, $"{forkLabel} holds an earlier {primaryLabel} version of this file (partial per-commit import); {primaryLabel} version taken")
                : (Category.ForkStale, Decision.DeleteBoth, $"exact content {primaryLabel} held before deleting it; left behind by an import");
        if (!manifestFirst && i is not null && e is not null && x.IsManifest)
            return (Category.Packages, Decision.ManifestUnion, "add-only union of dependency entries; version differences reported");
        if (eEff == b)
        {
            if (i is null) return (Category.ForkDeleted, Decision.TakePrimary, $"{forkLabel} deletion not propagated: restored in {forkLabel} (no code loss). TAKE_ISL deletes it in {primaryLabel}.");
            if (b is null) return (Category.ForkAdded, Decision.KeepForkOnly, "");
            return (Category.ForkChanged, Decision.TakeFork, $"semantic {forkLabel} change since sync: promoted to {primaryLabel}" + (fmtE ? $"; {primaryLabel} change was format-only" : ""));
        }
        if (x.CleanMerge) return (Category.BothChanged, Decision.Merged, "both changed; clean 3-way merge");
        return (Category.Conflict, Decision.Manual, i is not null && e is not null ? "content conflict; Merged column holds the blob with conflict markers" : "modify/delete conflict");
    }

    /// <summary>Target entry for one side of a row (port of Get-SideTarget): a blob, null (absent) or the
    /// manifest-union marker.</summary>
    public static SideTarget TargetFor(AlignmentRow r, Side side) => r.Decision switch
    {
        Decision.TakePrimary => SideTarget.Blob(r.Primary),
        Decision.TakeFork => SideTarget.Blob(r.Fork),
        Decision.Merged => SideTarget.Blob(r.Merged),
        Decision.DeleteBoth => SideTarget.Blob(null),
        Decision.KeepForkOnly or Decision.KeepBoth or Decision.Manual => SideTarget.Blob(r.Of(side)),
        Decision.ManifestUnion => SideTarget.Union,
        _ => throw new InvalidOperationException($"Unknown decision {r.Decision} for {r.Path}"),
    };
}

public readonly record struct SideTarget(BlobRef? Ref, bool IsUnion)
{
    public static SideTarget Blob(BlobRef? r) => new(r, false);
    public static readonly SideTarget Union = new(null, true);
    public bool Absent => !IsUnion && Ref is null;
}
