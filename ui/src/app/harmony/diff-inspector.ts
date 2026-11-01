import { Component, computed, effect, HostListener, input, output, signal } from '@angular/core';
import { categoryLabel, Decision, DECISIONS, decisionInfo, RowDiff, short } from './harmony-api';

type Tab = 'forkVsPrimary' | 'baseToFork' | 'baseToPrimary' | 'merged' | 'side';

/** Harmony File Diff Inspector: full unified diffs of one plan row (the blobs the plan recorded), Prev/Next through
 *  the rows currently listed, and the row's direction set in place. Keys: ← / → (or K / J) previous / next,
 *  1–5 switch tabs, Esc closes. */
@Component({
  selector: 'fb-diff-inspector',
  styles: [`
    .panel-x { width: 96vw; max-width: 1500px; height: 92vh; }
    .meta { display: flex; flex-wrap: wrap; gap: 4px 18px; margin-top: 4px; font-size: 12px; color: var(--muted); }
    .meta code { color: var(--ink2); }
    .pill { display: inline-block; font-size: 12px; font-weight: 600; border-radius: 999px; padding: 2px 10px; border: 1px solid; }
    .decide { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; padding: 8px 10px; margin-top: 8px; background: var(--bg); border: 1px solid var(--line); border-radius: 8px; }
    .decide button { font-size: 12px; padding: 3px 10px; border-radius: 999px; border: 1px solid var(--line); background: transparent; cursor: pointer; }
    .decide button.on { font-weight: 700; box-shadow: 0 0 0 2px rgba(249,115,22,.45); }
    .apply { font-size: 12px; color: var(--ink3); }
    .reason { font-size: 12px; color: var(--ink3); margin-top: 6px; }
    .badge { display: inline-block; margin-left: 6px; padding: 0 6px; border-radius: 10px; font-size: 10px; background: var(--infobg); color: var(--muted); }
    .loading { display: flex; align-items: center; gap: 10px; padding: 24px; color: var(--muted); }
    .ring { width: 18px; height: 18px; border-radius: 50%; border: 3px solid rgba(249,115,22,.25); border-top-color: var(--accent); animation: spin .8s linear infinite; }
    @keyframes spin { to { transform: rotate(360deg); } }
    .side { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; height: 100%; }
    .side > div { display: flex; flex-direction: column; min-height: 0; }
    .side pre { flex: 1; margin: 4px 0 0; max-height: none; }
    .stack h4 { margin: 10px 0 4px; }
    .keys { font-size: 11px; color: var(--faint); }
    kbd { font: 11px var(--mono); border: 1px solid var(--line); border-bottom-width: 2px; border-radius: 4px; padding: 0 4px; color: var(--ink3); }
  `],
  template: `
    <div class="diff-modal-backdrop" (click)="close.emit()">
      <div class="diff-modal-panel panel-x" (click)="$event.stopPropagation()" role="dialog" aria-modal="true" aria-label="File diff inspector">
        <div class="diff-modal-header">
          <div class="diff-modal-title-wrap">
            <div class="row" style="margin: 0">
              <strong>File Diff Inspector</strong>
              @if (d(); as x) {
                <span class="chip">{{ catLabel(x.category) }}</span>
                <span class="pill" [style.color]="info(effective()).fg" [style.border-color]="'rgba(' + info(effective()).rgb + ',.5)'" [style.background]="'rgba(' + info(effective()).rgb + ',.14)'">{{ info(effective()).label }} · {{ info(effective()).legacy(code()) }}</span>
                @if (effective() !== x.decision) { <span class="chip s-REVIEW_REQUIRED">changed in review (was {{ info(x.decision).label }})</span> }
                @if (x.refs) { <span class="chip s-REVIEW_REQUIRED">still referenced</span> }
                @if (copied()) { <span class="copied-badge">{{ copied() }}</span> }
              }
            </div>
            <div class="meta"><span><b>Path:</b> <code>{{ path() }}</code></span></div>
            @if (d(); as x) {
              <div class="meta">
                <span><b>SyncBase</b> <code>{{ short(x.base?.sha) }}</code></span>
                <span><b>{{ primaryLabel() }}</b> <code>{{ short(x.primary?.sha) }}</code></span>
                <span><b>{{ forkLabel() }}</b> <code>{{ short(x.fork?.sha) }}</code></span>
                @if (x.merged) { <span><b>merged</b> <code>{{ short(x.merged.sha) }}</code></span> }
              </div>
              <div class="meta">
                @if (x.primaryLastCommit) { <span><b>Last {{ primaryLabel() }} commit:</b> <code>{{ x.primaryLastCommit }}</code></span> }
                @if (x.forkLastCommit) { <span><b>Last {{ forkLabel() }} commit:</b> <code>{{ x.forkLastCommit }}</code></span> }
              </div>
            }
          </div>
          <div class="diff-modal-actions">
            <button type="button" class="small" [disabled]="index() <= 0" (click)="navigate.emit(-1)" title="Previous file (← or K)">← Prev</button>
            <span class="small muted">{{ index() + 1 }} / {{ count() }}</span>
            <button type="button" class="small" [disabled]="index() >= count() - 1" (click)="navigate.emit(1)" title="Next file (→ or J)">Next →</button>
            <button type="button" class="small" (click)="copy()" [disabled]="!activeText()">Copy Diff</button>
            <button type="button" class="small" (click)="close.emit()" title="Close (Esc)">✕ Close</button>
          </div>
        </div>

        @if (d(); as x) {
          <div class="decide" aria-label="Direction">
            <span class="small muted"><b>Direction:</b></span>
            @for (o of decisions; track o.key) {
              <button type="button" [class.on]="o.key === effective()" [disabled]="o.key === 'Merged' && !x.merged" (click)="decide.emit(o.key)"
                [style.color]="o.fg" [style.border-color]="'rgba(' + o.rgb + ',.5)'" [style.background]="o.key === effective() ? 'rgba(' + o.rgb + ',.18)' : 'transparent'"
                [title]="o.key === 'Merged' && !x.merged ? 'No clean merge exists for this file' : o.legacy(code())">{{ o.label }}</button>
            }
            <span class="apply grow" style="margin-left: auto">{{ onApply() }}</span>
          </div>
          <div class="reason">{{ x.reason }}@if (x.refs) { · referenced by {{ x.refs }} }</div>
        }

        <nav class="tabs sub-tabs" style="margin: 8px 0">
          <button type="button" [class.active]="tab() === 'forkVsPrimary'" (click)="tab.set('forkVsPrimary')">{{ forkLabel() }} vs {{ primaryLabel() }}@if (d()?.forkVsPrimary) { <span class="badge">diff</span> }</button>
          <button type="button" [class.active]="tab() === 'baseToFork'" (click)="tab.set('baseToFork')">{{ forkLabel() }} changes (SyncBase → {{ forkLabel() }})@if (d()?.baseToFork) { <span class="badge">changed</span> }</button>
          <button type="button" [class.active]="tab() === 'baseToPrimary'" (click)="tab.set('baseToPrimary')">{{ primaryLabel() }} changes (SyncBase → {{ primaryLabel() }})@if (d()?.baseToPrimary) { <span class="badge">changed</span> }</button>
          @if (d()?.merged) { <button type="button" [class.active]="tab() === 'merged'" (click)="tab.set('merged')">Merged result</button> }
          <button type="button" [class.active]="tab() === 'side'" (click)="tab.set('side')">Side-by-side content</button>
          <span class="keys" style="margin-left: auto; align-self: center"><kbd>←</kbd> <kbd>→</kbd> files · <kbd>1</kbd>–<kbd>5</kbd> tabs · <kbd>Esc</kbd> close</span>
        </nav>

        <div class="diff-modal-body">
          @if (loading() || !d()) {
            <div class="loading"><span class="ring" aria-hidden="true"></span>Loading diff…</div>
          } @else if (d()!.binary) {
            <p class="warn">Binary file: a textual diff is not applicable.</p>
          } @else if (tab() === 'side') {
            <div class="side">
              <div><h4>{{ primaryLabel() }}</h4><pre class="out diff-side-pre">{{ d()!.primaryText ?? '(absent in ' + primaryLabel() + ')' }}</pre></div>
              <div><h4>{{ forkLabel() }}</h4><pre class="out diff-side-pre">{{ d()!.forkText ?? '(absent in ' + forkLabel() + ')' }}</pre></div>
            </div>
          } @else if (tab() === 'merged') {
            <div class="stack">
              <h4>{{ primaryLabel() }} → merged</h4>
              @if (d()!.primaryToMerged) { <pre class="diff diff-viewer-pre">@for (l of lines(d()!.primaryToMerged); track $index) {<span [class]="l.cls">{{ l.text }}
</span>}</pre> } @else { <div class="empty-diff-note muted">{{ primaryLabel() }} already holds the merged content.</div> }
              <h4>{{ forkLabel() }} → merged</h4>
              @if (d()!.forkToMerged) { <pre class="diff diff-viewer-pre">@for (l of lines(d()!.forkToMerged); track $index) {<span [class]="l.cls">{{ l.text }}
</span>}</pre> } @else { <div class="empty-diff-note muted">{{ forkLabel() }} already holds the merged content.</div> }
            </div>
          } @else if (!activeText()) {
            <div class="empty-diff-note"><p class="muted">No differences in this view ({{ tabLabel() }}).</p></div>
          } @else {
            <pre class="diff diff-viewer-pre">@for (l of lines(activeText()); track $index) {<span [class]="l.cls">{{ l.text }}
</span>}</pre>
          }
          @if (d()?.truncated) { <p class="small muted">Large file: side-by-side content is truncated at 2 MB (the diffs are complete).</p> }
        </div>
      </div>
    </div>
  `,
})
export class DiffInspector {
  readonly d = input<RowDiff | null>(null);
  readonly path = input('');
  readonly loading = input(false);
  readonly effective = input<Decision>('Manual');
  readonly index = input(0);
  readonly count = input(0);
  readonly code = input('BE');
  readonly primaryLabel = input('Primary');
  readonly forkLabel = input('Fork');
  readonly onApply = input('');
  readonly close = output<void>();
  readonly navigate = output<number>();
  readonly decide = output<Decision>();

