import { Component, computed, input } from '@angular/core';
import { Finding } from './api';

@Component({
  selector: 'fb-status',
  template: `<span class="chip" [class]="'chip s-' + status()">{{ status() }}</span>`,
})
export class StatusChip {
  status = input.required<string>();
}

@Component({
  selector: 'fb-sha',
  template: `<code class="sha" [title]="sha() ?? ''">{{ (sha() ?? '').slice(0, len()) }}</code>`,
})
export class Sha {
  sha = input<string | undefined | null>();
  len = input(10);
}

@Component({
  selector: 'fb-findings',
  template: `
    @if (sorted().length === 0) { <p class="muted">No findings.</p> }
    @for (f of sorted(); track f.id) {
      <details class="finding" [class.block]="f.blocking" [class.review]="!f.blocking && f.requiresReview">
        <summary>
          <span class="gate">{{ f.blocking ? 'BLOCKS' : f.requiresReview ? (accepted().includes(f.id) ? 'ACCEPTED' : 'REVIEW') : f.severity }}</span>
          <span class="ftitle">{{ f.title }}</span>
          <code class="fid">{{ f.id }}</code>
        </summary>
        <dl>
          <dt>Category</dt><dd>{{ f.category }} · {{ f.severity }} · {{ f.origin }}</dd>
          @if (f.component) { <dt>Component</dt><dd>{{ f.component }}</dd> }
          @if (f.commits.length) { <dt>Commits</dt><dd>@for (c of f.commits; track c) { <code>{{ c.slice(0, 10) }}</code> }</dd> }
          @if (f.files.length) { <dt>Files</dt><dd class="files">@for (x of f.files.slice(0, 30); track x) { <code>{{ x }}</code> }</dd> }
          <dt>Evidence</dt><dd><ul>@for (e of f.evidence; track $index) { <li>{{ e }}</li> }</ul></dd>
          @if (f.suggestedAction) { <dt>Action</dt><dd>{{ f.suggestedAction }}</dd> }
        </dl>
      </details>
    }
  `,
})
export class Findings {
  findings = input.required<Finding[]>();
  accepted = input<string[]>([]);
  sorted = computed(() => [...this.findings()].sort((a, b) =>
    Number(b.blocking) - Number(a.blocking) || Number(b.requiresReview) - Number(a.requiresReview) || a.code.localeCompare(b.code)));
}
