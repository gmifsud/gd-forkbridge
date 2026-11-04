import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api } from '../api';
import { BusyVeil } from '../harmony/busy-veil';
import { MergeApi } from './merge-api';
import { MergeFileList } from './merge-file-list';
import { MergeInspector, ResolveEvent } from './merge-inspector';
import { errorHint, FILTERS, matches, MergeFilter, namespaceLabel, rows, short } from './merge-logic';
import type { FileView, SessionResponse, SessionView } from './merge-models';

/** One merge session: summary, filtered file list, inspector/editor, then finalize → validate → approve → publish.
 *  The server is authoritative (session revision + content hash on every write); the URL keeps the open file and
 *  filter so a reload restores the same view: /merge/sessions/:id?file=&filter= */
@Component({
  selector: 'fb-merge-session-page',
  imports: [RouterLink, MergeFileList, MergeInspector, BusyVeil],
  styles: [`
    :host { display: block; position: relative; }
    .hdr code { font-size: 12px; }
    .stats { display: flex; flex-wrap: wrap; gap: 18px; margin: 8px 0; font-size: 13px; }
    .stats b { font-size: 16px; display: block; }
    .steps { display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: 10px; }
    .step { border: 1px solid var(--line); border-radius: 8px; padding: 10px 12px; background: var(--bg); }
    .step h4 { margin: 0 0 6px; }
    .step.done { border-color: rgba(94,234,212,.45); } .step.cur { border-color: var(--accent); }
    .bad { color: var(--bad); } .ok { color: var(--ok); }
    .integrity { background: var(--badbg); color: var(--bad); padding: 8px 12px; border-radius: 6px; margin: 8px 0; }
    pre.log { max-height: 220px; }
  `],
  template: `
    <fb-busy-veil [active]="!!api.busy()" />
    <div [attr.inert]="api.busy() ? '' : null">
      @if (view(); as v) {
        <div class="row" style="justify-content: space-between">
          <h2 style="margin: 0">Merge session <code>{{ v.session.id }}</code> <span class="chip" data-testid="session-state">{{ v.session.state }}</span></h2>
          <a [routerLink]="['/merge']" [queryParams]="{ alias: v.plan.repositoryAlias, plan: v.plan.id }" class="small">← Back to analysis</a>
        </div>
        <div class="card hdr">
          <div>Repository <b>{{ v.plan.repositoryAlias }}</b> · revision {{ v.session.revision }}</div>
          <div>Merging <b>{{ v.plan.source.name }}</b> &#64; <code>{{ sh(v.plan.source.sha) }}</code> <span class="small muted">({{ ns(v.plan.source.namespace) }})</span>
            into <b>{{ v.plan.target.name }}</b> &#64; <code>{{ sh(v.plan.target.sha) }}</code> <span class="small muted">({{ ns(v.plan.target.namespace) }})</span></div>
          <div class="stats">
            <span><b>{{ v.plan.counts.changed }}</b>changed</span><span><b>{{ v.plan.counts.autoMerged }}</b>auto-merged</span>
            <span><b data-testid="unresolved-count">{{ v.unresolved }}</b>unresolved</span><span><b>{{ v.resolved }}</b>resolved</span><span><b>{{ v.unsupported }}</b>unsupported</span>
          </div>
          @if (v.session.stateReason) { <div class="small warn">{{ v.session.stateReason }}</div> }
        </div>
        @if (!integrity().ok) {
          <div class="integrity" role="alert">
            Workspace check failed: {{ integrity().problems.join('; ') }}.
            <button type="button" class="small" (click)="recover()">Recover (rebuild from recorded resolutions)</button>
          </div>
        }

        <h3>Files</h3>
        <fb-merge-file-list [rows]="list()" [filter]="filter()" [query]="query()" [selected]="openId()"
          (filterChange)="setFilter($event)" (queryChange)="query.set($event)" (open)="openFile($event)" />

        <h3>Finalize, validate, approve, publish</h3>
        <div class="steps">
          <div class="step" [class.cur]="v.session.state === 'Resolving'" [class.done]="!!v.session.candidateSha">
            <h4>1 · Finalize candidate</h4>
            @if (v.session.candidateSha) {
              <div class="small">Candidate <code data-testid="candidate">{{ v.session.candidateSha }}</code></div>
              <div class="small muted">Parents: target <code>{{ sh(v.plan.target.sha) }}</code>, source <code>{{ sh(v.plan.source.sha) }}</code></div>
              @if (v.session.state === 'Finalized' || v.session.state === 'Approved') { <button type="button" class="small" (click)="reopen()">Reopen (discard candidate)</button> }
            } @else {
              <div class="small muted">Creates a local merge commit from the resolved workspace after integrity checks.</div>
              <button type="button" class="primary" [disabled]="!v.canFinalize" (click)="finalize()" data-testid="finalize">Finalize</button>
              @if (!v.canFinalize && v.session.state === 'Resolving') { <div class="small warn">{{ v.unsupported ? 'Unsupported conflicts block finalization.' : 'Resolve every conflict (drafts must be marked resolved).' }}</div> }
            }
          </div>
          <div class="step" [class.cur]="v.session.state === 'Finalized'" [class.done]="v.currentValidation?.verdict === 'PASSED'">
            <h4>2 · Validate</h4>
            @if (v.currentValidation; as val) {
              <div class="small" [class.ok]="val.verdict === 'PASSED'" [class.bad]="val.verdict === 'FAILED' || val.verdict === 'USER_REPO_MODIFIED'" data-testid="validation">{{ val.verdict }} on <code>{{ sh(val.candidateSha) }}</code></div>
              @if (val.note) { <div class="small muted">{{ val.note }}</div> }
              @for (s of val.steps; track s.stepId) { <div class="small">{{ s.stepId }}: {{ s.outcome }}{{ s.reason ? ' — ' + s.reason : '' }}</div> }
            } @else { <div class="small muted">Runs the repository's configured checks on this exact candidate. Commands run only when the profile is trusted in the config and you trust this run.</div> }
            <label class="inline small"><input type="checkbox" [checked]="trust()" (change)="trust.set($any($event.target).checked)" /> Trust validation commands for this run</label>
            <button type="button" [disabled]="v.session.state !== 'Finalized'" (click)="validate()" data-testid="validate">Validate candidate</button>
          </div>
          <div class="step" [class.cur]="v.session.state === 'Finalized' && !!v.currentValidation" [class.done]="!!v.session.approval">
            <h4>3 · Review and approve</h4>
            @if (v.session.approval; as a) {
              <div class="small ok">Approved by {{ a.approvedBy }} · {{ a.validationWaived ? 'validation waived' : 'validated' }}</div>
              <div class="small muted">Binding <code>{{ a.bindingHash.slice(0, 23) }}</code> · branch <code>{{ a.integrationBranch }}</code></div>
            } @else {
              <div class="small muted">Review the final target → result diffs above. Approval binds repository, SHAs, candidate, validation, options and destination <code>{{ v.integrationBranch }}</code>.</div>
              @if (v.pullRequestProviderConfigured) { <label class="inline small"><input type="checkbox" [checked]="createPr()" (change)="createPr.set($any($event.target).checked)" /> Create a pull request into {{ v.plan.target.name }}</label> }
              @if (v.currentValidation && v.currentValidation.verdict !== 'PASSED' && v.currentValidation.verdict !== 'FAILED' && v.currentValidation.verdict !== 'USER_REPO_MODIFIED') {
                <label class="inline small warn"><input type="checkbox" [checked]="acceptUnvalidated()" (change)="acceptUnvalidated.set($any($event.target).checked)" /> I approve this candidate without passing validation</label>
              }
              <button type="button" [disabled]="v.session.state !== 'Finalized' || !v.currentValidation" (click)="approve()" data-testid="approve">Approve {{ sh(v.session.candidateSha) }}</button>
            }
          </div>
          <div class="step" [class.cur]="v.session.state === 'Approved'" [class.done]="v.session.state === 'Published'">
            <h4>4 · Publish</h4>
            <div class="small muted">Pushes a new integration branch (never the target, never forced). Your local checkout is not updated.</div>
            @for (p of v.session.publications; track p.at) {
              <div class="small" [class.ok]="p.pushed" [class.bad]="!p.pushed">{{ p.pushed ? (p.alreadyPresent ? 'Already on remote' : 'Pushed') : 'Failed' }}: <code>{{ p.branch }}</code>
                @if (p.pullRequestUrl) { · <a [href]="p.pullRequestUrl" target="_blank" rel="noopener">pull request</a> }
                @if (p.sourceMovedTo) { <span class="warn"> · source has moved to {{ sh(p.sourceMovedTo) }}; analyze again to include newer commits</span> }
                @if (p.error) { <span class="bad"> · {{ p.error }}</span> }</div>
            }
            <button type="button" class="primary" [disabled]="v.session.state !== 'Approved'" (click)="publish()" data-testid="publish">Publish integration branch</button>
          </div>
        </div>

        <details><summary>Session log ({{ v.session.log.length }})</summary><pre class="out log">{{ v.session.log.join('\\n') }}</pre></details>
        <div class="row">
          @if (v.session.state !== 'Published' && v.session.state !== 'Aborted') { <button type="button" class="small danger" (click)="abort()">Abort session (removes the harness workspace)</button> }
          @if (v.session.state === 'Damaged' || v.session.state === 'Creating') { <button type="button" class="small" (click)="recover()">Recover</button> }
        </div>
      }
    </div>

    @if (openId() && view()) {
      <fb-merge-inspector [v]="file()" [loading]="fileLoading()" [index]="openIndex()" [count]="visibleIds().length"
        [sessionOpen]="view()!.session.state === 'Resolving'" [pending]="saving()" [error]="fileError()" [stale]="fileStale()"
        (close)="closeFile()" (navigate)="step($event)" (saveDraft)="saveDraft($event)" (resolve)="resolve($event)" (reload)="reloadFile()" />
    }
  `,
})
export class MergeSessionPage implements OnInit {
  readonly api = inject(Api);
  private readonly m = inject(MergeApi);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  readonly id = signal('');
  readonly resp = signal<SessionResponse | null>(null);
  readonly view = computed<SessionView | null>(() => this.resp()?.view ?? null);
  readonly integrity = computed(() => this.resp()?.integrity ?? { ok: true, problems: [] });
  readonly filter = signal<MergeFilter>('all');
  readonly query = signal('');
  readonly openId = signal<string | null>(null);
  readonly file = signal<FileView | null>(null);
  readonly fileLoading = signal(false);
  readonly fileError = signal<string | null>(null);
  readonly fileStale = signal(false);
  readonly saving = signal(false);
  readonly trust = signal(false);
  readonly createPr = signal(false);
  readonly acceptUnvalidated = signal(false);

