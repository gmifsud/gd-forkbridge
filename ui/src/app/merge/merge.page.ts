import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api } from '../api';
import { BusyVeil } from '../harmony/busy-veil';
import { MergeApi } from './merge-api';
import { MergeFileList } from './merge-file-list';
import { MergeFilter, namespaceLabel, outcomeLabel, rows, short } from './merge-logic';
import type { MergePlan, RefInfo, RefListing, RefNamespace, RefSelection, RepositorySummary } from './merge-models';

/** Merge tab: pick one configured repository, a source and a target branch (with explicit ref provenance), analyze
 *  the merge at pinned commits, review the outcome and changed files, then start an isolated merge session.
 *  URL state: /merge?alias=&plan= so a reload returns to the same analysis. */
@Component({
  selector: 'fb-merge-page',
  imports: [RouterLink, MergeFileList, BusyVeil],
  styles: [`
    :host { display: block; position: relative; }
    .sel { display: grid; grid-template-columns: 220px 1fr 1fr; gap: 12px; align-items: end; }
    @media (max-width: 1000px) { .sel { grid-template-columns: 1fr; } }
    .refpick select { width: 100%; }
    .prov { font-size: 11.5px; color: var(--muted); margin-top: 3px; min-height: 16px; }
    .hdr { font-size: 14px; margin: 6px 0; }
    .hdr code { font-size: 12px; }
    .outcome { display: inline-block; font-weight: 700; padding: 3px 10px; border-radius: 999px; }
    .tone-ok { background: var(--okbg); color: var(--ok); } .tone-warn { background: var(--warnbg); color: var(--warn); }
    .tone-bad { background: var(--badbg); color: var(--bad); } .tone-info { background: var(--infobg); color: var(--info); }
    .stats { display: flex; flex-wrap: wrap; gap: 18px; margin: 8px 0; font-size: 13px; }
    .stats b { font-size: 16px; display: block; }
    .stale { color: var(--warn); font-size: 12px; }
    .f-blocking { color: var(--bad); } .f-warning { color: var(--warn); } .f-info { color: var(--muted); }
  `],
  template: `
    <fb-busy-veil [active]="!!api.busy()" />
    <div [attr.inert]="api.busy() ? '' : null">
      <h2>Merge</h2>
      <p class="muted" style="margin-top: 0">Merge a <b>source</b> branch <b>into</b> a <b>target</b> branch of one repository. Analysis and resolution run in
        harness-owned storage at pinned commits; your checkout, its index, branch and remotes are never changed.</p>

      <div class="card">
        <div class="sel">
          <label>Repository
            <select [value]="alias()" (change)="pickRepo($any($event.target).value)" aria-label="Repository">
              <option value="">— choose —</option>
              @for (r of repos(); track r.alias) { <option [value]="r.alias" [disabled]="r.placeholder">{{ r.alias }}{{ r.placeholder ? ' (placeholder)' : '' }}</option> }
            </select>
          </label>
          <label class="refpick">Source (merged from)
            <select [value]="key(source())" (change)="source.set(parse($any($event.target).value)); persist()" aria-label="Source branch" [disabled]="!listing()?.refs?.length">
              <option value="">— source branch —</option>
              @for (g of groups(); track g.ns) { <optgroup [label]="nsLabel(g.ns)">@for (r of g.refs; track r.name) { <option [value]="g.ns + ':' + r.name">{{ r.name }}</option> }</optgroup> }
            </select>
            <div class="prov">@if (source(); as s) { {{ nsLabel(s.namespace) }} · <code>{{ sh(shaOf(s)) }}</code> }</div>
          </label>
          <label class="refpick">Target (merged into)
            <select [value]="key(target())" (change)="target.set(parse($any($event.target).value)); persist()" aria-label="Target branch" [disabled]="!listing()?.refs?.length">
              <option value="">— target branch —</option>
              @for (g of groups(); track g.ns) { <optgroup [label]="nsLabel(g.ns)">@for (r of g.refs; track r.name) { <option [value]="g.ns + ':' + r.name">{{ r.name }}</option> }</optgroup> }
            </select>
            <div class="prov">@if (target(); as t) { {{ nsLabel(t.namespace) }} · <code>{{ sh(shaOf(t)) }}</code> }</div>
          </label>
        </div>
        <div class="row">
          <button type="button" (click)="refresh(false)" [disabled]="!alias()">Refresh refs</button>
          <button type="button" class="small" (click)="refresh(true)" [disabled]="!alias()" title="Read your checkout's refs without contacting the remote">Offline refresh</button>
          <button type="button" (click)="swap()" [disabled]="!source() || !target()">Swap</button>
          <button type="button" class="primary" (click)="analyze()" [disabled]="!canAnalyze()">Analyze merge</button>
          @if (listing(); as l) {
            <span class="small" [class.stale]="l.freshness.stale" [class.muted]="!l.freshness.stale">
              @if (!l.mirrorExists) { Refs not loaded yet: refresh first. }
              @else if (l.freshness.refreshedAt) { Refs {{ l.freshness.offline ? 'read offline' : 'refreshed' }} {{ l.freshness.refreshedAt.slice(0, 19).replace('T', ' ') }} UTC{{ l.freshness.stale ? ' — stale' : '' }} }
              @else { Refs never refreshed by the merge workspace (stale). }
            </span>
          }
        </div>
        @if (source() && target() && same()) { <p class="warn small">Source and target are the same branch.</p> }
      </div>

      @if (plan(); as p) {
        <div class="card" data-testid="analysis">
          <div class="hdr">Repository <b>{{ p.repositoryAlias }}</b> · Merging <b>{{ p.source.name }}</b> &#64; <code>{{ sh(p.source.sha) }}</code> into <b>{{ p.target.name }}</b> &#64; <code>{{ sh(p.target.sha) }}</code></div>
          <div class="small muted">Source: {{ nsLabel(p.source.namespace) }} (<code>{{ p.source.fullRef }}</code>) · Target: {{ nsLabel(p.target.namespace) }} (<code>{{ p.target.fullRef }}</code>) · plan <code>{{ p.id }}</code></div>
          <div class="row"><span class="outcome" [class]="'outcome tone-' + outcome().tone" data-testid="outcome">{{ outcome().label }}</span><span class="muted">{{ outcome().text }}</span></div>
          @if (p.blockedReason) { <p class="warn">{{ p.blockedReason }}</p> }
          <div class="stats">
            <span><b>{{ p.counts.sourceOnlyCommits }}</b>source-only commits</span>
            <span><b>{{ p.counts.targetOnlyCommits }}</b>target-only commits</span>
            <span><b>{{ p.counts.changed }}</b>changed files</span>
            <span><b>{{ p.counts.autoMerged }}</b>auto-merged</span>
            <span><b>{{ p.counts.conflicted }}</b>conflicts</span>
            <span><b>{{ p.counts.unsupported }}</b>unsupported</span>
          </div>
          <div class="small muted">Merge base{{ p.mergeBases.length === 1 ? '' : 's' }}: @for (b of p.mergeBases; track b) { <code>{{ sh(b) }}</code> } @empty { none (unrelated histories) } · git {{ p.gitVersion }} · strategy ort, diff3 markers, rename detection on, directory renames off</div>
          @for (f of p.findings; track f.code) { <div class="small" [class]="'f-' + f.severity">● {{ f.message }}</div> }
          <div class="row">
            @if (p.outcome !== 'UpToDate' && p.outcome !== 'Blocked') { <button type="button" class="primary" (click)="start()" data-testid="start-session">Start merge session</button> }
            @for (s of sessions(); track s.id) { <a [routerLink]="['/merge/sessions', s.id]" class="small">session {{ s.id }} ({{ s.state }})</a> }
          </div>
        </div>
        @if (p.entries.length) {
          <h3>Changes</h3>
          <p class="small muted">Read-only analysis. Start a session to inspect diffs and resolve conflicts.</p>
          <fb-merge-file-list [rows]="list()" [filter]="filter()" [query]="query()" [openable]="false" (filterChange)="filter.set($event)" (queryChange)="query.set($event)" />
        }
      }
    </div>
  `,
})
export class MergePage implements OnInit {
  readonly api = inject(Api);
  private readonly m = inject(MergeApi);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  readonly repos = signal<RepositorySummary[]>([]);
  readonly alias = signal('');
  readonly listing = signal<RefListing | null>(null);
  readonly source = signal<RefSelection | null>(null);
  readonly target = signal<RefSelection | null>(null);
  readonly plan = signal<MergePlan | null>(null);
  readonly sessions = signal<{ id: string; state: string }[]>([]);
  readonly filter = signal<MergeFilter>('all');
  readonly query = signal('');

