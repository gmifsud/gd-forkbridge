import { Component, computed, ElementRef, input, output, signal, viewChild } from '@angular/core';
import { namespaceLabel, short } from './merge-logic';
import type { RefInfo, RefNamespace, RefSelection } from './merge-models';

const ORDER: RefNamespace[] = ['Remote', 'Local', 'CheckoutRemote'];
const BADGE: Record<RefNamespace, string> = { Remote: 'REMOTE', Local: 'LOCAL', CheckoutRemote: 'CHECKOUT TRACKING' };
/** Per group, so an unfiltered list of hundreds of branches stays fast; typing narrows it. */
const CAP = 80;

/** Searchable branch picker for the Merge tab (WAI-ARIA combobox + listbox), modelled on Harmony's worktree lookup.
 *  Every whitespace-separated term must match the branch name, SHA or subject. Results are grouped by provenance
 *  (remote / local / checkout's remote-tracking). Keyboard: ↑/↓, PgUp/PgDn, Home/End, Enter to pick, Esc to cancel.
 *  Nothing is selected by scrolling or hovering - only by click or Enter. */
@Component({
  selector: 'fb-ref-lookup',
  styles: [`
    :host { display: block; position: relative; }
    .box { display: flex; align-items: center; gap: 8px; min-height: 40px; padding: 0 10px; border: 1px solid var(--ctl); border-radius: 8px; background: var(--sunk); cursor: text; }
    .box:focus-within { border-color: var(--accent); box-shadow: 0 0 0 2px rgba(249,115,22,.25); }
    .box.off { opacity: .55; cursor: not-allowed; }
    .box svg { flex: none; color: var(--faint); }
    input { flex: 1; min-width: 0; border: 0; outline: 0; background: transparent; color: var(--ink); font: inherit; padding: 0; min-height: 38px; }
    input::placeholder { color: var(--faint); }
    .count { flex: none; font-size: 11px; color: var(--faint); }
    .clear { flex: none; border: 0; background: transparent; color: var(--muted); cursor: pointer; font-size: 16px; line-height: 1; padding: 4px; }
    .clear:hover { color: var(--ink); }
    ul { position: absolute; z-index: 20; left: 0; right: 0; top: calc(100% + 4px); margin: 0; padding: 4px; list-style: none; max-height: 380px; overflow: auto;
      background: var(--panel); border: 1px solid var(--line); border-radius: 10px; box-shadow: 0 16px 40px rgba(0,0,0,.5); }
    li { display: flex; flex-direction: column; gap: 2px; padding: 7px 10px; border-radius: 7px; cursor: pointer; }
    li.active { background: rgba(249,115,22,.16); box-shadow: inset 3px 0 0 var(--accent); }
    li.sel .br::after { content: ' ✓'; color: var(--accent-ink); }
    .row1 { display: flex; align-items: center; gap: 8px; min-width: 0; }
    .br { font-weight: 600; color: var(--ink); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .row1 .meta { margin-left: auto; flex: none; display: flex; gap: 4px; }
    .p { font: 11px/1.4 var(--mono); color: var(--faint); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .t { font-size: 10px; font-weight: 700; border-radius: 4px; padding: 1px 5px; border: 1px solid var(--line); color: var(--ink2); }
    .t.Remote { color: var(--accent-ink); border-color: rgba(249,115,22,.45); }
    .t.Local { color: var(--ok); border-color: rgba(94,234,212,.45); }
    .t.CheckoutRemote { color: var(--ink3); }
    :host ::ng-deep mark { background: rgba(249,115,22,.30); color: var(--accent-soft); border-radius: 2px; }   /* innerHTML is outside view encapsulation */
    .empty { padding: 10px; font-size: 12px; color: var(--muted); }
    li.hdr { cursor: default; flex-direction: row; justify-content: space-between; padding: 8px 10px 4px; font-size: 11px; font-weight: 700; letter-spacing: .5px; color: var(--faint); border-top: 1px solid var(--line2); margin-top: 4px; border-radius: 0; }
    li.hdr:first-child { border-top: 0; margin-top: 0; }
  `],
  template: `
    <div class="box" [class.off]="disabled()" (click)="!disabled() && fieldEl.focus()">
      <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true"><circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/></svg>
      <input #fieldEl [id]="inputId()" type="text" role="combobox" autocomplete="off" spellcheck="false" [disabled]="disabled()"
        [attr.aria-label]="label()" [attr.aria-expanded]="open()" [attr.aria-controls]="inputId() + '-list'" aria-autocomplete="list"
        [attr.aria-activedescendant]="open() && flat().length ? inputId() + '-opt-' + active() : null"
        [placeholder]="selectedRef() ? selectedRef()!.name : (disabled() ? 'Refresh refs first' : 'Search branches (name, SHA or subject)…')"
        [value]="open() ? query() : (selectedRef()?.name ?? '')"
        (focus)="openList()" (input)="onInput($any($event.target).value)" (keydown)="onKey($event)" (blur)="close()">
      @if (open()) { <span class="count">{{ matchCount() }} / {{ refs().length }}</span> }
      @if (open() && query()) { <button type="button" class="clear" aria-label="Clear search" (mousedown)="$event.preventDefault(); onInput('')">×</button> }
    </div>
    @if (open()) {
      <ul [id]="inputId() + '-list'" role="listbox" #list>
        @for (g of groups(); track g.ns) {
          <li class="hdr" role="presentation"><span>{{ nsLabel(g.ns).toUpperCase() }}</span><span>{{ g.shown.length }}@if (g.total > g.shown.length) { of {{ g.total }} — type to narrow }</span></li>
          @for (r of g.shown; track r.name; let j = $index) {
            <li role="option" [id]="inputId() + '-opt-' + (g.offset + j)" [class.active]="g.offset + j === active()" [class.sel]="isSel(r)" [attr.aria-selected]="isSel(r)"
              [attr.data-ref]="r.namespace + ':' + r.name" (mousedown)="$event.preventDefault(); choose(g.offset + j)" (mousemove)="active.set(g.offset + j)">
              <span class="row1">
                <span class="br" [innerHTML]="hl(r.name)"></span>
                <span class="meta"><span class="t" [class]="'t ' + r.namespace">{{ badge[r.namespace] }}</span></span>
              </span>
              <span class="p"><span [innerHTML]="hl(short(r.sha))"></span>@if (r.date) { · {{ r.date.slice(0, 10) }} }@if (r.subject) { · <span [innerHTML]="hl(r.subject)"></span> }</span>
            </li>
          }
        }
        @if (matchCount() === 0) { <li class="empty" aria-disabled="true">No branch matches “{{ query() }}”.</li> }
      </ul>
    }
  `,
})
export class RefLookup {
  readonly refs = input.required<RefInfo[]>();
  readonly value = input<RefSelection | null>(null);
  readonly inputId = input.required<string>();
  readonly label = input('Branch');
  readonly disabled = input(false);
  readonly valueChange = output<RefSelection>();

