using ForkBridge.Core.Config;
using ForkBridge.Core.Git;
using ForkBridge.Core.Model;
using ForkBridge.Core.Policy;

namespace ForkBridge.Core.Planning;

/// <summary>Content comparison of shared components between two pinned snapshots, independent of
/// ancestry. Blob ids are compared, so identical bytes are recognised regardless of history.</summary>
public static class DriftAnalyzer
{
    public static async Task<List<ComponentDrift>> AnalyzeAsync(GitClient git, string mirror, PairConfig pair, string from, string to,
        string sourceSnapshot, string targetSnapshot, string? baseline, IEnumerable<ComponentConfig> components)
    {
        var result = new List<ComponentDrift>();
        var srcCls = new PathClassifier(pair, from, asTarget: false);
        var tgtCls = new PathClassifier(pair, to, asTarget: true);
        foreach (var c in components)
        {
            var sp = c.Paths.GetValueOrDefault(from);
            var tp = c.Paths.GetValueOrDefault(to);
            if (string.IsNullOrWhiteSpace(sp) || string.IsNullOrWhiteSpace(tp))
            {
                result.Add(new ComponentDrift
                {
                    Component = c.Id, SourcePath = sp ?? "", TargetPath = tp ?? "",
                    IntentionallyAbsentInTarget = c.Absent.ContainsKey(to), PresentInSource = !string.IsNullOrWhiteSpace(sp),
                });
                continue;
            }
            sp = Glob.Normalize(sp); tp = Glob.Normalize(tp);
            var srcFiles = await Blobs(git, mirror, sourceSnapshot, sp);
            var tgtFiles = await Blobs(git, mirror, targetSnapshot, tp);
            var baseSrc = baseline is null ? new Dictionary<string, string>() : await Blobs(git, mirror, baseline, sp);

            int identical = 0;
            var differing = new List<string>(); var onlySrc = new List<string>(); var onlyTgt = new List<string>();
            var tgtDiverged = new List<string>(); var srcChanged = new List<string>(); var preserved = new List<string>();
            var mappedSrc = new Dictionary<string, (string srcPath, string blob)>();
            foreach (var (path, blob) in srcFiles)
            {
                var sc = srcCls.Classify(path);
                if (sc.Class != PathClass.Shared || sc.ComponentId != c.Id) continue; // nested product-specific/generated/ignored
                mappedSrc[PathClassifier.MapPath(c, from, to, path)!] = (path, blob);
            }
            foreach (var (tpath, (spath, sblob)) in mappedSrc)
            {
                if (!tgtFiles.TryGetValue(tpath, out var tblob)) { onlySrc.Add(tpath); continue; }
                if (tblob == sblob) { identical++; continue; }
                var tc = tgtCls.Classify(tpath);
                if (tc.Class is PathClass.TargetPinned or PathClass.ProductSpecific) { preserved.Add($"{tpath} ({tc.Class})"); continue; }
                differing.Add(tpath);
                var bs = baseSrc.GetValueOrDefault(spath);
                if (bs is not null && bs != sblob) srcChanged.Add(tpath);
                // Target divergence is decided by history, not by blob comparison with the baseline:
                // the target diverged only if it last changed the file AFTER integrating the baseline.
                // (After a filtered merge the baseline is the source head, while the target still
                // carries pre-integration content - that is missing content, not divergence.)
                if (baseline is not null)
                {
                    var last = (await git.RunAsync(mirror, ["log", "-1", "--format=%H", targetSnapshot, "--", tpath])).StdOut.Trim();
                    if (last.Length > 0 && last != baseline && await git.IsAncestorAsync(mirror, baseline, last)) tgtDiverged.Add(tpath);
                }
            }
            foreach (var tpath in tgtFiles.Keys)
            {
                var tc = tgtCls.Classify(tpath);
                if (mappedSrc.ContainsKey(tpath)) continue;
                if (tc.Class is PathClass.TargetPinned or PathClass.ProductSpecific) { preserved.Add($"{tpath} ({tc.Class})"); continue; }
                if (tc.Class == PathClass.Shared && tc.ComponentId == c.Id) onlyTgt.Add(tpath);
            }
            result.Add(new ComponentDrift
            {
                Component = c.Id, SourcePath = sp, TargetPath = tp, PresentInSource = srcFiles.Count > 0, PresentInTarget = tgtFiles.Count > 0,
                Identical = identical, Differing = differing, OnlyInSource = onlySrc, OnlyInTarget = onlyTgt,
                TargetDivergedSinceBaseline = tgtDiverged, SourceChangedSinceBaseline = srcChanged, PreservedProductDifferences = preserved,
                Baseline = baseline,
            });
        }
        return result;
    }

