import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  AlignPlan, AlignRow, Blob, categoryLabel, Decision, DECISIONS, decisionInfo, DiffLine, HarmonyApi, lineDiff, PlanSummary,
  ProfileInfo, short, Worktree,
} from './harmony-api';
import { HARMONY_THEME } from './harmony-theme';

type DiffTab = 'base-primary' | 'base-fork' | 'primary-fork' | 'merged';
interface ViewRow { row: AlignRow; eff: Decision; edited: boolean; leaf: string; dir: string; cat: string; }

@Component({
  selector: 'fb-harmony',
  imports: [RouterLink],
  styles: [HARMONY_THEME + `
    .sides { display: grid; grid-template-columns: repeat(auto-fit, minmax(300px, 1fr)); gap: 16px; }
    .side { display: flex; flex-direction: column; gap: 10px; border-top-width: 3px; }
    .side.q { border-top-color: #38BDF8; } .side.s { border-top-color: var(--h-accent); } .side.i { border-top-color: #FBBF24; }
    .side-head { display: flex; align-items: center; gap: 8px; }
    .path { font-size: 12px; color: var(--h-ink3); word-break: break-all; }
    .flags { display: flex; flex-wrap: wrap; gap: 6px; }
    .steps { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }
    .step { display: inline-flex; align-items: center; gap: 8px; min-height: 36px; padding: 0 12px 0 6px; border-radius: 999px; font-size: 13px; border: 1px solid var(--h-ctl); color: var(--h-ink); background: var(--h-panel); }
    .step .dot { display: inline-flex; align-items: center; justify-content: center; width: 24px; height: 24px; border-radius: 50%; font-size: 12px; font-weight: 700; background: rgba(45,212,191,.18); color: var(--h-ok); }
    .step.current { background: var(--h-accent); color: var(--h-on-accent); border-color: var(--h-accent); font-weight: 700; }
    .step.current .dot { background: var(--h-on-accent); color: var(--h-accent-ink); }
    .step.locked { background: transparent; color: var(--h-faint); border-style: dashed; }
    .step.locked .dot { background: #2E2E34; color: var(--h-faint); }
    .meter { display: flex; gap: 2px; height: 18px; border-radius: 6px; overflow: hidden; background: var(--h-sunk); }
    .meter span { min-width: 4px; }
    .legend { display: flex; flex-wrap: wrap; gap: 18px; font-size: 12px; color: var(--h-ink3); }
    .sw { display: inline-block; width: 12px; height: 12px; border-radius: 3px; margin-right: 6px; vertical-align: -1px; }
    .stripes { background-image: repeating-linear-gradient(135deg, rgba(0,0,0,.35) 0 4px, transparent 4px 8px); }
    .cols { display: flex; flex-wrap: wrap; gap: 16px; align-items: flex-start; }
    .queue { flex: 1 1 230px; max-width: 290px; min-width: 220px; padding: 12px; display: flex; flex-direction: column; gap: 14px; }
    .facet { display: flex; justify-content: space-between; align-items: center; width: 100%; min-height: 36px; padding: 6px 10px; border: 0; border-radius: 8px; background: transparent; font: inherit; font-size: 13px; color: var(--h-ink); cursor: pointer; text-align: left; }
    .facet:hover { background: #2E2E34; }
    .facet.on { background: rgba(249,115,22,.16); box-shadow: inset 3px 0 0 var(--h-accent); color: var(--h-accent-soft); font-weight: 600; }
    .table { flex: 999 1 560px; min-width: 0; overflow: hidden; }
    .thead, .tr { display: grid; grid-template-columns: minmax(0, 1fr) 150px 190px; gap: 12px; align-items: center; padding: 8px 14px; border-bottom: 1px solid var(--h-line2); }
    .thead { background: var(--h-head); font-size: 11px; font-weight: 700; letter-spacing: .5px; color: var(--h-faint); }
    .tr { cursor: pointer; } .tr:hover { background: #2C2C32; }
    .tr.sel { background: rgba(249,115,22,.12); box-shadow: inset 3px 0 0 var(--h-accent); }
    .leaf { font-weight: 500; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .dir { font-size: 11px; color: var(--h-faint); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .cat { font-size: 11px; color: var(--h-ink3); }
    .dec { min-height: 32px; font-size: 12px; font-weight: 600; padding: 0 6px; }
    .edited { margin-left: 8px; font-size: 11px; color: var(--h-on-accent); background: var(--h-accent-ink); border-radius: 4px; padding: 1px 6px; font-weight: 600; }
    .rows { max-height: 860px; overflow: auto; }
    .detail { flex: 1 1 360px; max-width: 460px; min-width: 300px; display: flex; flex-direction: column; gap: 12px; }
    .blobs { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); border: 1px solid var(--h-line); border-radius: 10px; overflow: hidden; font-size: 12px; }
    .blobs div { padding: 8px 10px; border-bottom: 1px solid var(--h-line2); background: var(--h-head); }
    .apply { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; font-size: 12px; }
    .apply > div { border-radius: 8px; padding: 8px 10px; border: 1px solid; }
    .tabs { display: flex; flex-wrap: wrap; gap: 4px; }
    .tabs button { min-height: 32px; padding: 0 10px; border-radius: 6px; border: 1px solid var(--h-line); background: transparent; color: var(--h-muted); font: inherit; font-size: 12px; cursor: pointer; }
    .tabs button.on { border-color: var(--h-accent); color: var(--h-accent-soft); }
    .diff { max-height: 420px; overflow: auto; background: #151518; border: 1px solid var(--h-line); border-radius: 8px; font: 12px/1.5 var(--h-mono); white-space: pre; }
    .diff div { padding: 0 8px; }
    .diff .add { background: rgba(45,212,191,.12); color: #A7F3D0; } .diff .del { background: rgba(248,113,113,.12); color: #FECACA; }
  `],
  template: `
  <div class="wrap">
    <div class="bar">
      <h1>Harmony</h1>
      <div class="seg" role="group" aria-label="Repository pair">
        @for (p of profiles(); track p.name) {
          <button type="button" [class.on]="p.name === profileName()" (click)="selectProfile(p.name)">{{ title(p.name) }}</button>
        }
      </div>
      @if (profile(); as p) { <span class="chip">profile <b>{{ p.name }}</b> {{ p.valid ? '✓' : '✗' }} · builds untrusted</span> }
      <span class="grow small faint">{{ configPath() }}</span>
    </div>

    @if (!configFound()) {
      <div class="note warn">No alignment config found at <span class="mono">{{ configPath() }}</span>. Start forkbridge-web with <span class="mono">FORKBRIDGE_ALIGNMENT_CONFIG</span> pointing at your forkbridge.alignment.yml, or place it next to forkbridge.yml.</div>
    }
    @if (profile(); as p) { @if (!p.valid) { <div class="note bad">{{ p.error }}</div> } }
    @if (toast()) { <div class="note ok" role="status">{{ toast() }}</div> }

    @if (profile()?.valid) {
      <section class="sides" aria-label="Sides">
        <div class="panel pad side q">
          <div class="side-head"><span class="eyebrow" style="color: var(--h-qisl)">Primary</span><b style="color: var(--h-white)">{{ profile()?.primary?.label }}</b><span class="grow mono small faint">{{ profile()?.primary?.repo }}</span></div>
          <label class="lbl" for="pwt">Worktree ({{ primaryWts().length }} found)</label>
          <select id="pwt" [value]="pwt() ?? ''" (change)="pwt.set($any($event.target).value)">
            @for (w of primaryWts(); track w.id) { <option [value]="w.id" [selected]="w.id === pwt()">{{ wtLabel(w) }}</option> }
          </select>
          @if (selPrimary(); as w) {
            <div class="mono path">{{ w.path }}</div>
            <div class="flags">
              <span class="chip">HEAD <span class="mono">{{ short(w.head) }}</span></span>
              @for (f of flags(w); track f.text) { <span class="chip" [style.color]="f.fg" [style.border-color]="'rgba(' + f.rgb + ',.4)'" [style.background]="'rgba(' + f.rgb + ',.12)'">{{ f.text }}</span> }
            </div>
          }
        </div>

        <div class="panel pad side s">
          <div class="side-head"><span class="eyebrow" style="color: var(--h-accent-ink)">Sync base</span><span class="small faint">3-way merge base for the shared scope</span></div>
          <div class="bar" style="gap: 10px"><span class="mono" style="font-size: 20px; color: var(--h-white)">{{ short(effectiveSyncBase()) }}</span>
            <span class="small muted">{{ syncBaseSource() }}</span></div>
          @if (sessionSyncBase(); as s) {
            <div class="note accent">Session SyncBase <span class="mono">{{ short(s) }}</span> will be used by the next plan. <button type="button" class="btn" style="min-height: 30px; margin-left: 8px" (click)="clearSessionSyncBase()">Clear</button></div>
          }
          @if (plan(); as pl) { @if (profile()?.syncBase && !pl.syncBase.startsWith(profile()!.syncBase!)) {
            <div class="small muted">Profile default <span class="mono">{{ short(profile()?.syncBase) }}</span> differs from this plan's <span class="mono">{{ short(pl.syncBase) }}</span>.</div>
          } }
          <a [routerLink]="['/harmony', profileName(), 'syncbase']" [queryParams]="{ plan: planId() }" style="font-weight: 600; font-size: 13px">Open commit graph &amp; Advisor →</a>
        </div>

        <div class="panel pad side i">
          <div class="side-head"><span class="eyebrow" style="color: var(--h-isl)">Fork</span><b style="color: var(--h-white)">{{ profile()?.fork?.label }}</b><span class="grow mono small faint">{{ profile()?.fork?.repo }}</span></div>
          <label class="lbl" for="fwt">Worktree ({{ forkWts().length }} found)</label>
          <select id="fwt" [value]="fwt() ?? ''" (change)="fwt.set($any($event.target).value)">
            @for (w of forkWts(); track w.id) { <option [value]="w.id" [selected]="w.id === fwt()">{{ wtLabel(w) }}</option> }
          </select>
          @if (selFork(); as w) {
            <div class="mono path">{{ w.path }}</div>
            <div class="flags">
              <span class="chip">HEAD <span class="mono">{{ short(w.head) }}</span></span>
              @for (f of flags(w); track f.text) { <span class="chip" [style.color]="f.fg" [style.border-color]="'rgba(' + f.rgb + ',.4)'" [style.background]="'rgba(' + f.rgb + ',.12)'">{{ f.text }}</span> }
            </div>
          }
        </div>
      </section>

      @if (selectionNote(); as n) { <div class="note warn" role="status">{{ n }}</div> }

      <nav class="steps" aria-label="Steps">
        @for (s of steps(); track s.n) {
          <span class="step" [class.current]="s.state === 'current'" [class.locked]="s.state === 'locked'" [title]="s.title"><span class="dot">{{ s.n }}</span>{{ s.label }}</span>
        }
        <span class="grow bar" style="gap: 8px">
          <label class="lbl" for="planSel">Plan</label>
          <select id="planSel" [value]="planId() ?? ''" (change)="openPlan($any($event.target).value)" style="max-width: 340px">
            @for (p of plans(); track p.id) { <option [value]="p.id" [selected]="p.id === planId()">{{ planLabel(p) }}</option> }
            @if (plans().length === 0) { <option value="">no plans yet</option> }
          </select>
          <button type="button" class="btn" (click)="runPlan(true)" [disabled]="!!busy()">Preview plan</button>
          <button type="button" class="btn primary" (click)="runPlan(false)" [disabled]="!!busy() || !pwt() || !fwt()">Plan</button>
        </span>
      </nav>

      @if (plan(); as pl) {
        <section class="panel pad" aria-label="Harmony meter" style="display: flex; flex-direction: column; gap: 10px">
          <div class="bar"><span class="eyebrow" style="color: var(--h-muted)">Harmony after apply</span>
            <span style="font-size: 28px; font-weight: 700; color: var(--h-accent)">{{ meter().pct }}</span>
            <span class="muted" style="font-size: 13px">of {{ meter().total }} shared paths byte-identical; the rest is declared divergence</span>
            <span class="grow small faint mono">{{ pl.id }} · {{ pl.contentHash.slice(0, 23) }}</span></div>
          <div class="meter" role="img" [attr.aria-label]="meter().aria">
            <span [style.flex]="meter().same + ' 0 0'" style="background: #5B5B66" [title]="meter().same + ' identical today'"></span>
            <span [style.flex]="meter().converge + ' 0 0'" style="background: #38BDF8" [title]="meter().converge + ' converge on apply'"></span>
            <span class="stripes" [style.flex]="meter().diverge + ' 0 0'" style="background-color: #A78BFA" [title]="meter().diverge + ' declared divergence'"></span>
            @if (meter().blocked > 0) { <span [style.flex]="meter().blocked + ' 0 0'" style="background: #F472B6" [title]="meter().blocked + ' need a decision'"></span> }
          </div>
          <div class="legend">
            <span><span class="sw" style="background: #5B5B66"></span>{{ meter().same }} identical today</span>
            <span><span class="sw" style="background: #38BDF8"></span>{{ meter().converge }} converge on apply</span>
            <span><span class="sw stripes" style="background-color: #A78BFA"></span>{{ meter().diverge }} declared divergence (ISL only · diverge · union)</span>
            <span><span class="sw" style="background: #F472B6"></span>{{ meter().blocked }} need a decision</span>
          </div>
        </section>

        @if (editCount() > 0) {
          <div class="note accent bar" role="status">
            <span><b style="color: var(--h-accent-soft)">Derived plan (unsaved)</b> — {{ editCount() }} decision change(s) on top of <span class="mono">{{ pl.id }}</span>. The original plan stays immutable.</span>
            <span class="grow bar" style="gap: 8px"><button type="button" class="btn" (click)="edits.set({})">Discard</button>
              <button type="button" class="btn primary" (click)="saveDerived()" [disabled]="!!busy()">Save as new plan</button></span>
          </div>
        }

        <div class="cols">
          <aside class="panel queue" aria-label="Queue">
            @for (g of facetGroups(); track g.title) {
              <div style="display: flex; flex-direction: column; gap: 2px">
                <div class="eyebrow" style="padding: 0 10px 4px">{{ g.title }}</div>
                @for (it of g.items; track it.key) {
                  <button type="button" class="facet" [class.on]="facet() === it.key" (click)="facet.set(it.key)">
                    <span>@if (it.color) { <span class="sw" [style.background]="it.color"></span> }{{ it.label }}</span><span class="mono small muted">{{ it.count }}</span>
                  </button>
                }
              </div>
            }
          </aside>

          <section class="panel table" aria-label="Plan rows">
            <div class="bar" style="padding: 12px 14px; border-bottom: 1px solid var(--h-line)">
              <label class="lbl" for="q">Filter</label>
              <input id="q" type="search" placeholder="path contains…" [value]="q()" (input)="q.set($any($event.target).value)" style="flex: 1 1 220px">
              <span class="small muted">{{ viewRows().length }} of {{ pl.rows.length }} rows · {{ facetLabel() }}</span>
            </div>
            <div class="thead"><span>PATH</span><span>CATEGORY</span><span>DECISION</span></div>
            <div class="rows">
              @for (v of viewRows(); track v.row.path) {
                <div class="tr" [class.sel]="v.row.path === selPath()" (click)="select(v.row.path)">
                  <span style="min-width: 0; display: flex; flex-direction: column">
                    <span class="leaf">{{ v.leaf }}@if (v.edited) { <span class="edited">edited</span> }</span>
                    <span class="dir mono">{{ v.dir }}</span>
                  </span>
                  <span class="cat mono">{{ v.cat }}</span>
                  <select class="dec" [attr.aria-label]="'Decision for ' + v.leaf" [value]="v.eff" (click)="$event.stopPropagation()" (change)="setDecision(v.row, $any($event.target).value)"
                    [style.color]="info(v.eff).fg" [style.background]="'rgba(' + info(v.eff).rgb + ',.12)'" [style.border-color]="'rgba(' + info(v.eff).rgb + ',.45)'">
                    @for (d of decisions; track d.key) { <option [value]="d.key" [selected]="d.key === v.eff">{{ d.label }}</option> }
                  </select>
                </div>
              }
            </div>
          </section>

          @if (sel(); as v) {
            <aside class="panel pad detail" aria-label="Selected row">
              <div class="eyebrow" style="color: var(--h-accent-ink)">Selected</div>
              <div style="font-weight: 600; font-size: 15px; color: var(--h-white); word-break: break-word">{{ v.leaf }}</div>
              <div class="mono small muted" style="word-break: break-all">{{ v.row.path }}</div>
              <div class="flags"><span class="chip mono">{{ v.cat }}</span>
                <span class="pill" [style.color]="info(v.eff).fg" [style.background]="'rgba(' + info(v.eff).rgb + ',.14)'" [style.border-color]="'rgba(' + info(v.eff).rgb + ',.45)'">{{ info(v.eff).label }} · {{ info(v.eff).legacy(code()) }}</span></div>
              <p style="margin: 0; font-size: 13px; color: var(--h-ink3)">{{ v.row.reason }}</p>
              @if (v.row.refs) { <div class="note warn small"><b style="color: var(--h-white)">Still referenced</b> by {{ v.row.refs }}</div> }
              <div class="blobs">
                <div><div class="faint" style="font-size: 11px">Base (SyncBase)</div><span class="mono">{{ short(v.row.base?.sha) }}</span></div>
                <div><div class="faint" style="font-size: 11px">Merged</div><span class="mono">{{ short(v.row.merged?.sha) }}</span></div>
                <div><div class="faint" style="font-size: 11px">{{ profile()?.primary?.label }}</div><span class="mono">{{ short(v.row.primary?.sha) }}</span></div>
                <div><div class="faint" style="font-size: 11px">{{ profile()?.fork?.label }}</div><span class="mono">{{ short(v.row.fork?.sha) }}</span></div>
              </div>
              <div class="eyebrow">On apply</div>
              <div class="apply">
                <div style="background: rgba(56,189,248,.08); border-color: rgba(56,189,248,.3)"><div style="font-weight: 600; color: var(--h-qisl)">{{ profile()?.primary?.label }}</div>{{ onApply(v, 'p') }}</div>
                <div style="background: rgba(251,191,36,.08); border-color: rgba(251,191,36,.3)"><div style="font-weight: 600; color: var(--h-isl)">{{ profile()?.fork?.label }}</div>{{ onApply(v, 'f') }}</div>
              </div>
              <div class="eyebrow">Diff</div>
              <div class="tabs" role="tablist">
                <button type="button" [class.on]="diffTab() === 'base-primary'" (click)="showDiff('base-primary')">base → {{ profile()?.primary?.label }}</button>
                <button type="button" [class.on]="diffTab() === 'base-fork'" (click)="showDiff('base-fork')">base → {{ profile()?.fork?.label }}</button>
                <button type="button" [class.on]="diffTab() === 'primary-fork'" (click)="showDiff('primary-fork')">{{ profile()?.primary?.label }} → {{ profile()?.fork?.label }}</button>
                <button type="button" [class.on]="diffTab() === 'merged'" (click)="showDiff('merged')">merged</button>
              </div>
              @if (diffMsg()) { <div class="small muted">{{ diffMsg() }}</div> }
              @if (diffLines(); as lines) {
                <div class="diff">@for (l of lines; track $index) { <div [class.add]="l.t === '+'" [class.del]="l.t === '-'">{{ l.t }} {{ l.s }}</div> }</div>
              }
              <div class="eyebrow">Make permanent</div>
              <input type="text" placeholder="reason for the override (shown in plans)" [value]="overrideReason()" (input)="overrideReason.set($any($event.target).value)">
              <button type="button" class="btn ghost-accent" (click)="promote(v)" [disabled]="!!busy()">Add override to profile ({{ info(v.eff).legacy(code()) }})</button>
            </aside>
          }
        </div>
      } @else if (profile()?.valid && plans().length === 0) {
        <div class="panel pad muted">No plan for this profile yet. Choose a worktree on each side and press <b>Plan</b>, or <b>Preview plan</b> to plan the profile's base refs without any branch.</div>
      }
    }
  </div>
  `,
})
export class HarmonyPage implements OnInit {
  private readonly h = inject(HarmonyApi);
  readonly busy = this.h.api.busy;
  readonly short = short;
  readonly decisions = DECISIONS;
  readonly info = decisionInfo;

