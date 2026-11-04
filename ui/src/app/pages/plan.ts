import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { Api, Plan, PlanFileDiff } from '../api';
import {
  copyTextToClipboard,
  DiffRequestEvent,
  downloadTextFile,
  extractLibrary,
  FileDiffModal,
  FindingAction,
  Findings,
  Sha,
  StatusChip,
} from '../shared';
import { PlanDetails } from './plan-details';

type PlanTab = 'findings' | 'libraries' | 'commits' | 'drift' | 'impact' | 'overview';

@Component({
  selector: 'fb-plan',
  imports: [FormsModule, RouterLink, Findings, Sha, StatusChip, PlanDetails, FileDiffModal],
  template: `
    @if (plan(); as p) {
      <div class="plan-top-bar card">
        <div class="plan-top-title">
          <h2>
            Plan <code>{{ p.id }}</code>
            <fb-status [status]="p.preliminaryStatus" />
            @if (mdToast()) { <span class="copied-badge">{{ mdToast() }}</span> }
          </h2>
          <div class="plan-top-meta muted small">
            <b>{{ p.pair }}: {{ p.fromProduct }} → {{ p.toProduct }}</b> ({{ p.mode }})
            · Source: <fb-sha [sha]="p.sourceHeadSha" [len]="10" /> ({{ p.sourceRef }})
            · Target: <fb-sha [sha]="p.targetBaseSha" [len]="10" /> ({{ p.targetBaseRef }})
            @if (p.parentPlanId) {
              · Derived from <a [routerLink]="['/plans', p.parentPlanId]"><code>{{ p.parentPlanId }}</code></a>
            }
          </div>
        </div>

        <div class="plan-top-actions">
          <label class="inline small"><input type="checkbox" [(ngModel)]="trust"> trust validation</label>
          <label class="inline small"><input type="checkbox" [(ngModel)]="preserve"> preserve workspace</label>
          <button class="primary" (click)="simulate()" [disabled]="!!api.busy() || p.preliminaryStatus === 'BLOCKED' || p.preliminaryStatus === 'NO_CHANGES'">
            Simulate
          </button>
          <button (click)="reevaluate()" [disabled]="!!api.busy()" title="Creates a derived plan re-classified against the current forkbridge.yml policy">
            Re-evaluate Policy
          </button>
          <button (click)="exportMarkdown()" [disabled]="!!api.busy()" title="Download this plan as a Markdown (.md) report">
            Export Plan (.md)
          </button>
          <button (click)="copyMarkdown()" [disabled]="!!api.busy()" title="Copy the full Markdown report for this plan to the clipboard">
            Copy Plan (.md)
          </button>
          @if (p.preliminaryStatus === 'BLOCKED') {
            <button class="primary" (click)="unblockAll()" [disabled]="!!api.busy()" title="Creates a derived plan that excludes all blocked paths and downgrades blocking findings to review items">
              Unblock All
            </button>
          }
        </div>
      </div>

      @if (p.preliminaryStatus === 'BLOCKED') {
        <div class="banner err">
          <span>
            <b>BLOCKED ({{ blockingCount() }} blocker{{ blockingCount() === 1 ? '' : 's' }}):</b>
            Inspect file diffs or exclude unused libraries/files in the <b>Findings</b> or <b>Libraries / Subprojects</b> tab, or click <b>Unblock All</b> above.
          </span>
        </div>
      }

      <nav class="tabs">
        <button type="button" [class.active]="tab() === 'findings'" (click)="tab.set('findings')">
          Findings ({{ p.findings.length }})
          @if (blockingCount() > 0) { <span class="tab-badge warn-badge">{{ blockingCount() }} blocking</span> }
        </button>
        <button type="button" [class.active]="tab() === 'libraries'" (click)="tab.set('libraries')">
          Libraries / Subprojects ({{ libraryCount() }})
        </button>
        <button type="button" [class.active]="tab() === 'commits'" (click)="tab.set('commits')">
          Changed Files &amp; Commits ({{ changedFileCount() }})
        </button>
        <button type="button" [class.active]="tab() === 'drift'" (click)="tab.set('drift')">
          Component Drift ({{ p.drift.length }})
        </button>
        <button type="button" [class.active]="tab() === 'impact'" (click)="tab.set('impact')">
          Dependency Impact ({{ impactCount() }})
        </button>
        <button type="button" [class.active]="tab() === 'overview'" (click)="tab.set('overview')">
          Overview &amp; Overrides ({{ overrideCount() }})
        </button>
      </nav>

      @switch (tab()) {
        @case ('findings') {
          <fb-findings
            [findings]="p.findings"
            [accepted]="p.acceptedFindings ?? []"
            [interactive]="true"
            (action)="onFindingAction($event)"
            (viewDiff)="openFileDiff($event)" />
        }
        @case ('libraries') {
          <fb-plan-details
            [plan]="p"
            [interactive]="true"
            [section]="'libraries'"
            (overridePath)="onOverridePath($event)"
            (overridePaths)="onOverridePaths($event)"
            (policyAction)="onPolicyAction($event)"
            (viewDiff)="openFileDiff($event)" />
        }
        @case ('commits') {
          <fb-plan-details
            [plan]="p"
            [interactive]="true"
            [section]="'commits'"
            (overridePath)="onOverridePath($event)"
            (overridePaths)="onOverridePaths($event)"
            (policyAction)="onPolicyAction($event)"
            (viewDiff)="openFileDiff($event)" />
        }
        @case ('drift') {
          <fb-plan-details
            [plan]="p"
            [interactive]="true"
            [section]="'drift'"
            (overridePath)="onOverridePath($event)"
            (overridePaths)="onOverridePaths($event)"
            (policyAction)="onPolicyAction($event)"
            (viewDiff)="openFileDiff($event)" />
        }
        @case ('impact') {
          <fb-plan-details
            [plan]="p"
            [interactive]="true"
            [section]="'impact'"
            (overridePath)="onOverridePath($event)"
            (overridePaths)="onOverridePaths($event)"
            (policyAction)="onPolicyAction($event)"
            (viewDiff)="openFileDiff($event)" />
        }
        @case ('overview') {
          <section class="card kv">
            <h3>Plan Metadata</h3>
            @if (p.parentPlanId) {
              <div><span>Derived from</span><a [routerLink]="['/plans', p.parentPlanId]"><code>{{ p.parentPlanId }}</code></a></div>
            }
            <div><span>Pair</span><b>{{ p.pair }}: {{ p.fromProduct }} → {{ p.toProduct }}</b> ({{ p.mode }})</div>
            <div><span>Source head</span><fb-sha [sha]="p.sourceHeadSha" [len]="40" /> {{ p.sourceRef }}</div>
            @if (p.sourceBaseSha) { <div><span>Source base</span><fb-sha [sha]="p.sourceBaseSha" [len]="40" /> {{ p.sourceBaseRef }}</div> }
            <div><span>Target base</span><fb-sha [sha]="p.targetBaseSha" [len]="40" /> {{ p.targetBaseRef }}</div>
            @if (p.mergeBase) { <div><span>Merge base</span><fb-sha [sha]="p.mergeBase" [len]="40" /></div> }
            <div><span>Components</span>{{ p.selectedComponents.join(', ') }}</div>
            <div><span>Policy hash</span><code>{{ p.policyHash }}</code></div>
            <div>
              <span>Freshness</span>
              @if (p.freshness.offline) { <b class="warn">{{ p.freshness.note }}</b> }
              @else { remote fetched {{ p.freshness.fetchedAt }} }
            </div>
            @if (p.mergeBaseExplanation) { <p class="explain">{{ p.mergeBaseExplanation }}</p> }
          </section>

          <section class="card">
            <h3>Active Path &amp; Library Overrides in Plan ({{ pathOverrideEntries().length }})</h3>
            @if (pathOverrideEntries().length === 0) {
              <p class="muted small">No per-plan path or library overrides active.</p>
            } @else {
              <div class="table-scroll">
                <table class="grid compact-grid">
                  <thead>
                    <tr>
                      <th class="col-idx">#</th>
                      <th>Path / Library Glob</th>
                      <th>Override Decision</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (kv of pathOverrideEntries(); track kv[0]; let i = $index) {
                      <tr>
                        <td class="col-idx">{{ i + 1 }}</td>
                        <td><code>{{ kv[0] }}</code></td>
                        <td><b>{{ kv[1] }}</b></td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            }
          </section>
        }
      }

      <fb-file-diff-modal
        [diff]="activeFileDiff()"
        [fromProduct]="p.fromProduct"
        [toProduct]="p.toProduct"
        [interactive]="true"
        [fileList]="diffFileList()"
        (close)="activeFileDiff.set(null)"
        (selectFile)="openFileDiff({ path: $event, files: diffFileList() })"
        (excludePaths)="onOverridePaths({ paths: $event, decision: 'Exclude' })"
        (includePaths)="onOverridePaths({ paths: $event, decision: 'Include' })"
        (policyIgnore)="onPolicyAction({ action: 'add-ignored', patterns: $event })"
        (policyProductSpecific)="onPolicyAction({ action: 'add-product-specific', patterns: $event })" />
    }
  `,
})
export class PlanPage {
  id = input.required<string>();
  api = inject(Api);
  router = inject(Router);
  plan = signal<Plan | null>(null);
  tab = signal<PlanTab>('findings');
  activeFileDiff = signal<PlanFileDiff | null>(null);
  diffFileList = signal<string[]>([]);
  mdToast = signal('');
  trust = false;
  preserve = false;