    private static async Task<Dictionary<string, string>> Blobs(GitClient git, string mirror, string treeish, string path)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in await git.LsTreeAsync(mirror, treeish, path))
            if (e.Type == "blob" && (e.Path == path || e.Path.StartsWith(path + "/", StringComparison.Ordinal))) d[e.Path] = e.Sha;
        return d;
    }

    public static IEnumerable<Finding> Findings(List<ComponentDrift> drift, string sourceHead, string targetBase, string? mergeBase)
    {
        foreach (var d in drift)
        {
            if (d.TargetDivergedSinceBaseline.Count > 0)
                yield return new Finding
                {
                    Id = Finding.MakeId("PREEXISTING_DIVERGENCE", d.Component, string.Join(",", d.TargetDivergedSinceBaseline)), Code = "PREEXISTING_DIVERGENCE",
                    Category = "drift", Severity = Severity.Info, Origin = Origin.PreExisting, Component = d.Component,
                    Files = d.TargetDivergedSinceBaseline.Take(50).ToList(),
                    Title = $"{d.TargetDivergedSinceBaseline.Count} shared file(s) in '{d.Component}' were changed in the target since the baseline",
                    Evidence = ["These differences existed before this transfer; three-way application preserves them unless a hunk overlaps (then it conflicts - never auto-resolved)."],
                };
            // Ancestry says everything is merged, but content still differs on files the target never changed.
            if (mergeBase is not null && mergeBase == sourceHead)
            {
                var suspicious = d.Differing.Except(d.TargetDivergedSinceBaseline).ToList();
                if (suspicious.Count > 0)
                    yield return new Finding
                    {
                        Id = Finding.MakeId("ANCESTRY_CONTENT_MISMATCH", d.Component, sourceHead, targetBase), Code = "ANCESTRY_CONTENT_MISMATCH",
                        Category = "drift", Severity = Severity.Warning, Origin = Origin.PreExisting, Component = d.Component, RequiresReview = true,
                        Files = suspicious.Take(50).ToList(),
                        Title = "Source is an ancestor of the target, yet shared content differs",
                        Evidence = [$"merge-base(source, target) = source head {sourceHead[..10]}: ancestry claims full integration.",
                                    $"{suspicious.Count} shared file(s) still differ - typical after a filtered/pinned fork-over merge discarded content."],
                        SuggestedAction = "Run --mode reconcile with an explicit --baseline from before the filtered merge to restore the missing shared content.",
                    };
            }
            if (d.OnlyInTarget.Count > 0)
                yield return new Finding
                {
                    Id = Finding.MakeId("TARGET_ONLY_SHARED_FILES", d.Component, string.Join(",", d.OnlyInTarget)), Code = "TARGET_ONLY_SHARED_FILES",
                    Category = "drift", Severity = Severity.Info, Origin = Origin.PreExisting, Component = d.Component, Files = d.OnlyInTarget.Take(50).ToList(),
                    Title = $"{d.OnlyInTarget.Count} file(s) exist only in the target copy of '{d.Component}'",
                    Evidence = ["Not touched by this transfer; consider classifying them as product-specific if intentional."],
                };
        }
    }
}
