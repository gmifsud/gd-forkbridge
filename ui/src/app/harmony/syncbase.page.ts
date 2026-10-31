import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { AdvisorResult, HarmonyApi, LinePoint, ProfileInfo, short } from './harmony-api';
import { HARMONY_THEME } from './harmony-theme';
import { BusyVeil } from './busy-veil';

interface Badge { text: string; title?: string; fg: string; rgb: string; }

/** SyncBase screen: the primary's commit lane with Advisor evidence, so the merge base is chosen from descriptions
 *  and scores rather than by sifting git log. Choosing is either per session (next plan) or the profile default. */
@Component({
  selector: 'fb-syncbase',
  imports: [RouterLink, BusyVeil],
  styles: [HARMONY_THEME + `
    .top { display: grid; grid-template-columns: repeat(auto-fit, minmax(340px, 1fr)); gap: 16px; }
    .rec { border-top: 3px solid var(--h-accent); display: flex; flex-direction: column; gap: 10px; }
    .rec ul { margin: 0; padding-left: 18px; font-size: 13px; color: var(--h-ink3); display: flex; flex-direction: column; gap: 4px; }
    .cmp { display: grid; grid-template-columns: 1fr auto auto; gap: 6px 16px; font-size: 13px; align-items: center; }
    .cmp .h { font-size: 11px; font-weight: 700; letter-spacing: .5px; color: var(--h-faint); }
    .better { color: var(--h-ok); } .worse { color: var(--h-bad); }
    .cols { display: flex; flex-wrap: wrap; gap: 16px; align-items: flex-start; }
    .lane { flex: 999 1 620px; min-width: 0; overflow: hidden; }
    .side { flex: 1 1 340px; max-width: 460px; display: flex; flex-direction: column; gap: 16px; }
    .c { display: grid; grid-template-columns: 150px 92px 92px minmax(0, 1fr) 120px; gap: 12px; align-items: center; padding: 8px 14px; border-bottom: 1px solid var(--h-line2); cursor: pointer; font-size: 13px; }
    .c:hover { background: #2C2C32; }
    .c.head { cursor: default; background: var(--h-head); font-size: 11px; font-weight: 700; letter-spacing: .5px; color: var(--h-faint); }
    .c.sel { background: rgba(249,115,22,.12); box-shadow: inset 3px 0 0 var(--h-accent); }
    .c.trans { opacity: .62; }
    .badges { display: flex; flex-wrap: wrap; gap: 4px; }
    .b { font-size: 10px; font-weight: 700; letter-spacing: .3px; border-radius: 4px; padding: 1px 6px; border: 1px solid; }
    .subj { display: flex; flex-direction: column; min-width: 0; }
    .subj span:first-child { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--h-ink); }
    .subj span:last-child { font-size: 11px; color: var(--h-faint); }
    .score { display: flex; align-items: center; gap: 8px; }
    .track { flex: 1; height: 8px; background: var(--h-sunk); border-radius: 4px; overflow: hidden; }
    .track span { display: block; height: 100%; border-radius: 4px; }
    .rows { max-height: 760px; overflow: auto; }
    .metrics { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 8px; }
    .metrics > div { background: var(--h-sunk); border: 1px solid var(--h-line2); border-radius: 8px; padding: 8px 10px; }
    .metrics .k { font-size: 11px; color: var(--h-faint); }
    .metrics .v { font-family: var(--h-mono); font-size: 16px; color: var(--h-white); }
    .bulk { border-bottom: 1px solid var(--h-line2); padding: 8px 0; font-size: 13px; }
    .t { display: grid; grid-template-columns: 110px minmax(120px, 1fr) repeat(6, 64px); gap: 10px; padding: 8px 14px; border-bottom: 1px solid var(--h-line2); font-size: 13px; align-items: center; cursor: pointer; }
    .t.head { cursor: default; background: var(--h-head); font-size: 11px; font-weight: 700; letter-spacing: .5px; color: var(--h-faint); }
    .t:hover:not(.head) { background: #2C2C32; }
    .t.sel { background: rgba(249,115,22,.12); box-shadow: inset 3px 0 0 var(--h-accent); }
    .w { display: flex; flex-wrap: wrap; gap: 12px; align-items: flex-end; }
    .w label { display: flex; flex-direction: column; gap: 4px; font-size: 12px; color: var(--h-muted); }
    .w input { width: 84px; }
    .scroll-x { overflow-x: auto; }
  `],
  template: `
  <fb-busy-veil [active]="blocking()" />
  <div class="wrap" [attr.inert]="blocking() ? '' : null" [attr.aria-busy]="blocking()">
    <div class="bar">
      <a [routerLink]="['/harmony']" style="font-weight: 600">← Back to Harmony</a>
      <h1>SyncBase · {{ title() }}</h1>
      @if (res(); as r) {
        <span class="muted small">{{ profile()?.primary?.label }} <span class="mono">{{ short(r.primaryRev) }}</span> ↔ {{ profile()?.fork?.label }} <span class="mono">{{ short(r.forkRev) }}</span>@if (planId()) { · plan {{ planId() }} }</span>
      }
    </div>
    <p class="muted" style="margin: 0; font-size: 13px">Pick the {{ profile()?.primary?.label ?? 'primary' }} commit the fork last synchronised from. It is the 3-way merge base for every shared path: too old and the fork's deliberate changes look like losses; too new and the fork's copies would revert primary work.</p>

    @if (toast()) { <div class="note ok" role="status">{{ toast() }}</div> }
    @if (!planId()) { <div class="note warn">No plan selected. Open this screen from Harmony (it passes the plan) so the Advisor knows which two commits to compare.</div> }

    @if (!res() && planId()) {
      <div class="panel pad bar">
        <span class="muted">The Advisor simulates the plan for each candidate base and scores it. It reads history only; nothing is written.</span>
        <button type="button" class="btn primary grow" (click)="advise()" [disabled]="!!busy()">Run the Advisor</button>
      </div>
    }

    @if (res(); as r) {
      @for (w of r.warnings; track w) { <div class="note warn">{{ w }}</div> }

      <section class="top">
        <div class="panel pad rec">
          @if (rec(); as c) {
            <div class="eyebrow" style="color: var(--h-accent-ink)">Recommended · confidence {{ r.confidence }}</div>
            <div class="bar" style="gap: 10px"><span class="mono" style="font-size: 22px; font-weight: 500; color: var(--h-white)">{{ short(c.sha) }}</span><span class="muted small">{{ day(c.date) }} · {{ c.author }}</span></div>
            <div style="color: var(--h-ink)">{{ c.subject }}</div>
          } @else {
            <div class="eyebrow">No recommendation · confidence {{ r.confidence }}</div>
          }
          <ul>@for (x of r.rationale; track $index) { <li>{{ x }}</li> }</ul>
          @if (rec(); as c) {
            <div class="bar">
              <button type="button" class="btn primary" (click)="useSession(c.sha)">Use for this session</button>
              <button type="button" class="btn ghost-accent" (click)="makeDefault(c.sha)" [disabled]="!!busy()">Make profile default</button>
            </div>
          }
        </div>

        <div class="panel pad" style="display: flex; flex-direction: column; gap: 10px">
          @if (compare(); as cmp) {
            <div class="eyebrow">Compare · {{ short(cmp.a.sha) }} ({{ cmp.aLabel }}) → {{ short(cmp.b.sha) }} ({{ cmp.bLabel }})</div>
            <div class="cmp">
              <span class="h">IF CHOSEN</span><span class="h mono">{{ short(cmp.a.sha, 7) }}</span><span class="h mono">{{ short(cmp.b.sha, 7) }}</span>
              @for (m of cmp.rows; track m.k) {
                <span>{{ m.k }}</span><span class="mono">{{ m.a }}</span><span class="mono" [class.better]="m.dir < 0" [class.worse]="m.dir > 0">{{ m.b }}</span>
              }
            </div>
            @if (cmp.same) { <p class="small muted" style="margin: 0">Both bases classify every row identically, so the choice does not change the plan.</p> }
          } @else {
            <div class="eyebrow">Compare</div>
            <p class="small muted" style="margin: 0">Select a candidate below to compare it with the profile default ({{ short(r.configured) }}).</p>
          }
        </div>
      </section>

      <div class="cols">
        <section class="panel lane" aria-label="Primary commits">
          <div class="bar" style="padding: 12px 14px; border-bottom: 1px solid var(--h-line)">
            <span class="eyebrow">{{ profile()?.primary?.label }} lane</span>
            <div class="seg" role="group" aria-label="Line">
              @for (l of r.lines; track l.name; let i = $index) { <button type="button" [class.on]="i === lineIdx()" (click)="lineIdx.set(i)">{{ l.name }}</button> }
            </div>
            <label class="small muted" style="display: flex; gap: 6px; align-items: center"><input type="checkbox" [checked]="hideTransient()" (change)="hideTransient.set($any($event.target).checked)" style="min-height: auto"> hide transient</label>
            <span class="small faint grow">commits touching the shared scope · newest first · score = prefilter sweep (lower is better)</span>
          </div>
          <div class="c head"><span>EVIDENCE</span><span>COMMIT</span><span>DATE</span><span>DESCRIPTION</span><span>SCORE</span></div>
          <div class="rows">
            @for (c of commits(); track c.sha) {
              <div class="c" [class.sel]="c.sha === selSha()" [class.trans]="c.transient" (click)="selSha.set(c.sha)">
                <span class="badges">@for (b of badges(c.sha, c.transient); track b.text) { <span class="b" [attr.title]="b.title ?? null" [style.color]="b.fg" [style.border-color]="'rgba(' + b.rgb + ',.45)'" [style.background]="'rgba(' + b.rgb + ',.12)'">{{ b.text }}</span> }</span>
                <span class="mono" style="font-size: 12px; color: var(--h-ink)">{{ short(c.sha) }}</span>
                <span class="small muted">{{ day(c.date) }}</span>
                <span class="subj"><span>{{ c.subject }}</span><span>{{ c.author }}</span></span>
                <span class="score"><span class="track"><span [style.width.%]="bar(c.score)" [style.background]="barColour(c)"></span></span><span class="mono" style="font-size: 12px; width: 36px; text-align: right">{{ c.score }}</span></span>
              </div>
            }
            @if (commits().length === 0) { <div class="pad muted">No commits on this line touch the shared scope in the window.</div> }
          </div>
        </section>

        <aside class="side">
          <section class="panel pad" aria-label="Selected commit" style="display: flex; flex-direction: column; gap: 10px">
            <div class="eyebrow" style="color: var(--h-accent-ink)">Selected commit</div>
            @if (sel(); as s) {
              <div class="bar" style="gap: 10px"><span class="mono" style="font-size: 18px; color: var(--h-white)">{{ short(s.sha) }}</span><span class="small muted">{{ day(s.date) }} · {{ s.author }}</span></div>
              <div style="font-size: 13px">{{ s.subject }}</div>
              <div class="badges">@for (b of badges(s.sha, !!s.point?.transient); track b.text) { <span class="b" [attr.title]="b.title ?? null" [style.color]="b.fg" [style.border-color]="'rgba(' + b.rgb + ',.45)'" [style.background]="'rgba(' + b.rgb + ',.12)'">{{ b.text }}</span> }</div>
              @if (s.cand; as c) {
                <div class="metrics">
                  <div><div class="k">score</div><div class="v">{{ c.score }}</div></div>
                  <div><div class="k">revert risk</div><div class="v">{{ c.risk }}</div></div>
                  <div><div class="k">merges</div><div class="v">{{ c.merges }}</div></div>
                  <div><div class="k">conflicts</div><div class="v">{{ c.conflicts }}</div></div>
                  <div><div class="k">promotions</div><div class="v">{{ c.promotions }}</div></div>
                  <div><div class="k">fit</div><div class="v">{{ pct(c.fit) }}</div></div>
                </div>
                @if (c.riskSample.length) { <div class="note warn small"><b style="color: var(--h-white)">Flagged:</b> @for (p of c.riskSample; track p) { <div class="mono" style="word-break: break-all">{{ p }}</div> }</div> }
                @if (c.isTransient) { <div class="note warn small">Transient: the line's tip restores most of what this commit changed, so it is a poor base.</div> }
              } @else if (s.point) {
                <div class="note small" style="border-color: var(--h-line); color: var(--h-ink3)">Prefilter score {{ s.point.score }} ({{ s.point.risk }} revert risk, {{ s.point.merges }} merge(s)). Not among the fully simulated candidates.</div>
              }
              <div class="bar">
                <button type="button" class="btn" (click)="useSession(s.sha)">Use for this session</button>
                <button type="button" class="btn ghost-accent" (click)="makeDefault(s.sha)" [disabled]="!!busy()">Make profile default</button>
              </div>
            } @else { <div class="small muted">Select a commit in the lane or a candidate below.</div> }
          </section>

          <section class="panel pad" aria-label="Fork lane">
            <div class="eyebrow">{{ profile()?.fork?.label }} lane · bulk imports → closest {{ profile()?.primary?.label }} state</div>
            @for (k of r.forkBulk; track k.sha) {
              <div class="bulk">
                <div class="bar" style="gap: 8px"><span class="mono" style="font-size: 12px; color: var(--h-ink)">{{ short(k.sha) }}</span><span class="small faint">{{ day(k.date) }} · {{ k.author }}</span></div>
                <div style="color: var(--h-ink3)">{{ k.subject }}</div>
                <div class="small muted">{{ k.scopedFiles }} scoped files @if (k.matchSha) { · ≈ <a href="" class="mono" (click)="$event.preventDefault(); selSha.set(k.matchSha!)" style="color: var(--h-qisl)">{{ short(k.matchSha) }}</a> · {{ k.same }} same / {{ k.differ }} differ }</div>
              </div>
            }
            @if (r.forkBulk.length === 0) { <div class="small muted" style="padding-top: 8px">No bulk imports found in the window.</div> }
            <p class="small faint" style="margin: 8px 0 0">Upstream provenance from <span class="mono">FromUpstreamCommit.ps1</span> imports is matched on author, author date and subject (no SHA trailer exists).</p>
          </section>
        </aside>
      </div>

      <section class="panel" aria-label="Candidates">
        <div class="bar" style="padding: 12px 14px; border-bottom: 1px solid var(--h-line)">
          <span class="eyebrow">Candidates · full simulation</span>
          <span class="small faint">score = {{ r.weights.revert }} × revert risks + {{ r.weights.merge }} × merges + {{ r.weights.conflict }} × conflicts</span>
          <div class="w grow">
            <label>revert<input type="number" min="0" max="100" [value]="wR()" (input)="wR.set(+$any($event.target).value)"></label>
            <label>merge<input type="number" min="0" max="100" [value]="wM()" (input)="wM.set(+$any($event.target).value)"></label>
            <label>conflict<input type="number" min="0" max="100" [value]="wC()" (input)="wC.set(+$any($event.target).value)"></label>
            <button type="button" class="btn" (click)="advise()" [disabled]="!!busy()">Re-run</button>
          </div>
        </div>
        <div class="scroll-x">
          <div class="t head"><span>COMMIT</span><span>SOURCE</span><span>SCORE</span><span>RISK</span><span>MERGES</span><span>CONFL.</span><span>PROMO.</span><span>FIT</span></div>
          @for (c of r.candidates; track c.sha) {
            <div class="t" [class.sel]="c.sha === selSha()" (click)="selSha.set(c.sha)" [style.opacity]="c.isTransient ? .6 : 1">
              <span class="mono" [style.color]="c.sha === r.recommended ? 'var(--h-accent-soft)' : 'var(--h-ink)'">{{ short(c.sha) }}</span>
              <span class="small muted">{{ c.sources.join(', ') }}</span>
              <span class="mono" style="font-weight: 600; color: var(--h-white)">{{ c.score }}</span>
              <span class="mono">{{ c.risk }}</span><span class="mono">{{ c.merges }}</span><span class="mono">{{ c.conflicts }}</span><span class="mono">{{ c.promotions }}</span><span class="mono">{{ pct(c.fit) }}</span>
            </div>
          }
        </div>
      </section>
    }
  </div>
  `,
})
export class SyncBasePage implements OnInit {
  private readonly h = inject(HarmonyApi);
  private readonly route = inject(ActivatedRoute);
  readonly busy = this.h.api.busy;
  readonly short = short;

