import { Component, input } from '@angular/core';
import { Plan } from '../api';

/** Commit list, per-path classification, drift and dependency impact for a plan. */
@Component({
  selector: 'fb-plan-details',
  template: `
    @let p = plan();
    <h3>Commits and changed-file classification</h3>
    @for (c of allCommits(); track c.sha) {
      <details class="commit" [open]="c.state === 'Pending' || c.state === 'Blocked'">
        <summary>
          <code>{{ c.sha.slice(0, 10) }}</code> <span class="chip s-{{ c.state }}">{{ c.state }}</span>
          @if (c.mixed) { <span class="chip s-REVIEW_REQUIRED">mixed</span> } @if (c.isMerge) { <span class="chip">merge</span> }
          {{ c.subject }} <span class="muted small">— {{ c.author }}</span>
        </summary>
        @if (c.stateEvidence) { <p class="muted small">{{ c.stateEvidence }}</p> }
        <table class="tbl">
          <tr><th>St</th><th>Source path</th><th>Target path</th><th>Class</th><th>Decision</th><th>Reason</th></tr>
          @for (d of c.paths; track $index) {
            <tr [class]="'d-' + d.decision">
              <td>{{ d.status }}</td>
              <td><code>{{ d.sourceOldPath ? d.sourceOldPath + ' → ' : '' }}{{ d.sourcePath }}</code></td>
              <td>@if (d.targetPath) { <code>{{ d.targetPath }}</code> }</td>
              <td>{{ d.sourceClass }}</td><td><b>{{ d.decision }}</b></td><td>{{ d.reason }}{{ d.binary ? ' (binary)' : '' }}</td>
            </tr>
          }
        </table>
      </details>
    }

    <h3>Existing drift (pinned snapshots)</h3>
    <table class="tbl">
      <tr><th>Component</th><th>{{ p.fromProduct }}</th><th>{{ p.toProduct }}</th><th>Identical</th><th>Differing</th><th>Target diverged</th><th>Only source</th><th>Only target</th><th>Preserved product diffs</th></tr>
      @for (d of p.drift; track d.component) {
        <tr>
          <td><b>{{ d.component }}</b></td><td><code>{{ d.sourcePath }}</code></td>
          <td><code>{{ d.targetPath }}</code> @if (d.intentionallyAbsentInTarget) { <i>(intentionally absent)</i> } @else if (!d.presentInTarget) { <b class="warn">(missing)</b> }</td>
          <td>{{ d.identical }}</td><td [title]="d.differing.join('\\n')">{{ d.differing.length }}</td>
          <td [title]="d.targetDivergedSinceBaseline.join('\\n')">{{ d.targetDivergedSinceBaseline.length }}</td>
          <td [title]="d.onlyInSource.join('\\n')">{{ d.onlyInSource.length }}</td><td [title]="d.onlyInTarget.join('\\n')">{{ d.onlyInTarget.length }}</td>
          <td [title]="d.preservedProductDifferences.join('\\n')">{{ d.preservedProductDifferences.length }}</td>
        </tr>
      }
    </table>

    @if (p.impact; as i) {
      <h3>Dependency impact in {{ p.toProduct }}</h3>
      <div class="impact">
        <div><b>Affected projects</b><ul>@for (x of i.affectedProjects; track x) { <li><code>{{ x }}</code></li> } @empty { <li class="muted">none</li> }</ul></div>
        <div><b>Shared consumers</b><ul>@for (x of i.sharedConsumers; track x) { <li><code>{{ x }}</code></li> } @empty { <li class="muted">none</li> }</ul></div>
        <div><b>Product consumers</b><ul>@for (x of i.productConsumers; track x) { <li><code>{{ x }}</code></li> } @empty { <li class="muted">none</li> }</ul></div>
        <div><b>Frontend consumers</b><ul>@for (x of i.frontendConsumers; track x) { <li><code>{{ x }}</code></li> } @empty { <li class="muted">none</li> }</ul></div>
      </div>
    }
  `,
})
export class PlanDetails {
  plan = input.required<Plan>();
  allCommits() {
    const p = this.plan();
    return p.reconciliation ? [...p.commits, p.reconciliation] : p.commits;
  }
}
