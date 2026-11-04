using ForkBridge.Core.Config;
using ForkBridge.Core.Git;
using ForkBridge.Core.Infrastructure;
using ForkBridge.Core.Storage;
using ForkBridge.Merge.Git;
using ForkBridge.Merge.Model;
using ForkBridge.Merge.Storage;

namespace ForkBridge.Merge.Planning;

/// <summary>Produces an immutable merge plan for "source INTO target" at pinned commits. Git decides everything:
/// merge bases (including several), fast-forward/no-op, and the merge itself via `merge-tree --write-tree -z`,
/// which writes objects into the harness mirror but touches no worktree or index. Exit code 1 means conflicts;
/// any other non-zero exit is an invocation failure and is reported as an error, never as a conflict.</summary>
public sealed class MergeAnalyzer
{
    private readonly ForkBridgeConfig _cfg;
    private readonly MergeGit _git;
    private readonly MergeStore _store;
    private readonly RefCatalog _refs;

    public MergeAnalyzer(ForkBridgeConfig cfg, MergeGit git, MergeStore store, RefCatalog refs)
    {
        _cfg = cfg; _git = git; _store = store; _refs = refs;
    }

    public async Task<MergePlan> AnalyzeAsync(string alias, RefSelection source, RefSelection target, CancellationToken ct = default)
    {
        await _git.EnsureSupportedVersionAsync();
        _refs.Repo(alias);
        var gitVersion = (await _git.VersionAsync()).ToString();
        var mirror = _store.Core.MirrorDir(alias);
        var src = await _refs.ResolveAsync(alias, source);
        var tgt = await _refs.ResolveAsync(alias, target);
        var fresh = _store.LoadFreshness(alias, RefCatalog.StaleAfter);
        var fmt = await _git.RunAsync(mirror, ["rev-parse", "--show-object-format"]);
        var findings = new List<MergeFinding>();
        if (fresh.Stale)
            findings.Add(new MergeFinding("REFS_STALE", "warning", fresh.Offline
                ? "Refs were refreshed offline: remote branches may be out of date."
                : fresh.RefreshedAt is null ? "Refs have never been refreshed." : $"Refs were last refreshed {fresh.RefreshedAt:u}; refresh to see newer commits."));
        if (src.Namespace == RefNamespace.Local || tgt.Namespace == RefNamespace.Local)
            findings.Add(new MergeFinding("LOCAL_BRANCH", "info", "A local branch of your checkout was selected; it may contain commits that are not on the remote."));

        var bases = await MergeBasesAsync(mirror, tgt.Sha, src.Sha, ct);
        int sourceOnly = 0, targetOnly = 0;
        if (bases.Count > 0)
        {
            sourceOnly = int.Parse(await _git.OutAsync(mirror, "rev-list", "--count", $"{tgt.Sha}..{src.Sha}"));
            targetOnly = int.Parse(await _git.OutAsync(mirror, "rev-list", "--count", $"{src.Sha}..{tgt.Sha}"));
        }
        if (bases.Count > 1)
            findings.Add(new MergeFinding("MULTIPLE_MERGE_BASES", "info",
                $"{bases.Count} merge bases (criss-cross history). Git merges them into a virtual base; per-file 'base' content shown for clean files is from merge base {bases[0][..10]}, for conflicted files it is Git's virtual base."));

        MergeOutcome outcome; string? blocked = null; string? tree = null;
        var entries = new List<MergeEntry>();
        if (bases.Count == 0)
        {
            outcome = MergeOutcome.Blocked;
            blocked = "The branches have no common history (unrelated histories). Merging them is refused by default; ForkBridge does not enable --allow-unrelated-histories.";
        }
        else if (sourceOnly == 0)
        {
            outcome = MergeOutcome.UpToDate;
            tree = await _git.OutAsync(mirror, "rev-parse", tgt.Sha + "^{tree}");
        }
        else if (targetOnly == 0)
        {
            outcome = MergeOutcome.FastForward;
            tree = await _git.OutAsync(mirror, "rev-parse", src.Sha + "^{tree}");
            entries = await CleanEntriesAsync(mirror, bases[0], tgt.Sha, src.Sha, tree, new HashSet<string>(), ct);
        }
        else
        {
            var r = await _git.RunAsync(mirror, ["merge-tree", "--write-tree", "-z", "--messages", tgt.Sha, src.Sha], ct: ct);
            if (r.ExitCode is not (0 or 1) || r.TimedOut || r.OutputTruncated)
                throw new MergeException("git merge-tree failed (this is not a merge conflict): " + Redactor.Redact(MergeGit.Trim(r.StdErr)), MergeErrorKind.GitFailure);
            var mt = MergeTreeParser.Parse(r.StdOutBytes, clean: r.ExitCode == 0);
            tree = mt.Tree;
            var conflicted = mt.Stages.Select(s => s.Path).ToHashSet(StringComparer.Ordinal);
            entries = await CleanEntriesAsync(mirror, bases[0], tgt.Sha, src.Sha, tree, conflicted, ct);
            entries.AddRange(ConflictEntries(mt, await _git.LsTreeAsync(mirror, tree, ct)));
            AttachMessages(entries, mt.Messages);
            outcome = mt.Clean && conflicted.Count == 0 ? MergeOutcome.Clean : MergeOutcome.Conflicts;
        }
        entries = entries.OrderBy(e => e.Path, StringComparer.Ordinal).ToList();

        if (tree is not null && outcome is not (MergeOutcome.UpToDate or MergeOutcome.Blocked))
        {
            var collisions = await CaseCollisionsAsync(mirror, tree, ct);
            if (collisions.Count > 0)
            {
                bool insensitive = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
                findings.Add(new MergeFinding("CASE_COLLISION", insensitive ? "blocking" : "warning",
                    "Paths in the merge result differ only by letter case; they cannot coexist on a case-insensitive filesystem.", collisions));
                if (insensitive) { outcome = MergeOutcome.Blocked; blocked = "The merge result contains paths that differ only by case (" + string.Join(", ", collisions.Take(4)) + "). This host's filesystem cannot represent them."; }
            }
        }

        var counts = new MergeCounts(sourceOnly, targetOnly, entries.Count,
            entries.Count(e => e.Status == EntryStatus.AutoMerged), entries.Count(e => e.Status == EntryStatus.Incoming),
            entries.Count(e => e.Status == EntryStatus.Conflicted), entries.Count(e => !e.Capabilities.Supported));
        if (counts.Unsupported > 0)
            findings.Add(new MergeFinding("UNSUPPORTED_CONFLICTS", "blocking",
                $"{counts.Unsupported} conflict(s) cannot be resolved in the browser; finalization stays blocked until they are gone (re-plan after resolving them elsewhere).",
                entries.Where(e => !e.Capabilities.Supported).Select(e => e.Path).ToList()));

        var plan = new MergePlan
        {
            Id = StateStore.NewId("mplan"), CreatedAt = DateTimeOffset.UtcNow, RepositoryAlias = alias,
            RemoteUrlRedacted = fresh.RemoteUrlRedacted, ObjectFormat = fmt.Success ? fmt.StdOut.Trim() : "sha1",
            Source = src, Target = tgt, Freshness = fresh, MergeBases = bases, Outcome = outcome, BlockedReason = blocked,
            ResultTree = tree, Counts = counts, Entries = entries, Findings = findings, GitVersion = gitVersion,
            MergeOptions = MergeGit.PinnedOptions.ToDictionary(k => k.Key, v => v.Value), OptionsFingerprint = MergeGit.OptionsFingerprint(gitVersion),
        };
        _store.SavePlan(plan);
        using (_store.Core.Lock("mirror-" + alias))
        {
            await _git.CheckedAsync(mirror, ["update-ref", $"refs/fb/merge/plans/{plan.Id}/source", src.Sha], ct: ct);
            await _git.CheckedAsync(mirror, ["update-ref", $"refs/fb/merge/plans/{plan.Id}/target", tgt.Sha], ct: ct);
        }
        return plan;
    }

