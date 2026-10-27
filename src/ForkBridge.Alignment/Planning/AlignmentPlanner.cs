using System.Text.RegularExpressions;
using ForkBridge.Alignment.Analysis;
using ForkBridge.Alignment.Config;
using ForkBridge.Alignment.Git;
using ForkBridge.Alignment.Manifests;
using ForkBridge.Alignment.Model;
using ForkBridge.Alignment.Text;
using ForkBridge.Core.Storage;

namespace ForkBridge.Alignment.Planning;

/// <summary>How each side's revision is chosen.</summary>
public sealed record SideSelection
{
    /// <summary>Explicit commit (sha or ref resolvable in the side's repository) - read-only replay, used by parity.</summary>
    public string? Rev { get; init; }
    /// <summary>A worktree (canonical path from the catalogue): its HEAD is planned and its scope must be clean.</summary>
    public string? WorktreePath { get; init; }
    /// <summary>Preview: base ref plus merge refs merged in memory (no branch needed).</summary>
    public bool Preview { get; init; }
}

public sealed record AlignmentPlanRequest(SideSelection Primary, SideSelection Fork, string? SyncBaseOverride = null, string? ParentPlanId = null);

public sealed class AlignmentPlanner(ResolvedProfile profile, AlignGit git, Action<string>? progress = null)
{
    private readonly AlignmentProfile _p = profile.Profile;
    private void Step(string s) => progress?.Invoke(s);

