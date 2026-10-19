using ForkBridge.Core.Config;

namespace ForkBridge.Core.Policy;

public enum PathClass { Shared, ProductSpecific, TargetPinned, Generated, Ignored, Unknown }

public sealed record Classification(PathClass Class, string? ComponentId, string? Rule, string? MappedPath = null)
{
    public override string ToString() => ComponentId is null ? $"{Class}" : $"{Class}({ComponentId})";
}

/// <summary>Classifies repository-relative paths for one product of a pair. Precedence (most
/// restrictive first): target-pinned (only when this product is the target), product-specific,
/// generated, ignored, shared component (longest component path wins), unknown.</summary>
public sealed class PathClassifier
{
    private readonly PairConfig _pair;
    private readonly string _product;
    private readonly bool _asTarget;

    public PathClassifier(PairConfig pair, string product, bool asTarget)
    {
        _pair = pair; _product = product; _asTarget = asTarget;
    }

    public Classification Classify(string path)
    {
        path = Glob.Normalize(path);
        if (_asTarget && _pair.TargetPinned.TryGetValue(_product, out var pins))
        {
            var pin = pins.Where(p => Glob.IsMatch(p, path)).OrderByDescending(Glob.Specificity).FirstOrDefault();
            if (pin is not null) return new(PathClass.TargetPinned, ComponentOf(path)?.Id, pin);
        }
        if (_pair.ProductSpecific.TryGetValue(_product, out var ps))
        {
            var rule = ps.Where(p => Glob.IsMatch(p, path)).OrderByDescending(Glob.Specificity).FirstOrDefault();
            if (rule is not null) return new(PathClass.ProductSpecific, null, rule);
        }
        var gen = _pair.Generated.Where(g => Glob.IsMatch(g.Pattern, path)).OrderByDescending(g => Glob.Specificity(g.Pattern)).FirstOrDefault();
        if (gen is not null) return new(PathClass.Generated, ComponentOf(path)?.Id, gen.Pattern);
        var ign = _pair.Ignored.FirstOrDefault(p => Glob.IsMatch(p, path));
        if (ign is not null) return new(PathClass.Ignored, null, ign);
        var comp = ComponentOf(path);
        if (comp is not null) return new(PathClass.Shared, comp.Id, comp.Paths[_product]);
        return new(PathClass.Unknown, null, null);
    }

    public ComponentConfig? ComponentOf(string path)
    {
        path = Glob.Normalize(path);
        return _pair.Components
            .Where(c => c.Paths.TryGetValue(_product, out var p) && !string.IsNullOrWhiteSpace(p) && Glob.IsMatch(p!, path))
            .OrderByDescending(c => Glob.Normalize(c.Paths[_product]!).Length)
            .FirstOrDefault();
    }

    /// <summary>Maps a source path inside <paramref name="component"/> to the target product's
    /// path. Returns null when the component is absent in the target.</summary>
    public static string? MapPath(ComponentConfig component, string sourceProduct, string targetProduct, string sourcePath)
    {
        var srcRoot = component.Paths.GetValueOrDefault(sourceProduct);
        var tgtRoot = component.Paths.GetValueOrDefault(targetProduct);
        if (string.IsNullOrWhiteSpace(srcRoot) || string.IsNullOrWhiteSpace(tgtRoot)) return null;
        srcRoot = Glob.Normalize(srcRoot); tgtRoot = Glob.Normalize(tgtRoot);
        sourcePath = Glob.Normalize(sourcePath);
        if (sourcePath == srcRoot) return tgtRoot;
        if (!sourcePath.StartsWith(srcRoot + "/", StringComparison.Ordinal)) return null;
        return tgtRoot + sourcePath[srcRoot.Length..];
    }
}