  readonly profileName = signal('');
  readonly profile = signal<ProfileInfo | null>(null);
  readonly planId = signal<string | null>(null);
  readonly res = signal<AdvisorResult | null>(null);
  readonly lineIdx = signal(0);
  readonly selSha = signal<string | null>(null);
  readonly hideTransient = signal(false);
  readonly toast = signal<string | null>(null);
  readonly wR = signal(5); readonly wM = signal(1); readonly wC = signal(3);

  readonly title = computed(() => { const n = this.profileName(); return n.charAt(0).toUpperCase() + n.slice(1); });
  readonly cands = computed(() => new Map((this.res()?.candidates ?? []).map((c) => [c.sha, c] as const)));
  readonly rec = computed(() => { const r = this.res(); return r?.recommended ? this.cands().get(r.recommended) ?? null : null; });
  readonly line = computed(() => this.res()?.lines[this.lineIdx()] ?? null);
  readonly commits = computed(() => (this.line()?.commits ?? []).filter((c) => !this.hideTransient() || !c.transient));
  private readonly maxScore = computed(() => Math.max(1, ...(this.line()?.commits ?? []).map((c) => c.score)));
  readonly session = signal<string | null>(null);

  readonly sel = computed(() => {
    const sha = this.selSha(); const r = this.res(); if (!sha || !r) return null;
    const cand = this.cands().get(sha) ?? null;
    const point = r.lines.flatMap((l) => l.commits).find((c) => c.sha === sha) ?? null;
    const meta = cand ?? point;
    return meta ? { sha, date: meta.date, author: meta.author, subject: meta.subject, cand, point } : null;
  });