  readonly list = computed(() => rows(this.view()?.plan.entries ?? [], this.view()?.entries));
  readonly visibleIds = computed(() => this.list().filter((r) => matches(r, this.filter(), this.query())).map((r) => r.id));
  readonly openIndex = computed(() => Math.max(0, this.visibleIds().indexOf(this.openId() ?? '')));
  sh = short;
  ns = namespaceLabel;

  async ngOnInit() {
    this.id.set(this.route.snapshot.paramMap.get('sessionId') ?? '');
    const q = this.route.snapshot.queryParamMap;
    const f = q.get('filter') as MergeFilter | null;
    if (f && FILTERS.some((x) => x.key === f)) this.filter.set(f);
    await this.load();
    const file = q.get('file');
    if (file && this.view()?.plan.entries.some((e) => e.id === file)) await this.openFile(file);
  }

  private async load() { await this.api.run('Loading session', async () => this.resp.set(await this.m.session(this.id()))); }

  private syncUrl() {
    void this.router.navigate([], { queryParams: { file: this.openId() ?? null, filter: this.filter() === 'all' ? null : this.filter() }, replaceUrl: true });
  }

  setFilter(f: MergeFilter) { this.filter.set(f); this.syncUrl(); }

  async openFile(entryId: string) {
    this.openId.set(entryId); this.syncUrl();
    await this.reloadFile();
  }

