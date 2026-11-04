using System.Text.RegularExpressions;
using ForkBridge.Alignment.Config;
using ForkBridge.Alignment.Git;
using ForkBridge.Alignment.Model;
using ForkBridge.Alignment.Planning;
using ForkBridge.Alignment.Text;
using ForkBridge.Core.Infrastructure;

namespace ForkBridge.Alignment.Advisor;

public sealed record AdvisorRequest(string PrimaryRev, string ForkRev, AdvisorWeights? Weights = null);

public sealed record AdvisorWeights(int Revert = 5, int Merge = 1, int Conflict = 3);

public sealed record CommitInfo(string Sha, string Date, string Author, string Subject);

public sealed record LinePoint(string Sha, string Date, string Author, string Subject, int Score, int Risk, int Merges, bool Transient);

public sealed record AdvisorLine(string Name, string Ref, bool Selected, IReadOnlyList<LinePoint> Commits);

public sealed record ForkBulkCommit(string Sha, string Date, string Author, string Subject, int ScopedFiles, string? MatchSha, int Same, int Differ);

public sealed record CandidateScore(
    string Sha, string Date, string Author, string Subject, IReadOnlyList<string> Sources,
    int Score, int Risk, int Merges, int Conflicts, int Promotions, int Absorbed, int Transient, int Rows, double Fit,
    IReadOnlyList<string> RiskSample, bool IsTransient, string Fingerprint);

public sealed record AdvisorResult(
    string Profile, string PrimaryRev, string ForkRev, AdvisorWeights Weights, string? Configured,
    string? Recommended, string Confidence, IReadOnlyList<string> Rationale,
    IReadOnlyList<CandidateScore> Candidates, IReadOnlyList<AdvisorLine> Lines, IReadOnlyList<ForkBulkCommit> ForkBulk,
    IReadOnlyList<string> Warnings);

/// <summary>Recommends a SyncBase by simulating the alignment classifier for candidate commits.
/// Risk is judged with evidence from BOTH histories (back-test 2026-10-27): a row that would promote fork content
/// is risky only when the fork never held the base's content for that path; a row that would take primary is risky
/// when the fork once held primary's current content and moved away from it. Declared divergence (overrides) never
/// counts as risk, bases later reverted on the primary line are flagged transient, and identical classifications
/// across candidates are reported as "indifferent".</summary>
public sealed class SyncBaseAdvisor(ResolvedProfile profile, AlignGit git, Action<string>? progress = null)
{
    private readonly AlignmentProfile _p = profile.Profile;
    private static readonly Regex RawLine = new(@"^:\d+ \d+ ([0-9a-f]+) ([0-9a-f]+) \w+\t(.+)$");
    private void Step(string s) => progress?.Invoke(s);

    private sealed class Scored
    {
        public int R, M, C, G, A, RT, Rows; public double Fit; public List<string> RiskSample = new(); public string Fingerprint = "";
        public int Score(AdvisorWeights w) => w.Revert * (R + RT) + w.Merge * M + w.Conflict * C;
    }