    public async Task<AlignmentPlan> PlanAsync(AlignmentPlanRequest req, CancellationToken ct = default)
    {
        await git.AssertVersionAsync();
        Directory.CreateDirectory(profile.StateDir);
        using var x = await ScratchRepo.OpenAsync(git, profile.StateDir, profile.PrimaryRoot, profile.ForkRoot, _p.Scope, _p.Protected, ct);
        var syncBase = req.SyncBaseOverride ?? _p.SyncBase;
        if (string.IsNullOrWhiteSpace(syncBase)) throw new AlignmentException("No SyncBase: set syncBase in the profile or pass one for this plan.", 2);
        var preview = req.Primary.Preview || req.Fork.Preview;

        Step("revisions");
        var prim = await ResolveSideAsync(x, Side.Primary, req.Primary, ct);
        var fork = await ResolveSideAsync(x, Side.Fork, req.Fork, ct);
        if (!await x.TestAsync("cat-file", "-e", syncBase + "^{commit}"))
            throw new AlignmentException($"SyncBase {syncBase} not found in either object store (fetch {_p.Primary.Label} first).", 2);
        syncBase = await x.TextAsync("rev-parse", "--verify", syncBase + "^{commit}");
        var histRevs = new List<string> { prim.CommitRev, syncBase };
        foreach (var h in _p.Primary.HistoryRefs.Where(h => !string.IsNullOrWhiteSpace(h)))
            histRevs.Add(await git.TextAsync(profile.PrimaryRoot, "rev-parse", "--verify", h + "^{commit}"));

        Step("scoped 3-way merge");
        var baseMap = await x.ScopedEntriesAsync(syncBase, ct);
        var forkMap = await x.ScopedEntriesAsync(fork.Rev, ct);
        var primMap = await x.ScopedEntriesAsync(prim.Rev, ct);
        var bT = await x.WriteTreeAsync(baseMap, ct: ct);
        var iT = await x.WriteTreeAsync(forkMap, ct: ct);
        var eT = await x.WriteTreeAsync(primMap, ct: ct);
        var (mT, conflicts) = await x.MergeAsync(bT, iT, eT, ct);
        var mrgMap = await x.ScopedEntriesAsync(mT, ct);

        // History of primary blobs per path. Chunk composition is replicated exactly per suite: git log path
        // simplification depends on the pathspec set, so the chunks must match the reference implementation.
        var hist = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (_p.Rules.BehindDetection)
        {
            Step("primary history (behind / stale)");
            var cand = forkMap.Keys.Where(k => !primMap.TryGetValue(k, out var pe) || pe.Sha != forkMap[k].Sha).ToList();
            await CollectHistoryAsync(x, histRevs, cand, hist, ct);
        }

        Step("classify");
        var labels = (P: _p.Primary.LegacyName.Length > 0 ? _p.Primary.LegacyName : _p.Primary.Label, F: "ISL");
        var formatKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        string Fk(string sha) => formatKeys.TryGetValue(sha, out var k) ? k : formatKeys[sha] = TextOps.FormatKey(x.ReadBlob(sha));
        bool FmtEq(string a, string b) => Fk(a) == Fk(b);

        var paths = new SortedSet<string>(baseMap.Keys.Concat(forkMap.Keys).Concat(primMap.Keys), StringComparer.Ordinal);
        var rows = new List<AlignmentRow>();
        var same = 0;
        foreach (var p in paths)
        {
            var b = baseMap.GetValueOrDefault(p); var i = forkMap.GetValueOrDefault(p); var e = primMap.GetValueOrDefault(p);
            if (i?.Sha == e?.Sha) { same++; continue; }
            var m = mrgMap.GetValueOrDefault(p);
            var clean = i is not null && e is not null && m is not null && !conflicts.Contains(p);
            var behind = i is not null && hist.TryGetValue(p, out var hs) && hs.Contains(i.Sha) && !Wildcard.Any(p, _p.ForkOwned);
            var c = Classifier.Classify(new ClassifyInput(p, b?.Sha, i?.Sha, e?.Sha,
                _p.FormatInsensitive.Contains(Path.GetExtension(p).ToLowerInvariant()), IsManifest(p), clean, behind, FmtEq), _p.Rules, labels.P, labels.F)!.Value;
            rows.Add(new AlignmentRow { Path = p, Base = b, Fork = i, Primary = e, Merged = m, CleanMerge = clean, Category = c.Category, Decision = c.Decision, Reason = c.Reason });
        }

        // Manifest unions must be computable in both directions; otherwise the row needs a human.
        var merger = Merger();
        foreach (var r in rows.Where(r => r.Decision == Decision.ManifestUnion))
        {
            try
            {
                var pe = TextOps.ToText(x.ReadBlob(r.Primary!.Sha)); var fe = TextOps.ToText(x.ReadBlob(r.Fork!.Sha));
                merger.Merge(pe, fe, labels.F, _p.Manifest.VersionOverrides);
                merger.Merge(fe, pe, labels.P, _p.Manifest.VersionOverrides);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException)
            { r.Decision = Decision.Manual; r.Reason = $"package union not possible: {ex.Message}"; }
        }

        Step("fork additions");
        var added = rows.Where(r => r.Category == Category.ForkAdded).ToList();
        var deleted = rows.Where(r => r.Category == Category.ForkDeleted).ToList();
        var delKeys = new Dictionary<string, AlignmentRow>(StringComparer.Ordinal);
        foreach (var r in deleted) delKeys[TextOps.ContentKey(x.ReadBlob(r.Primary!.Sha))] = r;
        if (!_p.Rules.BehindDetection)
            await CollectHistoryAsync(x, histRevs, added.Where(r => !Wildcard.Any(r.Path, _p.ForkOwned)).Select(r => r.Path).ToList(), hist, ct);
        var backupRx = _p.Rules.BehindDetection ? @"\.(bak|orig|backup)$" : @"\.(bak|orig)$";
        foreach (var r in added)
        {
            if (Wildcard.Any(r.Path, _p.ForkOwned)) { r.Category = Category.ForkOwned; r.Reason = "fork-owned path (config ForkOwned)"; continue; }
            var key = TextOps.ContentKey(x.ReadBlob(r.Fork!.Sha));
            if (delKeys.TryGetValue(key, out var src))
            {
                r.Category = Category.ForkRenamed; r.Decision = Decision.DeleteBoth; r.Reason = $"pure rename of {src.Path} (identical content); {labels.P} name kept";
                src.Category = Category.ForkRenameSource; src.Reason = $"ISL renamed this to {r.Path} with identical content; {labels.P} name restored in ISL";
                continue;
            }
            var known = hist.TryGetValue(r.Path, out var h) ? h : null;
            if (!_p.Rules.BehindDetection && known is not null && known.Contains(r.Fork.Sha))
            {
                r.Category = Category.ForkStale; r.Decision = Decision.DeleteBoth; r.Reason = "exact content Backend held before deleting it; left behind by the additive overlay";
                continue;
            }
            r.Category = Category.ForkOnly;
            r.Reason = Regex.IsMatch(r.Path, backupRx, RegexOptions.IgnoreCase) ? "fork addition; cleanup candidate (backup file)"
                : known is not null && known.Count > 0 ? $"fork addition; {labels.P} once had this path with different content - review"
                : $"fork addition (not in {labels.P} history)";
        }

        Step("overrides");
        foreach (var r in rows)
            foreach (var o in _p.Overrides)
                if (Wildcard.IsMatch(r.Path, o.Pattern))
                {
                    var d = DecisionNames.Parse(o.Decision);
                    var note = $"default {DecisionNames.Legacy(r.Decision, PrimaryCode)}" + (r.CleanMerge && d != Decision.Merged ? "; clean 3-way merge available - set MERGED to take it" : "");
                    r.Decision = d; r.Reason = $"override: {o.Reason} [{note}]";
                    break;
                }

        if (!string.IsNullOrWhiteSpace(_p.Primary.ReferenceRef))
        {
            Step("reference tags");
            var refName = _p.Primary.ReferenceRef!;
            var refSha = await git.TextAsync(profile.PrimaryRoot, "rev-parse", "--verify", refName + "^{commit}");
            foreach (var r in rows.Where(r => r.Decision is Decision.TakeFork or Decision.Merged or Decision.KeepBoth or Decision.Manual))
            {
                var d = (await x.RunAsync(["rev-parse", "--verify", "--quiet", $"{refSha}:{r.Path}"], okExit: [0, 1], ct: ct)).StdOut.Trim();
                var tag = d.Length == 0 ? $"absent in {refName}"
                    : d == r.Primary?.Sha && d == r.Fork?.Sha ? "both match"
                    : d == r.Primary?.Sha ? $"{labels.P} matches {refName}"
                    : d == r.Fork?.Sha ? $"ISL matches {refName}"
                    : $"neither matches {refName}";
                r.Reason += $" [{tag}]";
            }
        }

        Step("reference check for fork deletions");
        await ReferenceFixpointAsync(x, rows, fork.Rev, labels.P, ct);

        var plan = new AlignmentPlan
        {
            Id = StateStore.NewId("ap"), ParentPlanId = req.ParentPlanId, Profile = profile.Name, ProfileHash = profile.Hash, SyncBase = syncBase,
            Scope = _p.Scope.ToList(), Primary = prim with { ScopedTree = eT }, Fork = fork with { ScopedTree = iT },
            BaseTree = bT, MergedTree = mT, Identical = same, Preview = preview, Rows = rows, CreatedAt = DateTimeOffset.UtcNow,
        };
        plan.ContentHash = PlanHash.Compute(plan);
        return plan;
    }

