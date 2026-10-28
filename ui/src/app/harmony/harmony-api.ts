import { Injectable, inject } from '@angular/core';
import { Api } from '../api';

/** Typed client for /api/align/* (Harmony). Uses the shared Api for the token, errors and job polling. */
@Injectable({ providedIn: 'root' })
export class HarmonyApi {
  readonly api = inject(Api);

  profiles() { return this.api.get<ProfilesResponse>('/align/profiles'); }
  worktrees(profile: string) { return this.api.get<Worktree[]>(`/align/${enc(profile)}/worktrees`); }
  plans(profile: string) { return this.api.get<PlanSummary[]>(`/align/${enc(profile)}/plans`); }
  plan(profile: string, id: string) { return this.api.get<AlignPlan>(`/align/${enc(profile)}/plans/${enc(id)}`); }
  blob(profile: string, id: string, sha: string) { return this.api.get<Blob>(`/align/${enc(profile)}/plans/${enc(id)}/blob?sha=${enc(sha)}`); }
  runPlan(profile: string, body: PlanBody) { return this.api.job<{ id: string; contentHash: string; rows: number; identical: number }>('Planning', `/align/${enc(profile)}/plan`, body); }
  saveDecisions(profile: string, id: string, edits: Record<string, string>) {
    return this.api.post<{ id: string; parentPlanId: string; contentHash: string; changed: number }>(`/align/${enc(profile)}/plans/${enc(id)}/decisions`, { edits });
  }
  advise(profile: string, body: AdviseBody) { return this.api.job<AdvisorResult>('Running the SyncBase Advisor', `/align/${enc(profile)}/syncbase/advise`, body); }
  setSyncBase(profile: string, sha: string, note?: string) { return this.api.post<{ syncBase: string }>(`/align/${enc(profile)}/profile/syncbase`, { sha, note }); }
  addOverride(profile: string, pattern: string, decision: string, reason: string) {
    return this.api.post<{ pattern: string }>(`/align/${enc(profile)}/profile/override`, { pattern, decision, reason });
  }

  /** Session SyncBase chosen on the SyncBase screen; used by the next plan of that profile (not the profile file). */
  sessionSyncBase(profile: string): string | null { try { return sessionStorage.getItem('harmony.syncBase.' + profile); } catch { return null; } }
  setSessionSyncBase(profile: string, sha: string | null) {
    try { if (sha) sessionStorage.setItem('harmony.syncBase.' + profile, sha); else sessionStorage.removeItem('harmony.syncBase.' + profile); } catch { /* storage unavailable */ }
  }
}

const enc = encodeURIComponent;

export type Decision = 'TakePrimary' | 'TakeFork' | 'Merged' | 'DeleteBoth' | 'KeepForkOnly' | 'KeepBoth' | 'ManifestUnion' | 'Manual';
export interface ProfileInfo {
  name: string; valid: boolean; error?: string; hash?: string; syncBase?: string; scope?: string[]; code?: string;
  primary?: { label: string; repo: string; root: string; plannedBranch?: string };
  fork?: { label: string; repo: string; root: string; plannedBranch?: string };
  neverPush?: string[]; weights?: { revertWeight: number; mergeWeight: number; conflictWeight: number };
}
export interface ProfilesResponse { configPath: string; found: boolean; profiles: ProfileInfo[]; }
export interface Worktree {
  id: string; side: 'Primary' | 'Fork'; repoAlias: string; path: string; branch?: string; head?: string;
  isMain: boolean; detached: boolean; bare: boolean; locked: boolean; prunable: boolean; exists: boolean;
  scopeDirty?: boolean; ahead?: number; behind?: number; protected: boolean;
}
export interface PlanSummary {
  id: string; parentPlanId?: string; createdAt?: string; preview?: boolean; rows?: number; identical?: number; syncBase?: string; manual?: number;
  primary?: { branch?: string; rev: string }; fork?: { branch?: string; rev: string }; error?: string;
}
export interface BlobRef { sha: string; mode: string; }
export interface AlignRow {
  path: string; category: string; decision: Decision; reason: string; refs: string;
  base?: BlobRef; primary?: BlobRef; fork?: BlobRef; merged?: BlobRef; cleanMerge: boolean;
}
export interface SideState { repoAlias: string; label: string; branch?: string; rev: string; commitRev: string; scopedTree: string; worktreePath?: string; }
export interface AlignPlan {
  id: string; parentPlanId?: string; profile: string; profileHash: string; syncBase: string; scope: string[];
  primary: SideState; fork: SideState; baseTree: string; mergedTree: string; identical: number; preview: boolean;
  rows: AlignRow[]; createdAt: string; contentHash: string;
}
export interface Blob { sha: string; size: number; binary: boolean; truncated: boolean; text?: string; }
export interface PlanBody { primaryWorktreeId?: string; forkWorktreeId?: string; preview?: boolean; syncBase?: string; }
export interface AdviseBody { planId?: string; primaryWorktreeId?: string; forkWorktreeId?: string; revertWeight?: number; mergeWeight?: number; conflictWeight?: number; }
export interface LinePoint { sha: string; date: string; author: string; subject: string; score: number; risk: number; merges: number; transient: boolean; }
export interface AdvisorLine { name: string; ref: string; selected: boolean; commits: LinePoint[]; }
export interface ForkBulk { sha: string; date: string; author: string; subject: string; scopedFiles: number; matchSha?: string; same: number; differ: number; }
export interface Candidate {
  sha: string; date: string; author: string; subject: string; sources: string[]; score: number; risk: number; merges: number; conflicts: number;
  promotions: number; absorbed: number; transient: number; rows: number; fit: number; riskSample: string[]; isTransient: boolean; fingerprint: string;
}
export interface AdvisorResult {
  profile: string; primaryRev: string; forkRev: string; weights: { revert: number; merge: number; conflict: number }; configured?: string;
  recommended?: string; confidence: string; rationale: string[]; candidates: Candidate[]; lines: AdvisorLine[]; forkBulk: ForkBulk[]; warnings: string[];
}