  readonly compare = computed(() => {
    const r = this.res(); if (!r) return null;
    const a = (r.configured && this.cands().get(r.configured)) || null;
    const selC = this.sel()?.cand ?? null;
    const b = selC && selC.sha !== a?.sha ? selC : this.rec();
    if (!a || !b || a.sha === b.sha) return null;
    const row = (k: string, x: number, y: number, lowerBetter = true, fmt = (n: number) => String(n)) =>
      ({ k, a: fmt(x), b: fmt(y), dir: x === y ? 0 : (y < x) === lowerBetter ? -1 : 1 });
    return {
      a, b, aLabel: 'profile default', bLabel: b.sha === r.recommended ? 'recommended' : 'selected',
      same: a.fingerprint === b.fingerprint,
      rows: [row('score', a.score, b.score), row('revert risk', a.risk, b.risk), row('merges', a.merges, b.merges), row('conflicts', a.conflicts, b.conflicts),
        row('promotions', a.promotions, b.promotions, true), row('fit', a.fit, b.fit, false, (n) => this.pct(n)), row('rows', a.rows, b.rows)],
    };
  });

  /** Locked from navigation until the profile and the Advisor run have loaded, and during any later busy step. */
  readonly ready = signal(false);
  readonly blocking = computed(() => !this.ready() || !!this.busy());