  readonly short = short;
  readonly nsLabel = namespaceLabel;
  readonly badge = BADGE;
  readonly field = viewChild.required<ElementRef<HTMLInputElement>>('fieldEl');
  private readonly list = viewChild<ElementRef<HTMLUListElement>>('list');
  readonly open = signal(false);
  readonly query = signal('');
  readonly active = signal(0);

  readonly selectedRef = computed(() => { const v = this.value(); return v ? this.refs().find((r) => r.namespace === v.namespace && r.name === v.name) ?? null : null; });
  private readonly terms = computed(() => this.query().trim().toLowerCase().split(/\s+/).filter(Boolean));
  private readonly matches = computed(() => {
    const t = this.terms();
    const all = t.length ? this.refs().filter((r) => t.every((x) => `${r.name} ${r.sha} ${r.subject ?? ''}`.toLowerCase().includes(x))) : this.refs();
    return [...all].sort((a, b) => a.name.localeCompare(b.name));
  });
  readonly matchCount = computed(() => this.matches().length);
  readonly groups = computed(() => {
    let offset = 0;
    return ORDER.map((ns) => {
      const all = this.matches().filter((r) => r.namespace === ns);
      const g = { ns, total: all.length, shown: all.slice(0, CAP), offset };
      offset += g.shown.length;
      return g;
    }).filter((g) => g.total > 0);
  });
  readonly flat = computed(() => this.groups().flatMap((g) => g.shown));

  isSel(r: RefInfo) { const v = this.value(); return !!v && v.namespace === r.namespace && v.name === r.name; }

  openList() {
    if (this.disabled()) return;
    this.picked = null;
    if (!this.open()) this.field().nativeElement.value = '';   // typing must never land inside the old label
    this.query.set('');
    this.open.set(true);
    this.active.set(Math.max(0, this.flat().findIndex((r) => this.isSel(r))));
    this.scrollActive();
  }
  /** Name of a ref picked but not yet echoed back through `value` (the parent's update arrives on the next change
   *  detection; the blur that follows a pick must not blank the field in between). */
  private picked: string | null = null;

  close() {
    this.open.set(false); this.query.set('');
    this.field().nativeElement.value = this.picked ?? this.selectedRef()?.name ?? '';
  }
  onInput(v: string) { this.query.set(v); this.open.set(true); this.active.set(0); this.scrollActive(); }

  choose(i: number) {
    const r = this.flat()[i]; if (!r) return;
    this.picked = r.name;
    if (!this.isSel(r)) this.valueChange.emit({ namespace: r.namespace, name: r.name });
    this.close();
    this.field().nativeElement.blur();
  }

  onKey(e: KeyboardEvent) {
    const n = this.flat().length;
    const move = (i: number) => { e.preventDefault(); if (!this.open()) this.openList(); this.active.set(Math.max(0, Math.min(n - 1, i))); this.scrollActive(); };
    switch (e.key) {
      case 'ArrowDown': return move(this.open() ? this.active() + 1 : this.active());
      case 'ArrowUp': return move(this.active() - 1);
      case 'PageDown': return move(this.active() + 8);
      case 'PageUp': return move(this.active() - 8);
      case 'Home': if (this.open()) move(0); return;
      case 'End': if (this.open()) move(n - 1); return;
      case 'Enter': { e.preventDefault(); if (this.open() && n) this.choose(this.active()); else this.openList(); return; }
      case 'Escape': if (this.open()) { e.preventDefault(); this.close(); } return;
      case 'Tab': this.close(); return;
    }
  }

  private scrollActive() {
    queueMicrotask(() => this.list()?.nativeElement.querySelector('li.active')?.scrollIntoView({ block: 'nearest' }));
  }

  /** Escapes the text, then marks each matched term (case-insensitive). */
  hl(text: string): string {
    const esc = (s: string) => s.replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]!);
    const t = this.terms();
    if (!t.length) return esc(text);
    const lower = text.toLowerCase(); const marks = new Array<boolean>(text.length).fill(false);
    for (const x of t) for (let i = lower.indexOf(x); i >= 0; i = lower.indexOf(x, i + 1)) for (let k = i; k < i + x.length; k++) marks[k] = true;
    let out = ''; let on = false;
    for (let i = 0; i < text.length; i++) {
      if (marks[i] !== on) { out += marks[i] ? '<mark>' : '</mark>'; on = marks[i]; }
      out += esc(text[i]);
    }
    return on ? out + '</mark>' : out;
  }
}
