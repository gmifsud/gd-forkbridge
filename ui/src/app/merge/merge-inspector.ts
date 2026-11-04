import { Component, computed, effect, HostListener, input, output, signal } from '@angular/core';
import { UnifiedDiff } from '../shared/diff/unified-diff';
import { shortcutAllowed } from '../shared/keyboard';
import { conflictLabel, isDirty, short, statusLabel } from './merge-logic';
import type { FileView, ResolutionAction } from './merge-models';

type Tab = 'result' | 'target' | 'source' | 'base' | 'final';
export interface ResolveEvent { action: ResolutionAction; content?: string; acknowledgeMarkers?: boolean; }

/** Merge inspector: base / target / source / editable result / final target→result diff for one entry, with the
 *  resolution controls the entry actually supports. Whole-file editing (plain textarea; no editor dependency).
 *  Keys outside the editor: ← / → (or K / J) previous / next file, 1–5 tabs, Esc close. Inside the editor only
 *  Ctrl/⌘+S (save draft) is handled; every other key keeps its editing meaning. */
@Component({
  selector: 'fb-merge-inspector',
  imports: [UnifiedDiff],
  styles: [`
    .panel-x { width: 96vw; max-width: 1500px; height: 92vh; }
    .meta { display: flex; flex-wrap: wrap; gap: 4px 18px; margin-top: 4px; font-size: 12px; color: var(--muted); }
    .actions { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; padding: 8px 10px; margin-top: 8px; background: var(--bg); border: 1px solid var(--line); border-radius: 8px; }
    .why { font-size: 12px; color: var(--ink3); margin-top: 6px; }
    textarea.ed { width: 100%; height: 56vh; font: 12px/1.45 var(--mono); color: var(--ink); background: var(--sunk); border: 1px solid var(--ctl); border-radius: 6px; padding: 8px 10px; resize: vertical; tab-size: 4; }
    textarea.ed:focus { outline: 2px solid rgba(249,115,22,.5); }
    pre.ro { max-height: 56vh; }
    .err { margin-top: 8px; padding: 8px 10px; border-radius: 6px; background: var(--badbg); color: var(--bad); font-size: 12.5px; display: flex; gap: 10px; align-items: center; }
    .note { margin-top: 8px; padding: 6px 10px; border-radius: 6px; background: var(--warnbg); color: var(--warn); font-size: 12.5px; }
    .lbl-src { color: var(--accent-soft); } .lbl-tgt { color: var(--ok); }
    kbd { font: 11px var(--mono); border: 1px solid var(--line); border-bottom-width: 2px; border-radius: 4px; padding: 0 4px; color: var(--ink3); }
    .keys { font-size: 11px; color: var(--faint); }
  `],
  template: `
    <div class="diff-modal-backdrop" (click)="tryClose()">
      <div class="diff-modal-panel panel-x" (click)="$event.stopPropagation()" role="dialog" aria-modal="true" aria-label="Merge inspector">
        <div class="diff-modal-header">
          <div class="diff-modal-title-wrap">
            <div class="row" style="margin: 0">
              <strong>Merge inspector</strong>
              @if (v(); as x) {
                <span class="chip">{{ x.entry.status === 'Conflicted' ? conflict(x.entry.conflict, x.entry.binary) : status(x) }}</span>
                <span class="chip" [class.s-READY]="x.state === 'resolved' || x.state === 'clean'" [class.s-REVIEW_REQUIRED]="x.state === 'unresolved' || x.state === 'draft'">{{ stateText(x.state) }}</span>
                @if (dirty()) { <span class="chip s-REVIEW_REQUIRED">unsaved edits</span> }
              }
            </div>
            @if (v(); as x) {
              <div class="meta"><span><b>Path:</b> <code>{{ x.entry.path }}</code></span>@if (x.entry.oldPath) { <span>was <code>{{ x.entry.oldPath }}</code></span> }</div>
              <div class="meta">
                <span class="lbl-src"><b>Source</b> {{ x.sourceLabel }} <code>{{ sh(x.entry.source?.sha) }}</code> {{ x.entry.source?.mode }}</span>
                <span class="lbl-tgt"><b>Target</b> {{ x.targetLabel }} <code>{{ sh(x.entry.target?.sha) }}</code> {{ x.entry.target?.mode }}</span>
                <span><b>Base</b> <code>{{ sh(x.entry.base?.sha) }}</code></span>
                @if (x.result.codec; as c) { <span><b>Encoding</b> {{ c.encoding ?? (c.binary ? 'binary' : '—') }} · {{ c.eol }}{{ c.finalNewline ? '' : ' · no final newline' }}</span> }
              </div>
            }
          </div>
          <div class="diff-modal-actions">
            <button type="button" class="small" [disabled]="index() <= 0" (click)="go(-1)" title="Previous file (← or K)">← Prev</button>
            <span class="small muted">{{ index() + 1 }} / {{ count() }}</span>
            <button type="button" class="small" [disabled]="index() >= count() - 1" (click)="go(1)" title="Next file (→ or J)">Next →</button>
            <button type="button" class="small" (click)="tryClose()" title="Close (Esc)">✕ Close</button>
          </div>
        </div>

        @if (v(); as x) {
          @if (x.entry.status === 'Conflicted' && x.entry.capabilities.supported && sessionOpen()) {
            <div class="actions" aria-label="Resolution">
              @if (x.entry.capabilities.takeTarget) { <button type="button" class="small" (click)="act('TakeTarget')" data-act="TakeTarget">Use target file ({{ x.targetLabel }})</button> }
              @if (x.entry.capabilities.takeSource) { <button type="button" class="small" (click)="act('TakeSource')" data-act="TakeSource">Use source file ({{ x.sourceLabel }})</button> }
              @if (x.entry.capabilities.keepModified) { <button type="button" class="small" (click)="act('KeepModified')" data-act="KeepModified">Keep modified file</button> }
              @if (x.entry.capabilities.delete) { <button type="button" class="small danger" (click)="act('Delete')" data-act="Delete">Delete file</button> }
              @if (x.editable) {
                <span style="width: 12px"></span>
                <button type="button" class="small" [disabled]="!dirty() || pending()" (click)="saveDraft.emit(text())" data-act="SaveDraft" title="Ctrl/⌘+S in the editor">Save draft</button>
                <button type="button" class="small primary" [disabled]="pending()" (click)="markResolved()" data-act="MarkResolved">Mark resolved</button>
              }
              <span class="small muted" style="margin-left: auto">Choosing a version or marking resolved stages exactly this file in the isolated workspace.</span>
            </div>
          }
          <div class="why">{{ x.entry.capabilities.reason }}@if (!x.editable && x.editBlockedReason && x.entry.status === 'Conflicted') { · {{ x.editBlockedReason }} }</div>
          @if (x.markerLines > 0 && x.editable) { <div class="note">The result contains {{ x.markerLines }} conflict-marker line(s). Marker detection is a check, not the definition of resolved: remove them or confirm they are intended.</div> }
          @if (x.filterAttribute) { <div class="note">Filter attribute <code>{{ x.filterAttribute }}</code>: the stored content is shown, not the materialized file.</div> }
          @if (ackNeeded()) {
            <div class="note">Conflict markers are still present. <button type="button" class="small" (click)="markResolved(true)">Mark resolved anyway</button></div>
          }
          @if (escArmed()) { <div class="note" role="status">Unsaved edits. Save a draft, or repeat the action (Esc / Close / Prev / Next) to discard them.</div> }
          @if (error(); as e) {
            <div class="err" role="alert"><span style="flex: 1">{{ e }}</span>@if (stale()) { <button type="button" class="small" (click)="reload.emit()">Reload file</button> }</div>
          }
        }

        <nav class="tabs sub-tabs" style="margin: 8px 0">
          <button type="button" [class.active]="tab() === 'result'" (click)="tab.set('result')">Result{{ v()?.editable ? ' (editable)' : '' }}</button>
          <button type="button" [class.active]="tab() === 'target'" (click)="tab.set('target')">Target changes (base → {{ v()?.targetLabel }})</button>
          <button type="button" [class.active]="tab() === 'source'" (click)="tab.set('source')">Source changes (base → {{ v()?.sourceLabel }})</button>
          <button type="button" [class.active]="tab() === 'base'" (click)="tab.set('base')">Base</button>
          <button type="button" [class.active]="tab() === 'final'" (click)="tab.set('final')">Final diff (target → result)</button>
          <span class="keys" style="margin-left: auto; align-self: center"><kbd>←</kbd> <kbd>→</kbd> files · <kbd>1</kbd>–<kbd>5</kbd> tabs · <kbd>Esc</kbd> close · <kbd>Ctrl</kbd>+<kbd>S</kbd> save draft</span>
        </nav>

        <div class="diff-modal-body">
          @if (loading() || !v()) {
            <div class="loading muted" style="padding: 24px">Loading…</div>
          } @else if (tab() === 'result') {
            @if (v()!.editable) {
              <textarea class="ed" spellcheck="false" aria-label="Merge result editor" [value]="text()" (input)="text.set($any($event.target).value)"></textarea>
            } @else if (v()!.resultDeleted) {
              <div class="empty-diff-note muted">The result deletes this file.</div>
            } @else if (v()!.result.text !== undefined && v()!.result.text !== null) {
              <pre class="out ro">{{ v()!.result.text }}</pre>
            } @else {
              <div class="empty-diff-note muted">{{ v()!.result.codec?.reason ?? 'Content not shown.' }}</div>
            }
          } @else if (tab() === 'target') {
            <fb-unified-diff [diff]="v()!.targetChanges.diff" [truncated]="v()!.targetChanges.truncated" [empty]="v()!.targetChanges.binary ? 'Binary change.' : 'The target did not change this file.'" />
          } @else if (tab() === 'source') {
            <fb-unified-diff [diff]="v()!.sourceChanges.diff" [truncated]="v()!.sourceChanges.truncated" [empty]="v()!.sourceChanges.binary ? 'Binary change.' : 'The source did not change this file.'" />
          } @else if (tab() === 'base') {
            @if (v()!.base.text !== undefined && v()!.base.text !== null) { <pre class="out ro">{{ v()!.base.text }}</pre> }
            @else { <div class="empty-diff-note muted">{{ v()!.entry.base ? (v()!.base.codec?.reason ?? 'Content not shown.') : 'No base version (added on both sides or no common version).' }}</div> }
          } @else {
            <fb-unified-diff [diff]="v()!.targetToResult.diff" [truncated]="v()!.targetToResult.truncated" [empty]="v()!.targetToResult.binary ? 'Binary change.' : 'The result equals the target version.'" />
          }
        </div>
      </div>
    </div>
  `,
})
export class MergeInspector {
  readonly v = input<FileView | null>(null);
  readonly loading = input(false);
  readonly index = input(0);
  readonly count = input(0);
  readonly sessionOpen = input(true);
  readonly pending = input(false);
  readonly error = input<string | null>(null);
  readonly stale = input(false);
  readonly close = output<void>();
  readonly navigate = output<number>();
  readonly saveDraft = output<string>();
  readonly resolve = output<ResolveEvent>();
  readonly reload = output<void>();

