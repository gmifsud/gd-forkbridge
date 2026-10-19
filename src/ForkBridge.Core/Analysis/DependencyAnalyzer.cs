using System.Text.RegularExpressions;
using ForkBridge.Core.Config;
using ForkBridge.Core.Git;
using ForkBridge.Core.Model;
using ForkBridge.Core.Policy;

namespace ForkBridge.Core.Analysis;

public sealed record DependencyImpact
{
    public List<string> AffectedProjects { get; init; } = new();
    public List<string> SharedConsumers { get; init; } = new();
    public List<string> ProductConsumers { get; init; } = new();
    public List<string> FrontendConsumers { get; init; } = new();
    public List<string> Notes { get; init; } = new();
}

/// <summary>One transferred source change, used as analyzer input.</summary>
public sealed record AnalyzedChange(string SourceCommit, string SourceParent, PathDecision Path);

/// <summary>Static, deterministic dependency/compatibility analysis. Produces evidence-backed findings.
/// Blocking findings here are limited to facts that are certain from manifests (e.g. a reference to
/// a project the target does not have). API/semantic concerns are reported as warnings and are
/// decided by the consumer build/test validation.</summary>
public sealed class DependencyAnalyzer
{
    private readonly GitClient _git;
    private readonly string _mirror;
    private readonly PairConfig _pair;
    private readonly string _from, _to;
    private readonly PathClassifier _srcCls, _tgtCls;

    public DependencyAnalyzer(GitClient git, string mirror, PairConfig pair, string from, string to)
    {
        _git = git; _mirror = mirror; _pair = pair; _from = from; _to = to;
        _srcCls = new PathClassifier(pair, from, asTarget: false);
        _tgtCls = new PathClassifier(pair, to, asTarget: true);
    }

    public async Task<(List<Finding> Findings, DependencyImpact Impact)> AnalyzeAsync(
        IReadOnlyList<AnalyzedChange> changes, string sourceHead, string targetBase, DependencyGraph targetGraph, DependencyGraph sourceGraph)
    {
        var findings = new List<Finding>();
        var included = changes.Where(c => c.Path.Decision == Decision.Include).ToList();
        var excluded = changes.Where(c => c.Path.Decision != Decision.Include).ToList();
        var includedTargetPaths = included.Select(c => c.Path.TargetPath!).ToHashSet(StringComparer.Ordinal);

        bool ExistsInTargetAfter(string tgtPath)
            => (targetGraph.Files.Contains(tgtPath) || includedTargetPaths.Contains(tgtPath)) &&
               !included.Any(c => c.Path.Status == 'D' && c.Path.TargetPath == tgtPath);

        foreach (var ch in included)
        {
            var p = ch.Path;
            if (p.Status == 'D')
            {
                findings.AddRange(await AnalyzeDeletionAsync(ch, targetBase));
                continue;
            }
            var src = p.SourcePath;
            var newBytes = await _git.ReadBlobAsync(_mirror, ch.SourceCommit, src) ?? [];
            var oldBytes = p.Status == 'A' ? null : await _git.ReadBlobAsync(_mirror, ch.SourceParent, p.SourceOldPath ?? src);

            if (src.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                findings.AddRange(AnalyzeCsproj(ch, oldBytes, newBytes, targetGraph, sourceGraph, ExistsInTargetAfter, included));
            else if (Path.GetFileName(src).Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase))
                findings.AddRange(AnalyzeCentralPackages(ch, oldBytes, newBytes, targetGraph));
            else if (Path.GetFileName(src) == "package.json")
                findings.AddRange(await AnalyzePackageJsonAsync(ch, oldBytes, newBytes, targetBase, included));
            else if (Regex.IsMatch(Path.GetFileName(src), @"^tsconfig.*\.json$"))
                findings.Add(Warn("TSCONFIG_CHANGED", "TypeScript configuration changed", ch, "dependency",
                    "Path aliases or compiler options change for every consumer of this tsconfig.", "Run type checking and the production build for all target apps."));
            else if (src.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && oldBytes is not null)
                findings.AddRange(await AnalyzeCSharpSurfaceAsync(ch, oldBytes, newBytes, targetBase));
            else if (Regex.IsMatch(src, @"\.(ts|tsx|mts)$"))
                findings.AddRange(await AnalyzeTypeScriptAsync(ch, oldBytes, newBytes, targetBase, targetGraph, ExistsInTargetAfter));

            if (Regex.IsMatch(src, @"(?i)(dto|contract|\.Application\.Shared/|service-proxies|/api/|\.api\.)"))
                findings.Add(new Finding
                {
                    Id = Finding.MakeId("CONTRACT_CHANGED", ch.SourceCommit, src), Code = "CONTRACT_CHANGED", Category = "contract",
                    Severity = Severity.Warning, Title = "Possible API/DTO contract change", Component = p.Component,
                    Files = [src], Commits = [ch.SourceCommit],
                    Evidence = [$"{src} matches a DTO/contract/API path pattern and is modified ({p.Status})."],
                    SuggestedAction = "Check generated API clients in the paired frontend repo; consider a coordinated bundle.",
                });
        }

        // Generated-client staleness: contract changes without any generated file in the transfer.
        if (findings.Any(f => f.Code == "CONTRACT_CHANGED") && _pair.Generated.Any(g => g.Regenerate is not null) &&
            !changes.Any(c => c.Path.SourceClass.StartsWith("Generated")))
            findings.Add(new Finding
            {
                Id = Finding.MakeId("GENERATED_CLIENT_STALE", sourceHead), Code = "GENERATED_CLIENT_STALE", Category = "generated",
                Severity = Severity.Warning, Title = "Contract changed but no generated client changed",
                Evidence = ["Contract-like files changed; no file classified as generated is part of the change set."],
                SuggestedAction = "Run the configured regeneration step and include its output, or confirm no client depends on these contracts.",
            });

        // Mixed commits: dependencies crossing the include/exclude boundary.
        findings.AddRange(await CrossBoundaryAsync(included, excluded, targetBase));

        var impact = BuildImpact(included, targetGraph);
        impact.FrontendConsumers.AddRange(await FrontendConsumersAsync(included, targetBase));
        return (findings, impact);
    }

