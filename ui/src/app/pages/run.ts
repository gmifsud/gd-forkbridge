import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { Api, Plan, Run } from '../api';
import { Findings, Sha, StatusChip } from '../shared';
import { PlanDetails } from './plan-details';

type Tab = 'summary' | 'findings' | 'classification' | 'conflict' | 'validation' | 'diff' | 'publish' | 'log';

@Component({
  selector: 'fb-run',
  imports: [FormsModule, RouterLink, Findings, Sha, StatusChip, PlanDetails],
  template: `
    @if (run(); as r) {
      @let p = plan()!;
      <h2>Run <code>{{ r.id }}</code> <fb-status [status]="r.status" /></h2>
      <nav class="tabs">
        @for (t of tabs; track t) { <button [class.active]="tab() === t" (click)="tab.set(t)">{{ t }}{{ t === 'conflict' && r.conflict ? ' ⚠' : '' }}</button> }
      </nav>

      @switch (tab()) {
        @case ('summary') {
          <section class="card kv">
            <div><span>Plan</span><a [routerLink]="['/plans', r.planId]"><code>{{ r.planId }}</code></a> · {{ p.pair }}: {{ p.fromProduct }} → {{ p.toProduct }} ({{ p.mode }})</div>
            <div><span>Source head</span><fb-sha [sha]="p.sourceHeadSha" [len]="40" /></div>
            <div><span>Target base</span><fb-sha [sha]="p.targetBaseSha" [len]="40" /> ({{ p.targetBaseRef }})</div>
            <div><span>Candidate</span><fb-sha [sha]="r.candidateSha" [len]="40" /></div>
            <div><span>Harness branch</span><code>{{ r.branch }}</code> <span class="muted small">(harness mirror only until published)</span></div>
            <div><span>Workspace</span><code>{{ r.candidateWorktree }}</code> {{ workspaceExists() ? '' : '(removed)' }}</div>
            @if (r.bundleId) { <div><span>Bundle</span><code>{{ r.bundleId }}</code></div> }
            <div><span>Result commits</span>
              @for (kv of resultCommits(); track kv[0]) { <div class="small"><code>{{ kv[0].slice(0, 10) }}</code> → <code>{{ kv[1].slice(0, 10) }}</code></div> }
            </div>
          </section>
          <div class="row">
            @if (r.conflict || r.status === 'CANCELLED') {
              <label class="inline"><input type="checkbox" [(ngModel)]="trust"> trust validation</label>
              <button class="primary" (click)="resume()">Resume</button>
            }
            @if (!published()) { <button (click)="abort()">Abort</button> }
            <button class="danger" (click)="cleanup()">Clean up harness artifacts</button>
          </div>
        }
        @case ('findings') { <fb-findings [findings]="allFindings()" [accepted]="r.approval?.acceptedFindings ?? []" /> }
        @case ('classification') { <fb-plan-details [plan]="p" /> }
        @case ('conflict') {
          @if (r.conflict; as c) {
            <section class="card">
              <p><b>{{ c.operation }}</b> stopped. Shared-code conflicts are never auto-resolved.</p>
              <ol>@for (i of c.instructions; track $index) { <li><code>{{ i }}</code></li> }</ol>
              <p>Files: @for (f of c.files; track f) { <code>{{ f }}</code> }</p>
              @if (c.output) { <pre class="out">{{ c.output }}</pre> }
            </section>
            <div class="evidence">
              @for (e of evidence(); track e.name) { <div><h4>{{ e.name }}</h4><pre>{{ e.content }}</pre></div> }
            </div>
          } @else { <p class="muted">No open conflict.</p> }
        }
        @case ('validation') {
          <table class="tbl">
            <tr><th>Step</th><th>Required</th><th>Baseline</th><th>Candidate</th><th>Verdict</th></tr>
            @for (v of r.validationComparison; track v.stepId) {
              <tr [class]="'v-' + v.verdict"><td>{{ v.stepId }}</td><td>{{ v.required ? 'yes' : 'no' }}</td><td>{{ v.baseline }}</td><td>{{ v.candidate }}</td><td><b>{{ v.verdict }}</b></td></tr>
            } @empty { <tr><td colspan="5" class="muted">No validation executed.</td></tr> }
          </table>
          @for (v of r.candidateValidation; track v.stepId) {
            <details><summary>candidate · {{ v.stepId }} · {{ v.outcome }} · {{ v.durationSeconds }}s {{ v.reason ?? '' }}</summary><pre class="out">{{ v.outputTail }}</pre></details>
          }
          @for (v of r.baselineValidation; track v.stepId) {
            <details><summary>baseline · {{ v.stepId }} · {{ v.outcome }} · {{ v.durationSeconds }}s {{ v.reason ?? '' }}</summary><pre class="out">{{ v.outputTail }}</pre></details>
          }
        }
        @case ('diff') {
          @if (r.candidateDiffStat) { <pre class="out">{{ r.candidateDiffStat }}</pre> }
          <button (click)="loadDiff()">Load full candidate diff</button>
          <pre class="diff">@for (l of diffLines(); track $index) {<span [class]="lineClass(l)">{{ l }}
</span>}</pre>
        }
        @case ('publish') {
          <section class="card">
            <h3>Approval</h3>
            @if (r.approval; as a) {
              <p>Approved by {{ a.approvedBy }} at {{ a.approvedAt }}</p>
              <p class="small">Binding: <code>{{ a.binding }}</code></p>
            } @else {
              <p class="muted small">Approval binds the exact candidate SHA, target remote, branch, report hash, policy and target base.</p>
              @if (reviewItems().length) {
                <p><b>Explicit decisions required:</b></p>
                @for (f of reviewItems(); track f.id) {
                  <label class="block"><input type="checkbox" [checked]="accept().includes(f.id)" (change)="toggleAccept(f.id)"> accept <code>{{ f.id }}</code> — {{ f.title }}</label>
                }
              }
              <label>Type the first 10 characters of the candidate SHA to approve
                <input [(ngModel)]="confirmSha" [placeholder]="(r.candidateSha ?? '').slice(0, 3) + '…'"></label>
              <button class="primary" (click)="approve()" [disabled]="confirmSha.length < 10 || !!api.busy()">Approve candidate</button>
            }
          </section>
          <section class="card">
            <h3>Publish</h3>
            <p class="muted small">Pushes only the approved candidate to the new branch <code>{{ r.branch }}</code> using your git credential helper. Never forced; protected branches refused.</p>
            <label class="inline"><input type="checkbox" [(ngModel)]="createPr"> create pull request (configured provider)</label>
            <button class="primary" (click)="publish()" [disabled]="!r.approval || published() || !!api.busy()">Publish</button>
            @for (pb of r.publications; track pb.at) {
              <p>{{ pb.at }} · {{ pb.pushed ? 'pushed' : 'failed' }} <code>{{ pb.branch }}</code> @if (pb.pullRequestUrl) { · <a [href]="pb.pullRequestUrl" target="_blank" rel="noopener noreferrer">pull request</a> } {{ pb.error ?? '' }}</p>
            }
          </section>
        }
        @case ('log') { <pre class="out">{{ r.log.join('\\n') }}</pre> }
      }
    }
  `,
})
export class RunPage implements OnInit {
  id = input.required<string>();
  api = inject(Api);
  router = inject(Router);
  tabs: Tab[] = ['summary', 'findings', 'classification', 'conflict', 'validation', 'diff', 'publish', 'log'];
  tab = signal<Tab>('summary');
  run = signal<Run | null>(null);
  plan = signal<Plan | null>(null);
  workspaceExists = signal(false);
  evidence = signal<{ name: string; content: string }[]>([]);
  diffLines = signal<string[]>([]);
  accept = signal<string[]>([]);
  confirmSha = ''; createPr = false; trust = false;

