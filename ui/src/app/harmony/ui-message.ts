import { Component, computed, input, output, signal } from '@angular/core';

/** A message about the current state. `ctx` stamps the state it describes (profile|worktrees|plan); hosts show it
 *  only while that state is still current, so a message never outlives what it is about. */
export interface UiMessage {
  kind: 'ok' | 'warn' | 'error';
  title: string;
  hint?: string;
  detail?: string;
  files?: { st: string; path: string }[];
  ctx?: string;
  /** Optional remedy the host can run (e.g. "Plan committed HEAD"); emitted by id. */
  action?: { id: string; label: string };
}

const STATUS: Record<string, string> = { M: 'modified', A: 'added', D: 'deleted', R: 'renamed', C: 'copied', U: 'conflict', '??': 'untracked', T: 'type changed' };

/** Turns an API/job error into a readable message. Known refusals get a plain title, the affected files as a list
 *  and the next step; anything else keeps its first sentence as the title and the rest as collapsible detail. */
export function parseError(raw: string, labels?: { primary?: string; fork?: string }): UiMessage {
  const text = raw.replace(/^(failed|cancelled):\s*/i, '').replace(/\s*\(exit \d+\)\s*$/, '').trim();

  const dirty = /^(.+?) worktree (.+?) has uncommitted changes in the aligned scope:\s*([\s\S]*)$/.exec(text);
  if (dirty) {
    const [, side, path, list] = dirty;
    return {
      kind: 'error',
      title: `Plan refused: the ${side} worktree has uncommitted changes in the shared scope`,
      hint: `A plan holds committed content only, so planning stops rather than silently leave these changes out. Commit or stash them in ${path} and press Plan again, or plan the worktree's committed HEAD and leave them out.`,
      files: porcelain(list),
      action: { id: 'plan-committed', label: 'Plan committed HEAD (leave these changes out)' },
    };
  }
  const diverged = /^(.+?) has (\d+) local commit\(s\) not on (.+?) \(and is (\d+) behind\)/.exec(text);
  if (diverged) return { kind: 'error', title: `Update refused: ${diverged[1]} has ${diverged[2]} local commit(s) not on ${diverged[3]}`, hint: `A fast-forward can't keep them. Merge or rebase ${diverged[1]} yourself (it is ${diverged[4]} commit(s) behind), then update. Nothing was changed.` };
  const ffDirty = /^(.+?) has uncommitted changes to tracked files/.exec(text);
  if (ffDirty) return { kind: 'error', title: 'Update refused: the worktree has uncommitted changes', hint: `Commit or stash the changes in ${ffDirty[1]}, then update again. Nothing was changed.` };

  const cut = text.search(/[.:]\s|\n/);
  const title = cut > 0 && cut < 200 ? text.slice(0, cut + 1).replace(/[:]$/, '') : text.length > 200 ? text.slice(0, 200) + '…' : text;
  const detail = text.length > title.length ? text : undefined;
  void labels;
  return { kind: 'error', title, detail };
}

/** git status --porcelain lines ("XY path"), whether or not the newlines survived transport. */
function porcelain(list: string): { st: string; path: string }[] {
  const out: { st: string; path: string }[] = [];
  const re = /(?:^|\s)(\?\?|[MADRCUT][MADRCUT ]?)\s+("(?:[^"\\]|\\.)*"|\S+)/g;
  for (let m = re.exec(list); m; m = re.exec(list)) {
    const code = m[1].trim(); const st = STATUS[code] ?? STATUS[code[0]] ?? code;
    out.push({ st, path: m[2].replace(/^"|"$/g, '') });
  }
  return out;
}

@Component({
  selector: 'fb-message',
  styles: [`
    :host { display: block; }
    .m { border-radius: 10px; padding: 10px 14px; border: 1px solid; display: flex; flex-direction: column; gap: 6px; font-size: 13px; }
    .m.ok { background: rgba(94,234,212,.08); border-color: rgba(94,234,212,.35); color: #99F6E4; }
    .m.warn { background: rgba(251,191,36,.10); border-color: rgba(251,191,36,.40); color: #FDE68A; }
    .m.error { background: rgba(248,113,113,.10); border-color: rgba(248,113,113,.45); color: #FECACA; }
    .head { display: flex; align-items: flex-start; gap: 10px; }
    .title { font-weight: 600; color: var(--white); flex: 1; }
    .m.ok .title { font-weight: 500; color: inherit; }
    .x { border: 0; background: transparent; color: inherit; opacity: .7; cursor: pointer; font-size: 16px; line-height: 1; padding: 0 2px; }
    .x:hover { opacity: 1; }
    .hint { color: var(--ink3); }
    ul { margin: 0; padding: 6px 10px; list-style: none; background: rgba(0,0,0,.2); border-radius: 8px; max-height: 240px; overflow: auto; font: 12px/1.6 var(--mono); }
    li { display: flex; gap: 10px; color: var(--ink2); }
    .st { flex: none; width: 76px; color: var(--faint); }
    .p { word-break: break-all; }
    .act { align-self: flex-start; min-height: 30px; padding: 0 12px; border-radius: 8px; border: 1px solid var(--accent); background: transparent; color: var(--accent-ink); font: inherit; font-size: 12px; font-weight: 600; cursor: pointer; }
    .act:hover { background: rgba(249,115,22,.12); }
    .more { align-self: flex-start; border: 0; background: transparent; color: var(--accent-ink); cursor: pointer; font: inherit; font-size: 12px; padding: 0; }
    pre { margin: 0; white-space: pre-wrap; word-break: break-word; font: 12px/1.5 var(--mono); color: var(--ink2); background: rgba(0,0,0,.2); border-radius: 8px; padding: 8px 10px; max-height: 240px; overflow: auto; }
  `],
  template: `
    @if (msg(); as m) {
      <div class="m" [class]="m.kind" [attr.role]="m.kind === 'error' ? 'alert' : 'status'">
        <div class="head"><span class="title">{{ m.title }}</span>
          @if (dismissible()) { <button type="button" class="x" aria-label="Dismiss" (click)="dismiss.emit()">×</button> }</div>
        @if (m.hint) { <div class="hint">{{ m.hint }}</div> }
        @if (m.files?.length) {
          <ul aria-label="Files">@for (f of shownFiles(); track f.path) { <li><span class="st">{{ f.st }}</span><span class="p">{{ f.path }}</span></li> }</ul>
          @if (m.files!.length > 8) { <button type="button" class="more" (click)="all.set(!all())">{{ all() ? 'Show fewer' : 'Show all ' + m.files!.length + ' files' }}</button> }
        }
        @if (m.action) { <button type="button" class="act" (click)="act.emit(m.action!.id)">{{ m.action!.label }}</button> }
        @if (m.detail) {
          <button type="button" class="more" (click)="open.set(!open())">{{ open() ? 'Hide details' : 'Show details' }}</button>
          @if (open()) { <pre>{{ m.detail }}</pre> }
        }
      </div>
    }
  `,
})
export class MessageView {
  readonly msg = input<UiMessage | null>(null);
  readonly dismissible = input(true);
  readonly dismiss = output<void>();
  readonly act = output<string>();
  readonly all = signal(false);
  readonly open = signal(false);
  readonly shownFiles = computed(() => { const f = this.msg()?.files ?? []; return this.all() ? f : f.slice(0, 8); });
}
