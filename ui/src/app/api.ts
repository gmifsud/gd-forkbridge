import { Injectable, signal } from '@angular/core';

/** Thin client for the loopback ForkBridge API. The per-launch token arrives in the URL fragment
 *  (never sent to the server in a URL), is kept in sessionStorage and sent as a header. */
@Injectable({ providedIn: 'root' })
export class Api {
  readonly token = signal<string | null>(null);
  readonly busy = signal<string | null>(null);
  readonly error = signal<string | null>(null);

  constructor() {
    const m = /token=([0-9a-f]{16,})/.exec(location.hash);
    if (m) {
      sessionStorage.setItem('fb-token', m[1]);
      history.replaceState(null, '', location.pathname + location.search);
    }
    this.token.set(sessionStorage.getItem('fb-token'));
  }

  private headers(json: boolean): HeadersInit {
    const h: Record<string, string> = { 'X-ForkBridge-Token': this.token() ?? '' };
    if (json) h['Content-Type'] = 'application/json';
    return h;
  }

  async get<T>(url: string): Promise<T> {
    const r = await fetch('/api' + url, { headers: this.headers(false) });
    return this.handle<T>(r);
  }

  async text(url: string): Promise<string> {
    const r = await fetch('/api' + url, { headers: this.headers(false) });
    if (!r.ok) throw new Error(`${r.status} ${await r.text()}`);
    return r.text();
  }

  async post<T>(url: string, body: unknown = {}): Promise<T> {
    const r = await fetch('/api' + url, { method: 'POST', headers: this.headers(true), body: JSON.stringify(body) });
    return this.handle<T>(r);
  }

  private async handle<T>(r: Response): Promise<T> {
    if (r.status === 401) throw new Error('Unauthorized: open the URL printed by forkbridge-web (it contains the session token).');
    const text = await r.text();
    if (text.trimStart().startsWith('<'))
      throw new Error('The API returned HTML instead of JSON: this page is not connected to forkbridge-web. ' +
        'Open the URL printed by forkbridge-web (http://127.0.0.1:5178/#token=…), or, for `ng serve`, keep proxy.conf.json enabled ' +
        'and open http://localhost:4300/#token=<same token>.');
    const data = text ? JSON.parse(text) : null;
    if (!r.ok) throw new Error(data?.error ? `${data.error} (exit ${data.exitCode})` : `${r.status} ${text}`);
    return data as T;
  }

  /** Starts a background job and polls until it finishes. */
  async job<T>(label: string, url: string, body: unknown = {}): Promise<T> {
    this.error.set(null);
    this.busy.set(label);
    try {
      const { jobId } = await this.post<{ jobId: string }>(url, body);
      this.currentJob = jobId;
      for (;;) {
        await new Promise((res) => setTimeout(res, 600));
        const j = await this.get<Job>(`/jobs/${jobId}`);
        if (j.state === 'succeeded') return j.result as T;
        if (j.state !== 'running') throw new Error(`${j.state}: ${j.error ?? ''} (exit ${j.exitCode})`);
      }
    } catch (e: any) {
      this.error.set(e.message ?? String(e));
      throw e;
    } finally {
      this.busy.set(null);
      this.currentJob = null;
    }
  }

  currentJob: string | null = null;
  async cancel() {
    if (this.currentJob) await this.post(`/jobs/${this.currentJob}/cancel`);
  }

  async run<T>(label: string, f: () => Promise<T>): Promise<T | undefined> {
    this.error.set(null);
    this.busy.set(label);
    try { return await f(); }
    catch (e: any) { this.error.set(e.message ?? String(e)); return undefined; }
    finally { this.busy.set(null); }
  }
}

export interface Job { id: string; kind: string; state: string; result?: unknown; error?: string; exitCode?: number; }

export interface Finding {
  id: string; code: string; category: string; severity: string; title: string; component?: string;
  files: string[]; commits: string[]; evidence: string[]; origin: string; blocking: boolean; requiresReview: boolean; suggestedAction?: string;
}
export interface PathDecision {
  sourcePath: string; sourceOldPath?: string; targetPath?: string; targetOldPath?: string; status: string;
  sourceClass: string; targetClass?: string; component?: string; decision: string; reason: string; binary: boolean;
}
export interface PlannedCommit {
  sha: string; subject: string; author: string; authorDate: string; isMerge: boolean; paths: PathDecision[]; mixed: boolean;
  state: string; stateEvidence?: string; components: string[];
}
export interface Drift {
  component: string; sourcePath: string; targetPath: string; presentInTarget: boolean; intentionallyAbsentInTarget: boolean;
  identical: number; differing: string[]; onlyInSource: string[]; onlyInTarget: string[]; targetDivergedSinceBaseline: string[];
  sourceChangedSinceBaseline: string[]; preservedProductDifferences: string[];
}
export interface Plan {
  id: string; createdAt: string; pair: string; fromProduct: string; toProduct: string; mode: string;
  source: { alias: string; remoteUrlRedacted?: string }; target: { alias: string; remoteUrlRedacted?: string };
  sourceRef?: string; sourceBaseRef?: string; targetBaseRef: string; sourceHeadSha: string; sourceBaseSha?: string; targetBaseSha: string;
  mergeBase?: string; mergeBaseExplanation?: string; selectedComponents: string[]; commits: PlannedCommit[]; reconciliation?: PlannedCommit;
  drift: Drift[]; findings: Finding[]; policyHash: string; freshness: { offline: boolean; note?: string; fetchedAt?: string };
  preliminaryStatus: string; impact?: { affectedProjects: string[]; sharedConsumers: string[]; productConsumers: string[]; frontendConsumers: string[]; notes: string[] };
}
export interface ValidationCmp { stepId: string; baseline: string; candidate: string; verdict: string; required: boolean; }
export interface ValidationResult { stepId: string; outcome: string; reason?: string; outputTail?: string; durationSeconds: number; }
export interface Run {
  id: string; planId: string; bundleId?: string; status: string; startedAt: string; finishedAt?: string; branch: string; candidateSha?: string;
  candidateWorktree: string; workspaceDir: string; resultCommits: Record<string, string>; findings: Finding[];
  conflict?: { operation: string; files: string[]; evidenceDir?: string; output?: string; instructions: string[] };
  validationComparison: ValidationCmp[]; candidateValidation: ValidationResult[]; baselineValidation: ValidationResult[];
  candidateDiffStat?: string; approval?: { binding: string; approvedBy: string; approvedAt: string; acceptedFindings: string[] };
  publications: { at: string; branch: string; pushed: boolean; pullRequestUrl?: string; error?: string }[]; log: string[];
}