    // ---------------------------------------------------------------------------------------------
    private IEnumerable<Finding> AnalyzeCsproj(AnalyzedChange ch, byte[]? oldBytes, byte[] newBytes, DependencyGraph tgt, DependencyGraph srcGraph,
        Func<string, bool> existsAfter, List<AnalyzedChange> included)
    {
        var p = ch.Path;
        var oldN = oldBytes is null ? null : DependencyGraph.ParseCsproj(p.SourceOldPath ?? p.SourcePath, oldBytes);
        var newN = DependencyGraph.ParseCsproj(p.SourcePath, newBytes);
        if (newN is null) { yield return Block("CSPROJ_UNPARSEABLE", "Project file could not be parsed", ch, "dependency", [$"{p.SourcePath} is not valid XML."], "Fix the project file in the source first."); yield break; }

        foreach (var refPath in newN.ProjectReferences.Except(oldN?.ProjectReferences ?? []))
        {
            var cls = _srcCls.Classify(refPath);
            var comp = _srcCls.ComponentOf(refPath);
            string? mapped = comp is null ? null : PathClassifier.MapPath(comp, _from, _to, refPath);
            if (cls.Class is PathClass.ProductSpecific)
                yield return Block("NEW_DEP_ON_PRODUCT_SPECIFIC", "Shared project gains a reference to a product-specific project", ch, "dependency",
                    [$"{p.SourcePath} adds <ProjectReference Include> -> {refPath} (classified {cls})."], "Remove the reference from the shared project or reclassify the referenced project.");
            else if (cls.Class is PathClass.Unknown || mapped is null)
                yield return Block("NEW_DEP_UNCLASSIFIED", "New project reference to an unclassified path", ch, "dependency",
                    [$"{p.SourcePath} adds a reference to {refPath}, which is not in any component ({cls})."], "Add the referenced project to a shared component (confirmed) or remove the reference.");
            else if (!existsAfter(mapped))
                yield return Block("MISSING_PROJECT", "New shared dependency is missing in the target", ch, "dependency",
                    [$"{p.SourcePath} adds a reference to {refPath} -> target path {mapped}, which does not exist in the target and is not part of this transfer."],
                    $"Include component '{comp?.Id}' in the transfer (--components) or transfer its prerequisite commits first.");
        }
        foreach (var asm in newN.AssemblyReferences.Except(oldN?.AssemblyReferences ?? [], StringComparer.OrdinalIgnoreCase))
        {
            if (srcGraph.AssemblyProducers.TryGetValue(asm, out var producer))
            {
                var cls = _srcCls.Classify(producer);
                if (cls.Class is PathClass.ProductSpecific or PathClass.Unknown)
                    yield return Block("NEW_DEP_ON_PRODUCT_SPECIFIC", "Shared project gains an assembly reference to a product-specific project", ch, "dependency",
                        [$"{p.SourcePath} adds <Reference Include=\"{asm}\"> produced by {producer} ({cls})."], "Remove the reference or reclassify the producing project.");
                else if (!tgt.AssemblyProducers.ContainsKey(asm) && !included.Any(i => i.Path.SourcePath == producer))
                    yield return Block("MISSING_ASSEMBLY_PRODUCER", "Referenced assembly has no producing project in the target", ch, "dependency",
                        [$"{p.SourcePath} references assembly {asm} (source producer {producer}); no target project produces it."], "Transfer the producing project first.");
            }
            else
                yield return Warn("NEW_BINARY_REFERENCE", "New binary assembly reference", ch, "dependency",
                    $"{p.SourcePath} adds <Reference Include=\"{asm}\"> with no producing project in the source repo (prebuilt binary, e.g. lib/).",
                    "Make sure the binary exists in the target (lib/ is generated content - regenerate it in the target).");
        }
        foreach (var (pkg, ver) in newN.Packages)
        {
            bool added = oldN is null || !oldN.Packages.ContainsKey(pkg);
            if (ver is null && added && !tgt.CentralPackageVersions.ContainsKey(pkg) &&
                !included.Any(i => Path.GetFileName(i.Path.SourcePath).Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase)))
                yield return Block("MISSING_PACKAGE_VERSION", "Package added without a central version in the target", ch, "dependency",
                    [$"{p.SourcePath} adds PackageReference {pkg}; target Directory.Packages.props has no PackageVersion for it."],
                    "Include the Directory.Packages.props change (or add the version in the target) before transferring.");
            var srcVer = ver ?? srcGraph.CentralPackageVersions.GetValueOrDefault(pkg);
            var tgtVer = tgt.CentralPackageVersions.GetValueOrDefault(pkg);
            if (srcVer is not null && tgtVer is not null && srcVer != tgtVer)
                yield return Warn("PACKAGE_VERSION_MISMATCH", "Package version differs between source and target", ch, "dependency",
                    $"{pkg}: source {srcVer}, target {tgtVer}.", "Confirm the shared code compiles against the target's version (consumer build).");
        }
        if (oldN is not null && !oldN.TargetFrameworks.SequenceEqual(newN.TargetFrameworks))
            yield return Warn("TFM_CHANGED", "Target framework changed", ch, "toolchain",
                $"{p.SourcePath}: {string.Join(';', oldN.TargetFrameworks)} -> {string.Join(';', newN.TargetFrameworks)}.",
                "Check every target consumer supports the new framework and the pinned SDK can build it.");
    }

    private IEnumerable<Finding> AnalyzeCentralPackages(AnalyzedChange ch, byte[]? oldBytes, byte[] newBytes, DependencyGraph tgt)
    {
        var oldV = oldBytes is null ? new() : DependencyGraph.ParseCentralPackages(oldBytes);
        var newV = DependencyGraph.ParseCentralPackages(newBytes);
        var changed = newV.Where(kv => !oldV.TryGetValue(kv.Key, out var o) || o != kv.Value).ToList();
        if (changed.Count == 0) yield break;
        yield return new Finding
        {
            Id = Finding.MakeId("CENTRAL_PACKAGES_CHANGED", ch.SourceCommit), Code = "CENTRAL_PACKAGES_CHANGED", Category = "dependency",
            Severity = Severity.Warning, Title = "Central package versions change for every target project", Component = ch.Path.Component,
            Files = [ch.Path.SourcePath], Commits = [ch.SourceCommit], RequiresReview = true,
            Evidence = changed.Select(kv => $"{kv.Key}: target {tgt.CentralPackageVersions.GetValueOrDefault(kv.Key) ?? "<none>"} -> {kv.Value}").ToList(),
            SuggestedAction = "Accept explicitly only after the full consumer build/test profile passes.",
        };
    }

    private async Task<IEnumerable<Finding>> AnalyzePackageJsonAsync(AnalyzedChange ch, byte[]? oldBytes, byte[] newBytes, string targetBase, List<AnalyzedChange> included)
    {
        var list = new List<Finding>();
        var oldN = oldBytes is null ? null : DependencyGraph.ParsePackageJson(ch.Path.SourcePath, oldBytes);
        var newN = DependencyGraph.ParsePackageJson(ch.Path.SourcePath, newBytes);
        var tgtBytes = await _git.ReadBlobAsync(_mirror, targetBase, ch.Path.TargetPath!);
        var tgtN = tgtBytes is null ? null : DependencyGraph.ParsePackageJson(ch.Path.TargetPath!, tgtBytes);
        if (newN is null) return list;
        var addedOrChanged = newN.Packages.Where(kv => oldN is null || !oldN.Packages.TryGetValue(kv.Key, out var o) || o != kv.Value).ToList();
        foreach (var (pkg, ver) in addedOrChanged)
        {
            var tv = tgtN?.Packages.GetValueOrDefault(pkg);
            if (pkg.StartsWith('('))
                list.Add(Warn("TOOLCHAIN_MISMATCH", "Package manager / engine declaration changed", ch, "toolchain",
                    $"{pkg}: target {tv ?? "<none>"} -> {ver}", "Validation must use the declared package manager version."));
            else if (tv is not null && tv != ver)
                list.Add(Warn("PACKAGE_VERSION_MISMATCH", "npm dependency version differs", ch, "dependency", $"{pkg}: target {tv}, transferred {ver}", "Run install with the frozen lockfile and the production build."));
        }
        var dir = Path.GetDirectoryName(ch.Path.TargetPath!)?.Replace('\\', '/') ?? "";
        var lockNames = new[] { "package-lock.json", "yarn.lock", "pnpm-lock.yaml" };
        bool lockChanged = included.Any(i => lockNames.Contains(Path.GetFileName(i.Path.TargetPath ?? "")) &&
                                             (Path.GetDirectoryName(i.Path.TargetPath!)?.Replace('\\', '/') ?? "") == dir);
        if (addedOrChanged.Any(kv => !kv.Key.StartsWith('(')) && !lockChanged)
        {
            bool targetHasLock = false;
            foreach (var l in lockNames) targetHasLock |= await _git.ObjectExistsAsync(_mirror, $"{targetBase}:{(dir.Length > 0 ? dir + "/" : "")}{l}");
            if (targetHasLock)
                list.Add(Block("LOCKFILE_OUT_OF_SYNC", "Dependencies change but the target lockfile is not updated by this transfer", ch, "dependency",
                    [$"{ch.Path.TargetPath} dependencies change; the lockfile next to it is not part of the transfer."],
                    "Transfer the matching lockfile change, or update the lockfile in the target in a reviewed commit. ForkBridge will not rewrite lockfiles."));
        }
        return list;
    }

    private async Task<IEnumerable<Finding>> AnalyzeCSharpSurfaceAsync(AnalyzedChange ch, byte[] oldBytes, byte[] newBytes, string targetBase)
    {
        var before = CodeSurface.CSharpPublicMembers(DependencyGraph.DecodeText(oldBytes));
        var after = CodeSurface.CSharpPublicMembers(DependencyGraph.DecodeText(newBytes));
        var removed = before.Except(after).ToList();
        if (removed.Count == 0) return [];
        var types = CodeSurface.CSharpTypeNames(before).ToList();
        var consumers = await ProductConsumersByNameAsync(types, targetBase, ["*.cs", "*.cshtml"]);
        return [new Finding
        {
            Id = Finding.MakeId("PUBLIC_API_CHANGED", ch.SourceCommit, ch.Path.SourcePath), Code = "PUBLIC_API_CHANGED", Category = "api",
            Severity = Severity.Warning, Title = "Public C# declarations removed or changed", Component = ch.Path.Component,
            Files = [ch.Path.SourcePath, .. consumers.Take(25)], Commits = [ch.SourceCommit],
            Evidence = [.. removed.Take(20).Select(r => "removed/changed: " + r),
                        consumers.Count > 0 ? $"{consumers.Count} target product file(s) mention the declaring type(s): {string.Join(", ", consumers.Take(10))}" : "No target product file mentions the declaring type(s) by name."],
            SuggestedAction = "The affected-consumer build decides compatibility; review the listed consumers.",
        }];
    }

    private async Task<IEnumerable<Finding>> AnalyzeTypeScriptAsync(AnalyzedChange ch, byte[]? oldBytes, byte[] newBytes, string targetBase,
        DependencyGraph tgt, Func<string, bool> existsAfter)
    {
        var list = new List<Finding>();
        var newCode = DependencyGraph.DecodeText(newBytes);
        var oldCode = oldBytes is null ? "" : DependencyGraph.DecodeText(oldBytes);
        var srcDir = Path.GetDirectoryName(ch.Path.SourcePath)?.Replace('\\', '/') ?? "";
        foreach (var spec in CodeSurface.TsImports(newCode).Except(CodeSurface.TsImports(oldCode)))
        {
            if (spec.StartsWith('.'))
            {
                var baseSrc = DependencyGraph.NormalizeRelative(srcDir, spec);
                var srcCandidates = new[] { baseSrc, baseSrc + ".ts", baseSrc + ".tsx", baseSrc + "/index.ts", baseSrc + ".js" };
                var srcFile = srcCandidates.FirstOrDefault(c => _srcCls.Classify(c).Class != PathClass.Unknown || true);
                var cls = _srcCls.Classify(baseSrc);
                var comp = _srcCls.ComponentOf(baseSrc);
                if (cls.Class == PathClass.ProductSpecific)
                    list.Add(Block("NEW_DEP_ON_PRODUCT_SPECIFIC", "Shared TypeScript imports product-specific code", ch, "dependency",
                        [$"{ch.Path.SourcePath} imports '{spec}' -> {baseSrc} ({cls})."], "Move the dependency into a shared component or remove the import."));
                else if (comp is not null)
                {
                    var mappedBase = PathClassifier.MapPath(comp, _from, _to, baseSrc)!;
                    var tgtCandidates = new[] { mappedBase, mappedBase + ".ts", mappedBase + ".tsx", mappedBase + "/index.ts", mappedBase + ".js" };
                    if (!tgtCandidates.Any(existsAfter))
                        list.Add(Block("MISSING_IMPORT_TARGET", "Imported module is missing in the target", ch, "dependency",
                            [$"{ch.Path.SourcePath} imports '{spec}'; none of {string.Join(", ", tgtCandidates)} exists in the target or this transfer."],
                            "Transfer the prerequisite change that adds the module first."));
                }
                else if (cls.Class == PathClass.Unknown)
                    list.Add(Block("NEW_DEP_UNCLASSIFIED", "TypeScript import of an unclassified path", ch, "dependency",
                        [$"{ch.Path.SourcePath} imports '{spec}' -> {baseSrc} (unclassified)."], "Classify the imported path."));
            }
            else
            {
                var alias = tgt.TsPathAliases.SelectMany(t => t.Value).FirstOrDefault(a => MatchesAlias(a.Key, spec));
                if (alias.Key is not null)
                {
                    var resolved = alias.Value.Replace("*", spec[(alias.Key.TrimEnd('*').Length)..]);
                    var tcls = _tgtCls.Classify(resolved);
                    if (tcls.Class == PathClass.ProductSpecific)
                        list.Add(Block("NEW_DEP_ON_PRODUCT_SPECIFIC", "Shared TypeScript imports a target product-specific alias", ch, "dependency",
                            [$"{ch.Path.SourcePath} imports '{spec}', which the target tsconfig maps to {resolved} ({tcls})."], "Remove the import from shared code."));
                }
            }
        }
        var removedExports = CodeSurface.TsExports(oldCode).Except(CodeSurface.TsExports(newCode)).ToList();
        if (removedExports.Count > 0)
        {
            var consumers = await ProductConsumersByNameAsync(removedExports, targetBase, ["*.ts", "*.html"]);
            list.Add(new Finding
            {
                Id = Finding.MakeId("EXPORT_REMOVED", ch.SourceCommit, ch.Path.SourcePath), Code = "EXPORT_REMOVED", Category = "api",
                Severity = Severity.Warning, Title = "TypeScript exports removed or renamed", Component = ch.Path.Component,
                Files = [ch.Path.SourcePath, .. consumers.Take(25)], Commits = [ch.SourceCommit],
                Evidence = [$"removed exports: {string.Join(", ", removedExports)}",
                            consumers.Count > 0 ? $"{consumers.Count} target product file(s) reference these names" : "No target product file references these names."],
                SuggestedAction = "Type checking and the production build of the target apps decide compatibility.",
            });
        }
        var oldSel = Regex.Match(oldCode, @"selector\s*:\s*['""]([^'""]+)['""]");
        var newSel = Regex.Match(newCode, @"selector\s*:\s*['""]([^'""]+)['""]");
        if (oldSel.Success && (!newSel.Success || newSel.Groups[1].Value != oldSel.Groups[1].Value))
        {
            var users = await ProductConsumersByNameAsync([oldSel.Groups[1].Value], targetBase, ["*.html", "*.ts"], wordMatch: false);
            list.Add(Warn("ANGULAR_SELECTOR_CHANGED", "Angular component selector changed", ch, "api",
                $"selector '{oldSel.Groups[1].Value}' -> '{(newSel.Success ? newSel.Groups[1].Value : "<removed>")}'; used in {users.Count} target product template(s): {string.Join(", ", users.Take(10))}",
                "Update the templates in the target or keep the selector."));
        }
        return list;
    }

    /// <summary>A deleted shared file removes its whole public surface; report target consumers.</summary>
    private async Task<IEnumerable<Finding>> AnalyzeDeletionAsync(AnalyzedChange ch, string targetBase)
    {
        var p = ch.Path;
        var oldBytes = await _git.ReadBlobAsync(_mirror, ch.SourceParent, p.SourcePath);
        if (oldBytes is null) return [];
        var code = DependencyGraph.DecodeText(oldBytes);
        List<string> names; string[] globs; string code2;
        if (p.SourcePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        { names = CodeSurface.CSharpTypeNames(CodeSurface.CSharpPublicMembers(code)).ToList(); globs = ["*.cs", "*.cshtml"]; code2 = "PUBLIC_API_CHANGED"; }
        else if (Regex.IsMatch(p.SourcePath, @"\.(ts|tsx|mts)$"))
        { names = CodeSurface.TsExports(code).ToList(); globs = ["*.ts", "*.html"]; code2 = "EXPORT_REMOVED"; }
        else if (p.SourcePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return [Warn("PROJECT_DELETED", "Shared project deleted", ch, "dependency", $"{p.TargetPath} is removed; target consumers referencing it will break.", "The consumer build decides; review referencing projects.")];
        else return [];
        if (names.Count == 0) return [];
        var consumers = await ProductConsumersByNameAsync(names, targetBase, globs);
        var list = new List<Finding>
        {
            new()
            {
                Id = Finding.MakeId(code2, ch.SourceCommit, p.SourcePath, "deleted"), Code = code2, Category = "api", Severity = Severity.Warning,
                Title = "Shared file deleted: its public surface disappears", Component = p.Component, Files = [p.SourcePath, .. consumers.Take(25)],
                Commits = [ch.SourceCommit], SuggestedAction = "The consumer build/type check decides; review the listed consumers.",
                Evidence = [$"deleted {p.TargetPath} declared: {string.Join(", ", names.Take(20))}",
                            consumers.Count > 0 ? $"{consumers.Count} target product file(s) reference these names: {string.Join(", ", consumers.Take(10))}" : "No target product file references these names."],
            },
        };
        var sel = Regex.Match(code, @"selector\s*:\s*['""]([^'""]+)['""]");
        if (sel.Success)
        {
            var users = await ProductConsumersByNameAsync([sel.Groups[1].Value], targetBase, ["*.html", "*.ts"], wordMatch: false);
            list.Add(Warn("ANGULAR_SELECTOR_CHANGED", "Angular component deleted", ch, "api",
                $"selector '{sel.Groups[1].Value}' removed; used in {users.Count} target product template(s): {string.Join(", ", users.Take(10))}", "Update the templates in the target."));
        }
        return list;
    }

    private static bool MatchesAlias(string alias, string spec)
        => alias.EndsWith('*') ? spec.StartsWith(alias.TrimEnd('*'), StringComparison.Ordinal) : spec == alias;

    /// <summary>Files outside shared components in the target that mention any of the names.</summary>
    private async Task<List<string>> ProductConsumersByNameAsync(IEnumerable<string> names, string targetBase, string[] globs, bool wordMatch = true)
    {
        var nameList = names.Where(n => n.Length >= 3).Distinct().Take(30).ToList();
        if (nameList.Count == 0) return [];
        var args = new List<string> { "grep", "-l", "-I", "-F" };
        if (wordMatch) args.Add("-w");
        foreach (var n in nameList) { args.Add("-e"); args.Add(n); }
        args.Add(targetBase); args.Add("--"); args.AddRange(globs);
        var r = await _git.RunAsync(_mirror, args, timeout: TimeSpan.FromMinutes(2));
        if (!r.Success) return [];
        return GitClient.SplitLines(r.StdOut)
            .Select(l => l.StartsWith(targetBase + ":") ? l[(targetBase.Length + 1)..] : l)
            .Where(f => _tgtCls.Classify(f).Class is not PathClass.Shared and not PathClass.Generated)
            .ToList();
    }

    /// <summary>An included change is only a cross-boundary problem when it introduces a reference
    /// (absent from the target's current version of that file) to a type that an excluded file of the
    /// same commit declares AND that the target base does not already declare anywhere. Without those
    /// two filters a reconcile (one synthetic commit spanning the whole repo) matches common type names
    /// such as Entity/Startup against hundreds of unrelated excluded files.</summary>
    private async Task<IEnumerable<Finding>> CrossBoundaryAsync(List<AnalyzedChange> included, List<AnalyzedChange> excluded, string targetBase)
    {
        var list = new List<Finding>();
        foreach (var group in excluded.Where(e => e.Path.Decision == Decision.Exclude && e.Path.Status is 'A' or 'M')
                     .GroupBy(e => e.SourceCommit))
        {
            var inc = included.Where(i => i.SourceCommit == group.Key && i.Path.Status != 'D').ToList();
            if (inc.Count == 0) continue;
            var declaredIn = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var e in group)
            {
                var bytes = await _git.ReadBlobAsync(_mirror, e.SourceCommit, e.Path.SourcePath);
                if (bytes is null) continue;
                var code = DependencyGraph.DecodeText(bytes);
                IEnumerable<string> names = e.Path.SourcePath.EndsWith(".cs") ? CodeSurface.CSharpTypeNames(CodeSurface.CSharpPublicMembers(code))
                    : Regex.IsMatch(e.Path.SourcePath, @"\.tsx?$") ? CodeSurface.TsExports(code) : [];
                foreach (var n in names.Where(n => n.Length >= 3))
                    (declaredIn.TryGetValue(n, out var l) ? l : declaredIn[n] = []).Add(e.Path.SourcePath);
            }
            if (declaredIn.Count == 0) continue;

            var candidates = new Dictionary<AnalyzedChange, List<string>>();
            foreach (var i in inc)
            {
                var bytes = await _git.ReadBlobAsync(_mirror, i.SourceCommit, i.Path.SourcePath);
                if (bytes is null) continue;
                var code = CodeSurface.CodeOnly(DependencyGraph.DecodeText(bytes));
                var tgtBytes = i.Path.TargetPath is null ? null : await _git.ReadBlobAsync(_mirror, targetBase, i.Path.TargetPath);
                var tgtCode = tgtBytes is null ? "" : CodeSurface.CodeOnly(DependencyGraph.DecodeText(tgtBytes));
                var hits = declaredIn.Keys.Where(n => Referenced(code, n) && !Referenced(tgtCode, n)).ToList();
                if (hits.Count > 0) candidates[i] = hits;
            }
            if (candidates.Count == 0) continue;

            var alreadyInTarget = await DeclaredInTargetAsync(candidates.Values.SelectMany(h => h).Distinct().ToList(), targetBase);
            foreach (var (i, all) in candidates)
            {
                var hits = all.Where(n => !alreadyInTarget.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
                if (hits.Count == 0) continue;
                var decl = hits.SelectMany(n => declaredIn[n]).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
                list.Add(new Finding
                {
                    Id = Finding.MakeId("CROSS_BOUNDARY_DEPENDENCY", i.SourceCommit, i.Path.SourcePath), Code = "CROSS_BOUNDARY_DEPENDENCY",
                    Category = "mixed-commit", Severity = Severity.Error, Blocking = true, Component = i.Path.Component,
                    Title = "Included shared change uses code that this transfer excludes",
                    Files = [i.Path.SourcePath, .. decl], Commits = [i.SourceCommit],
                    Evidence = [$"{i.Path.SourcePath} newly references {string.Join(", ", hits)}; not declared anywhere in the target base, declared only in excluded file(s): {string.Join(", ", decl)}."],
                    SuggestedAction = "Select the component that declares it, split the source commit, or move the declaration into the shared component.",
                });
            }
        }
        return list;

        // A type-reference position, not a member: excludes instance-member access (`entry.X`, `this.X`, `Get()?.X`,
        // `a[0].X`), member declarations/assignments (`int? X { get; }`, `X = 1;`, `X => ...`) and calls (`X(...)`
        // unless `new X(...)`). PascalCase-qualified (`Vam.VinHelper`) and bare type usages remain candidates.
        static bool Referenced(string code, string name) => code.Length > 0 && Regex.IsMatch(code,
            $@"(?<!\w)(?<!(?<!\w)[a-z_]\w*\s*\??\.\s*)(?<![)\]]\s*\??\.\s*){Regex.Escape(name)}(?!\w)(?!\s*(?:=(?!=)|\{{|;))(?:(?!\s*\()|(?<=\bnew\s+{Regex.Escape(name)}))");
    }

    /// <summary>Names from <paramref name="names"/> that the target base already declares (C# type or TS export).</summary>
    private async Task<HashSet<string>> DeclaredInTargetAsync(List<string> names, string targetBase)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in names.Chunk(40))
        {
            var alt = string.Join("|", chunk.Select(Regex.Escape));
            var args = new List<string> { "grep", "-h", "-o", "-I", "-E",
                "-e", $"(class|interface|struct|record|enum|type|function|const|let)[[:space:]]+({alt})([^A-Za-z0-9_$]|$)",
                targetBase, "--", "*.cs", "*.ts", "*.tsx" };
            var r = await _git.RunAsync(_mirror, args, timeout: TimeSpan.FromMinutes(2));
            foreach (var line in GitClient.SplitLines(r.StdOut))
            {
                var m = Regex.Match(line, @"\s+(?<n>[A-Za-z_$][\w$]*)");
                if (m.Success && chunk.Contains(m.Groups["n"].Value)) found.Add(m.Groups["n"].Value);
            }
        }
        return found;
    }

    private DependencyImpact BuildImpact(List<AnalyzedChange> included, DependencyGraph tgt)
    {
        var impact = new DependencyImpact();
        var owners = new Dictionary<string, ProjectNode>();
        foreach (var c in included)
        {
            var path = c.Path.TargetPath!;
            var owner = tgt.OwnerOf(path, "dotnet") ?? tgt.OwnerOf(path, "npm");
            if (owner is not null) owners[owner.Path] = owner;
            else if (path.EndsWith(".csproj")) impact.Notes.Add($"New project in target: {path}");
        }
        impact.AffectedProjects.AddRange(owners.Keys.OrderBy(x => x));
        foreach (var c in tgt.TransitiveConsumers(owners.Values.Where(o => o.Kind == "dotnet")).OrderBy(x => x.Path))
        {
            var cls = _tgtCls.Classify(c.Path);
            if (cls.Class == PathClass.Shared) impact.SharedConsumers.Add(c.Path); else impact.ProductConsumers.Add($"{c.Path} ({cls.Class})");
        }
        return impact;
    }

    private async Task<List<string>> FrontendConsumersAsync(List<AnalyzedChange> included, string targetBase)
    {
        var tsFiles = included.Where(i => Regex.IsMatch(i.Path.TargetPath ?? "", @"\.tsx?$") && !i.Path.TargetPath!.EndsWith(".spec.ts")).Take(50).ToList();
        if (tsFiles.Count == 0) return [];
        var stems = tsFiles.Select(f => Path.GetFileNameWithoutExtension(f.Path.TargetPath!)).Where(s => s.Length >= 3).Distinct().ToList();
        var hits = await ProductConsumersByNameAsync(stems, targetBase, ["*.ts"], wordMatch: false);
        return hits.Select(h => h + " (imports a changed module by name - verify)").Take(100).ToList();
    }

    private static Finding Block(string code, string title, AnalyzedChange ch, string category, List<string> evidence, string action) => new()
    {
        Id = Finding.MakeId(code, ch.SourceCommit, ch.Path.SourcePath, string.Join("|", evidence)), Code = code, Category = category,
        Severity = Severity.Error, Title = title, Component = ch.Path.Component, Files = [ch.Path.SourcePath], Commits = [ch.SourceCommit],
        Evidence = evidence, Blocking = true, SuggestedAction = action,
    };

    private static Finding Warn(string code, string title, AnalyzedChange ch, string category, string evidence, string action) => new()
    {
        Id = Finding.MakeId(code, ch.SourceCommit, ch.Path.SourcePath, evidence), Code = code, Category = category,
        Severity = Severity.Warning, Title = title, Component = ch.Path.Component, Files = [ch.Path.SourcePath], Commits = [ch.SourceCommit],
        Evidence = [evidence], SuggestedAction = action,
    };
}