  readonly profiles = signal<ProfileInfo[]>([]);
  readonly configPath = signal('');
  readonly configFound = signal(true);
  readonly profileName = signal<string>('');
  readonly worktrees = signal<Worktree[]>([]);
  readonly pwt = signal<string | null>(null);
  readonly fwt = signal<string | null>(null);
  readonly plans = signal<PlanSummary[]>([]);
  readonly planId = signal<string | null>(null);
  readonly plan = signal<AlignPlan | null>(null);
  readonly edits = signal<Record<string, Decision>>({});
  readonly facet = signal('all');
  readonly q = signal('');
  readonly selPath = signal<string | null>(null);
  readonly toast = signal<string | null>(null);
  readonly sessionSyncBase = signal<string | null>(null);
  readonly diffTab = signal<DiffTab>('primary-fork');
  readonly diffLines = signal<DiffLine[] | null>(null);
  readonly diffMsg = signal<string | null>(null);
  readonly overrideReason = signal('');
  private readonly blobCache = new Map<string, Blob>();

  readonly profile = computed(() => this.profiles().find((p) => p.name === this.profileName()) ?? null);
  readonly code = computed(() => this.profile()?.code ?? 'BE');
  readonly primaryWts = computed(() => this.worktrees().filter((w) => w.side === 'Primary'));
  readonly forkWts = computed(() => this.worktrees().filter((w) => w.side === 'Fork'));
  readonly selPrimary = computed(() => this.primaryWts().find((w) => w.id === this.pwt()) ?? null);
  readonly selFork = computed(() => this.forkWts().find((w) => w.id === this.fwt()) ?? null);
  readonly effectiveSyncBase = computed(() => this.sessionSyncBase() ?? this.plan()?.syncBase ?? this.profile()?.syncBase ?? null);
  readonly syncBaseSource = computed(() => this.sessionSyncBase() ? 'session choice (next plan)' : this.plan() ? 'used by this plan' : 'profile default');
  readonly editCount = computed(() => Object.keys(this.edits()).length);