  readonly groups = computed(() => {
    const refs = this.listing()?.refs ?? [];
    const order: RefNamespace[] = ['Remote', 'Local', 'CheckoutRemote'];
    return order.map((ns) => ({ ns, refs: refs.filter((r) => r.namespace === ns).sort((a, b) => a.name.localeCompare(b.name)) })).filter((g) => g.refs.length);
  });
  readonly same = computed(() => this.key(this.source()) === this.key(this.target()));
  readonly canAnalyze = computed(() => !!this.alias() && !!this.source() && !!this.target() && !this.same());
  readonly outcome = computed(() => outcomeLabel(this.plan()?.outcome ?? 'Blocked'));
  readonly list = computed(() => rows(this.plan()?.entries ?? []));
  sh = short;
  nsLabel = namespaceLabel;

  async ngOnInit() {
    await this.api.run('Loading repositories', async () => this.repos.set(await this.m.repositories()));
    const q = this.route.snapshot.queryParamMap;
    const ui = this.m.loadUi();
    const alias = q.get('alias') ?? ui.alias ?? '';
    if (alias) await this.pickRepo(alias, ui);
    const planId = q.get('plan');
    if (planId) await this.loadPlan(planId);
  }

  key(s: RefSelection | null) { return s ? `${s.namespace}:${s.name}` : ''; }
  parse(v: string): RefSelection | null { const i = v.indexOf(':'); return i > 0 ? { namespace: v.slice(0, i) as RefNamespace, name: v.slice(i + 1) } : null; }
  shaOf(s: RefSelection): string | undefined { return this.listing()?.refs.find((r: RefInfo) => r.namespace === s.namespace && r.name === s.name)?.sha; }

