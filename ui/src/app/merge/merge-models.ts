/** DTOs of /api/merge/* (mirrors ForkBridge.Merge.Model). No Angular imports. */
export type RefNamespace = 'Remote' | 'Local' | 'CheckoutRemote';
export type MergeOutcome = 'UpToDate' | 'FastForward' | 'Clean' | 'Conflicts' | 'Blocked';
export type EntryStatus = 'Incoming' | 'AutoMerged' | 'Conflicted';
export type ConflictKind = 'None' | 'Content' | 'AddAdd' | 'DeletedInSource' | 'DeletedInTarget' | 'Binary' | 'RenameRename' | 'RenameDelete'
  | 'FileDirectory' | 'Symlink' | 'Submodule' | 'DistinctTypes' | 'Other';
export type SessionState = 'Creating' | 'Resolving' | 'Finalized' | 'Approved' | 'Published' | 'Aborted' | 'Damaged';
export type ResolutionAction = 'TakeTarget' | 'TakeSource' | 'EditResult' | 'KeepModified' | 'Delete';
/** Per-entry resolution state as the session reports it. */
export type EntryResolution = 'clean' | 'unsupported' | 'unresolved' | 'draft' | 'resolved';

export interface RefSelection { namespace: RefNamespace; name: string; }
export interface RefFreshness { refreshedAt?: string; offline: boolean; remoteFetched: boolean; note?: string; remoteUrlRedacted?: string; stale: boolean; }
export interface RefInfo { namespace: RefNamespace; name: string; sha: string; date?: string; subject?: string; }
export interface RefListing { alias: string; mirrorExists: boolean; freshness: RefFreshness; refs: RefInfo[]; }
export interface RepositorySummary { alias: string; kind: string; placeholder: boolean; mirrorExists: boolean; freshness: RefFreshness; validationConfigured: boolean; validationTrusted: boolean; remote: string; }

export interface PinnedRef { namespace: RefNamespace; name: string; fullRef: string; sha: string; subject?: string; commitDate?: string; }
export interface SideBlob { mode: string; sha: string; }
export interface EntryCapabilities { supported: boolean; editText: boolean; takeTarget: boolean; takeSource: boolean; keepModified: boolean; delete: boolean; restriction: string; reason?: string; }
export interface MergeEntry {
  id: string; path: string; oldPath?: string; status: EntryStatus; conflict: ConflictKind; targetChange?: string; sourceChange?: string;
  base?: SideBlob; target?: SideBlob; source?: SideBlob; result?: SideBlob; binary: boolean; messages: string[]; relatedPaths: string[]; capabilities: EntryCapabilities;
}
export interface MergeCounts { sourceOnlyCommits: number; targetOnlyCommits: number; changed: number; autoMerged: number; incoming: number; conflicted: number; unsupported: number; }
export interface MergeFinding { code: string; severity: string; message: string; paths?: string[]; }
export interface MergePlan {
  id: string; createdAt: string; repositoryAlias: string; remoteUrlRedacted?: string; source: PinnedRef; target: PinnedRef; freshness: RefFreshness;
  mergeBases: string[]; outcome: MergeOutcome; blockedReason?: string; resultTree?: string; counts: MergeCounts; entries: MergeEntry[];
  findings: MergeFinding[]; gitVersion: string; mergeOptions: Record<string, string>; optionsFingerprint: string; analysisHash?: string;
}
export interface ResolutionRecord { entryId: string; path: string; action: ResolutionAction; result?: SideBlob; resultContentHash?: string; markersAcknowledged: boolean; revision: number; at: string; }
export interface ValidationStep { stepId: string; kind: string; required: boolean; outcome: string; exitCode?: number; durationSeconds: number; reason?: string; outputTail?: string; }
export interface ValidationRecord { id: string; candidateSha: string; sessionRevision: number; at: string; verdict: string; steps: ValidationStep[]; note?: string; }
export interface MergeApproval { binding: string; bindingHash: string; candidateSha: string; sessionRevision: number; validationId: string; integrationBranch: string; createPullRequest: boolean; validationWaived: boolean; approvedBy: string; approvedAt: string; }
export interface MergePublication { at: string; branch: string; candidateSha: string; pushed: boolean; alreadyPresent: boolean; pullRequestUrl?: string; sourceMovedTo?: string; error?: string; }
export interface MergeSession {
  id: string; planId: string; repositoryAlias: string; createdAt: string; revision: number; state: SessionState; stateReason?: string;
  resolutions: Record<string, ResolutionRecord>; drafts: Record<string, { entryId: string; contentHash: string; revision: number; at: string }>;
  candidateSha?: string; candidateTree?: string; validations: ValidationRecord[]; approval?: MergeApproval; publications: MergePublication[]; log: string[];
}
export interface SessionEntryState { entryId: string; path: string; status: EntryStatus; conflict: ConflictKind; supported: boolean; resolution: EntryResolution; action?: ResolutionAction; hasDraft: boolean; }
export interface SessionView {
  session: MergeSession; plan: MergePlan; entries: SessionEntryState[]; unresolved: number; resolved: number; unsupported: number; canFinalize: boolean;
  integrationBranch?: string; pullRequestProviderConfigured: boolean; currentValidation?: ValidationRecord;
}
export interface SessionResponse { view: SessionView; integrity: { ok: boolean; problems: string[] }; }
export interface CodecInfo { encoding?: string; eol: string; finalNewline: boolean; binary: boolean; size: number; editable: boolean; restriction: string; reason?: string; }
export interface SideView { label: string; blob?: SideBlob; codec?: CodecInfo; text?: string; }
export interface DiffView { diff: string; truncated: boolean; binary: boolean; }
export interface FileView {
  entry: MergeEntry; sourceLabel: string; targetLabel: string; state: 'unresolved' | 'draft' | 'resolved' | 'clean'; sessionRevision: number;
  base: SideView; target: SideView; source: SideView; result: SideView; resultDeleted: boolean; resultContentHash: string; editable: boolean; editBlockedReason?: string;
  markerLines: number; resolution?: ResolutionRecord; draft?: unknown; targetChanges: DiffView; sourceChanges: DiffView; targetToResult: DiffView;
  filterAttribute?: string; mergeAttribute?: string;
}