  readonly selectionNote = computed(() => {
    const pl = this.plan(); const p = this.selPrimary(); const f = this.selFork();
    if (!pl || !p || !f) return null;
    if (pl.preview) return 'This is a preview plan (profile base refs, no worktree). Press Plan to plan the selected worktrees.';
    const same = (w: Worktree, s: { branch?: string; worktreePath?: string; rev: string }) =>
      (s.worktreePath ? s.worktreePath.toLowerCase() === w.path.toLowerCase() : s.branch === w.branch) && (!w.head || s.rev.startsWith(w.head) || w.head.startsWith(s.rev));
    if (same(p, pl.primary) && same(f, pl.fork)) return null;
    return `The selected worktrees differ from this plan (${pl.primary.branch ?? short(pl.primary.rev)} ↔ ${pl.fork.branch ?? short(pl.fork.rev)}), or a HEAD moved. Re-plan before reviewing.`;
  });

  readonly steps = computed(() => {
    const st = (n: string, label: string, state: string, title = label) => ({ n, label, state, title });
    const locked = 'Phase 4 — writes to worktrees under the write-safety contract';
    return [st('1', 'Select', this.pwt() && this.fwt() ? 'done' : 'current'), st('2', 'Plan', this.plan() ? 'done' : 'todo'), st('3', 'Review', this.plan() ? 'current' : 'todo'),
      st('4', 'Apply', 'locked', locked), st('5', 'Verify', 'locked', locked), st('6', 'Publish', 'locked', locked)];
  });