    public async Task<AdvisorResult> AdviseAsync(AdvisorRequest req, CancellationToken ct = default)
    {
        await git.AssertVersionAsync();
        Directory.CreateDirectory(profile.StateDir);
        var w = req.Weights ?? new AdvisorWeights(_p.Advisor.RevertWeight, _p.Advisor.MergeWeight, _p.Advisor.ConflictWeight);
        using var x = await ScratchRepo.OpenAsync(git, profile.StateDir, profile.PrimaryRoot, profile.ForkRoot, _p.Scope, _p.Protected, ct);
        var warnings = new List<string>();

        Step("revisions");
        var pr = await ResolveAsync(x, profile.PrimaryRoot, req.PrimaryRev);
        var fr = await ResolveAsync(x, profile.ForkRoot, req.ForkRev);
        var forkIsCommit = (await x.TextAsync("cat-file", "-t", fr)) == "commit";
        var PM = await x.ScopedEntriesAsync(pr, ct);
        var FM = await x.ScopedEntriesAsync(fr, ct);

        var historyRefs = new List<(string Name, string Sha)>();
        foreach (var h in _p.Primary.HistoryRefs.Where(h => !string.IsNullOrWhiteSpace(h)))
        {
            var sha = await TryRevAsync(profile.PrimaryRoot, h);
            if (sha is null) warnings.Add($"history ref {h} not found in {_p.Primary.Label}"); else historyRefs.Add((h, sha));
        }

        // Candidates with evidence sources.
        var sources = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        void Add(string sha, string src) { if (!sources.TryGetValue(sha, out var s)) sources[sha] = s = new SortedSet<string>(StringComparer.Ordinal); s.Add(src); }
        string? configured = null;
        if (!string.IsNullOrWhiteSpace(_p.SyncBase) && await x.TestAsync("cat-file", "-e", _p.SyncBase + "^{commit}"))
        { configured = await x.TextAsync("rev-parse", _p.SyncBase + "^{commit}"); Add(configured, "configured"); }
        foreach (var (label, sha) in await LedgerAsync(x, warnings)) Add(sha, label);
        string? mergeBase = null;
        if (forkIsCommit)
        {
            var mb = await x.RunAsync(["merge-base", pr, fr], okExit: [0, 1], ct: ct);
            if (mb.ExitCode == 0) { mergeBase = mb.StdOut.Trim(); Add(mergeBase, "merge-base"); }
        }

        // Time window: from the oldest evidence minus a week (bounded), so the sweep covers every plausible base.
        var anchorDates = new List<long>();
        foreach (var sha in sources.Keys) anchorDates.Add(long.Parse(await x.TextAsync("log", "-1", "--format=%ct", sha)));
        var since = anchorDates.Count > 0 ? DateTimeOffset.FromUnixTimeSeconds(anchorDates.Min()).AddDays(-7) : DateTimeOffset.UtcNow.AddDays(-120);
        if (since < DateTimeOffset.UtcNow.AddDays(-_p.Advisor.MaxWindowDays)) since = DateTimeOffset.UtcNow.AddDays(-_p.Advisor.MaxWindowDays);
        var sinceArg = "--since=" + since.ToString("yyyy-MM-dd");

        Step("history evidence (primary and fork)");
        var lineRefs = new List<(string Name, string Sha, bool Selected)> { ("selected primary", pr, true) };
        lineRefs.AddRange(historyRefs.Where(h => h.Sha != pr).Select(h => (h.Name, h.Sha, false)));
        var hp = await HistoryAsync(x, lineRefs.Select(l => l.Sha).Distinct().ToList(), ct);
        foreach (var (k, v) in PM) Get(hp, k).Add(v.Sha);
        var hf = forkIsCommit ? await HistoryAsync(x, [fr], ct) : new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (k, v) in FM) Get(hf, k).Add(v.Sha);

        var fmtCache = new Dictionary<string, string>(StringComparer.Ordinal);
        bool FmtEq(string a, string b) => Fk(a) == Fk(b);
        string Fk(string sha) => fmtCache.TryGetValue(sha, out var k) ? k : fmtCache[sha] = TextOps.FormatKey(x.ReadBlob(sha));

        Step("timeline sweep");
        var lines = new List<AdvisorLine>();
        var snapshots = new Dictionary<string, Dictionary<string, BlobRef>>(StringComparer.Ordinal);
        var meta = new Dictionary<string, CommitInfo>(StringComparer.Ordinal);
        foreach (var (name, sha, selected) in lineRefs)
        {
            ct.ThrowIfCancellationRequested();
            var shas = (await x.RunAsync(["log", "--first-parent", "--format=%H%x09%ad%x09%an%x09%s", "--date=short", sinceArg, $"--max-count={_p.Advisor.MaxCommitsPerLine}", sha, "--", .. _p.Scope], ct: ct)).StdOut
                .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t', 4)).ToList();
            shas.Reverse();
            if (shas.Count == 0) continue;
            foreach (var s in shas) meta[s[0]] = new CommitInfo(s[0], s[1], s[2], s.Length > 3 ? s[3] : "");
            var map = new Dictionary<string, BlobRef>(await x.ScopedEntriesAsync(shas[0][0], ct), StringComparer.Ordinal);
            Dictionary<string, BlobRef>? prev = null;
            var points = new List<LinePoint>();
            for (var i = 0; i < shas.Count; i++)
            {
                var c = shas[i][0];
                if (i > 0) await ApplyDiffAsync(x, shas[i - 1][0], c, map, ct);
                var snap = new Dictionary<string, BlobRef>(map, StringComparer.Ordinal);
                snapshots.TryAdd(c, snap);
                var sc = Score(snap, PM, FM, hp, hf, prev, null, exact: true);
                points.Add(new LinePoint(c, shas[i][1], shas[i][2], shas[i].Length > 3 ? shas[i][3] : "", sc.Score(w), sc.R + sc.RT, sc.M, sc.RT > 0));
                prev = snap;
            }
            if (points.Count > 0)
            {
                var best = points.Select((p, i) => (p, i)).OrderBy(t => t.p.Score).ThenByDescending(t => t.i).First().p;
                Add(best.Sha, selected ? "sweep minimum" : $"sweep minimum ({name})");
            }
            points.Reverse();
            lines.Add(new AdvisorLine(name == "selected primary" ? "selected primary branch" : name, sha, selected, points));
        }