  async ngOnInit() {
    try { await this.init(); } finally { this.ready.set(true); }
  }

  private async init() {
    const p = this.route.snapshot.paramMap.get('profile') ?? 'backend';
    this.profileName.set(p);
    this.planId.set(this.route.snapshot.queryParamMap.get('plan'));
    this.session.set(this.h.sessionSyncBase(p));
    const res = await this.h.api.run('Loading profile', () => this.h.profiles());
    const prof = res?.profiles.find((x) => x.name === p) ?? null;
    this.profile.set(prof);
    if (prof?.weights) { this.wR.set(prof.weights.revertWeight); this.wM.set(prof.weights.mergeWeight); this.wC.set(prof.weights.conflictWeight); }
    if (this.planId()) await this.advise();
  }

  async advise() {
    const id = this.planId(); if (!id) return;
    this.toast.set(null);
    try {
      const r = await this.h.advise(this.profileName(), { planId: id, revertWeight: this.wR(), mergeWeight: this.wM(), conflictWeight: this.wC() });
      this.res.set(r);
      this.lineIdx.set(Math.max(0, r.lines.findIndex((l) => l.selected)));
      this.selSha.set(r.recommended ?? r.candidates[0]?.sha ?? null);
    } catch { /* shown by the shared error banner */ }
  }

