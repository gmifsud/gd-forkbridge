import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { Api, ConflictFileDetail, Plan, Run } from '../api';
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
              <div class="row" style="justify-content: space-between">
                <div>
                  <b>{{ c.operation }}</b> stopped.
                  <span class="muted small">Resolve each conflicted file below directly in the UI, then click Resume.</span>
                </div>
                <div class="row">
                  <label class="inline"><input type="checkbox" [(ngModel)]="trust"> trust validation</label>
                  <button class="primary" (click)="resume()" [disabled]="!!api.busy()">Resume Run</button>
                </div>
              </div>
              @if (c.output) { <details><summary class="small muted">Git output & CLI instructions</summary><pre class="out">{{ c.output }}</pre></details> }
            </section>

            @for (cf of conflictFiles(); track cf.file) {
              <section class="card">
                <div class="row" style="justify-content: space-between">
                  <div>
                    <code>{{ cf.file }}</code>
                    @if (cf.isBinary) { <span class="chip">BINARY</span> }
                    <span class="chip" [class.s-BLOCKED]="cf.hasConflictMarkers || cf.nothingApplied" [class.s-READY]="!cf.hasConflictMarkers && !cf.nothingApplied">
                      {{ cf.hasConflictMarkers ? 'Needs Resolution' : cf.nothingApplied ? 'Patch Rejected (Select Resolution)' : 'Staged / Clean' }}
                    </span>
                  </div>
                  <div class="row">
                    <button class="small" (click)="resolveFile(cf.file, 'target')" [disabled]="!!api.busy()" title="Keep the target repository version of this file">Use Target (Ours)</button>
                    <button class="small" (click)="resolveFile(cf.file, 'source')" [disabled]="!!api.busy()" title="Take the incoming source repository version of this file">Use Source (Theirs)</button>
                    <button class="small danger" (click)="resolveFile(cf.file, 'exclude')" [disabled]="!!api.busy()" title="Drop changes to this file from the commit">Exclude File</button>
                  </div>
                </div>

                @if (cf.isBinary) {
                  <p class="muted small">Binary file conflict — choose <b>Use Target (Ours)</b>, <b>Use Source (Theirs)</b>, or <b>Exclude File</b> above.</p>
                } @else {
                  <details [open]="cf.hasConflictMarkers || cf.nothingApplied">
                    <summary class="small">Inspect &amp; Edit File Content (Working Tree / Target / Source / Base)</summary>
                    <div class="row small" style="margin-top: 6px">
                      <button class="small" (click)="fillEditor(cf.file, cf.targetContent ?? '')">Load Target into Editor</button>
                      <button class="small" (click)="fillEditor(cf.file, cf.sourceContent ?? '')">Load Source into Editor</button>
                      @if (cf.baseContent !== null && cf.baseContent !== undefined) {
                        <button class="small" (click)="fillEditor(cf.file, cf.baseContent)">Load Base into Editor</button>
                      }
                      <button class="small" (click)="fillEditor(cf.file, cf.workingTree ?? '')">Reset to Working Tree</button>
                      <button class="small primary" (click)="resolveFile(cf.file, 'custom', customContents[cf.file])" [disabled]="!!api.busy()">Save Custom Resolution &amp; Stage</button>
                    </div>
                    <textarea class="code-editor" rows="14" [(ngModel)]="customContents[cf.file]"></textarea>
                    <div class="grid2">
                      @if (cf.targetContent !== null && cf.targetContent !== undefined) {
                        <div><h4>Target (Ours)</h4><pre class="out">{{ cf.targetContent }}</pre></div>
                      }
                      @if (cf.sourceContent !== null && cf.sourceContent !== undefined) {
                        <div><h4>Source (Theirs)</h4><pre class="out">{{ cf.sourceContent }}</pre></div>
                      }
                      @if (cf.baseContent !== null && cf.baseContent !== undefined) {
                        <div><h4>Base (Ancestor)</h4><pre class="out">{{ cf.baseContent }}</pre></div>
                      }
                    </div>
                  </details>
                }
              </section>
            } @empty {
              <p class="muted">No conflicted file details found.</p>
            }

            @if (evidence().length) {
              <details>
                <summary class="small muted">Raw conflict evidence files</summary>
                <div class="evidence">
                  @for (e of evidence(); track e.name) { <div><h4>{{ e.name }}</h4><pre>{{ e.content }}</pre></div> }
                </div>
              </details>
            }
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
              <p>Approved by {{ a.approvedBy }} at {{ a.approvedAt }} @if (a.validationOverridden) { <span class="chip s-REVIEW_REQUIRED">validation overridden</span> }</p>
              <p class="small">Binding: <code>{{ a.binding }}</code></p>
            } @else {
              <p class="muted small">Approval binds the exact candidate SHA (<fb-sha [sha]="r.candidateSha" [len]="12" />), target remote, branch, report hash, policy and target base.</p>
              @if (reviewItems().length) {
                <div class="row" style="justify-content: space-between">
                  <b>Explicit decisions required:</b>
                  <button class="small" (click)="acceptAllReviewItems()">Accept all review items</button>
                </div>
                @for (f of reviewItems(); track f.id) {
                  <label class="block"><input type="checkbox" [checked]="accept().includes(f.id)" (change)="toggleAccept(f.id)"> accept <code>{{ f.id }}</code> — {{ f.title }}</label>
                }
              }
              @if (r.status === 'VALIDATION_FAILED' || hasValidationFailure()) {
                <label class="block warn" style="margin: 8px 0">
                  <input type="checkbox" [(ngModel)]="overrideValidation">
                  <b>Override validation failure / unavailable validation environment</b> and allow approving this candidate
                </label>
              }
              <div class="row">
                <label style="flex: 1">Type the first 10+ characters of the candidate SHA to approve
                  <input [(ngModel)]="confirmSha" [placeholder]="(r.candidateSha ?? '').slice(0, 10)">
                </label>
                @if (r.candidateSha) {
                  <button class="small" (click)="confirmSha = r.candidateSha!.slice(0, 12)">Fill SHA ({{ r.candidateSha.slice(0, 10) }})</button>
                }
              </div>
              <button class="primary" (click)="approve()" [disabled]="confirmSha.trim().length < 10 || !!api.busy()">Approve candidate</button>
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
  conflictFiles = signal<ConflictFileDetail[]>([]);
  customContents: Record<string, string> = {};
  diffLines = signal<string[]>([]);
  accept = signal<string[]>([]);
  confirmSha = ''; createPr = false; trust = false; overrideValidation = false;

  allFindings = computed(() => {
    const seen = new Set<string>();
    return [...(this.plan()?.findings ?? []), ...(this.run()?.findings ?? [])].filter((f) => !seen.has(f.id) && !!seen.add(f.id));
  });
  reviewItems = computed(() => this.allFindings().filter((f) => f.requiresReview && !f.blocking));
  hasValidationFailure = computed(() => (this.run()?.validationComparison ?? []).some((v) => v.required && v.verdict !== 'PASS' && v.verdict !== 'SKIPPED'));
  published = computed(() => (this.run()?.publications ?? []).some((p) => p.pushed));
  resultCommits = computed(() => Object.entries(this.run()?.resultCommits ?? {}));

  async ngOnInit() { await this.load(); }

  async load() {
    const r = await this.api.run('Loading run', () => this.api.get<{ run: Run; plan: Plan; workspaceExists: boolean }>(`/runs/${this.id()}`));
    if (!r) return;
    this.run.set(r.run); this.plan.set(r.plan); this.workspaceExists.set(r.workspaceExists);
    if (r.run.conflict) {
      this.evidence.set(await this.api.get(`/runs/${this.id()}/evidence`));
      const cfs = await this.api.get<ConflictFileDetail[]>(`/runs/${this.id()}/conflict-files`);
      this.conflictFiles.set(cfs);
      for (const cf of cfs) {
        if (this.customContents[cf.file] === undefined) {
          this.customContents[cf.file] = cf.workingTree ?? cf.sourceContent ?? cf.targetContent ?? '';
        }
      }
      this.tab.set('conflict');
    }
  }

  fillEditor(file: string, content: string) {
    this.customContents[file] = content;
  }

  async resolveFile(file: string, resolution: 'target' | 'source' | 'custom' | 'exclude', content?: string) {
    const updated = await this.api.run(`Resolving ${file} (${resolution})`, () =>
      this.api.post<ConflictFileDetail[]>(`/runs/${this.id()}/resolve-conflict`, { file, resolution, content })
    );
    if (updated) {
      this.conflictFiles.set(updated);
      const resolvedEntry = updated.find((cf) => cf.file === file);
      if (resolvedEntry) {
        this.customContents[file] = resolvedEntry.workingTree ?? '';
      }
      for (const cf of updated) {
        if (this.customContents[cf.file] === undefined) {
          this.customContents[cf.file] = cf.workingTree ?? cf.sourceContent ?? cf.targetContent ?? '';
        }
      }
    }
  }

  acceptAllReviewItems() {
    this.accept.set(this.reviewItems().map((f) => f.id));
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
    await this.api.run('Approving', () => this.api.post(`/runs/${this.id()}/approve`, {
      candidate: this.confirmSha.trim(),
      accept: this.accept(),
      overrideValidation: this.overrideValidation,
    }));
    await this.load();
  }
  async publish() {
    if (!confirm(`Push ${this.run()!.candidateSha} to new branch ${this.run()!.branch} on the target remote?`)) return;
    await this.api.job('Publishing', `/runs/${this.id()}/publish`, { createPr: this.createPr }).catch(() => {});
    await this.load();
  }
}