  allFindings = computed(() => {
    const seen = new Set<string>();
    return [...(this.plan()?.findings ?? []), ...(this.run()?.findings ?? [])].filter((f) => !seen.has(f.id) && !!seen.add(f.id));
  });
  reviewItems = computed(() => this.allFindings().filter((f) => f.requiresReview && !f.blocking));
  published = computed(() => (this.run()?.publications ?? []).some((p) => p.pushed));
  resultCommits = computed(() => Object.entries(this.run()?.resultCommits ?? {}));

  async ngOnInit() { await this.load(); }

  async load() {
    const r = await this.api.run('Loading run', () => this.api.get<{ run: Run; plan: Plan; workspaceExists: boolean }>(`/runs/${this.id()}`));
    if (!r) return;
    this.run.set(r.run); this.plan.set(r.plan); this.workspaceExists.set(r.workspaceExists);
    if (r.run.conflict) {
      this.evidence.set(await this.api.get(`/runs/${this.id()}/evidence`));
      this.tab.set('conflict');
    }
  }

  lineClass(l: string) {
    return l.startsWith('+++') || l.startsWith('---') ? 'dh' : l.startsWith('+') ? 'da' : l.startsWith('-') ? 'dd' : l.startsWith('@@') ? 'dhunk' : l.startsWith('diff ') ? 'dfile' : '';
  }
  async loadDiff() { this.diffLines.set(((await this.api.run('Loading diff', () => this.api.text(`/runs/${this.id()}/diff`))) ?? '').split('\n')); }
  toggleAccept(id: string) { const a = this.accept(); this.accept.set(a.includes(id) ? a.filter((x) => x !== id) : [...a, id]); }

  async resume() { await this.api.job('Resuming run', `/runs/${this.id()}/resume`, { trustValidation: this.trust }).catch(() => {}); await this.load(); }
  async abort() { if (confirm('Abort this run? The harness workspace and branch are removed; reports are kept.')) { await this.api.run('Aborting', () => this.api.post(`/runs/${this.id()}/abort`)); await this.load(); } }
  async cleanup() {
    if (!confirm('Remove all harness-owned artifacts of this run (workspace, harness branch, reports)?')) return;
    if (await this.api.run('Cleaning up', () => this.api.post(`/runs/${this.id()}/cleanup`))) this.router.navigate(['/runs']);
  }
  async approve() {
    await this.api.run('Approving', () => this.api.post(`/runs/${this.id()}/approve`, { candidate: this.confirmSha.trim(), accept: this.accept() }));
    await this.load();
  }
  async publish() {
    if (!confirm(`Push ${this.run()!.candidateSha} to new branch ${this.run()!.branch} on the target remote?`)) return;
    await this.api.job('Publishing', `/runs/${this.id()}/publish`, { createPr: this.createPr }).catch(() => {});
    await this.load();
  }
}
