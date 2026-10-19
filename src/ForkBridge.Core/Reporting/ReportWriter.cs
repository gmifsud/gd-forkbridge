using System.Text;
using System.Text.Json;
using ForkBridge.Core.Model;
using ForkBridge.Core.Storage;

namespace ForkBridge.Core.Reporting;

public static class ReportWriter
{
    public static string JsonPath(StateStore s, string runId) => Path.Combine(s.RunDir(runId), "report.json");
    public static string MarkdownPath(StateStore s, string runId) => Path.Combine(s.RunDir(runId), "report.md");

    public static void Write(StateStore store, Plan plan, Run run)
    {
        var json = JsonSerializer.Serialize(new { schema = "forkbridge-report/1", plan, run }, StateStore.Json);
        File.WriteAllText(JsonPath(store, run.Id), json);
        File.WriteAllText(MarkdownPath(store, run.Id), Markdown(plan, run));
    }

    public static string ReportHash(StateStore store, string runId) => StateStore.Sha256(File.ReadAllBytes(JsonPath(store, runId)));

    public static string PlanMarkdown(Plan plan) => Markdown(plan, null);

    public static string Markdown(Plan plan, Run? run)
    {
        var sb = new StringBuilder();
        var status = run?.Status ?? plan.PreliminaryStatus;
        sb.AppendLine($"# ForkBridge {(run is null ? "plan" : "run")} report — {status}");
        sb.AppendLine();
        sb.AppendLine("| | |\n|---|---|");
        sb.AppendLine($"| Plan | `{plan.Id}` (created {plan.CreatedAt:u}) |");
        if (run is not null) sb.AppendLine($"| Run | `{run.Id}` ({run.StartedAt:u} → {(run.FinishedAt is null ? "…" : run.FinishedAt.Value.ToString("u"))}) |");
        if (run?.BundleId is not null) sb.AppendLine($"| Bundle | `{run.BundleId}` |");
        sb.AppendLine($"| Pair / direction | `{plan.Pair}`: **{plan.FromProduct} → {plan.ToProduct}** |");
        sb.AppendLine($"| Mode | {plan.Mode} |");
        sb.AppendLine($"| Source | `{plan.Source.Alias}` {plan.Source.RemoteUrlRedacted} |");
        sb.AppendLine($"| Target | `{plan.Target.Alias}` {plan.Target.RemoteUrlRedacted} |");
        sb.AppendLine($"| Source head | `{plan.SourceHeadSha}`{(plan.SourceRef is null ? "" : $" ({plan.SourceRef})")} |");
        if (plan.SourceBaseSha is not null) sb.AppendLine($"| Source base | `{plan.SourceBaseSha}` ({plan.SourceBaseRef}) |");
        sb.AppendLine($"| Target base | `{plan.TargetBaseSha}` ({plan.TargetBaseRef}) |");
        if (plan.MergeBase is not null) sb.AppendLine($"| Merge base / baseline | `{plan.MergeBase}` |");
        sb.AppendLine($"| Components | {string.Join(", ", plan.SelectedComponents)} |");
        sb.AppendLine($"| Policy hash | `{plan.PolicyHash}` |");
        sb.AppendLine($"| Freshness | {(plan.Freshness.Offline ? "⚠ " + plan.Freshness.Note : $"remote fetched {plan.Freshness.FetchedAt:u}")} |");
        if (run?.CandidateSha is not null) sb.AppendLine($"| Candidate | `{run.CandidateSha}` on `{run.Branch}` (harness mirror only) |");
        sb.AppendLine();
        if (plan.MergeBaseExplanation is not null) sb.AppendLine($"> {plan.MergeBaseExplanation}\n");

        var findings = plan.Findings.Concat(run?.Findings ?? []).GroupBy(f => f.Id).Select(g => g.First())
            .OrderByDescending(f => f.Blocking).ThenByDescending(f => f.RequiresReview).ThenByDescending(f => f.Severity).ToList();
        sb.AppendLine($"## Findings ({findings.Count})\n");
        if (findings.Count == 0) sb.AppendLine("None.\n");
        foreach (var f in findings)
        {
            var gate = f.Blocking ? "🛑 BLOCKS" : f.RequiresReview ? (plan.AcceptedFindings.Contains(f.Id) || (run?.Approval?.AcceptedFindings.Contains(f.Id) ?? false) ? "✅ accepted" : "🔎 REVIEW") : "ℹ";
            sb.AppendLine($"### {gate} `{f.Id}` — {f.Title}");
            sb.AppendLine($"- Category/severity: {f.Category} / {f.Severity}; origin: {f.Origin}");
            if (f.Component is not null) sb.AppendLine($"- Component: {f.Component}");
            if (f.Commits.Count > 0) sb.AppendLine($"- Commits: {string.Join(", ", f.Commits.Select(c => $"`{c[..Math.Min(10, c.Length)]}`"))}");
            if (f.Files.Count > 0) sb.AppendLine($"- Files: {string.Join(", ", f.Files.Take(15).Select(x => $"`{x}`"))}{(f.Files.Count > 15 ? $" (+{f.Files.Count - 15})" : "")}");
            foreach (var e in f.Evidence.Take(15)) sb.AppendLine($"  - {e}");
            if (f.SuggestedAction is not null) sb.AppendLine($"- Suggested action: {f.SuggestedAction}");
            sb.AppendLine();
        }

        sb.AppendLine("## Commits\n");
        sb.AppendLine("| # | Source | State | Author | Subject | Result |\n|---|---|---|---|---|---|");
        int i = 0;
        foreach (var c in plan.Commits)
        {
            i++;
            var result = run?.ResultCommits.GetValueOrDefault(c.Sha);
            sb.AppendLine($"| {i} | `{c.Sha[..10]}`{(c.IsMerge ? " (merge)" : "")}{(c.Mixed ? " (mixed)" : "")} | {c.State} | {Esc(c.Author)} | {Esc(c.Subject)} | {(result is null ? "" : $"`{result[..10]}`")} |");
        }
        if (plan.Reconciliation is not null)
            sb.AppendLine($"| R | snapshot `{plan.SourceHeadSha[..10]}` | {plan.Reconciliation.State} | (reconciliation commit) | {Esc(plan.Reconciliation.Subject)} | {(run?.ResultCommits.GetValueOrDefault("reconcile") ?? run?.ResultCommits.GetValueOrDefault("merge")) ?? ""} |");
        sb.AppendLine();

        sb.AppendLine("## Changed-file classification\n");
        sb.AppendLine("| Commit | St | Source path | Target path | Class | Decision | Reason |\n|---|---|---|---|---|---|---|");
        foreach (var c in plan.Commits.Concat(plan.Reconciliation is null ? [] : [plan.Reconciliation]))
            foreach (var p in c.Paths.Take(400))
                sb.AppendLine($"| `{c.Sha[..Math.Min(10, c.Sha.Length)]}` | {p.Status} | `{(p.SourceOldPath is null ? p.SourcePath : p.SourceOldPath + " → " + p.SourcePath)}` | {(p.TargetPath is null ? "" : $"`{p.TargetPath}`")} | {p.SourceClass} | **{p.Decision}** | {Esc(p.Reason)}{(p.Binary ? " (binary)" : "")} |");
        sb.AppendLine();

        sb.AppendLine("## Existing drift (pinned snapshots)\n");
        sb.AppendLine("| Component | Source path | Target path | Identical | Differing | Only source | Only target | Target diverged | Preserved product diffs |\n|---|---|---|---|---|---|---|---|---|");
        foreach (var d in plan.Drift)
            sb.AppendLine($"| {d.Component} | `{d.SourcePath}` | `{d.TargetPath}`{(d.IntentionallyAbsentInTarget ? " (intentionally absent)" : !d.PresentInTarget ? " (MISSING)" : "")} | {d.Identical} | {d.Differing.Count} | {d.OnlyInSource.Count} | {d.OnlyInTarget.Count} | {d.TargetDivergedSinceBaseline.Count} | {d.PreservedProductDifferences.Count} |");
        sb.AppendLine();

        if (plan.Impact is not null)
        {
            sb.AppendLine("## Dependency impact (target)\n");
            sb.AppendLine($"- Affected projects: {List(plan.Impact.AffectedProjects)}");
            sb.AppendLine($"- Shared transitive consumers: {List(plan.Impact.SharedConsumers)}");
            sb.AppendLine($"- **Product consumers**: {List(plan.Impact.ProductConsumers)}");
            sb.AppendLine($"- Frontend consumers: {List(plan.Impact.FrontendConsumers)}");
            foreach (var n in plan.Impact.Notes) sb.AppendLine($"- {n}");
            sb.AppendLine();
        }

        if (run is not null)
        {
            if (run.Conflict is not null)
            {
                sb.AppendLine("## Conflict\n");
                sb.AppendLine($"- Operation: {run.Conflict.Operation}");
                sb.AppendLine($"- Files: {List(run.Conflict.Files)}");
                foreach (var ins in run.Conflict.Instructions) sb.AppendLine($"- {ins}");
                if (run.Conflict.Output is not null) sb.AppendLine($"\n```\n{Trunc(run.Conflict.Output, 3000)}\n```\n");
            }
            sb.AppendLine("## Validation (baseline vs candidate)\n");
            if (run.ValidationComparison.Count == 0) sb.AppendLine("No validation executed.\n");
            else
            {
                sb.AppendLine("| Step | Required | Baseline | Candidate | Verdict |\n|---|---|---|---|---|");
                foreach (var v in run.ValidationComparison) sb.AppendLine($"| {v.StepId} | {(v.Required ? "yes" : "no")} | {v.Baseline} | {v.Candidate} | **{v.Verdict}** |");
                sb.AppendLine();
                foreach (var v in run.CandidateValidation.Where(v => v.Outcome != "PASSED"))
                    sb.AppendLine($"<details><summary>candidate {v.StepId}: {v.Outcome} {v.Reason}</summary>\n\n```\n{Trunc(v.OutputTail ?? "", 3000)}\n```\n</details>\n");
            }
            if (run.CandidateDiffStat is not null) sb.AppendLine($"## Candidate diff (stat)\n\n```\n{Trunc(run.CandidateDiffStat, 6000)}\n```\nFull diff: `{run.CandidateDiffFile}`\n");
            if (run.Approval is not null) sb.AppendLine($"## Approval\n\n- By {run.Approval.ApprovedBy} at {run.Approval.ApprovedAt:u}\n- Binding: `{run.Approval.Binding}`\n");
            foreach (var p in run.Publications) sb.AppendLine($"- Publication {p.At:u}: branch `{p.Branch}` pushed={p.Pushed} {(p.PullRequestUrl is null ? "" : "PR " + p.PullRequestUrl)} {p.Error}");
            if (run.Log.Count > 0) { sb.AppendLine("\n## Log\n"); foreach (var l in run.Log.TakeLast(60)) sb.AppendLine($"- {Esc(l)}"); }
        }
        sb.AppendLine("\n---\nStatic analysis and successful builds are evidence, not proof of behavioural compatibility.");
        return sb.ToString();
    }

    private static string List(IEnumerable<string> xs) { var l = xs.ToList(); return l.Count == 0 ? "none" : string.Join(", ", l.Take(30).Select(x => $"`{x}`")) + (l.Count > 30 ? $" (+{l.Count - 30})" : ""); }
    private static string Esc(string s) => s.Replace("|", "\\|").Replace("\n", " ");
    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "\n…(truncated)";
}
