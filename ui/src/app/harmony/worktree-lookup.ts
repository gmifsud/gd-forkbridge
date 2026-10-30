import { Component, computed, ElementRef, input, output, signal, viewChild } from '@angular/core';
import { short, Worktree } from './harmony-api';

/** Searchable worktree picker (WAI-ARIA combobox + listbox). Every whitespace-separated term must match the branch,
 *  path or HEAD, so "24326 angular" or "wt-3 fix" narrows hundreds of worktrees quickly. Keyboard: ↑/↓, PgUp/PgDn,
 *  Home/End, Enter to pick, Esc to cancel. */
@Component({
  selector: 'fb-worktree-lookup',
  styles: [`
    :host { display: block; position: relative; }
    .box { display: flex; align-items: center; gap: 8px; min-height: 40px; padding: 0 10px; border: 1px solid var(--ctl); border-radius: 8px; background: var(--sunk); cursor: text; }
    .box:focus-within { border-color: var(--accent); box-shadow: 0 0 0 2px rgba(249,115,22,.25); }
    .box svg { flex: none; color: var(--faint); }
    input { flex: 1; min-width: 0; border: 0; outline: 0; background: transparent; color: var(--ink); font: inherit; padding: 0; min-height: 38px; }
    input::placeholder { color: var(--faint); }
    .count { flex: none; font-size: 11px; color: var(--faint); }
    .clear { flex: none; border: 0; background: transparent; color: var(--muted); cursor: pointer; font-size: 16px; line-height: 1; padding: 4px; }
    .clear:hover { color: var(--ink); }
    ul { position: absolute; z-index: 20; left: 0; right: 0; top: calc(100% + 4px); margin: 0; padding: 4px; list-style: none; max-height: 360px; overflow: auto;
      background: var(--panel); border: 1px solid var(--line); border-radius: 10px; box-shadow: 0 16px 40px rgba(0,0,0,.5); }
    li { display: flex; flex-direction: column; gap: 2px; padding: 7px 10px; border-radius: 7px; cursor: pointer; }
    li.active { background: rgba(249,115,22,.16); box-shadow: inset 3px 0 0 var(--accent); }
    li.sel .br::after { content: ' ✓'; color: var(--accent-ink); }
    .row1 { display: flex; align-items: center; gap: 8px; min-width: 0; }
    .br { font-weight: 600; color: var(--ink); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .row1 .meta { margin-left: auto; flex: none; display: flex; gap: 4px; }
    .p { font: 11px/1.4 var(--mono); color: var(--faint); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; direction: rtl; text-align: left; }
    .t { font-size: 10px; font-weight: 700; border-radius: 4px; padding: 1px 5px; border: 1px solid var(--line); color: var(--ink2); }
    .t.main { border-color: rgba(161,161,170,.5); } .t.dirty { color: var(--warn); border-color: rgba(251,191,36,.45); }
    .t.prot { color: var(--bad); border-color: rgba(248,113,113,.45); } .t.miss { color: var(--bad); border-color: rgba(248,113,113,.45); }
    :host ::ng-deep mark { background: rgba(249,115,22,.30); color: var(--accent-soft); border-radius: 2px; }   /* innerHTML is outside view encapsulation */
    .empty { padding: 10px; font-size: 12px; color: var(--muted); }
  `],
  template: `
    <div class="box" (click)="fieldEl.focus()">
      <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true"><circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/></svg>
      <input #fieldEl [id]="inputId()" type="text" role="combobox" autocomplete="off" spellcheck="false"
        [attr.aria-expanded]="open()" [attr.aria-controls]="inputId() + '-list'" aria-autocomplete="list"
        [attr.aria-activedescendant]="open() && matches().length ? inputId() + '-opt-' + active() : null"
        [placeholder]="selected() ? display(selected()!) : 'Search branch, path or HEAD…'"
        [value]="open() ? query() : (selected() ? display(selected()!) : '')"
        (focus)="openList()" (input)="onInput($any($event.target).value)" (keydown)="onKey($event)" (blur)="close()">
      @if (open()) { <span class="count">{{ matches().length }} / {{ items().length }}</span> }
      @if (open() && query()) { <button type="button" class="clear" aria-label="Clear search" (mousedown)="$event.preventDefault(); onInput('')">×</button> }
    </div>
    @if (open()) {
      <ul [id]="inputId() + '-list'" role="listbox" #list>
        @for (w of matches(); track w.id; let i = $index) {
          <li role="option" [id]="inputId() + '-opt-' + i" [class.active]="i === active()" [class.sel]="w.id === value()" [attr.aria-selected]="w.id === value()"
            (mousedown)="$event.preventDefault(); pick(w)" (mousemove)="active.set(i)">
            <span class="row1">
              <span class="br" [innerHTML]="hl(w.branch ?? '(detached ' + short(w.head, 7) + ')')"></span>
              <span class="meta">
                @if (w.isMain) { <span class="t main">MAIN</span> }
                @if (w.scopeDirty) { <span class="t dirty">DIRTY</span> }
                @if (w.protected) { <span class="t prot">PROTECTED</span> }
                @if (!w.exists) { <span class="t miss">MISSING</span> }
                @if (w.ahead != null) { <span class="t">↑{{ w.ahead }} ↓{{ w.behind }}</span> }
              </span>
            </span>
            <span class="p"><bdi [innerHTML]="hl(w.path)"></bdi></span>
          </li>
        }
        @if (matches().length === 0) { <li class="empty" aria-disabled="true">No worktree matches “{{ query() }}”.</li> }
      </ul>
    }
  `,
})
export class WorktreeLookup {
  readonly items = input.required<Worktree[]>();
  readonly value = input<string | null>(null);
  readonly inputId = input.required<string>();
  readonly valueChange = output<string>();
  readonly short = short;

