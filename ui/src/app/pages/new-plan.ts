import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Api } from '../api';

interface PairCfg {
  name: string; products: Record<string, string>; defaults: { targetBase: string; sourceBase: string };
  directions: { from: string; to: string; modes: string[] }[];
  components: { id: string; status: string; paths: Record<string, string | null>; absent: Record<string, string> }[];
}
interface RefRow { name: string; local: boolean; sha: string; date: string; subject: string; }
interface CommitRow { sha: string; author: string; date: string; merge: boolean; subject: string; }

@Component({
  selector: 'fb-new-plan',
  imports: [FormsModule],
  template: `
    <h2>New transfer plan</h2>
    <p class="muted">Every ref is resolved to an exact SHA and frozen in an immutable plan. Nothing touches your checkouts.</p>
    <div class="grid2">
      <section class="card">
        <h3>1 · Pair and direction</h3>
        <label>Pair
          <select [(ngModel)]="pairName" (ngModelChange)="onPair()">
            @for (p of pairs(); track p.name) { <option [value]="p.name">{{ p.name }}</option> }
          </select>
        </label>
        <label>Direction
          <select [(ngModel)]="direction" (ngModelChange)="onDirection()">
            @for (d of pair()?.directions ?? []; track d.from + d.to) { <option [value]="d.from + '>' + d.to">{{ d.from }} → {{ d.to }}</option> }
          </select>
        </label>
        <label>Selection
          <select [(ngModel)]="mode">
            <option value="feature">Feature branch (relative to its base)</option>
            <option value="range">Explicit commit range A..B</option>
            <option value="commits">Explicit ordered commit list</option>
            @if (modes().includes('reconcile')) { <option value="reconcile">Reconcile component snapshots</option> }
            @if (modes().includes('merge')) { <option value="merge">Full-history merge (advanced)</option> }
          </select>
        </label>
        <div class="row">
          <button (click)="sync()" [disabled]="!!api.busy()">Refresh refs (fetch)</button>
          <label class="inline"><input type="checkbox" [(ngModel)]="offline"> offline (stale refs, recorded)</label>
        </div>
        <p class="muted small">{{ refs().length }} source refs known{{ refs().length === 0 ? ' — refresh first' : '' }}.</p>
      </section>

      <section class="card">
        <h3>2 · Source selection</h3>
        <datalist id="srcrefs">@for (r of refs(); track r.name + r.local) { <option [value]="r.name">{{ r.local ? 'local' : 'remote' }} {{ r.sha.slice(0, 10) }} {{ r.subject }}</option> }</datalist>
        @switch (mode) {
          @case ('feature') {
            <label>Source branch <input list="srcrefs" [(ngModel)]="sourceRef" placeholder="feature/shared-kernel-fix"></label>
            <label>Source base <input list="srcrefs" [(ngModel)]="sourceBase"></label>
            <button (click)="loadCommits()" [disabled]="!sourceRef">Preview commits</button>
          }
          @case ('range') {
            <label>Range <input [(ngModel)]="range" placeholder="development~5..development"></label>
            <p class="muted small">Exclusive start, inclusive end. Both ends are resolved to SHAs.</p>
          }
          @case ('commits') {
            <label>Branch to pick from <input list="srcrefs" [(ngModel)]="sourceRef"></label>
            <button (click)="loadCommits()" [disabled]="!sourceRef">List commits</button>
            <p class="muted small">Selected (in order): {{ picked().join(', ') || 'none' }}</p>
          }
          @case ('reconcile') {
            <label>Source snapshot ref <input list="srcrefs" [(ngModel)]="sourceRef" [placeholder]="pair()?.defaults?.sourceBase"></label>
            <label>Baseline (optional SHA) <input [(ngModel)]="baseline" placeholder="defaults to merge-base"></label>
            <p class="muted small">Produces one reconciliation commit — never presented as the original source commits.</p>
          }
          @case ('merge') {
            <label>Source ref <input list="srcrefs" [(ngModel)]="sourceRef"></label>
            <p class="warn small">Ancestry will record excluded content as merged. Prefer pick or reconcile.</p>
          }
        }
        <label>Target base <input [(ngModel)]="targetBase"></label>
        @if (commits().length) {
          <div class="scroll">
            <table class="tbl">
              <tr><th></th><th>SHA</th><th>Author</th><th>Subject</th></tr>
              @for (c of commits(); track c.sha) {
                <tr [class.merge]="c.merge">
                  <td>@if (mode === 'commits') { <input type="checkbox" [checked]="picked().includes(c.sha)" (change)="toggle(c.sha)"> }</td>
                  <td><code>{{ c.sha.slice(0, 10) }}</code></td><td>{{ c.author }}</td><td>{{ c.merge ? '[merge] ' : '' }}{{ c.subject }}</td>
                </tr>
              }
            </table>
          </div>
        }
      </section>
    </div>

    <section class="card">
      <h3>3 · Components</h3>
      <p class="muted small">Only <b>confirmed</b> components transfer by default. Proposed mappings need explicit opt-in (adds a review finding).</p>
      <div class="comps">
        @for (c of pair()?.components ?? []; track c.id) {
          <label class="comp" [class.dim]="c.status !== 'confirmed'">
            <input type="checkbox" [checked]="components().includes(c.id)" (change)="toggleComp(c.id)" [disabled]="c.status === 'placeholder'">
            <b>{{ c.id }}</b> <span class="chip s-{{ c.status }}">{{ c.status }}</span>
            <span class="small">{{ from }}: <code>{{ c.paths[from] ?? '(absent)' }}</code> → {{ to }}: <code>{{ c.paths[to] ?? ('absent: ' + (c.absent[to] || '')) }}</code></span>
          </label>
        }
      </div>
      <div class="row">
        <label class="inline"><input type="checkbox" [(ngModel)]="allowProposed"> allow proposed components</label>
        <label class="inline"><input type="checkbox" [(ngModel)]="preferLocal"> prefer local branches</label>
        <label class="inline">re-apply SHAs <input [(ngModel)]="reapply" placeholder="sha1 sha2"></label>
      </div>
      <button class="primary" (click)="plan()" [disabled]="!!api.busy()">Create immutable plan</button>
    </section>
  `,
})
export class NewPlan implements OnInit {
  api = inject(Api);
  router = inject(Router);
  pairs = signal<PairCfg[]>([]);
  refs = signal<RefRow[]>([]);
  commits = signal<CommitRow[]>([]);
  picked = signal<string[]>([]);
  components = signal<string[]>([]);
  pairName = ''; direction = ''; mode = 'feature'; sourceRef = ''; sourceBase = ''; targetBase = ''; range = ''; baseline = ''; reapply = '';
  offline = false; allowProposed = false; preferLocal = false;
  pair = computed(() => this.pairs().find((p) => p.name === this.pairSig()));
  private pairSig = signal('');
  private dirSig = signal('');
  modes = computed(() => this.pair()?.directions.find((d) => d.from + '>' + d.to === this.dirSig())?.modes ?? []);
  get from() { return this.direction.split('>')[0]; }
  get to() { return this.direction.split('>')[1]; }

