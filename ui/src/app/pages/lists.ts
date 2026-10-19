import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api } from '../api';
import { StatusChip } from '../shared';

interface RunRow { id: string; planId: string; status: string; candidateSha?: string; branch: string; startedAt: string; bundleId?: string; published: boolean; workspace: boolean; }
interface PlanRow { id: string; pair: string; fromProduct: string; toProduct: string; mode: string; preliminaryStatus: string; createdAt: string; sourceRef?: string; commits: number; }

@Component({
  selector: 'fb-history',
  imports: [RouterLink, StatusChip, FormsModule],
  template: `
    <h2>Runs</h2>
    <table class="tbl">
      <tr><th>Run</th><th>Status</th><th>Plan</th><th>Candidate</th><th>Started</th><th>Workspace</th><th>Published</th><th></th></tr>
      @for (r of runs(); track r.id) {
        <tr>
          <td><a [routerLink]="['/runs', r.id]"><code>{{ r.id }}</code></a></td><td><fb-status [status]="r.status" /></td>
          <td><a [routerLink]="['/plans', r.planId]"><code>{{ r.planId }}</code></a></td><td><code>{{ r.candidateSha?.slice(0, 10) }}</code></td>
          <td>{{ r.startedAt.slice(0, 19) }}</td><td>{{ r.workspace ? 'kept' : '—' }}</td><td>{{ r.published ? 'yes' : '' }}</td>
          <td><button class="danger small" (click)="cleanup(r.id)">clean up</button></td>
        </tr>
      } @empty { <tr><td colspan="8" class="muted">No runs yet.</td></tr> }
    </table>

    <h2>Plans</h2>
    <p class="muted small">Tick two or more plans that target different repositories to create a coordinated bundle.</p>
    <table class="tbl">
      <tr><th></th><th>Plan</th><th>Status</th><th>Pair</th><th>Mode</th><th>Source</th><th>Commits</th><th>Created</th></tr>
      @for (p of plans(); track p.id) {
        <tr>
          <td><input type="checkbox" (change)="toggle(p.id)" [checked]="picked().includes(p.id)"></td>
          <td><a [routerLink]="['/plans', p.id]"><code>{{ p.id }}</code></a></td><td><fb-status [status]="p.preliminaryStatus" /></td>
          <td>{{ p.pair }} {{ p.fromProduct }}→{{ p.toProduct }}</td><td>{{ p.mode }}</td><td>{{ p.sourceRef }}</td><td>{{ p.commits }}</td><td>{{ p.createdAt.slice(0, 19) }}</td>
        </tr>
      }
    </table>
    @if (picked().length >= 2) {
      <div class="row"><input [(ngModel)]="bundleName" placeholder="bundle name"><button class="primary" (click)="bundle()">Create bundle</button></div>
    }
  `,
})
export class History implements OnInit {
  api = inject(Api);
  runs = signal<RunRow[]>([]);
  plans = signal<PlanRow[]>([]);
  picked = signal<string[]>([]);
  bundleName = '';
  async ngOnInit() { await this.load(); }
  async load() {
    this.runs.set((await this.api.run('Loading runs', () => this.api.get<RunRow[]>('/runs'))) ?? []);
    this.plans.set((await this.api.run('Loading plans', () => this.api.get<PlanRow[]>('/plans'))) ?? []);
  }
  toggle(id: string) { const p = this.picked(); this.picked.set(p.includes(id) ? p.filter((x) => x !== id) : [...p, id]); }
  async cleanup(id: string) {
    if (!confirm(`Remove harness artifacts of ${id}?`)) return;
    await this.api.run('Cleaning up', () => this.api.post(`/runs/${id}/cleanup`));
    await this.load();
  }
  async bundle() {
    const b = await this.api.run('Creating bundle', () => this.api.post<{ id: string }>('/bundles', { plans: this.picked(), name: this.bundleName || null }));
    if (b) location.assign(`/bundles/${b.id}`);
  }
}

@Component({
  selector: 'fb-bundle',
  imports: [RouterLink, StatusChip, FormsModule],
  template: `
    @if (bundle(); as b) {
      <h2>Bundle <code>{{ b.id }}</code> <fb-status [status]="b.status" /></h2>
      <p class="warn small">Backend and frontend are published as separate branches/PRs linked by this bundle id. There is no cross-repository atomicity.</p>
      <table class="tbl"><tr><th>Plan</th><th>Run</th></tr>
        @for (p of b.planIds; track p) { <tr><td><a [routerLink]="['/plans', p]"><code>{{ p }}</code></a></td><td>@if (b.runIds[p]) { <a [routerLink]="['/runs', b.runIds[p]]"><code>{{ b.runIds[p] }}</code></a> }</td></tr> }
      </table>
      @for (f of b.findings; track f.id) { <p><b>{{ f.title }}</b> — {{ f.evidence.join(' · ') }}</p> }
      @for (v of b.combinedValidation; track v.stepId) { <p>combined validation {{ v.stepId }}: <b>{{ v.outcome }}</b> {{ v.reason ?? '' }}</p> }
      <div class="row">
        <label class="inline"><input type="checkbox" [(ngModel)]="trust"> trust validation</label>
        <input [(ngModel)]="profile" placeholder="bundle validation profile">
        <button class="primary" (click)="simulate()">Simulate bundle</button>
        <label class="inline"><input type="checkbox" [(ngModel)]="createPr"> create PRs</label>
        <button (click)="publish()">Publish approved runs</button>
      </div>
      @if (b.publicationLog.length) { <pre class="out">{{ b.publicationLog.join('\\n') }}</pre> }
    }
  `,
})
export class BundlePage implements OnInit {
  api = inject(Api);
  bundle = signal<any>(null);
  trust = false; createPr = false; profile = '';
  get id() { return location.pathname.split('/').pop()!; }
  async ngOnInit() { this.bundle.set(await this.api.run('Loading bundle', () => this.api.get(`/bundles/${this.id}`))); }
  async simulate() { await this.api.job('Simulating bundle', `/bundles/${this.id}/simulate`, { trustValidation: this.trust, profile: this.profile }).catch(() => {}); await this.ngOnInit(); }
  async publish() { await this.api.job('Publishing bundle', `/bundles/${this.id}/publish`, { createPr: this.createPr }).catch(() => {}); await this.ngOnInit(); }
}

@Component({
  selector: 'fb-discover',
  imports: [FormsModule],
  template: `
    <h2>Discovery</h2>
    <p class="muted">Read-only audit of the configured repositories. Proposed mappings require evidence (present at the merge-base and byte-identical content); name matches alone are never proposed.</p>
    <div class="row">
      <label class="inline"><input type="checkbox" [(ngModel)]="fetch"> contact remotes</label>
      <label class="inline">depth <input type="number" min="1" max="4" [(ngModel)]="depth" style="width:4em"></label>
      <button class="primary" (click)="go()" [disabled]="!!api.busy()">Run discovery</button>
    </div>
    @if (md()) { <pre class="out md">{{ md() }}</pre> }
  `,
})
export class Discover {
  api = inject(Api);
  fetch = false; depth = 2;
  md = signal('');
  async go() {
    const r = await this.api.job<{ markdown: string }>('Discovering repositories', '/discover', { fetch: this.fetch, depth: this.depth }).catch(() => null);
    if (r) this.md.set(r.markdown);
  }
}
