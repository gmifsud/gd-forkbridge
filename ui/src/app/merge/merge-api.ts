import { Injectable, inject } from '@angular/core';
import { Api } from '../api';
import type { FileView, MergeOutcome, MergePlan, RefFreshness, RefListing, RefSelection, RepositorySummary, ResolutionAction, SessionResponse, SessionView } from './merge-models';

const enc = encodeURIComponent;

/** Typed client for /api/merge/*. Uses the shared Api for token, errors and job polling. */
@Injectable({ providedIn: 'root' })
export class MergeApi {
  readonly api = inject(Api);

  repositories() { return this.api.get<RepositorySummary[]>('/merge/repositories'); }
  refs(alias: string) { return this.api.get<RefListing>(`/merge/repositories/${enc(alias)}/refs`); }
  refresh(alias: string, offline: boolean) { return this.api.job<RefFreshness>(offline ? 'Reading local refs' : 'Refreshing refs', `/merge/repositories/${enc(alias)}/refresh`, { offline }); }
  analyze(alias: string, source: RefSelection, target: RefSelection) {
    return this.api.job<{ planId: string; outcome: MergeOutcome }>('Analyzing merge', '/merge/plans', { alias, source, target });
  }
  plan(id: string) { return this.api.get<MergePlan>(`/merge/plans/${enc(id)}`); }
  planSessions(id: string) { return this.api.get<{ id: string; state: string; revision: number; createdAt: string; candidateSha?: string }[]>(`/merge/plans/${enc(id)}/sessions`); }
  createSession(planId: string) { return this.api.job<{ sessionId: string }>('Creating merge session', `/merge/plans/${enc(planId)}/sessions`, {}); }
  sessions() { return this.api.get<{ id: string; planId: string; repositoryAlias: string; state: string; revision: number; createdAt: string }[]>('/merge/sessions'); }
  session(id: string) { return this.api.get<SessionResponse>(`/merge/sessions/${enc(id)}`); }
  file(id: string, entryId: string) { return this.api.get<FileView>(`/merge/sessions/${enc(id)}/files/${enc(entryId)}`); }
  saveDraft(id: string, entryId: string, rev: number, hash: string, content: string) {
    return this.api.post<SessionView>(`/merge/sessions/${enc(id)}/drafts`, { entryId, expectedSessionRevision: rev, expectedContentHash: hash, content });
  }
  resolve(id: string, entryId: string, action: ResolutionAction, rev: number, hash: string, content?: string, acknowledgeMarkers = false) {
    return this.api.post<SessionView>(`/merge/sessions/${enc(id)}/resolutions`, { entryId, action, expectedSessionRevision: rev, expectedContentHash: hash, content, acknowledgeMarkers });
  }
  finalize(id: string, rev: number) { return this.api.post<SessionView>(`/merge/sessions/${enc(id)}/finalize`, { expectedSessionRevision: rev }); }
  reopen(id: string, rev: number) { return this.api.post<SessionView>(`/merge/sessions/${enc(id)}/reopen`, { expectedSessionRevision: rev }); }
  validate(id: string, rev: number, trustValidation: boolean) { return this.api.job<SessionView>('Validating candidate', `/merge/sessions/${enc(id)}/validate`, { expectedSessionRevision: rev, trustValidation }); }
  approve(id: string, rev: number, candidate: string, createPullRequest: boolean, acceptUnvalidated: boolean) {
    return this.api.post<SessionView>(`/merge/sessions/${enc(id)}/approve`, { expectedSessionRevision: rev, candidate, createPullRequest, acceptUnvalidated });
  }
  publish(id: string, rev: number) { return this.api.job<SessionView>('Publishing integration branch', `/merge/sessions/${enc(id)}/publish`, { expectedSessionRevision: rev }); }
  recover(id: string, rev: number) { return this.api.job<SessionView>('Rebuilding workspace', `/merge/sessions/${enc(id)}/recover`, { expectedSessionRevision: rev }); }
  abort(id: string, rev: number) { return this.api.post<SessionView>(`/merge/sessions/${enc(id)}/abort`, { expectedSessionRevision: rev }); }

  /** Browser-side convenience only (selected repo/branches/filter); the server is authoritative for drafts. */
  loadUi(): MergeUiState { try { return JSON.parse(sessionStorage.getItem('merge.ui') ?? '{}'); } catch { return {}; } }
  saveUi(s: MergeUiState) { try { sessionStorage.setItem('merge.ui', JSON.stringify(s)); } catch { /* storage unavailable */ } }
}

export interface MergeUiState { alias?: string; source?: RefSelection; target?: RefSelection; filter?: string; }