    private async Task<List<string>> MergeBasesAsync(string mirror, string a, string b, CancellationToken ct)
    {
        var r = await _git.RunAsync(mirror, ["merge-base", "--all", a, b], ct: ct);
        if (r.ExitCode == 1 && r.StdOut.Trim().Length == 0) return [];
        if (!r.Success) throw new MergeException("git merge-base failed: " + MergeGit.Trim(r.StdErr), MergeErrorKind.GitFailure);
        return GitClient.SplitLines(r.StdOut).Select(x => x.Trim()).ToList();
    }

    /// <summary>Every path the merge changes in the target that Git merged without conflict.</summary>
    private async Task<List<MergeEntry>> CleanEntriesAsync(string mirror, string mergeBase, string target, string source, string resultTree,
        HashSet<string> conflicted, CancellationToken ct)
    {
        var toResult = await _git.Git.DiffTreeAsync(mirror, target, resultTree);
        var baseToTarget = await _git.Git.DiffTreeAsync(mirror, mergeBase, target);
        var baseToSource = await _git.Git.DiffTreeAsync(mirror, mergeBase, source);
        var tgtChanged = Index(baseToTarget);
        var srcChanged = Index(baseToSource);
        var list = new List<MergeEntry>();
        foreach (var c in toResult)
        {
            if (conflicted.Contains(c.Path) || (c.OldPath is not null && conflicted.Contains(c.OldPath))) continue;
            tgtChanged.TryGetValue(c.Path, out var tc);
            if (tc is null && c.OldPath is not null) tgtChanged.TryGetValue(c.OldPath, out tc);
            srcChanged.TryGetValue(c.Path, out var sc);
            var status = tc is not null && sc is not null ? EntryStatus.AutoMerged : EntryStatus.Incoming;
            list.Add(new MergeEntry
            {
                Id = EntryClassifier.EntryId(c.Path), Path = c.Path, OldPath = c.OldPath, Status = status,
                TargetChange = tc?.Status.ToString(), SourceChange = sc?.Status.ToString() ?? c.Status.ToString(),
                Base = sc is not null && !sc.IsAdd ? Blob(sc.OldMode, sc.OldBlob) : tc is not null && !tc.IsAdd ? Blob(tc.OldMode, tc.OldBlob) : null,
                Target = c.IsAdd ? null : Blob(c.OldMode, c.OldBlob),
                Source = sc is not null ? (sc.IsDelete ? null : Blob(sc.NewMode, sc.NewBlob)) : c.IsDelete ? null : Blob(c.NewMode, c.NewBlob),
                Result = c.IsDelete ? null : Blob(c.NewMode, c.NewBlob),
                Binary = c.IsBinary,
                Capabilities = EntryClassifier.CleanEntry,
            });
        }
        return list;
    }

