using System.Text;
using System.Text.Json;
using ForkBridge.Alignment.Model;
using ForkBridge.Alignment.Planning;
using ForkBridge.Core.Storage;

namespace ForkBridge.Alignment.Storage;

/// <summary>State for one profile: plans/&lt;id&gt;/{plan.json, plan.md, plan.tsv}. plan.json is written once,
/// made read-only and verified by content hash on load; edits create a derived plan.</summary>
public sealed class AlignmentStore
{
    public string Root { get; }

    public AlignmentStore(string profileStateDir)
    {
        Root = Path.GetFullPath(profileStateDir);
        Directory.CreateDirectory(Path.Combine(Root, "plans"));
    }

    public string PlanDir(string id) => Path.Combine(Root, "plans", StateStore.Sanitize(id));

    public string Save(AlignmentPlan plan, string primaryCode)
    {
        if (plan.ContentHash != PlanHash.Compute(plan)) throw new PlanIntegrityException($"Plan {plan.Id}: content hash mismatch before save.");
        var dir = PlanDir(plan.Id);
        if (Directory.Exists(dir)) throw new PlanIntegrityException($"Plan {plan.Id} already exists (plans are immutable).");
        Directory.CreateDirectory(dir);
        var json = Path.Combine(dir, "plan.json");
        File.WriteAllText(json, JsonSerializer.Serialize(plan, StateStore.Json), new UTF8Encoding(false));
        File.SetAttributes(json, File.GetAttributes(json) | FileAttributes.ReadOnly);
        File.WriteAllText(Path.Combine(dir, "plan.tsv"), PlanTsv.Write(plan, primaryCode), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, "plan.md"), PlanMarkdown.Write(plan, primaryCode), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(Root, "latest-plan.txt"), plan.Id);
        return dir;
    }

    public AlignmentPlan Load(string id)
    {
        var f = Path.Combine(PlanDir(id), "plan.json");
        if (!File.Exists(f)) throw new FileNotFoundException($"Alignment plan '{id}' not found.");
        var plan = JsonSerializer.Deserialize<AlignmentPlan>(File.ReadAllText(f), StateStore.Json)!;
        if (plan.ContentHash != PlanHash.Compute(plan)) throw new PlanIntegrityException($"Plan {id} was modified on disk (content hash mismatch).");
        return plan;
    }

    public IEnumerable<string> ListPlanIds()
        => Directory.Exists(Path.Combine(Root, "plans"))
            ? Directory.GetDirectories(Path.Combine(Root, "plans")).Select(Path.GetFileName).OfType<string>().OrderByDescending(x => x, StringComparer.Ordinal)
            : [];
}