        Step("overlay match and provenance");
        var bulk = new List<ForkBulkCommit>();
        if (forkIsCommit)
        {
            var forkLog = (await x.RunAsync(["log", "--first-parent", "--format=%H%x09%ad%x09%an%x09%ae%x09%at%x09%s", "--date=short", sinceArg, $"--max-count={_p.Advisor.MaxCommitsPerLine * 2}", fr, "--", .. _p.Scope], ct: ct)).StdOut
                .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t', 6)).ToList();
            foreach (var f in forkLog)
            {
                var n = (await x.RunAsync(["diff-tree", "-r", "--name-only", "--no-commit-id", "--root", f[0], "--", .. _p.Scope], ct: ct)).StdOut
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(x.InScope);
                if (n < _p.Advisor.OverlayThreshold) continue;
                var om = await x.ScopedEntriesAsync(f[0], ct);
                string? bestSha = null; int bestSame = 0, bestDiff = int.MaxValue;
                foreach (var (sha, cm) in snapshots)
                {
                    var same = om.Count(kv => cm.TryGetValue(kv.Key, out var v) && v.Sha == kv.Value.Sha);
                    var diff = om.Keys.Union(cm.Keys).Count() - same;
                    if (diff < bestDiff) { bestSha = sha; bestSame = same; bestDiff = diff; }
                }
                bulk.Add(new ForkBulkCommit(f[0], f[1], f[2], f.Length > 5 ? f[5] : "", n, bestSha, bestSame, bestSha is null ? 0 : bestDiff));
                if (bestSha is not null) Add(bestSha, $"overlay ≈ {f[0][..9]}");
                if (bulk.Count >= 12) break;
            }
            // Provenance: FromUpstreamCommit.ps1 imports keep upstream author, author date and subject verbatim.
            var primaryKeys = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, sha, _) in lineRefs)
                foreach (var l in (await x.RunAsync(["log", "--format=%H%x09%ae%x09%at%x09%s", sinceArg, sha, "--", .. _p.Scope], ct: ct)).StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var p = l.Split('\t', 4);
                    if (p.Length == 4) primaryKeys.TryAdd($"{p[1]}|{p[2]}|{p[3]}", p[0]);
                }
            var lastImported = forkLog.Select(f => f.Length == 6 && primaryKeys.TryGetValue($"{f[3]}|{f[4]}|{f[5]}", out var ps) ? ps : null).FirstOrDefault(s => s is not null);
            if (lastImported is not null) Add(lastImported, "provenance (latest imported upstream commit)");
        }

        Step("full simulation of candidates");
        var scored = new List<CandidateScore>();
        foreach (var (sha, srcs) in sources.OrderByDescending(s => s.Value.Count).Take(_p.Advisor.MaxCandidates))
        {
            ct.ThrowIfCancellationRequested();
            var bm = snapshots.TryGetValue(sha, out var snap) ? snap : new Dictionary<string, BlobRef>(await x.ScopedEntriesAsync(sha, ct), StringComparer.Ordinal);
            var parent = (await x.RunAsync(["rev-parse", "--verify", "--quiet", sha + "^1"], okExit: [0, 1], ct: ct)).StdOut.Trim();
            var prevMap = parent.Length > 0 ? new Dictionary<string, BlobRef>(await x.ScopedEntriesAsync(parent, ct), StringComparer.Ordinal) : null;
            var sc = Score(bm, PM, FM, hp, hf, prevMap, FmtEq, exact: false);
            sc.C = await CountConflictsAsync(x, bm, PM, FM, FmtEq, ct);
            if (!meta.TryGetValue(sha, out var info))
            {
                var l = (await x.TextAsync("log", "-1", "--format=%H%x09%ad%x09%an%x09%s", "--date=short", sha)).Split('\t', 4);
                info = meta[sha] = new CommitInfo(l[0], l[1], l[2], l.Length > 3 ? l[3] : "");
            }
            var transient = await IsTransientAsync(x, sha, pr, ct);
            scored.Add(new CandidateScore(sha, info.Date, info.Author, info.Subject, srcs.ToList(), sc.Score(w), sc.R + sc.RT, sc.M, sc.C, sc.G, sc.A, sc.RT,
                sc.Rows, Math.Round(sc.Fit, 4), sc.RiskSample.Take(6).ToList(), transient, sc.Fingerprint));
        }

        var ranked = scored.OrderBy(c => c.Score).ThenBy(c => c.Risk).ThenBy(c => c.IsTransient)
            .ThenByDescending(c => c.Sources.Count).ThenByDescending(c => c.Date, StringComparer.Ordinal).ToList();
        var (recommended, confidence, rationale) = Decide(ranked, configured, warnings);
        return new AdvisorResult(profile.Name, pr, fr, w, configured, recommended, confidence, rationale, ranked, lines, bulk, warnings);
    }

    internal static (string? Recommended, string Confidence, List<string> Rationale) Decide(List<CandidateScore> ranked, string? configured, List<string> warnings)
    {
        var why = new List<string>();
        var eligible = ranked.Where(c => !c.IsTransient).ToList();
        if (eligible.Count == 0) return (null, "none", ["No eligible candidate (all were later reverted on the primary line)."]);
        if (ranked.Select(c => c.Fingerprint).Distinct().Count() == 1)
        {
            var pick = eligible.FirstOrDefault(c => c.Sources.Any(s => s.StartsWith("ledger"))) ?? eligible.OrderByDescending(c => c.Date, StringComparer.Ordinal).First();
            why.Add("Every candidate classifies the differing paths identically: the plan does not depend on the SyncBase here (typical right after an alignment).");
            why.Add($"Picked {pick.Sha[..9]} as the newest eligible base{(pick.Sources.Any(s => s.StartsWith("ledger")) ? " recorded in the ledger" : "")}.");
            if (configured is not null && configured != pick.Sha) warnings.Add($"Profile syncBase {configured[..9]} differs from the newest ledger entry {pick.Sha[..9]}; promote it once both alignment PRs have merged.");
            return (pick.Sha, "indifferent", why);
        }
        var top = eligible[0];
        var next = eligible.Skip(1).FirstOrDefault();
        var margin = next is null ? int.MaxValue : next.Score - top.Score;
        var independent = top.Sources.Count(s => s.StartsWith("sweep") || s.StartsWith("overlay") || s.StartsWith("ledger") || s.StartsWith("provenance") || s == "merge-base");
        string conf;
        if (top.Score >= 20 && top.Score > 0.15 * Math.Max(1, top.Rows)) conf = "low";
        else if ((margin >= 3 && independent >= 2) || margin >= 10) conf = "high";
        else conf = "medium";
        why.Add($"{top.Sha[..9]} has the lowest score ({top.Score}): {top.Risk} revert risk(s), {top.Merges} merge(s), {top.Conflicts} conflict(s), {top.Promotions} promotion(s); explains {top.Fit:P1} of the files the fork took from the primary.");
        if (next is not null) why.Add($"Runner-up {next.Sha[..9]} scores {next.Score} (margin {margin}).");
        if (independent >= 2) why.Add($"Independent evidence agrees: {string.Join(", ", top.Sources)}.");
        if (conf == "low") why.Add("Confidence is low: a large residual remains with every base (typical of per-commit imports). Review the risky rows rather than trusting one base.");
        foreach (var t in ranked.Where(c => c.IsTransient)) why.Add($"{t.Sha[..9]} was excluded: a later primary commit restores its parent content (transient base).");
        return (top.Sha, conf, why);
    }

    private Scored Score(IReadOnlyDictionary<string, BlobRef> bm, IReadOnlyDictionary<string, BlobRef> pm, IReadOnlyDictionary<string, BlobRef> fm,
        Dictionary<string, HashSet<string>> hp, Dictionary<string, HashSet<string>> hf, IReadOnlyDictionary<string, BlobRef>? prev,
        Func<string, string, bool>? fmtEq, bool exact)
    {
        var s = new Scored();
        var fp = new System.Text.StringBuilder();
        var paths = new SortedSet<string>(pm.Keys.Concat(fm.Keys), StringComparer.Ordinal);
        var tookFromPrimary = 0; var explained = 0;
        foreach (var path in paths)
        {
            var b = bm.GetValueOrDefault(path)?.Sha; var p = pm.GetValueOrDefault(path)?.Sha; var f = fm.GetValueOrDefault(path)?.Sha;
            if (f is not null && hp.TryGetValue(path, out var hpp) && hpp.Contains(f)) { tookFromPrimary++; if (b == f) explained++; }
            if (p == f) continue;
            s.Rows++;
            if (_p.Overrides.Any(o => Wildcard.IsMatch(path, o.Pattern))) { fp.Append(path).Append("=O;"); continue; }
            var behind = f is not null && hp.TryGetValue(path, out var h1) && h1.Contains(f) && !Wildcard.Any(path, _p.ForkOwned);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var c = Classifier.Classify(new ClassifyInput(path, b, f, p, !exact && _p.FormatInsensitive.Contains(ext), IsManifest(path), CleanMerge: true, behind,
                fmtEq ?? ((_, _) => false)), _p.Rules, _p.Primary.Label, _p.Fork.Label)!.Value;
            var cls = c.Category.ToString();
            switch (c.Category)
            {
                case Category.ForkChanged:
                    if (b is null || !Get(hf, path).Contains(b)) { s.R++; cls = "R"; s.RiskSample.Add((f is not null && Get(hp, path).Contains(f) ? "revert: " : "unseen: ") + path); }
                    else s.G++;
                    break;
                case Category.PrimaryChanged or Category.PrimaryAdded or Category.PrimaryDeleted:
                    if (p is not null && f is not null && p != f && Get(hf, path).Contains(p)) { s.R++; cls = "L"; s.RiskSample.Add("fork-loss: " + path); }
                    else if (prev is not null && b != prev.GetValueOrDefault(path)?.Sha && p == prev.GetValueOrDefault(path)?.Sha && f == b) { s.RT++; cls = "T"; s.RiskSample.Add("transient: " + path); }
                    break;
                case Category.BothChanged or Category.Conflict: s.M++; break;
                case Category.ForkBehind or Category.ForkStale: s.A++; break;
            }
            fp.Append(path).Append('=').Append(cls).Append(';');
        }
        s.Fit = tookFromPrimary == 0 ? 1 : (double)explained / tookFromPrimary;
        s.Fingerprint = Core.Storage.StateStore.Sha256(fp.ToString());
        return s;
    }

    private bool IsManifest(string path) => _p.Manifest.Kind == "packageJson" ? Wildcard.Any(path, _p.Manifest.Files) : Wildcard.Any(path, _p.Manifest.Files, ignoreCase: true);

    private async Task<int> CountConflictsAsync(ScratchRepo x, IReadOnlyDictionary<string, BlobRef> bm, IReadOnlyDictionary<string, BlobRef> pm,
        IReadOnlyDictionary<string, BlobRef> fm, Func<string, string, bool> fmtEq, CancellationToken ct)
    {
        var conflicts = 0;
        var dir = Path.Combine(Path.GetTempPath(), "fbadv-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var path in pm.Keys.Intersect(fm.Keys))
            {
                string? b = bm.GetValueOrDefault(path)?.Sha; var p = pm[path].Sha; var f = fm[path].Sha;
                if (p == f || b is null || b == p || b == f || _p.Overrides.Any(o => Wildcard.IsMatch(path, o.Pattern))) continue;
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (_p.FormatInsensitive.Contains(ext) && (fmtEq(p, f) || fmtEq(b, p) || fmtEq(b, f))) continue;
                var fo = Path.Combine(dir, "o"); var fb = Path.Combine(dir, "b"); var ft = Path.Combine(dir, "t");
                await File.WriteAllBytesAsync(fo, x.ReadBlob(f), ct); await File.WriteAllBytesAsync(fb, x.ReadBlob(b), ct); await File.WriteAllBytesAsync(ft, x.ReadBlob(p), ct);
                var r = await git.RunAsync(dir, ["merge-file", "-q", "-p", fo, fb, ft], okExit: Enumerable.Range(0, 128).ToArray(), ct: ct);
                if (r.ExitCode != 0) conflicts++;
            }
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        return conflicts;
    }

    /// <summary>A base is transient when some later commit on the primary line restores, for a path the base changed,
    /// the content the base's parent had (e.g. a bad promotion that was reverted).</summary>
    private static async Task<bool> IsTransientAsync(ScratchRepo x, string sha, string primaryRev, CancellationToken ct)
    {
        var parent = (await x.RunAsync(["rev-parse", "--verify", "--quiet", sha + "^1"], okExit: [0, 1], ct: ct)).StdOut.Trim();
        if (parent.Length == 0) return false;
        if ((await x.RunAsync(["merge-base", "--is-ancestor", sha, primaryRev], okExit: [0, 1], ct: ct)).ExitCode != 0) return false;
        var changed = (await x.RunAsync(["diff-tree", "-r", "-z", "--no-renames", "--raw", parent, sha, "--", .. x.ScopePathspecs()], ct: ct)).StdOut.Split('\0');
        var oldBlobs = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var k = 0; k + 1 < changed.Length; k += 2)
        {
            var m = changed[k].Split(' ');
            if (m.Length >= 4 && m[0].StartsWith(':')) oldBlobs[changed[k + 1]] = m[2];
        }
        if (oldBlobs.Count == 0) return false;
        var tipOk = 0;
        foreach (var (path, oldSha) in oldBlobs.Take(50))
        {
            var now = (await x.RunAsync(["rev-parse", "--verify", "--quiet", $"{primaryRev}:{path}"], okExit: [0, 1], ct: ct)).StdOut.Trim();
            var old = oldSha.Trim('0').Length == 0 ? "" : oldSha;
            if (now == old) tipOk++;
        }
        return tipOk * 2 >= Math.Min(oldBlobs.Count, 50) && tipOk > 0;
    }

    private async Task<List<(string Label, string Sha)>> LedgerAsync(ScratchRepo x, List<string> warnings)
    {
        var res = new List<(string, string)>();
        foreach (var file in new[] { _p.Ledger, Path.Combine(profile.StateDir, "sync-ledger.json") }.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct())
        {
            if (!File.Exists(file)) continue;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(file!));
                var n = 0;
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    n++;
                    if (e.TryGetProperty("nextSyncBase", out var nb) && nb.GetString() is { Length: >= 7 } s && await x.TestAsync("cat-file", "-e", s + "^{commit}"))
                        res.Add(($"ledger #{n}", await x.TextAsync("rev-parse", s + "^{commit}")));
                }
            }
            catch (System.Text.Json.JsonException) { warnings.Add($"ledger {file} is not valid JSON"); }
        }
        return res;
    }

    private async Task<Dictionary<string, HashSet<string>>> HistoryAsync(ScratchRepo x, List<string> revs, CancellationToken ct)
    {
        var h = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var outp = (await x.RunAsync(["-c", "core.quotePath=false", "log", "--format=", "--raw", "--no-abbrev", "--no-renames", .. revs, "--", .. _p.Scope], ct: ct)).StdOut;
        foreach (var l in outp.Split('\n'))
        {
            var m = RawLine.Match(l.TrimEnd('\r'));
            if (!m.Success || !x.InScope(m.Groups[3].Value)) continue;
            var set = Get(h, m.Groups[3].Value);
            foreach (var g in new[] { m.Groups[1].Value, m.Groups[2].Value }) if (g.Trim('0').Length > 0) set.Add(g);
        }
        return h;
    }

    private static async Task ApplyDiffAsync(ScratchRepo x, string from, string to, Dictionary<string, BlobRef> map, CancellationToken ct)
    {
        var tok = (await x.RunAsync(["-c", "core.quotePath=false", "diff-tree", "-r", "-z", "--no-renames", "--raw", from, to, "--", .. x.ScopePathspecs()], ct: ct)).StdOut.Split('\0');
        for (var k = 0; k + 1 < tok.Length; k += 2)
        {
            var meta = tok[k];
            if (!meta.StartsWith(':')) { k--; continue; }
            var m = meta.Split(' ');
            var path = tok[k + 1];
            if (!x.InScope(path)) continue;
            if (m[3].Trim('0').Length == 0) map.Remove(path); else map[path] = new BlobRef(m[3], m[1]);
        }
    }

    private static HashSet<string> Get(Dictionary<string, HashSet<string>> d, string k)
        => d.TryGetValue(k, out var s) ? s : d[k] = new HashSet<string>(StringComparer.Ordinal);

    private async Task<string> ResolveAsync(ScratchRepo x, string root, string rev)
    {
        if (Regex.IsMatch(rev, "^[0-9a-f]{40}$") && await x.TestAsync("cat-file", "-e", rev)) return rev;
        return (await git.RunAsync(root, ["rev-parse", "--verify", rev + "^{object}"])).StdOut.Trim();
    }

    private async Task<string?> TryRevAsync(string root, string rev)
    {
        var r = await git.RunAsync(root, ["rev-parse", "--verify", "--quiet", rev + "^{commit}"], okExit: [0, 1]);
        return r.ExitCode == 0 ? r.StdOut.Trim() : null;
    }
}