  async reloadFile() {
    const id = this.openId(); if (!id) return;
    this.fileLoading.set(true); this.fileError.set(null); this.fileStale.set(false);
    try { this.file.set(await this.m.file(this.id(), id)); }
    catch (e: any) { this.fileError.set(e.message ?? String(e)); }
    finally { this.fileLoading.set(false); }
  }

  closeFile() { this.openId.set(null); this.file.set(null); this.fileError.set(null); this.syncUrl(); }

  async step(d: number) {
    const ids = this.visibleIds(); const i = ids.indexOf(this.openId() ?? '') + d;
    if (i >= 0 && i < ids.length) await this.openFile(ids[i]);
  }

  /** Writes report errors inside the inspector; stale-write errors offer an explicit reload (no silent overwrite). */
  private async write(f: () => Promise<SessionView>, reloadAfter = true) {
    this.saving.set(true); this.fileError.set(null); this.fileStale.set(false);
    try {
      const v = await f();
      this.resp.set({ view: v, integrity: this.resp()?.integrity ?? { ok: true, problems: [] } });
      if (reloadAfter) await this.reloadFile();
    } catch (e: any) {
      const h = errorHint(e.message ?? String(e));
      this.fileError.set(h.text); this.fileStale.set(h.stale);
      if (h.stale) await this.refreshSession();
    } finally { this.saving.set(false); }
  }

