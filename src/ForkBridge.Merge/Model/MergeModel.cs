using System.Text.Json.Serialization;

namespace ForkBridge.Merge.Model;

/// <summary>Where a selectable ref lives in the harness mirror. The namespace is part of the selection so a
/// branch name is never ambiguous between the checkout's local branch and the remote branch.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RefNamespace
{
    /// <summary>refs/remotes/origin/* - fetched by ForkBridge straight from the checkout's remote URL.</summary>
    Remote,
    /// <summary>refs/fb/local/heads/* - the checkout's local branches as of the last refresh.</summary>
    Local,
    /// <summary>refs/fb/local/remote/* - the checkout's own remote-tracking refs (as last fetched by the user).</summary>
    CheckoutRemote,
}

public sealed record RefSelection(RefNamespace Namespace, string Name);

public sealed record PinnedRef
{
    public required RefNamespace Namespace { get; init; }
    public required string Name { get; init; }
    /// <summary>Full ref inside the harness mirror, e.g. refs/remotes/origin/develop.</summary>
    public required string FullRef { get; init; }
    public required string Sha { get; init; }
    public string? Subject { get; init; }
    public string? CommitDate { get; init; }
}

public sealed record RefFreshness
{
    public DateTimeOffset? RefreshedAt { get; init; }
    public bool Offline { get; init; }
    public bool RemoteFetched { get; init; }
    public string? Note { get; init; }
    public string? RemoteUrlRedacted { get; init; }
    /// <summary>True when there is no refresh record or it is older than the staleness window.</summary>
    public bool Stale { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MergeOutcome { UpToDate, FastForward, Clean, Conflicts, Blocked }

/// <summary>What happened to a path when Git merged source into target.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EntryStatus
{
    /// <summary>Changed only by the source branch; taken cleanly.</summary>
    Incoming,
    /// <summary>Changed by both branches; Git merged it without conflict.</summary>
    AutoMerged,
    /// <summary>Git left the path unmerged.</summary>
    Conflicted,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConflictKind
{
    None,
    Content,
    AddAdd,
    /// <summary>Target modified, source deleted.</summary>
    DeletedInSource,
    /// <summary>Target deleted, source modified.</summary>
    DeletedInTarget,
    Binary,
    RenameRename,
    RenameDelete,
    FileDirectory,
    Symlink,
    Submodule,
    DistinctTypes,
    Other,
}

/// <summary>Why an entry cannot be resolved here (null = no restriction).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Restriction
{
    None,
    StructuralConflict,
    Symlink,
    Submodule,
    FilteredContent,
    UnsupportedEncoding,
    MixedLineEndings,
    Oversized,
    CaseCollision,
    UnsafePath,
}

public sealed record EntryCapabilities
{
    /// <summary>False = the entry cannot be resolved here at all and blocks finalization.</summary>
    public bool Supported { get; init; }
    public bool EditText { get; init; }
    public bool TakeTarget { get; init; }
    public bool TakeSource { get; init; }
    public bool KeepModified { get; init; }
    public bool Delete { get; init; }
    public Restriction Restriction { get; init; }
    public string? Reason { get; init; }
}

public sealed record SideBlob(string Mode, string Sha);

/// <summary>One path of the merge. Ids are derived from the path(s) so they are stable across analysis and session.</summary>
public sealed record MergeEntry
{
    public required string Id { get; init; }
    public required string Path { get; init; }
    public string? OldPath { get; init; }
    public required EntryStatus Status { get; init; }
    public ConflictKind Conflict { get; init; }
    /// <summary>Change letter base→target / base→source (A, M, D, R, T or null when unchanged).</summary>
    public string? TargetChange { get; init; }
    public string? SourceChange { get; init; }
    public SideBlob? Base { get; init; }
    public SideBlob? Target { get; init; }
    public SideBlob? Source { get; init; }
    /// <summary>Blob in the analysis result tree (null for a conflicted path that is deleted, or absent).</summary>
    public SideBlob? Result { get; init; }
    public bool Binary { get; init; }
    public List<string> Messages { get; init; } = new();
    /// <summary>Paths that Git reported in the same conflict message (renames).</summary>
    public List<string> RelatedPaths { get; init; } = new();
    public required EntryCapabilities Capabilities { get; init; }
}

public sealed record MergeCounts(int SourceOnlyCommits, int TargetOnlyCommits, int Changed, int AutoMerged, int Incoming, int Conflicted, int Unsupported);

public sealed record MergeFinding(string Code, string Severity, string Message, List<string>? Paths = null);

/// <summary>Immutable merge analysis. Written once, read-only, content-hashed (id and time excluded).</summary>
public sealed record MergePlan
{
    public required string Id { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string RepositoryAlias { get; init; }
    public string? RemoteUrlRedacted { get; init; }
    public required string ObjectFormat { get; init; }
    public required PinnedRef Source { get; init; }
    public required PinnedRef Target { get; init; }
    public required RefFreshness Freshness { get; init; }
    public required List<string> MergeBases { get; init; }
    public required MergeOutcome Outcome { get; init; }
    public string? BlockedReason { get; init; }
    public string? ResultTree { get; init; }
    public required MergeCounts Counts { get; init; }
    public required List<MergeEntry> Entries { get; init; }
    public List<MergeFinding> Findings { get; init; } = new();
    public required string GitVersion { get; init; }
    public required Dictionary<string, string> MergeOptions { get; init; }
    public required string OptionsFingerprint { get; init; }
    public string? AnalysisHash { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SessionState
{
    /// <summary>Workspace being created (or creation interrupted: recover rebuilds it).</summary>
    Creating,
    Resolving,
    /// <summary>Candidate commit created; resolution content frozen until reopened.</summary>
    Finalized,
    Approved,
    Published,
    Aborted,
    /// <summary>Workspace missing or modified outside the harness; recover rebuilds it from recorded resolutions.</summary>
    Damaged,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResolutionAction { TakeTarget, TakeSource, EditResult, KeepModified, Delete }

public sealed record ResolutionRecord
{
    public required string EntryId { get; init; }
    public required string Path { get; init; }
    public SideBlob? Base { get; init; }
    public SideBlob? Target { get; init; }
    public SideBlob? Source { get; init; }
    public required ResolutionAction Action { get; init; }
    /// <summary>Resulting index entry; null = path deleted.</summary>
    public SideBlob? Result { get; init; }
    /// <summary>SHA-256 of the resulting bytes (null for deletion).</summary>
    public string? ResultContentHash { get; init; }
    public bool MarkersAcknowledged { get; init; }
    public required int Revision { get; init; }
    public required DateTimeOffset At { get; init; }
}

public sealed record DraftRecord
{
    public required string EntryId { get; init; }
    public required string ContentHash { get; init; }
    public required int Revision { get; init; }
    public required DateTimeOffset At { get; init; }
}

public sealed record ValidationStepRecord(string StepId, string Kind, bool Required, string Outcome, int? ExitCode, double DurationSeconds, string? Reason, string? OutputTail);

public sealed record ValidationRecord
{
    public required string Id { get; init; }
    public required string CandidateSha { get; init; }
    public required int SessionRevision { get; init; }
    public required DateTimeOffset At { get; init; }
    /// <summary>PASSED | FAILED | NOT_CONFIGURED | UNTRUSTED | USER_REPO_MODIFIED</summary>
    public required string Verdict { get; init; }
    public required List<ValidationStepRecord> Steps { get; init; }
    public string? Note { get; init; }
}

public sealed record MergeApproval
{
    public required string Binding { get; init; }
    public required string BindingHash { get; init; }
    public required string CandidateSha { get; init; }
    public required int SessionRevision { get; init; }
    public required string ValidationId { get; init; }
    public required string IntegrationBranch { get; init; }
    public required bool CreatePullRequest { get; init; }
    public bool ValidationWaived { get; init; }
    public required string ApprovedBy { get; init; }
    public required DateTimeOffset ApprovedAt { get; init; }
}

public sealed record MergePublication
{
    public required DateTimeOffset At { get; init; }
    public required string Branch { get; init; }
    public required string CandidateSha { get; init; }
    public required bool Pushed { get; init; }
    public bool AlreadyPresent { get; init; }
    public string? PullRequestUrl { get; init; }
    public string? SourceMovedTo { get; init; }
    public string? Error { get; init; }
}

public sealed record MergeSession
{
    public required string Id { get; init; }
    public required string PlanId { get; init; }
    public required string RepositoryAlias { get; init; }
    public required string WorkspaceDir { get; init; }
    public required string Worktree { get; init; }
    public required DateTimeOffset CreatedAt { get; set; }
    public int Revision { get; set; }
    public SessionState State { get; set; }
    public string? StateReason { get; set; }
    public Dictionary<string, ResolutionRecord> Resolutions { get; init; } = new();
    public Dictionary<string, DraftRecord> Drafts { get; init; } = new();
    public string? CandidateSha { get; set; }
    public string? CandidateTree { get; set; }
    public List<ValidationRecord> Validations { get; init; } = new();
    public MergeApproval? Approval { get; set; }
    public List<MergePublication> Publications { get; init; } = new();
    /// <summary>Committer identity read from the checkout's config at creation (used for the candidate commit).</summary>
    public required string CommitterName { get; init; }
    public required string CommitterEmail { get; init; }
    public List<string> Log { get; init; } = new();
}