  blockingCount = computed(() => (this.plan()?.findings ?? []).filter((f) => f.blocking).length);

  changedFileCount = computed(() => {
    const p = this.plan();
    if (!p) return 0;
    const commits = p.reconciliation ? [...p.commits, p.reconciliation] : p.commits;
    return commits.reduce((acc, c) => acc + c.paths.length, 0);
  });

  libraryCount = computed(() => {
    const p = this.plan();
    if (!p) return 0;
    const libs = new Set<string>();
    const commits = p.reconciliation ? [...p.commits, p.reconciliation] : p.commits;
    for (const c of commits) {
      for (const d of c.paths) libs.add(extractLibrary(d.sourcePath));
    }
    for (const d of p.drift) {
      for (const f of d.targetDivergedSinceBaseline) libs.add(extractLibrary(f));
      for (const f of d.differing) libs.add(extractLibrary(f));
      for (const f of d.onlyInTarget) libs.add(extractLibrary(f));
    }
    return libs.size;
  });

  impactCount = computed(() => {
    const i = this.plan()?.impact;
    if (!i) return 0;
    return i.affectedProjects.length + i.sharedConsumers.length + i.productConsumers.length + i.frontendConsumers.length;
  });

  pathOverrideEntries = computed<[string, string][]>(() => {
    const po = this.plan()?.pathOverrides;
    if (!po) return [];
    return Object.entries(po);
  });