  async ngOnInit() {
    const cfg = await this.api.run('Loading configuration', () => this.api.get<{ pairs: PairCfg[] }>('/config'));
    if (!cfg) return;
    this.pairs.set(cfg.pairs);
    this.pairName = cfg.pairs[0]?.name ?? '';
    this.onPair();
  }

  onPair() {
    this.pairSig.set(this.pairName);
    const p = this.pair();
    if (!p) return;
    const d = p.directions[0];
    this.direction = d ? d.from + '>' + d.to : '';
    this.sourceBase = p.defaults.sourceBase;
    this.targetBase = p.defaults.targetBase;
    this.components.set(p.components.filter((c) => c.status === 'confirmed').map((c) => c.id));
    this.onDirection();
  }

  async onDirection() {
    this.dirSig.set(this.direction);
    this.commits.set([]); this.picked.set([]);
    const r = await this.api.run('Loading refs', () => this.api.get<{ refs: RefRow[] }>(`/pairs/${this.pairName}/refs?product=${this.from}`));
    this.refs.set(r?.refs ?? []);
  }

  async sync() {
    await this.api.job('Fetching both repositories into harness mirrors', `/pairs/${this.pairName}/sync`, { offline: this.offline }).catch(() => {});
    await this.onDirection();
  }

  async loadCommits() {
    const base = this.mode === 'feature' ? `&base=${encodeURIComponent(this.sourceBase)}` : '';
    const c = await this.api.run('Listing commits', () => this.api.get<CommitRow[]>(`/pairs/${this.pairName}/commits?product=${this.from}&head=${encodeURIComponent(this.sourceRef)}${base}`));
    this.commits.set(c ?? []);
  }

  toggle(sha: string) {
    const p = this.picked();
    this.picked.set(p.includes(sha) ? p.filter((x) => x !== sha) : [...p, sha]);
  }

  toggleComp(id: string) {
    const c = this.components();
    this.components.set(c.includes(id) ? c.filter((x) => x !== id) : [...c, id]);
  }

  async plan() {
    const body = {
      pair: this.pairName, from: this.from, to: this.to, mode: this.mode,
      sourceRef: this.sourceRef, sourceBase: this.mode === 'feature' ? this.sourceBase : null, targetBase: this.targetBase,
      range: this.mode === 'range' ? this.range : null, commits: this.mode === 'commits' ? this.picked() : [],
      components: this.components(), allowProposed: this.allowProposed, offline: this.offline, preferLocal: this.preferLocal,
      baseline: this.baseline, reapply: this.reapply.split(/[\s,]+/).filter((x) => x),
    };
    const r = await this.api.job<{ planId: string }>('Planning (fetch, resolve SHAs, classify, analyse)', '/plans', body).catch(() => null);
    if (r) this.router.navigate(['/plans', r.planId]);
  }
}
