using System.Text.Json.Serialization;

namespace ForkBridge.Core.Model;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunStatus { PLANNED, READY, REVIEW_REQUIRED, BLOCKED, VALIDATION_FAILED, NO_CHANGES, IN_PROGRESS, ABORTED, CANCELLED }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Severity { Info, Warning, Error, Critical }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Origin { TransferIntroduced, PreExisting, Environment, Policy }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SelectionMode { Feature, Range, Commits, Reconcile, Merge }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Decision { Include, Exclude, Block }

public sealed record Finding
{
    /// <summary>Stable ID: rule code + short hash of the identifying facts, so the same issue keeps
    /// the same ID across re-plans.</summary>
    public required string Id { get; init; }
    public required string Code { get; init; }
    public required string Category { get; init; }
    public required Severity Severity { get; init; }
    public required string Title { get; init; }
    public string? Component { get; init; }
    public List<string> Files { get; init; } = new();
    public List<string> Commits { get; init; } = new();
    public List<string> Evidence { get; init; } = new();
    public Origin Origin { get; init; } = Origin.TransferIntroduced;
    /// <summary>Blocks publication outright.</summary>
    public bool Blocking { get; init; }
    /// <summary>Requires an explicit human decision (accept) before publication.</summary>
    public bool RequiresReview { get; init; }
    public string? SuggestedAction { get; init; }

    public static string MakeId(string code, params string?[] facts)
    {
        var h = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code + "|" + string.Join("|", facts)));
        return $"FB-{code}-{Convert.ToHexString(h)[..8].ToLowerInvariant()}";
    }
}

public sealed record RepoIdentity
{
    public required string Alias { get; init; }
    public required string Product { get; init; }
    public required string Path { get; init; }
    public string? RemoteUrlRedacted { get; init; }
    public string? RootCommit { get; init; }
    public string ObjectFormat { get; init; } = "sha1";
}

public sealed record Freshness
{
    public bool Offline { get; init; }
    public bool RemoteFetched { get; init; }
    public DateTimeOffset? FetchedAt { get; init; }
    public string? Note { get; init; }
}