  overrideCount = computed(() => {
    const p = this.plan();
    if (!p) return 0;
    return this.pathOverrideEntries().length + (p.overriddenFindings?.length ?? 0);
  });

  constructor() {
    effect(() => {
      const targetId = this.id();
      if (this.plan()?.id === targetId) return;
      void this.loadPlan(targetId);
    });
  }

  private async loadPlan(planId: string) {
    const loaded = await this.api.run('Loading plan', () => this.api.get<Plan>(`/plans/${planId}`));
    if (loaded) this.plan.set(loaded);
  }

  private applyDerivedPlan(updated?: Plan) {
    if (!updated) return;
    this.plan.set(updated);
    if (updated.id !== this.id()) {
      void this.router.navigate(['/plans', updated.id]);
    }
  }

  async openFileDiff(ev: DiffRequestEvent) {
    const p = this.plan();
    if (!p) return;
    if (ev.files && ev.files.length > 0) {
      this.diffFileList.set(ev.files);
    }
    const q = new URLSearchParams({ path: ev.path });
    if (ev.commit) q.set('commit', ev.commit);
    const res = await this.api.run(`Loading diff for ${ev.path}`, () =>
      this.api.get<PlanFileDiff>(`/plans/${p.id}/file-diff?${q.toString()}`)
    );
    if (res) this.activeFileDiff.set(res);
  }

  async exportMarkdown() {
    const p = this.plan();
    if (!p) return;
    const md = await this.api.run('Exporting plan Markdown', () => this.api.text(`/plans/${p.id}/markdown`));
    if (md !== undefined) {
      downloadTextFile(`forkbridge-plan-${p.id}.md`, md);
      this.flashToast(`Downloaded forkbridge-plan-${p.id}.md`);
    }
  }

  async copyMarkdown() {
    const p = this.plan();
    if (!p) return;
    const md = await this.api.run('Fetching plan Markdown', () => this.api.text(`/plans/${p.id}/markdown`));
    if (md !== undefined && (await copyTextToClipboard(md))) {
      this.flashToast('Copied Plan Markdown to clipboard!');
    }
  }

  private flashToast(msg: string) {
    this.mdToast.set(msg);
    setTimeout(() => {
      if (this.mdToast() === msg) this.mdToast.set('');
    }, 2500);
  }

  async simulate() {
    const currentId = this.plan()?.id ?? this.id();
    const r = await this.api
      .job<{ runId: string }>('Simulating (apply, analyse, baseline + candidate validation)', `/plans/${currentId}/simulate`, {
        trustValidation: this.trust,
        preserveWorkspace: this.preserve,
      })
      .catch(() => null);
    if (r) this.router.navigate(['/runs', r.runId]);
  }