    public string PrimaryCode => (_p.Primary.LegacyName.Length > 0 ? _p.Primary.LegacyName : _p.Primary.Label) switch
    {
        "Backend" => "BE", "Frontend" => "FE", var s => s.Length >= 2 ? s[..2].ToUpperInvariant() : "PR",
    };

    internal IManifestMerger Merger() => _p.Manifest.Kind == "packageJson" ? new PackageJsonMerger(_p.Manifest.Sections) : new DirectoryPackagesPropsMerger();

    internal bool IsManifest(string path) => _p.Manifest.Kind == "packageJson"
        ? Wildcard.Any(path, _p.Manifest.Files)
        : Wildcard.Any(path, _p.Manifest.Files, ignoreCase: true);

    private async Task<SideState> ResolveSideAsync(ScratchRepo x, Side side, SideSelection sel, CancellationToken ct)
    {
        var sp = profile.SideOf(side);
        var root = profile.RootOf(side);
        if (sel.Rev is not null)
        {
            // A PS preview plan may record a tree (base ref with merge refs merged in memory).
            if (Regex.IsMatch(sel.Rev, "^[0-9a-f]{40}$") && await x.TestAsync("cat-file", "-e", sel.Rev + "^{tree}")
                && (await x.TextAsync("cat-file", "-t", sel.Rev)) == "tree")
                return new SideState(sp.Repo, sp.Label, null, sel.Rev, sel.Rev, "", null);
            var sha = await ResolveCommitAsync(x, root, sel.Rev);
            return new SideState(sp.Repo, sp.Label, null, sha, sha, "", null);
        }
        if (sel.Preview)
        {
            var baseRef = sp.Session.BaseRef ?? throw new AlignmentException($"{sp.Label}: no session.baseRef for a preview plan.", 2);
            var commit = await git.TextAsync(root, "rev-parse", "--verify", baseRef + "^{commit}");
            var rev = commit;
            foreach (var m in sp.Session.MergeRefs.Where(m => !string.IsNullOrWhiteSpace(m)))
            {
                var ms = await git.TextAsync(root, "rev-parse", "--verify", m + "^{commit}");
                rev = await x.MergeCommitsAsync(rev, ms, ct);
            }
            return new SideState(sp.Repo, sp.Label, baseRef, rev, commit, "", null);
        }
        if (sel.WorktreePath is not null)
        {
            var wt = sel.WorktreePath;
            var status = (await git.RunAsync(wt, ["status", "--porcelain=v1", "--untracked-files=all", "--", .. x.ScopePathspecs()], ct: ct)).StdOut.TrimEnd();
            if (status.Length > 0) throw new AlignmentException($"{sp.Label} worktree {wt} has uncommitted changes in the aligned scope:\n{status}", 11);
            var head = await git.TextAsync(wt, "rev-parse", "HEAD");
            var branch = await git.TextAsync(wt, "rev-parse", "--abbrev-ref", "HEAD");
            return new SideState(sp.Repo, sp.Label, branch == "HEAD" ? null : branch, head, head, "", wt);
        }
        throw new AlignmentException($"{sp.Label}: choose a worktree, an explicit revision or preview.", 2);
    }