  private eff(r: AlignRow): Decision { return this.edits()[r.path] ?? r.decision; }

  readonly meter = computed(() => {
    const pl = this.plan();
    if (!pl) return { pct: '—', total: '0', same: 0, converge: 0, diverge: 0, blocked: 0, aria: '' };
    let converge = 0, diverge = 0, blocked = 0;
    for (const r of pl.rows) {
      const d = this.eff(r);
      if (d === 'Manual') blocked++; else if (d === 'KeepForkOnly' || d === 'KeepBoth' || d === 'ManifestUnion') diverge++; else converge++;
    }
    const total = pl.identical + pl.rows.length;
    return { pct: (100 * (pl.identical + converge) / Math.max(1, total)).toFixed(1) + '%', total: total.toLocaleString('en-GB'), same: pl.identical, converge, diverge, blocked,
      aria: `${pl.identical} identical, ${converge} converge on apply, ${diverge} declared divergence, ${blocked} need a decision` };
  });

  private matches(r: AlignRow, f: string): boolean {
    const d = this.eff(r);
    if (f === 'all') return true;
    if (f === 'review') return d === 'TakeFork';
    if (f === 'kept') return r.category === 'ForkStale' && d === 'KeepForkOnly';
    if (f === 'backup') return r.reason.includes('backup file');
    if (f === 'edited') return this.edits()[r.path] !== undefined;
    if (f.startsWith('cat:')) return r.category === f.slice(4);
    return d === f;
  }