  useSession(sha: string) {
    this.h.setSessionSyncBase(this.profileName(), sha); this.session.set(sha);
    this.toast.set(`Session SyncBase set to ${short(sha)}. The next plan on Harmony for ${this.profileName()} uses it; the profile file is unchanged.`);
  }

  async makeDefault(sha: string) {
    const note = `chosen on the SyncBase screen${this.res()?.recommended === sha ? ' (Advisor recommendation)' : ''}`;
    const r = await this.h.api.run('Updating profile', () => this.h.setSyncBase(this.profileName(), sha, note));
    if (r) {
      this.h.setSessionSyncBase(this.profileName(), null); this.session.set(null);
      this.toast.set(`Profile ${this.profileName()} SyncBase is now ${short(sha)} (previous value kept as a comment, .bak written). Re-plan on Harmony to use it.`);
    }
  }

  badges(sha: string, transient: boolean): Badge[] {
    const r = this.res(); if (!r) return [];
    const out: Badge[] = [];
    if (sha === r.recommended) out.push({ text: 'RECOMMENDED', fg: '#FDBA74', rgb: '249,115,22' });
    if (r.configured && sha.startsWith(r.configured.slice(0, 9))) out.push({ text: 'PROFILE', fg: '#7DD3FC', rgb: '56,189,248' });
    if (this.session() && sha.startsWith(this.session()!)) out.push({ text: 'SESSION', fg: '#FDBA74', rgb: '249,115,22' });
    const c = this.cands().get(sha);
    for (const s of c?.sources ?? []) {
      if (s === 'configured') continue;
      out.push({ text: s.split(/[\s:(≈#]/)[0].toUpperCase() || s.toUpperCase(), title: s, fg: '#C4B5FD', rgb: '167,139,250' });
    }
    if (transient || c?.isTransient) out.push({ text: 'TRANSIENT', fg: '#FCA5A5', rgb: '248,113,113' });
    return out;
  }

  bar(score: number) { return Math.max(3, 100 * score / this.maxScore()); }
  barColour(c: LinePoint) { return c.sha === this.res()?.recommended ? '#F97316' : c.risk > 0 ? '#F472B6' : '#5B5B66'; }
  day(d: string) { return d ? d.slice(0, 10) : ''; }
  pct(n: number) { return (100 * n).toFixed(1) + '%'; }
}