public sealed record PathDecision
{
    public required string SourcePath { get; init; }
    public string? SourceOldPath { get; init; }
    public string? TargetPath { get; init; }
    public string? TargetOldPath { get; init; }
    public required char Status { get; init; }
    public required string SourceClass { get; init; }
    public string? TargetClass { get; init; }
    public string? Component { get; init; }
    public required Decision Decision { get; init; }
    public required string Reason { get; init; }
    public bool Binary { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CommitState
{
    Pending,                 // will be transferred
    EquivalentPresent,       // content already present in target (evidence-based)
    AlreadyInTargetHistory,  // the commit itself is an ancestor of the target base
    NothingToTransfer,       // no included paths after policy
    PreviouslyTransferredMissing, // provenance says transferred but content is gone/edited
    Blocked,
}

public sealed record PlannedCommit
{
    public required string Sha { get; init; }
    public required string Subject { get; init; }
    public required string Author { get; init; }
    public required string AuthorEmail { get; init; }
    public required string AuthorDate { get; init; }
    public bool IsMerge { get; init; }
    public List<PathDecision> Paths { get; init; } = new();
    public bool Mixed => Paths.Any(p => p.Decision == Decision.Include) && Paths.Any(p => p.Decision != Decision.Include);
    public CommitState State { get; set; }
    public string? StateEvidence { get; set; }
    /// <summary>File name of the path-mapped patch artifact inside the plan directory.</summary>
    public string? PatchFile { get; init; }
    public string? PatchSha256 { get; init; }
    /// <summary>`git patch-id --stable` of the mapped, path-scoped patch.</summary>
    public string? PatchId { get; init; }
    public List<string> Components { get; init; } = new();
}

public sealed record ComponentDrift
{
    public required string Component { get; init; }
    public required string SourcePath { get; init; }
    public required string TargetPath { get; init; }
    public bool PresentInSource { get; init; }
    public bool PresentInTarget { get; init; }
    public bool IntentionallyAbsentInTarget { get; init; }
    public int Identical { get; init; }
    public List<string> Differing { get; init; } = new();
    public List<string> OnlyInSource { get; init; } = new();
    public List<string> OnlyInTarget { get; init; } = new();
    /// <summary>Differing files changed on the target side since the baseline (pre-existing target divergence).</summary>
    public List<string> TargetDivergedSinceBaseline { get; init; } = new();
    public List<string> SourceChangedSinceBaseline { get; init; } = new();
    public List<string> PreservedProductDifferences { get; init; } = new();
    public string? Baseline { get; init; }
}

public sealed record Plan
{
    public required string Id { get; init; }
    public int SchemaVersion { get; init; } = 1;
    public required DateTimeOffset CreatedAt { get; init; }
    public required string Pair { get; init; }
    public required string FromProduct { get; init; }
    public required string ToProduct { get; init; }
    public required RepoIdentity Source { get; init; }
    public required RepoIdentity Target { get; init; }
    public required SelectionMode Mode { get; init; }
    public string? SourceRef { get; init; }
    public string? SourceBaseRef { get; init; }
    public required string TargetBaseRef { get; init; }
    public required string SourceHeadSha { get; init; }
    public string? SourceBaseSha { get; init; }
    public required string TargetBaseSha { get; init; }
    public string? MergeBase { get; init; }
    public string? MergeBaseExplanation { get; init; }
    public string? RangeExpression { get; init; }
    public List<string> SelectedComponents { get; init; } = new();
    public List<PlannedCommit> Commits { get; init; } = new();
    /// <summary>Reconcile mode: single aggregated patch from baseline to source snapshot.</summary>
    public string? ReconcileBaseline { get; init; }
    public PlannedCommit? Reconciliation { get; init; }
    public List<ComponentDrift> Drift { get; init; } = new();
    public List<Finding> Findings { get; init; } = new();
    public ForkBridge.Core.Analysis.DependencyImpact? Impact { get; init; }
    public required string PolicyHash { get; init; }
    public required Freshness Freshness { get; init; }
    public List<string> AcceptedFindings { get; init; } = new();
    public RunStatus PreliminaryStatus { get; set; }
    public string? ContentHash { get; set; }
}

public sealed record ValidationStepResult
{
    public required string StepId { get; init; }
    public required string Kind { get; init; }
    public bool Required { get; init; }
    /// <summary>PASSED | FAILED | UNAVAILABLE | SKIPPED | TIMED_OUT | CANCELLED</summary>
    public required string Outcome { get; init; }
    public int? ExitCode { get; init; }
    public double DurationSeconds { get; init; }
    public string? OutputTail { get; init; }
    public string? Reason { get; init; }
}

public sealed record ValidationComparison
{
    public required string StepId { get; init; }
    public required string Baseline { get; init; }
    public required string Candidate { get; init; }
    /// <summary>OK | NEW_FAILURE | BASELINE_FAILURE | RESOLVED | UNAVAILABLE</summary>
    public required string Verdict { get; init; }
    public bool Required { get; init; }
}

public sealed record ConflictRecord
{
    public required string Operation { get; init; }
    public string? SourceCommit { get; init; }
    public List<string> Files { get; init; } = new();
    public string? EvidenceDir { get; init; }
    public string? Output { get; init; }
    public List<string> Instructions { get; init; } = new();
}

public sealed record ApprovalRecord
{
    public required string CandidateSha { get; init; }
    public required string TargetRemoteRedacted { get; init; }
    public required string Branch { get; init; }
    public required string ReportSha256 { get; init; }
    public required string PolicyHash { get; init; }
    public required string TargetBaseSha { get; init; }
    public List<string> AcceptedFindings { get; init; } = new();
    public required string ApprovedBy { get; init; }
    public required DateTimeOffset ApprovedAt { get; init; }
    public string Binding => $"{CandidateSha}|{TargetRemoteRedacted}|{Branch}|{ReportSha256}|{PolicyHash}|{TargetBaseSha}";
}

public sealed record PublicationRecord
{
    public required DateTimeOffset At { get; init; }
    public required string Branch { get; init; }
    public required string CandidateSha { get; init; }
    public bool Pushed { get; init; }
    public string? PullRequestUrl { get; init; }
    public string? Error { get; init; }
}

public sealed record Run
{
    public required string Id { get; init; }
    public required string PlanId { get; init; }
    public string? BundleId { get; set; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; set; }
    public RunStatus Status { get; set; } = RunStatus.IN_PROGRESS;
    public required string WorkspaceDir { get; init; }
    public required string CandidateWorktree { get; init; }
    public string? BaselineWorktree { get; set; }
    public required string Branch { get; init; }
    public string? CandidateSha { get; set; }
    public List<string> AppliedCommits { get; init; } = new();
    public Dictionary<string, string> ResultCommits { get; init; } = new(); // source sha -> target sha
    public int NextCommitIndex { get; set; }
    public ConflictRecord? Conflict { get; set; }
    public List<Finding> Findings { get; init; } = new();
    public List<ValidationStepResult> BaselineValidation { get; set; } = new();
    public List<ValidationStepResult> CandidateValidation { get; set; } = new();
    public List<ValidationComparison> ValidationComparison { get; set; } = new();
    public string? CandidateDiffFile { get; set; }
    public string? CandidateDiffStat { get; set; }
    public ApprovalRecord? Approval { get; set; }
    public List<PublicationRecord> Publications { get; init; } = new();
    public bool PreserveWorkspace { get; set; }
    public bool ValidationTrusted { get; set; }
    public string? Error { get; set; }
    public List<string> Log { get; init; } = new();
}

public sealed record Bundle
{
    public required string Id { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string? Name { get; init; }
    public List<string> PlanIds { get; init; } = new();
    public Dictionary<string, string> RunIds { get; init; } = new(); // planId -> runId
    public List<Finding> Findings { get; init; } = new();
    public List<ValidationStepResult> CombinedValidation { get; set; } = new();
    public RunStatus Status { get; set; } = RunStatus.PLANNED;
    public List<string> PublicationLog { get; init; } = new();
}
