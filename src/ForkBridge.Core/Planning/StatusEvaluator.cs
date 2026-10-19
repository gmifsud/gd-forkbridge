using ForkBridge.Core.Model;

namespace ForkBridge.Core.Planning;

/// <summary>Documented process exit codes (CLI and CI).</summary>
public static class ExitCodes
{
    public const int Ok = 0;               // READY, or a successful informational command
    public const int UsageError = 2;       // bad arguments / unknown ids
    public const int ConfigError = 3;      // invalid configuration
    public const int NoChanges = 9;        // NO_CHANGES
    public const int ReviewRequired = 10;  // REVIEW_REQUIRED
    public const int Blocked = 11;         // BLOCKED (conflicts, policy violations)
    public const int ValidationFailed = 12;// VALIDATION_FAILED
    public const int Stale = 13;           // plan invalidated: target base / policy / artifacts changed
    public const int PolicyViolation = 14; // operation refused by policy (direction/mode/protected branch)
    public const int FetchFailed = 15;     // remote fetch failed (online mode)
    public const int Safety = 16;          // harness safety check refused the operation
    public const int Internal = 20;        // unexpected error
    public const int Cancelled = 130;

    public static int For(RunStatus s) => s switch
    {
        RunStatus.READY => Ok,
        RunStatus.PLANNED => Ok,
        RunStatus.NO_CHANGES => NoChanges,
        RunStatus.REVIEW_REQUIRED => ReviewRequired,
        RunStatus.BLOCKED => Blocked,
        RunStatus.VALIDATION_FAILED => ValidationFailed,
        RunStatus.CANCELLED => Cancelled,
        RunStatus.ABORTED => Blocked,
        _ => Internal,
    };
}

public static class StatusEvaluator
{
    public static RunStatus ForPlan(Plan plan)
    {
        if (plan.Findings.Any(f => f.Blocking) || plan.Commits.Any(c => c.State == CommitState.Blocked) ||
            plan.Reconciliation?.State == CommitState.Blocked)
            return RunStatus.BLOCKED;
        bool anyPending = plan.Commits.Any(c => c.State == CommitState.Pending) || plan.Reconciliation?.State == CommitState.Pending;
        if (!anyPending)
            return plan.Findings.Any(f => f.RequiresReview && !plan.AcceptedFindings.Contains(f.Id)) ? RunStatus.REVIEW_REQUIRED : RunStatus.NO_CHANGES;
        if (plan.Findings.Any(f => f.RequiresReview && !plan.AcceptedFindings.Contains(f.Id))) return RunStatus.REVIEW_REQUIRED;
        return RunStatus.PLANNED;
    }

    /// <summary>READY only when: transfer applied, no blocking findings, all review findings
    /// accepted, and every required validation check passed on the candidate. A baseline that also
    /// fails never makes a candidate ready.</summary>
    public static RunStatus ForRun(Plan plan, Run run, IEnumerable<string> accepted)
    {
        var acc = accepted.ToHashSet();
        var all = plan.Findings.Concat(run.Findings).ToList();
        if (run.Conflict is not null || all.Any(f => f.Blocking)) return RunStatus.BLOCKED;
        if (run.CandidateSha is null || run.AppliedCommits.Count == 0 && plan.Reconciliation is null && plan.Mode != SelectionMode.Merge)
            return all.Any(f => f.RequiresReview && !acc.Contains(f.Id)) ? RunStatus.REVIEW_REQUIRED : RunStatus.NO_CHANGES;
        var requiredCandidate = run.CandidateValidation.Where(v => v.Required && v.Outcome != "SKIPPED").ToList();
        if (requiredCandidate.Count == 0 || requiredCandidate.Any(v => v.Outcome != "PASSED")) return RunStatus.VALIDATION_FAILED;
        if (all.Any(f => f.RequiresReview && !acc.Contains(f.Id))) return RunStatus.REVIEW_REQUIRED;
        return RunStatus.READY;
    }
}