  async reevaluate() {
    const currentId = this.plan()?.id ?? this.id();
    const updated = await this.api.run('Re-evaluating plan with current policy', () =>
      this.api.post<Plan>(`/plans/${currentId}/resolve`, {})
    );
    this.applyDerivedPlan(updated);
  }

  async unblockAll() {
    const currentId = this.plan()?.id ?? this.id();
    const updated = await this.api.run('Unblocking plan (excluding blocked paths & overriding blockers)', () =>
      this.api.post<Plan>(`/plans/${currentId}/resolve`, { unblockAll: true, defaultFileDecision: 'Exclude' })
    );
    this.applyDerivedPlan(updated);
  }

  async onOverridePath(ev: { path: string; decision: 'Include' | 'Exclude' }) {
    await this.onOverridePaths({ paths: [ev.path], decision: ev.decision });
  }

  async onOverridePaths(ev: { paths: string[]; decision: 'Include' | 'Exclude' }) {
    const currentId = this.plan()?.id ?? this.id();
    const pathOverrides: Record<string, string> = {};
    for (const path of ev.paths) pathOverrides[path] = ev.decision;
    const label = ev.paths.length === 1 ? `${ev.paths[0]} -> ${ev.decision}` : `${ev.paths.length} path(s)/library -> ${ev.decision}`;
    const updated = await this.api.run(`Setting ${label}`, () =>
      this.api.post<Plan>(`/plans/${currentId}/resolve`, { pathOverrides })
    );
    this.applyDerivedPlan(updated);
  }

  async onPolicyAction(ev: { action: 'add-ignored' | 'add-product-specific' | 'add-target-pinned'; patterns: string[] }) {
    const p = this.plan();
    if (!p || ev.patterns.length === 0) return;
    const res = await this.api.run(`Updating policy (${ev.action}: ${ev.patterns[0]}) and re-evaluating plan`, () =>
      this.api.post<{ ok: boolean; plan?: Plan }>('/config/policy-action', {
        pair: p.pair,
        action: ev.action,
        product: 'both',
        patterns: ev.patterns,
        planId: p.id,
      })
    );
    this.applyDerivedPlan(res?.plan);
  }

  async onFindingAction(ev: FindingAction) {
    const p = this.plan();
    if (!p) return;
    const f = ev.finding;
    const targetPaths = ev.paths && ev.paths.length > 0 ? ev.paths : f.files;
    const isWholeFinding = !ev.paths || ev.paths.length === 0;

    if (ev.type === 'exclude-files' || ev.type === 'include-files') {
      const dec = ev.type === 'exclude-files' ? 'Exclude' : 'Include';
      const pathOverrides: Record<string, string> = {};
      for (const file of targetPaths) pathOverrides[file] = dec;
      const overrideFindings = isWholeFinding && f.blocking ? [f.id] : [];
      const updated = await this.api.run(`Applying ${dec} override to ${targetPaths[0]}${targetPaths.length > 1 ? ` (+${targetPaths.length - 1} more)` : ''}`, () =>
        this.api.post<Plan>(`/plans/${p.id}/resolve`, { pathOverrides, overrideFindings })
      );
      this.applyDerivedPlan(updated);
      return;
    }
    if (ev.type === 'override-finding') {
      const updated = await this.api.run(`Overriding finding ${f.id}`, () =>
        this.api.post<Plan>(`/plans/${p.id}/resolve`, { overrideFindings: [f.id], defaultFileDecision: 'Exclude' })
      );
      this.applyDerivedPlan(updated);
      return;
    }
    if (ev.type === 'policy-ignored' || ev.type === 'policy-product-both' || ev.type === 'policy-pinned') {
      const action =
        ev.type === 'policy-ignored'
          ? 'add-ignored'
          : ev.type === 'policy-product-both'
            ? 'add-product-specific'
            : 'add-target-pinned';
      await this.onPolicyAction({ action, patterns: targetPaths });
      return;
    }
    if (ev.type === 'policy-confirm-components') {
      const res = await this.api.run('Confirming proposed components in policy and re-evaluating plan', () =>
        this.api.post<{ ok: boolean; plan?: Plan }>('/config/policy-action', {
          pair: p.pair,
          action: 'set-component-status',
          componentId: '*',
          status: 'confirmed',
          planId: p.id,
        })
      );
      this.applyDerivedPlan(res?.plan);
    }
  }
}
