/** Pure presentation logic of the Merge tab (no Angular): filters, labels and state wording. Unit-tested with node --test. */
import type { ConflictKind, EntryResolution, EntryStatus, MergeEntry, MergeOutcome, RefNamespace, SessionEntryState } from './merge-models';

export type MergeFilter = 'all' | 'unresolved' | 'resolved' | 'auto' | 'special';
export const FILTERS: { key: MergeFilter; label: string }[] = [
  { key: 'all', label: 'All changes' }, { key: 'unresolved', label: 'Unresolved' }, { key: 'resolved', label: 'Resolved' },
  { key: 'auto', label: 'Auto-merged' }, { key: 'special', label: 'Special cases' },
];

export interface Row { id: string; path: string; oldPath?: string; status: EntryStatus; conflict: ConflictKind; supported: boolean; resolution: EntryResolution; entry: MergeEntry; }

/** Joins plan entries with session state (no session = analysis only: conflicted entries read as unresolved). */
export function rows(entries: MergeEntry[], session?: SessionEntryState[]): Row[] {
  const byId = new Map((session ?? []).map((s) => [s.entryId, s]));
  return entries.map((e) => {
    const s = byId.get(e.id);
    const resolution: EntryResolution = s?.resolution ?? (e.status !== 'Conflicted' ? 'clean' : !e.capabilities.supported ? 'unsupported' : 'unresolved');
    return { id: e.id, path: e.path, oldPath: e.oldPath, status: e.status, conflict: e.conflict, supported: e.capabilities.supported, resolution, entry: e };
  });
}

export function matches(r: Row, f: MergeFilter, q = ''): boolean {
  const okF = f === 'all' ? true
    : f === 'unresolved' ? r.resolution === 'unresolved' || r.resolution === 'draft'
    : f === 'resolved' ? r.resolution === 'resolved'
    : f === 'auto' ? r.status === 'AutoMerged'
    : !r.supported || r.entry.binary || r.conflict === 'DeletedInSource' || r.conflict === 'DeletedInTarget' || r.conflict === 'AddAdd';
  if (!okF) return false;
  const words = q.toLowerCase().split(/\s+/).filter(Boolean);
  const hay = (r.path + ' ' + (r.oldPath ?? '')).toLowerCase();
  return words.every((w) => hay.includes(w));
}

export function counts(list: Row[]): Record<MergeFilter, number> {
  return { all: list.length, unresolved: list.filter((r) => matches(r, 'unresolved')).length, resolved: list.filter((r) => matches(r, 'resolved')).length,
    auto: list.filter((r) => matches(r, 'auto')).length, special: list.filter((r) => matches(r, 'special')).length };
}

export function outcomeLabel(o: MergeOutcome): { label: string; tone: 'ok' | 'warn' | 'bad' | 'info'; text: string } {
  switch (o) {
    case 'UpToDate': return { label: 'Already up to date', tone: 'info', text: 'The source is already contained in the target. Nothing to merge.' };
    case 'FastForward': return { label: 'Fast-forward possible', tone: 'ok', text: 'The target has no commits of its own. ForkBridge still creates a merge commit (no fast-forward) so the integration branch is reviewable.' };
    case 'Clean': return { label: 'Clean merge', tone: 'ok', text: 'Git merged every file without conflict.' };
    case 'Conflicts': return { label: 'Conflicts need resolution', tone: 'warn', text: 'Git left some paths unmerged. Resolve them in a merge session.' };
    default: return { label: 'Blocked', tone: 'bad', text: 'This merge cannot proceed here.' };
  }
}

export function statusLabel(r: Pick<Row, 'status' | 'conflict'> & { entry: { binary: boolean } }): string {
  if (r.status === 'Incoming') return 'Incoming (source only)';
  if (r.status === 'AutoMerged') return 'Auto-merged';
  return conflictLabel(r.conflict, r.entry.binary);
}

export function conflictLabel(k: ConflictKind, binary = false): string {
  switch (k) {
    case 'Content': return binary ? 'Binary conflict' : 'Content conflict';
    case 'AddAdd': return binary ? 'Add/add (binary)' : 'Add/add conflict';
    case 'DeletedInSource': return 'Modified in target, deleted in source';
    case 'DeletedInTarget': return 'Deleted in target, modified in source';
    case 'Binary': return 'Binary conflict';
    case 'RenameRename': return 'Rename conflict';
    case 'RenameDelete': return 'Rename/delete conflict';
    case 'FileDirectory': return 'File/directory collision';
    case 'Symlink': return 'Symlink conflict';
    case 'Submodule': return 'Submodule conflict';
    case 'DistinctTypes': return 'File type/mode conflict';
    case 'None': return 'No conflict';
    default: return 'Other conflict';
  }
}

export function resolutionLabel(r: EntryResolution): string {
  return { clean: 'Merged by Git', unsupported: 'Not supported here', unresolved: 'Unresolved', draft: 'Draft saved (not resolved)', resolved: 'Resolved' }[r];
}

export function namespaceLabel(ns: RefNamespace): string {
  return { Remote: 'remote (fetched by ForkBridge)', Local: 'local branch (your checkout)', CheckoutRemote: "checkout's remote-tracking ref" }[ns];
}

export function short(sha?: string | null): string { return sha ? sha.slice(0, 10) : '—'; }

/** True when the editor text differs from what was loaded (unsaved edits). */
export function isDirty(loaded: string | undefined, current: string | undefined): boolean { return (loaded ?? '') !== (current ?? ''); }

/** Turns an API error message into a user hint (stale saves get an explicit reload instruction). */
export function errorHint(message: string): { stale: boolean; text: string } {
  const stale = /\(exit 13\)/.test(message) || /changed since you loaded it/.test(message);
  return { stale, text: stale ? message.replace(/\s*\(exit 13\)$/, '') + ' Your unsaved text stays in the editor until you click Reload file.' : message };
}