  readonly facetGroups = computed(() => {
    const pl = this.plan(); if (!pl) return [];
    const count = (f: string) => pl.rows.filter((r) => this.matches(r, f)).length;
    const cats = new Map<string, number>();
    for (const r of pl.rows) cats.set(r.category, (cats.get(r.category) ?? 0) + 1);
    const item = (key: string, label: string, color?: string) => ({ key, label, color, count: count(key) });
    return [
      { title: 'Queue', items: [item('Manual', '⚠ Needs a decision', decisionInfo('Manual').fg), item('review', `Review promotions (${this.profile()?.fork?.label} → ${this.profile()?.primary?.label})`, decisionInfo('TakeFork').fg),
        item('kept', 'Stale but still referenced'), item('backup', 'Cleanup candidates (.bak)'), item('edited', 'Edited in this review'), item('all', 'All rows')] },
      { title: 'Direction', items: DECISIONS.slice(0, 7).map((d) => item(d.key, d.label, d.fg)) },
      { title: 'Category', items: [...cats.entries()].sort((a, b) => b[1] - a[1]).map(([c]) => item('cat:' + c, categoryLabel(c, this.code()))) },
    ];
  });

  readonly facetLabel = computed(() => {
    for (const g of this.facetGroups()) for (const it of g.items) if (it.key === this.facet()) return it.label;
    return 'all rows';
  });