/** Direction vocabulary shared by both screens (dark-theme colours; orange is reserved for selection/actions). */
export const DECISIONS: { key: Decision; label: string; legacy: (code: string) => string; fg: string; rgb: string }[] = [
  { key: 'TakePrimary', label: 'QiSL → ISL', legacy: (c) => 'TAKE_' + c, fg: '#7DD3FC', rgb: '56,189,248' },
  { key: 'TakeFork', label: 'ISL → QiSL', legacy: () => 'TAKE_ISL', fg: '#FCD34D', rgb: '251,191,36' },
  { key: 'Merged', label: '⇄ merged', legacy: () => 'MERGED', fg: '#5EEAD4', rgb: '45,212,191' },
  { key: 'DeleteBoth', label: '✕ delete both', legacy: () => 'DELETE_BOTH', fg: '#FCA5A5', rgb: '248,113,113' },
  { key: 'KeepForkOnly', label: 'ISL only', legacy: () => 'KEEP_ISL_ONLY', fg: '#C4B5FD', rgb: '167,139,250' },
  { key: 'KeepBoth', label: '∥ diverge', legacy: () => 'KEEP_BOTH', fg: '#D4D4D8', rgb: '161,161,170' },
  { key: 'ManifestUnion', label: '∪ union', legacy: () => 'PACKAGES_UNION', fg: '#A5B4FC', rgb: '129,140,248' },
  { key: 'Manual', label: '⚠ decide', legacy: () => 'MANUAL', fg: '#F9A8D4', rgb: '244,114,182' },
];

const CATEGORY_LEGACY: Record<string, (c: string) => string> = {
  Packages: () => 'PACKAGES', FormatOnly: () => 'FORMAT_ONLY', PrimaryChanged: (c) => c + '_CHANGED', PrimaryAdded: (c) => c + '_ADDED',
  PrimaryDeleted: (c) => c + '_DELETED', ForkBehind: () => 'ISL_BEHIND', ForkStale: () => 'ISL_STALE', ForkDeleted: () => 'ISL_DELETED',
  ForkAdded: () => 'ISL_ADDED', ForkChanged: () => 'ISL_CHANGED', BothChanged: () => 'BOTH_CHANGED', Conflict: () => 'CONFLICT',
  ForkOwned: () => 'FORK_OWNED', ForkRenamed: () => 'ISL_RENAMED', ForkRenameSource: () => 'ISL_RENAME_SOURCE', ForkOnly: () => 'ISL_ONLY',
};
export const categoryLabel = (c: string, code: string) => (CATEGORY_LEGACY[c] ?? (() => c))(code);
export const decisionInfo = (d: Decision) => DECISIONS.find((x) => x.key === d) ?? DECISIONS[7];
export const short = (s?: string | null, n = 9) => (s ? s.slice(0, n) : '—');

/** Line diff (LCS) for the 3-way viewer; bounded so a huge file never freezes the page. */
export interface DiffLine { t: ' ' | '+' | '-'; s: string; }
export function lineDiff(a: string, b: string, maxCells = 4_000_000): DiffLine[] | null {
  const x = a.replace(/\r\n/g, '\n').split('\n'), y = b.replace(/\r\n/g, '\n').split('\n');
  let s = 0; while (s < x.length && s < y.length && x[s] === y[s]) s++;
  let ex = x.length, ey = y.length; while (ex > s && ey > s && x[ex - 1] === y[ey - 1]) { ex--; ey--; }
  const xs = x.slice(s, ex), ys = y.slice(s, ey);
  if (xs.length * ys.length > maxCells) return null;
  const n = xs.length, m = ys.length;
  const dp: Uint32Array[] = Array.from({ length: n + 1 }, () => new Uint32Array(m + 1));
  for (let i = n - 1; i >= 0; i--) for (let j = m - 1; j >= 0; j--) dp[i][j] = xs[i] === ys[j] ? dp[i + 1][j + 1] + 1 : Math.max(dp[i + 1][j], dp[i][j + 1]);
  const out: DiffLine[] = [];
  const ctx = 3;
  for (let k = Math.max(0, s - ctx); k < s; k++) out.push({ t: ' ', s: x[k] });
  let i = 0, j = 0;
  while (i < n || j < m) {
    if (i < n && j < m && xs[i] === ys[j]) { out.push({ t: ' ', s: xs[i] }); i++; j++; }
    else if (j < m && (i >= n || dp[i][j + 1] >= dp[i + 1][j])) { out.push({ t: '+', s: ys[j] }); j++; }
    else { out.push({ t: '-', s: xs[i] }); i++; }
  }
  for (let k = ex; k < Math.min(x.length, ex + ctx); k++) out.push({ t: ' ', s: x[k] });
  return out;
}