    private static Dictionary<string, FileChange> Index(IReadOnlyList<FileChange> changes)
    {
        var d = new Dictionary<string, FileChange>(StringComparer.Ordinal);
        foreach (var c in changes) { d[c.Path] = c; if (c.OldPath is not null) d.TryAdd(c.OldPath, c); }
        return d;
    }

    private static SideBlob? Blob(string mode, string sha) => mode.Trim('0').Length == 0 || sha.Trim('0').Length == 0 ? null : new SideBlob(mode, sha);

    private static IEnumerable<MergeEntry> ConflictEntries(MergeTreeResult mt, Dictionary<string, (string Mode, string Sha, string Type)> resultTree)
    {
        foreach (var g in mt.Stages.GroupBy(s => s.Path, StringComparer.Ordinal))
        {
            var stages = g.ToList();
            var msgs = mt.Messages.Where(m => m.Paths.Contains(g.Key) && m.Type.StartsWith("CONFLICT", StringComparison.Ordinal)).ToList();
            var (kind, binary) = EntryClassifier.Kind(stages, msgs);
            SideBlob? At(int st) => stages.FirstOrDefault(s => s.Stage == st) is { } s ? new SideBlob(s.Mode, s.Sha) : null;
            var related = msgs.SelectMany(m => m.Paths).Where(p => p != g.Key).Distinct(StringComparer.Ordinal).ToList();
            yield return new MergeEntry
            {
                Id = EntryClassifier.EntryId(g.Key), Path = g.Key, OldPath = related.Count == 1 && kind is ConflictKind.RenameDelete or ConflictKind.RenameRename ? related[0] : null,
                Status = EntryStatus.Conflicted, Conflict = kind,
                Base = At(1), Target = At(2), Source = At(3),
                TargetChange = At(2) is null ? "D" : At(1) is null ? "A" : "M",
                SourceChange = At(3) is null ? "D" : At(1) is null ? "A" : "M",
                Result = resultTree.TryGetValue(g.Key, out var rt) ? new SideBlob(rt.Mode, rt.Sha) : null,
                Binary = binary, RelatedPaths = related,
                Capabilities = EntryClassifier.Capabilities(kind, binary, g.Key),
            };
        }
    }

    private static void AttachMessages(List<MergeEntry> entries, List<MergeMessage> messages)
    {
        var byPath = entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
        foreach (var m in messages)
            foreach (var p in m.Paths)
                if (byPath.TryGetValue(p, out var e) && !e.Messages.Contains(m.Text)) e.Messages.Add(m.Text);
    }

    private async Task<List<string>> CaseCollisionsAsync(string mirror, string tree, CancellationToken ct)
    {
        var all = await _git.LsTreeAsync(mirror, tree, ct);
        // Directories collide too: compare every prefix.
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hits = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var p in all.Keys)
        {
            var parts = p.Split('/');
            for (int i = 1; i <= parts.Length; i++)
            {
                var prefix = string.Join('/', parts.Take(i));
                if (seen.TryGetValue(prefix, out var other)) { if (other != prefix) { hits.Add(other); hits.Add(prefix); } }
                else seen[prefix] = prefix;
            }
        }
        return hits.ToList();
    }
}