  readonly decisions = DECISIONS;
  readonly info = decisionInfo;
  readonly short = short;
  readonly tab = signal<Tab>('forkVsPrimary');
  readonly copied = signal('');

  constructor() {
    // Open each file on the most informative view: the cross-repo diff, else whichever side changed.
    effect(() => {
      const x = this.d(); if (!x) return;
      this.tab.set(x.forkVsPrimary ? 'forkVsPrimary' : x.baseToFork ? 'baseToFork' : x.baseToPrimary ? 'baseToPrimary' : x.merged ? 'merged' : 'side');
    });
  }

  catLabel(c: string) { return categoryLabel(c, this.code()); }

  readonly activeText = computed(() => {
    const x = this.d(); if (!x) return '';
    switch (this.tab()) {
      case 'forkVsPrimary': return x.forkVsPrimary;
      case 'baseToFork': return x.baseToFork;
      case 'baseToPrimary': return x.baseToPrimary;
      case 'merged': return [x.primaryToMerged, x.forkToMerged].filter(Boolean).join('\n');
      default: return '';
    }
  });

  readonly tabLabel = computed(() => ({ forkVsPrimary: `${this.forkLabel()} vs ${this.primaryLabel()}`, baseToFork: `SyncBase → ${this.forkLabel()}`,
    baseToPrimary: `SyncBase → ${this.primaryLabel()}`, merged: 'merged result', side: 'side-by-side' })[this.tab()]);