  readonly tab = signal<Tab>('result');
  readonly text = signal('');
  private readonly loadedText = signal<string | undefined>(undefined);
  private loadedKey = '';
  readonly ackNeeded = signal(false);
  readonly escArmed = signal(false);
  readonly dirty = computed(() => !!this.v()?.editable && isDirty(this.loadedText(), this.text()));
  sh = short;
  conflict = conflictLabel;

  constructor() {
    // Reset the editor only when a different entry or different server content arrives (never on unrelated refreshes).
    effect(() => {
      const x = this.v(); if (!x) return;
      const key = x.entry.id + '|' + x.resultContentHash;
      if (key === this.loadedKey) return;
      this.loadedKey = key;
      this.loadedText.set(x.result.text ?? '');
      this.text.set(x.result.text ?? '');
      this.ackNeeded.set(false);
      this.escArmed.set(false);
      if (x.entry.status !== 'Conflicted') this.tab.set('final');
      else if (this.tab() === 'final' || this.tab() === 'base') this.tab.set('result');
    });
  }

  status(x: FileView) { return statusLabel({ status: x.entry.status, conflict: x.entry.conflict, entry: x.entry }); }
  stateText(s: string) { return { unresolved: 'Unresolved', draft: 'Draft saved', resolved: 'Resolved', clean: 'Merged by Git' }[s] ?? s; }