    private async Task<string> ResolveCommitAsync(ScratchRepo x, string root, string rev)
    {
        if (Regex.IsMatch(rev, "^[0-9a-f]{40}$") && await x.TestAsync("cat-file", "-e", rev + "^{commit}")) return rev;
        return await git.TextAsync(root, "rev-parse", "--verify", rev + "^{commit}");
    }

    private static readonly Regex RawLine = new(@"^:\d+ \d+ [0-9a-f]+ ([0-9a-f]+) \w+\t(.+)$");

    private static async Task CollectHistoryAsync(ScratchRepo x, List<string> histRevs, List<string> cand, Dictionary<string, HashSet<string>> hist, CancellationToken ct)
    {
        for (var k = 0; k < cand.Count; k += 40)
        {
            var chunk = cand.Skip(k).Take(40);
            var args = new List<string> { "-c", "core.quotePath=false", "log", "--format=", "--raw", "--no-abbrev", "--no-renames" };
            args.AddRange(histRevs); args.Add("--"); args.AddRange(chunk);
            var log = (await x.RunAsync(args, ct: ct)).StdOut;
            foreach (var l in log.Split('\n'))
            {
                var m = RawLine.Match(l.TrimEnd('\r'));
                if (!m.Success) continue;
                var path = m.Groups[2].Value;
                if (!hist.TryGetValue(path, out var set)) hist[path] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(m.Groups[1].Value);
            }
        }
    }