  lines(raw: string) {
    return raw.split('\n').map((text) => ({
      text, cls: text.startsWith('+++') || text.startsWith('---') ? 'dfile' : text.startsWith('@@') ? 'dhunk' : text.startsWith('+') ? 'da' : text.startsWith('-') ? 'dd' : '',
    }));
  }

  async copy() {
    try { await navigator.clipboard.writeText(this.activeText()); this.copied.set('Copied'); }
    catch { this.copied.set('Copy failed'); }
    setTimeout(() => this.copied.set(''), 1500);
  }

  @HostListener('document:keydown', ['$event'])
  onKey(e: KeyboardEvent) {
    const t = e.target as HTMLElement | null;
    if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT' || t.isContentEditable)) return;
    if (e.ctrlKey || e.metaKey || e.altKey) return;
    const tabs: Tab[] = ['forkVsPrimary', 'baseToFork', 'baseToPrimary', 'merged', 'side'];
    if (e.key === 'Escape') { e.preventDefault(); this.close.emit(); }
    else if (e.key === 'ArrowRight' || e.key === 'j') { e.preventDefault(); if (this.index() < this.count() - 1) this.navigate.emit(1); }
    else if (e.key === 'ArrowLeft' || e.key === 'k') { e.preventDefault(); if (this.index() > 0) this.navigate.emit(-1); }
    else if (/^[1-5]$/.test(e.key)) { const tb = tabs[+e.key - 1]; if (tb !== 'merged' || this.d()?.merged) this.tab.set(tb); }
  }
}