  readonly viewRows = computed<ViewRow[]>(() => {
    const pl = this.plan(); if (!pl) return [];
    const q = this.q().trim().toLowerCase(); const f = this.facet();
    return pl.rows.filter((r) => this.matches(r, f) && (!q || r.path.toLowerCase().includes(q))).map((r) => this.view(r));
  });

  readonly sel = computed(() => {
    const pl = this.plan(); if (!pl) return null;
    const r = pl.rows.find((x) => x.path === this.selPath()) ?? pl.rows[0];
    return r ? this.view(r) : null;
  });

  private view(r: AlignRow): ViewRow {
    const parts = r.path.split('/');
    return { row: r, eff: this.eff(r), edited: this.edits()[r.path] !== undefined, leaf: parts[parts.length - 1], dir: parts.slice(0, -1).join('/'), cat: categoryLabel(r.category, this.code()) };
  }

  async ngOnInit() {
    const res = await this.h.api.run('Loading profiles', () => this.h.profiles());
    if (!res) return;
    this.configPath.set(res.configPath); this.configFound.set(res.found); this.profiles.set(res.profiles);
    const first = res.profiles.find((p) => p.name === 'backend' && p.valid) ?? res.profiles.find((p) => p.valid) ?? res.profiles[0];
    if (first) await this.selectProfile(first.name);
  }