    /// <summary>Port of the "reference fixpoint for deletions" block: a stale fork file still referenced by
    /// surviving code (or by another kept stale file) is kept; other deletions with references get a warning.</summary>
    private async Task ReferenceFixpointAsync(ScratchRepo x, List<AlignmentRow> rows, string forkRev, string primaryLabel, CancellationToken ct)
    {
        var analyzer = ReferenceAnalyzers.For(_p.References.Analyzer);
        var forkFull = await x.TextAsync("rev-parse", forkRev + "^{tree}");
        var changes = new List<KeyValuePair<string, BlobRef>>(); var dels = new List<string>();
        foreach (var r in rows)
        {
            var t = Classifier.TargetFor(r, Side.Fork);
            if (t.IsUnion) continue;
            if (t.Absent) { if (r.Fork is not null) dels.Add(r.Path); }
            else if (t.Ref!.Sha != r.Fork?.Sha) changes.Add(new(r.Path, t.Ref));
        }
        var projected = await x.WriteTreeAsync(changes, forkFull, dels, ct);
        var delRows = rows.Where(r => r.Fork is not null && Classifier.TargetFor(r, Side.Fork).Absent && r.Category != Category.ForkRenamed).ToList();
        var symbols = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var tokens = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var r in delRows)
        {
            r.ExtRefs = new(); r.KeptBy = new();
            var names = analyzer.Names(r.Path, TextOps.ToText(x.ReadBlob(r.Fork!.Sha)));
            var token = analyzer.Token(r.Path);
            symbols[r.Path] = names; tokens[r.Path] = token;
            var hits = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var q in analyzer.GrepQueries(r.Path, names, token, projected))
            {
                var outp = (await x.RunAsync(q, okExit: [0, 1], ct: ct)).StdOut;
                foreach (var line in outp.Split('\n'))
                {
                    if (line.Length == 0) continue;
                    var hit = line[(projected.Length + 1)..];
                    if (seen.Add(hit)) hits.Add(hit);
                }
            }
            r.ExtRefs = hits;
        }
        var keep = new HashSet<string>(StringComparer.Ordinal);
        var movable = delRows.Where(r => r.Category == Category.ForkStale).ToList();
        bool grew;
        do
        {
            grew = false;
            foreach (var r in movable)
            {
                if (keep.Contains(r.Path)) continue;
                var byKept = movable.Where(o => keep.Contains(o.Path) && analyzer.References(TextOps.ToText(x.ReadBlob(o.Fork!.Sha)), symbols[r.Path], tokens[r.Path])).Select(o => o.Path).ToList();
                if (r.ExtRefs.Count > 0 || byKept.Count > 0) { keep.Add(r.Path); r.KeptBy = r.ExtRefs.Concat(byKept).ToList(); grew = true; }
            }
        } while (grew);
        foreach (var r in delRows)
        {
            var refs = r.KeptBy.Count > 0 ? r.KeptBy : r.ExtRefs;
            if (refs.Count > 0) r.Refs = $"{refs.Count}: " + string.Join(", ", refs.Take(4).Select(s => s.Split('/')[^1]));
            if (keep.Contains(r.Path)) { r.Decision = Decision.KeepForkOnly; r.Reason = $"stale {primaryLabel} file still referenced in ISL; retarget the references, then set DELETE_BOTH"; }
            else if (refs.Count > 0 && r.Decision == Decision.TakePrimary) r.Reason += "; WARNING ISL references remain - fix before build";
        }
    }
}

public static class PlanHash
{
    /// <summary>Hash over everything that determines the plan's effect; excludes id and creation time so the
    /// same inputs always give the same hash.</summary>
    public static string Compute(AlignmentPlan p)
    {
        var canonical = new
        {
            p.ParentPlanId, p.Profile, p.ProfileHash, p.SyncBase, p.Scope, p.Primary, p.Fork, p.BaseTree, p.MergedTree, p.Identical, p.Preview,
            Rows = p.Rows.Select(r => new { r.Path, r.Category, r.Decision, r.Reason, r.Refs, r.Base, r.Primary, r.Fork, r.Merged, r.CleanMerge }),
        };
        return "sha256:" + StateStore.Sha256(System.Text.Json.JsonSerializer.Serialize(canonical, StateStore.Json));
    }
}
