import { Component, computed, input } from '@angular/core';
import { classifyDiffLines } from './diff-lines';

/** Read-only unified diff view. Shared by the Harmony File Diff Inspector and the Merge inspector. */
@Component({
  selector: 'fb-unified-diff',
  template: `
    @if (diff()) {
      <pre class="diff diff-viewer-pre">@for (l of lines(); track $index) {<span [class]="l.cls">{{ l.text }}
</span>}</pre>
      @if (truncated()) { <p class="small muted">Diff truncated for display (the full content is unaffected).</p> }
    } @else {
      <div class="empty-diff-note muted">{{ empty() }}</div>
    }
  `,
})
export class UnifiedDiff {
  readonly diff = input('');
  readonly empty = input('No differences.');
  readonly truncated = input(false);
  readonly lines = computed(() => classifyDiffLines(this.diff()));
}