  async selectProfile(name: string) {
    this.profileName.set(name);
    this.plan.set(null); this.planId.set(null); this.edits.set({}); this.facet.set('all'); this.selPath.set(null); this.toast.set(null);
    this.sessionSyncBase.set(this.h.sessionSyncBase(name));
    if (!this.profile()?.valid) return;
    const [wts, plans] = await Promise.all([this.h.api.run('Listing worktrees', () => this.h.worktrees(name)), this.h.api.run('Loading plans', () => this.h.plans(name))]);
    this.worktrees.set(wts ?? []);
    this.plans.set((plans ?? []).filter((p) => !p.error));
    const latest = this.plans()[0];
    this.pickDefaultWorktrees(latest);
    if (latest) await this.openPlan(latest.id);
  }

  private pickDefaultWorktrees(latest?: PlanSummary) {
    const p = this.profile();
    const pick = (list: Worktree[], planned?: string, planBranch?: string) =>
      (list.find((w) => planBranch && w.branch === planBranch) ?? list.find((w) => planned && w.branch === planned) ?? list.find((w) => w.isMain) ?? list[0])?.id ?? null;
    this.pwt.set(pick(this.primaryWts(), p?.primary?.plannedBranch, latest?.primary?.branch));
    this.fwt.set(pick(this.forkWts(), p?.fork?.plannedBranch, latest?.fork?.branch));
  }

  async openPlan(id: string) {
    if (!id) return;
    const pl = await this.h.api.run('Loading plan', () => this.h.plan(this.profileName(), id));
    if (!pl) return;
    this.planId.set(id); this.plan.set(pl); this.edits.set({}); this.diffLines.set(null); this.diffMsg.set(null);
    const firstPromo = pl.rows.find((r) => r.decision === 'Manual') ?? pl.rows.find((r) => r.decision === 'TakeFork') ?? pl.rows[0];
    this.selPath.set(firstPromo?.path ?? null);
  }

  async runPlan(preview: boolean) {
    this.toast.set(null);
    const body = preview ? { preview: true, syncBase: this.sessionSyncBase() ?? undefined }
      : { primaryWorktreeId: this.pwt() ?? undefined, forkWorktreeId: this.fwt() ?? undefined, syncBase: this.sessionSyncBase() ?? undefined };
    try {
      const r = await this.h.runPlan(this.profileName(), body);
      this.plans.set((await this.h.plans(this.profileName())).filter((p) => !p.error));
      await this.openPlan(r.id);
      this.toast.set(`Plan ${r.id}: ${r.rows} rows, ${r.identical} identical paths.`);
    } catch { /* surfaced by the shared error banner */ }
  }

  select(path: string) { this.selPath.set(path); this.diffLines.set(null); this.diffMsg.set(null); }

  setDecision(r: AlignRow, d: Decision) {
    const e = { ...this.edits() };
    if (d === r.decision) delete e[r.path]; else e[r.path] = d;
    this.edits.set(e); this.selPath.set(r.path); this.toast.set(null);
  }

  async saveDerived() {
    const id = this.planId(); if (!id) return;
    const r = await this.h.api.run('Saving derived plan', () => this.h.saveDecisions(this.profileName(), id, this.edits()));
    if (!r) return;
    this.plans.set((await this.h.plans(this.profileName())).filter((p) => !p.error));
    await this.openPlan(r.id);
    this.toast.set(`Saved derived plan ${r.id} (parent ${r.parentPlanId}, ${r.changed} change(s)). The original plan is unchanged.`);
  }

  async promote(v: ViewRow) {
    const legacy = decisionInfo(v.eff).legacy(this.code());
    const r = await this.h.api.run('Updating profile', () => this.h.addOverride(this.profileName(), v.row.path, legacy, this.overrideReason() || 'set in Harmony review'));
    if (r) { this.toast.set(`Override added to profile ${this.profileName()}: ${v.row.path} → ${legacy} (.bak kept). Plans made from now on apply it.`); this.overrideReason.set(''); }
  }

  clearSessionSyncBase() { this.h.setSessionSyncBase(this.profileName(), null); this.sessionSyncBase.set(null); }

