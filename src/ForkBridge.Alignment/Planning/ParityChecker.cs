using System.Text;
using ForkBridge.Alignment.Model;
using ForkBridge.Alignment.Storage;

namespace ForkBridge.Alignment.Planning;

public sealed record ParityDelta(string Path, string Field, string Reference, string Port);

public sealed record ParityResult(string Golden, int ReferenceRows, int PortRows, int ReferenceSame, int PortSame, IReadOnlyList<ParityDelta> Deltas)
{
    public bool Pass => Deltas.Count == 0 && ReferenceSame == PortSame;

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Parity: {Golden}").AppendLine();
        sb.AppendLine($"Verdict: **{(Pass ? "PASS" : "FAIL")}**").AppendLine();
        sb.AppendLine($"| | reference (PS) | port (C#) |").AppendLine("|---|---|---|");
        sb.AppendLine($"| rows | {ReferenceRows} | {PortRows} |");
        sb.AppendLine($"| identical paths | {ReferenceSame} | {PortSame} |").AppendLine();
        if (Deltas.Count > 0)
        {
            sb.AppendLine("| Path | Field | Reference | Port |").AppendLine("|---|---|---|---|");
            foreach (var d in Deltas.Take(500)) sb.AppendLine($"| `{d.Path}` | {d.Field} | {d.Reference} | {d.Port} |");
        }
        return sb.ToString();
    }
}

/// <summary>Compares a C# plan with a PS reference plan.tsv on (Path, Category, Decision, Merged) - the parity
/// gate of ADR-0002 §9 phase 2. Reason texts are deliberately not compared.</summary>
public static class ParityChecker
{
    public static ParityResult Compare(string goldenPlanTsv, AlignmentPlan port, string primaryCode)
    {
        var golden = PlanTsv.Read(goldenPlanTsv);
        var refSame = golden.Meta.TryGetProperty("same", out var s) ? s.GetInt32() : -1;
        var deltas = new List<ParityDelta>();
        var portRows = port.Rows.ToDictionary(r => r.Path, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in golden.Rows)
        {
            seen.Add(g.Path);
            if (!portRows.TryGetValue(g.Path, out var r)) { deltas.Add(new(g.Path, "row", $"{g.Category}/{g.Decision}", "(missing)")); continue; }
            var cat = DecisionNames.Legacy(r.Category, primaryCode);
            var dec = DecisionNames.Legacy(r.Decision, primaryCode);
            if (cat != g.Category) deltas.Add(new(g.Path, "Category", g.Category, cat));
            if (dec != g.Decision) deltas.Add(new(g.Path, "Decision", g.Decision, dec));
            if ((r.Merged?.Sha ?? "") != g.Merged) deltas.Add(new(g.Path, "Merged", Short(g.Merged), Short(r.Merged?.Sha)));
        }
        foreach (var r in port.Rows.Where(r => !seen.Contains(r.Path)))
            deltas.Add(new(r.Path, "row", "(missing)", $"{DecisionNames.Legacy(r.Category, primaryCode)}/{DecisionNames.Legacy(r.Decision, primaryCode)}"));
        return new ParityResult(goldenPlanTsv, golden.Rows.Count, port.Rows.Count, refSame, port.Identical, deltas);
    }

    private static string Short(string? s) => string.IsNullOrEmpty(s) ? "-" : s[..Math.Min(10, s.Length)];

    /// <summary>Revisions recorded in a PS plan's meta line (keys "backend"/"frontend" and "isl").</summary>
    public static (string PrimaryRev, string ForkRev, string SyncBase, bool Preview, IReadOnlyList<string> Scope) GoldenInputs(string goldenPlanTsv, string primaryCode)
    {
        var m = PlanTsv.Read(goldenPlanTsv).Meta;
        var pk = primaryCode == "FE" ? "frontend" : "backend";
        var scope = m.TryGetProperty("scope", out var sc) ? sc.EnumerateArray().Select(x => x.GetString()!).ToList() : new List<string>();
        return (m.GetProperty(pk).GetProperty("rev").GetString()!, m.GetProperty("isl").GetProperty("rev").GetString()!,
            m.GetProperty("syncBase").GetString()!, m.TryGetProperty("preview", out var pv) && pv.GetBoolean(), scope);
    }
}