  async pickRepo(alias: string, ui = this.m.loadUi()) {
    this.alias.set(alias); this.plan.set(null); this.listing.set(null);
    this.source.set(ui.alias === alias ? ui.source ?? null : null);
    this.target.set(ui.alias === alias ? ui.target ?? null : null);
    if (!alias) return;
    await this.api.run('Loading refs', async () => this.listing.set(await this.m.refs(alias)));
    this.persist();
  }

  async refresh(offline: boolean) {
    try { await this.m.refresh(this.alias(), offline); } catch { return; }
    await this.api.run('Loading refs', async () => this.listing.set(await this.m.refs(this.alias())));
  }

  swap() { const s = this.source(); this.source.set(this.target()); this.target.set(s); this.persist(); }

  persist() { this.m.saveUi({ alias: this.alias(), source: this.source() ?? undefined, target: this.target() ?? undefined }); }

  async analyze() {
    let r: { planId: string } | undefined;
    try { r = await this.m.analyze(this.alias(), this.source()!, this.target()!); } catch { return; }
    await this.router.navigate([], { queryParams: { alias: this.alias(), plan: r.planId }, replaceUrl: true });
    await this.loadPlan(r.planId);
  }

  private async loadPlan(id: string) {
    await this.api.run('Loading analysis', async () => {
      const p = await this.m.plan(id);
      this.plan.set(p);
      this.sessions.set(await this.m.planSessions(id));
    });
  }

  async start() {
    let r: { sessionId: string } | undefined;
    try { r = await this.m.createSession(this.plan()!.id); } catch { return; }
    await this.router.navigate(['/merge/sessions', r.sessionId]);
  }
}