  readonly field = viewChild.required<ElementRef<HTMLInputElement>>('fieldEl');
  private readonly list = viewChild<ElementRef<HTMLUListElement>>('list');
  readonly open = signal(false);
  readonly query = signal('');
  readonly active = signal(0);

  readonly selected = computed(() => this.items().find((w) => w.id === this.value()) ?? null);
  private readonly terms = computed(() => this.query().trim().toLowerCase().split(/\s+/).filter(Boolean));
  readonly matches = computed(() => {
    const t = this.terms();
    if (!t.length) return this.items();
    return this.items().filter((w) => {
      const hay = `${w.branch ?? 'detached'} ${w.path} ${w.head ?? ''}`.toLowerCase();
      return t.every((x) => hay.includes(x));
    });
  });

  display(w: Worktree) { return `${w.branch ?? '(detached ' + short(w.head, 7) + ')'}  ·  ${w.path.split(/[\\/]/).slice(-2).join('\\')}`; }

  openList() {
    if (!this.open()) this.field().nativeElement.value = '';   // typing must never land inside the old label
    this.query.set('');
    this.open.set(true);
    this.active.set(Math.max(0, this.matches().findIndex((w) => w.id === this.value())));
    this.scrollActive();
  }
  close() {
    this.open.set(false); this.query.set('');
    const w = this.selected(); this.field().nativeElement.value = w ? this.display(w) : '';
  }
  onInput(v: string) { this.query.set(v); this.open.set(true); this.active.set(0); this.scrollActive(); }

  pick(w: Worktree) {
    if (w.id !== this.value()) this.valueChange.emit(w.id);
    this.close();
    this.field().nativeElement.blur();
  }

  onKey(e: KeyboardEvent) {
    const n = this.matches().length;
    const move = (i: number) => { e.preventDefault(); if (!this.open()) this.openList(); this.active.set(Math.max(0, Math.min(n - 1, i))); this.scrollActive(); };
    switch (e.key) {
      case 'ArrowDown': return move(this.open() ? this.active() + 1 : this.active());
      case 'ArrowUp': return move(this.active() - 1);
      case 'PageDown': return move(this.active() + 8);
      case 'PageUp': return move(this.active() - 8);
      case 'Home': if (this.open()) move(0); return;
      case 'End': if (this.open()) move(n - 1); return;
      case 'Enter': { e.preventDefault(); const w = this.matches()[this.active()]; if (this.open() && w) this.pick(w); else this.openList(); return; }
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