  async showDiff(tab: DiffTab) {
    this.diffTab.set(tab); this.diffLines.set(null); this.diffMsg.set(null);
    const v = this.sel(); const id = this.planId(); if (!v || !id) return;
    const pick: Record<DiffTab, [string | undefined, string | undefined]> = {
      'base-primary': [v.row.base?.sha, v.row.primary?.sha], 'base-fork': [v.row.base?.sha, v.row.fork?.sha],
      'primary-fork': [v.row.primary?.sha, v.row.fork?.sha], merged: [v.row.primary?.sha, v.row.merged?.sha],
    };
    const [a, b] = pick[tab];
    if (!a && !b) { this.diffMsg.set('Absent on both sides.'); return; }
    if (a === b) { this.diffMsg.set('Identical.'); return; }
    const load = async (sha?: string) => {
      if (!sha) return { text: '', binary: false } as Blob;
      if (!this.blobCache.has(sha)) this.blobCache.set(sha, await this.h.blob(this.profileName(), id, sha));
      return this.blobCache.get(sha)!;
    };
    const res = await this.h.api.run('Loading blobs', async () => [await load(a), await load(b)] as const);
    if (!res) return;
    const [x, y] = res;
    if (x.binary || y.binary) { this.diffMsg.set('Binary content.'); return; }
    const lines = lineDiff(x.text ?? '', y.text ?? '');
    if (!lines) this.diffMsg.set('Too large to diff in the browser.'); else this.diffLines.set(lines);
    if (!a) this.diffMsg.set('Added (absent on the left).'); else if (!b) this.diffMsg.set('Deleted (absent on the right).');
  }

  onApply(v: ViewRow, side: 'p' | 'f'): string {
    const r = v.row; const own = side === 'p' ? r.primary?.sha : r.fork?.sha;
    switch (v.eff) {
      case 'TakePrimary': return side === 'p' ? 'unchanged' : r.primary ? 'gets ' + this.profile()?.primary?.label + ' ' + short(r.primary.sha) : 'file deleted';
      case 'TakeFork': return side === 'f' ? 'unchanged' : r.fork ? 'gets ' + this.profile()?.fork?.label + ' ' + short(r.fork.sha) : 'file deleted';
      case 'Merged': return own === r.merged?.sha ? 'unchanged (already the merge)' : 'gets merged ' + short(r.merged?.sha);
      case 'DeleteBoth': return own ? 'file deleted' : 'stays absent';
      case 'ManifestUnion': return 'gains the other side’s missing package entries';
      case 'Manual': return 'left untouched (blocks apply)';
      default: return own ? 'keeps its own ' + short(own) : 'stays absent';
    }
  }

  flags(w: Worktree) {
    const out: { text: string; fg: string; rgb: string }[] = [];
    if (w.isMain) out.push({ text: 'main checkout · 2nd confirmation to write', fg: '#D4D4D8', rgb: '161,161,170' });
    if (w.detached) out.push({ text: 'detached', fg: '#D4D4D8', rgb: '161,161,170' });
    if (!w.exists) out.push({ text: 'missing on disk', fg: '#FCA5A5', rgb: '248,113,113' });
    if (w.protected) out.push({ text: 'protected · never pushed', fg: '#FCA5A5', rgb: '248,113,113' });
    if (w.scopeDirty === true) out.push({ text: 'scope dirty', fg: '#FCD34D', rgb: '251,191,36' });
    if (w.scopeDirty === false) out.push({ text: 'scope clean ✓', fg: '#5EEAD4', rgb: '45,212,191' });
    if (w.ahead != null) out.push({ text: `↑${w.ahead} ↓${w.behind}`, fg: '#D4D4D8', rgb: '161,161,170' });
    const pl = this.plan();
    if (pl && !pl.preview && ((w.side === 'Primary' && pl.primary.branch === w.branch) || (w.side === 'Fork' && pl.fork.branch === w.branch)))
      out.push({ text: 'plan pinned to ' + short(w.side === 'Primary' ? pl.primary.rev : pl.fork.rev, 7), fg: '#FDBA74', rgb: '249,115,22' });
    return out;
  }

  wtLabel(w: Worktree) { return `${w.branch ?? '(detached ' + short(w.head, 7) + ')'}   ·   ${w.path.split(/[\\/]/).slice(-2).join('\\')}`; }
  planLabel(p: PlanSummary) { return `${p.id}${p.parentPlanId ? ' (derived)' : ''}${p.preview ? ' (preview)' : ''} · ${p.rows} rows`; }
  title(n: string) { return n.charAt(0).toUpperCase() + n.slice(1); }
}