/// <summary>plan.tsv in the PS suites' layout (legacy vocabulary), so either implementation can read the other's plans.</summary>
public static class PlanTsv
{
    public static string Write(AlignmentPlan p, string primaryCode)
    {
        var pn = primaryCode == "FE" ? "Frontend" : "Backend";
        var cols = new[] { "Path", "Category", "Decision", "Reason", "Refs", "Base", "Isl", pn, "Merged", "IslMode", pn + "Mode", "MergedMode" };
        var meta = new Dictionary<string, object?>
        {
            ["created"] = p.CreatedAt.ToString("o"), ["preview"] = p.Preview, ["syncBase"] = p.SyncBase, ["scope"] = p.Scope,
            [pn.ToLowerInvariant()] = new { branch = p.Primary.Branch, rev = p.Primary.Rev, scopedTree = p.Primary.ScopedTree },
            ["isl"] = new { branch = p.Fork.Branch, rev = p.Fork.Rev, scopedTree = p.Fork.ScopedTree },
            ["baseTree"] = p.BaseTree, ["mergedTree"] = p.MergedTree, ["same"] = p.Identical, ["forkbridgePlan"] = p.Id, ["contentHash"] = p.ContentHash,
        };
        var sb = new StringBuilder();
        sb.Append("# meta ").Append(JsonSerializer.Serialize(meta)).Append('\n');
        sb.Append("# Edit only the Decision column. Allowed: TAKE_").Append(primaryCode).Append(" TAKE_ISL MERGED DELETE_BOTH KEEP_ISL_ONLY KEEP_BOTH PACKAGES_UNION MANUAL\n");
        sb.Append(string.Join('\t', cols)).Append('\n');
        static string C(string? s) => (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        foreach (var r in p.Rows)
            sb.Append(string.Join('\t', new[]
            {
                r.Path, DecisionNames.Legacy(r.Category, primaryCode), DecisionNames.Legacy(r.Decision, primaryCode), r.Reason, r.Refs,
                r.Base?.Sha, r.Fork?.Sha, r.Primary?.Sha, r.Merged?.Sha, r.Fork?.Mode, r.Primary?.Mode, r.Merged?.Mode,
            }.Select(C))).Append('\n');
        return sb.ToString();
    }

    public sealed record LegacyRow(string Path, string Category, string Decision, string Reason, string Merged);
    public sealed record LegacyPlan(JsonElement Meta, IReadOnlyList<LegacyRow> Rows);

    public static LegacyPlan Read(string file)
    {
        var lines = File.ReadAllLines(file);
        var meta = JsonDocument.Parse(lines[0]["# meta ".Length..]).RootElement.Clone();
        string[]? hdr = null;
        var rows = new List<LegacyRow>();
        foreach (var l in lines)
        {
            if (l.Length == 0 || l.StartsWith('#')) continue;
            var f = l.Split('\t');
            if (hdr is null) { hdr = f; continue; }
            string Col(string n) { var i = Array.IndexOf(hdr, n); return i >= 0 && i < f.Length ? f[i] : ""; }
            rows.Add(new LegacyRow(Col("Path"), Col("Category"), Col("Decision"), Col("Reason"), Col("Merged")));
        }
        return new LegacyPlan(meta, rows);
    }
}

public static class PlanMarkdown
{
    public static string Write(AlignmentPlan p, string primaryCode)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Alignment plan {p.Id}{(p.Preview ? " (preview)" : "")}").AppendLine();
        sb.AppendLine($"| | {p.Primary.Label} | {p.Fork.Label} |").AppendLine("|---|---|---|");
        sb.AppendLine($"| branch | `{p.Primary.Branch ?? "-"}` | `{p.Fork.Branch ?? "-"}` |");
        sb.AppendLine($"| rev | `{p.Primary.Rev[..10]}` | `{p.Fork.Rev[..10]}` |").AppendLine();
        sb.AppendLine($"Sync base `{p.SyncBase[..10]}`; scope: {string.Join(", ", p.Scope)}. Identical paths: **{p.Identical}**. Rows below: **{p.Rows.Count}**. Content hash `{p.ContentHash}`.").AppendLine();
        sb.AppendLine("| Category | Decision | Count |").AppendLine("|---|---|---|");
        foreach (var g in p.Rows.GroupBy(r => (C: DecisionNames.Legacy(r.Category, primaryCode), D: DecisionNames.Legacy(r.Decision, primaryCode))).OrderBy(g => g.Key.C + "|" + g.Key.D, StringComparer.Ordinal))
            sb.AppendLine($"| {g.Key.C} | {g.Key.D} | {g.Count()} |");
        void Section(string title, Func<AlignmentRow, bool> f)
        {
            var sel = p.Rows.Where(f).ToList();
            if (sel.Count == 0) return;
            sb.AppendLine().AppendLine($"## {title} ({sel.Count})").AppendLine();
            foreach (var r in sel) sb.AppendLine($"- `{r.Path}` - {r.Reason}{(r.Refs.Length > 0 ? $" [refs {r.Refs}]" : "")}");
        }
        Section("MANUAL - resolve before apply", r => r.Decision == Decision.Manual);
        Section($"Promoted {p.Fork.Label} -> {p.Primary.Label} (review each)", r => r.Decision == Decision.TakeFork);
        Section("Merged into both", r => r.Decision == Decision.Merged);
        Section("Accepted divergence (KEEP_BOTH)", r => r.Decision == Decision.KeepBoth);
        Section($"Deleted in {p.Fork.Label}", r => r.Fork is not null && Classifier.TargetFor(r, Side.Fork).Absent);
        Section("Stale but kept (referenced)", r => r.Category == Category.ForkStale && r.Decision == Decision.KeepForkOnly);
        Section($"{p.Fork.Label}-only additions kept (not fork-owned)", r => r.Category == Category.ForkOnly);
        Section($"{p.Fork.Label} behind {p.Primary.Label} ({p.Primary.Label} version taken)", r => r.Category == Category.ForkBehind && r.Decision == Decision.TakePrimary);
        return sb.ToString();
    }
}