  private async refreshSession() { try { this.resp.set(await this.m.session(this.id())); } catch { /* shown elsewhere */ } }

  saveDraft(content: string) {
    const x = this.file(); if (!x) return;
    void this.write(() => this.m.saveDraft(this.id(), x.entry.id, x.sessionRevision, x.resultContentHash, content));
  }

  resolve(ev: ResolveEvent) {
    const x = this.file(); if (!x) return;
    void this.write(() => this.m.resolve(this.id(), x.entry.id, ev.action, x.sessionRevision, x.resultContentHash, ev.content, ev.acknowledgeMarkers ?? false));
  }

  private rev() { return this.view()!.session.revision; }
  private async lifecycle(f: () => Promise<SessionView | undefined>) {
    const v = await f();
    if (v) this.resp.set({ view: v, integrity: this.resp()?.integrity ?? { ok: true, problems: [] } });
    await this.refreshSession();
  }

  finalize() { void this.lifecycle(() => this.api.run('Finalizing candidate', () => this.m.finalize(this.id(), this.rev()))); }
  reopen() { void this.lifecycle(() => this.api.run('Reopening', () => this.m.reopen(this.id(), this.rev()))); }
  validate() { void this.lifecycle(async () => { try { return await this.m.validate(this.id(), this.rev(), this.trust()); } catch { return undefined; } }); }
  approve() {
    const v = this.view()!;
    void this.lifecycle(() => this.api.run('Approving', () => this.m.approve(this.id(), this.rev(), v.session.candidateSha!, this.createPr(), this.acceptUnvalidated())));
  }
  publish() { void this.lifecycle(async () => { try { return await this.m.publish(this.id(), this.rev()); } catch { return undefined; } }); }
  recover() { void this.lifecycle(async () => { try { return await this.m.recover(this.id(), this.rev()); } catch { return undefined; } }); }
  abort() { void this.lifecycle(() => this.api.run('Aborting', () => this.m.abort(this.id(), this.rev()))); }
}