  act(action: ResolutionAction) { this.resolve.emit({ action }); }
  markResolved(ack = false) {
    const x = this.v(); if (!x) return;
    if (!ack && /^(<{7} |={7}$|>{7} |\|{7} )/m.test(this.text())) { this.ackNeeded.set(true); return; }
    this.ackNeeded.set(false);
    this.resolve.emit({ action: 'EditResult', content: this.text(), acknowledgeMarkers: ack });
  }

  go(d: number) {
    if (this.dirty() && !this.escArmed()) { this.escArmed.set(true); return; }
    this.escArmed.set(false); this.navigate.emit(d);
  }
  /** Unsaved edits are never discarded silently: the first Esc / close arms, the second closes. */
  tryClose() {
    if (this.dirty() && !this.escArmed()) { this.escArmed.set(true); this.ackNeeded.set(false); return; }
    this.close.emit();
  }

  @HostListener('document:keydown', ['$event'])
  onKey(e: KeyboardEvent) {
    if ((e.ctrlKey || e.metaKey) && !e.altKey && e.key.toLowerCase() === 's' && this.v()?.editable) {
      e.preventDefault();
      if (this.dirty() && !this.pending()) this.saveDraft.emit(this.text());
      return;
    }
    if (e.key === 'Escape') { e.preventDefault(); this.tryClose(); return; }
    if (!shortcutAllowed(e)) return; // typing in the editor: no file/tab shortcuts
    const tabs: Tab[] = ['result', 'target', 'source', 'base', 'final'];
    if (e.key === 'ArrowRight' || e.key === 'j') { e.preventDefault(); if (this.index() < this.count() - 1) this.go(1); }
    else if (e.key === 'ArrowLeft' || e.key === 'k') { e.preventDefault(); if (this.index() > 0) this.go(-1); }
    else if (/^[1-5]$/.test(e.key)) this.tab.set(tabs[+e.key - 1]);
  }
}
