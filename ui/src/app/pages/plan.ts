import { Component, inject, input, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Api, Plan } from '../api';
import { Findings, Sha, StatusChip } from '../shared';
import { PlanDetails } from './plan-details';

@Component({
  selector: 'fb-plan',
  imports: [FormsModule, Findings, Sha, StatusChip, PlanDetails],
  template: `
    @if (plan(); as p) {
      <h2>Plan <code>{{ p.id }}</code> <fb-status [status]="p.preliminaryStatus" /></h2>
      <section class="card kv">
        <div><span>Pair</span><b>{{ p.pair }}: {{ p.fromProduct }} → {{ p.toProduct }}</b> ({{ p.mode }})</div>
        <div><span>Source head</span><fb-sha [sha]="p.sourceHeadSha" [len]="40" /> {{ p.sourceRef }}</div>
        @if (p.sourceBaseSha) { <div><span>Source base</span><fb-sha [sha]="p.sourceBaseSha" [len]="40" /> {{ p.sourceBaseRef }}</div> }
        <div><span>Target base</span><fb-sha [sha]="p.targetBaseSha" [len]="40" /> {{ p.targetBaseRef }}</div>
        @if (p.mergeBase) { <div><span>Merge base</span><fb-sha [sha]="p.mergeBase" [len]="40" /></div> }
        <div><span>Components</span>{{ p.selectedComponents.join(', ') }}</div>
        <div><span>Policy</span><code>{{ p.policyHash }}</code></div>
        <div><span>Freshness</span>@if (p.freshness.offline) { <b class="warn">{{ p.freshness.note }}</b> } @else { remote fetched {{ p.freshness.fetchedAt }} }</div>
        @if (p.mergeBaseExplanation) { <p class="explain">{{ p.mergeBaseExplanation }}</p> }
      </section>

      <section class="card">
        <h3>Simulate in an isolated workspace</h3>
        <p class="muted small">Validation commands execute repository code. They run only when the profile is trusted in the config <b>and</b> you tick the box below.</p>
        <div class="row">
          <label class="inline"><input type="checkbox" [(ngModel)]="trust"> trust validation commands for this run</label>
          <label class="inline"><input type="checkbox" [(ngModel)]="preserve"> preserve workspace</label>
          <button class="primary" (click)="simulate()" [disabled]="!!api.busy() || p.preliminaryStatus === 'BLOCKED' || p.preliminaryStatus === 'NO_CHANGES'">Simulate</button>
        </div>
      </section>

      <h3>Findings</h3>
      <fb-findings [findings]="p.findings" />
      <fb-plan-details [plan]="p" />
    }
  `,
})
export class PlanPage implements OnInit {
  id = input.required<string>();
  api = inject(Api);
  router = inject(Router);
  plan = signal<Plan | null>(null);
  trust = false; preserve = false;

  async ngOnInit() {
    this.plan.set((await this.api.run('Loading plan', () => this.api.get<Plan>(`/plans/${this.id()}`))) ?? null);
  }

  async simulate() {
    const r = await this.api.job<{ runId: string }>('Simulating (apply, analyse, baseline + candidate validation)', `/plans/${this.id()}/simulate`,
      { trustValidation: this.trust, preserveWorkspace: this.preserve }).catch(() => null);
    if (r) this.router.navigate(['/runs', r.runId]);
  }
}
