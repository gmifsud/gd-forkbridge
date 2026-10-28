/** Dark grey + orange theme for the Harmony screens, scoped to the components (the rest of ForkBridge keeps its
 *  own theme). Tokens are CSS custom properties on :host so a global dark theme can adopt them later. */
export const HARMONY_THEME = `
:host {
  --h-bg: #1C1C1F; --h-panel: #26262B; --h-sunk: #1F1F23; --h-head: #222227; --h-line: #3A3A42; --h-line2: #303036; --h-ctl: #4A4A54;
  --h-ink: #EDEDF0; --h-ink2: #D4D4D8; --h-ink3: #C4C4CC; --h-muted: #A1A1AA; --h-faint: #8B8B94; --h-white: #FFFFFF;
  --h-accent: #F97316; --h-accent-ink: #FB923C; --h-accent-soft: #FDBA74; --h-on-accent: #18181B;
  --h-qisl: #7DD3FC; --h-qisl-rgb: 56,189,248; --h-isl: #FCD34D; --h-isl-rgb: 251,191,36; --h-ok: #5EEAD4; --h-bad: #FCA5A5;
  --h-mono: ui-monospace, "Cascadia Code", "SF Mono", Consolas, monospace;
  display: block; color-scheme: dark; background: var(--h-bg); color: var(--h-ink);
  margin: -16px -20px -60px; padding: 20px 24px 48px; min-height: calc(100vh - 44px);
  /* full-bleed background beyond main's max-width without horizontal overflow */
  box-shadow: 0 0 0 100vmax var(--h-bg); clip-path: inset(0 -100vmax);
  font: 14px/1.45 system-ui, -apple-system, "Segoe UI", sans-serif;
}
* { box-sizing: border-box; }
a { color: var(--h-accent-ink); } a:hover { color: var(--h-accent-soft); }
.mono { font-family: var(--h-mono); }
h1 { margin: 0; font-size: 26px; font-weight: 700; color: var(--h-white); }
h2 { margin: 0; font-size: 18px; color: var(--h-white); }
.wrap { max-width: 1440px; margin: 0 auto; display: flex; flex-direction: column; gap: 16px; }
.bar { display: flex; flex-wrap: wrap; align-items: center; gap: 12px; }
.grow { margin-left: auto; }
.panel { background: var(--h-panel); border: 1px solid var(--h-line); border-radius: 12px; }
.pad { padding: 16px; }
.eyebrow { font-size: 11px; font-weight: 700; letter-spacing: .6px; color: var(--h-faint); text-transform: uppercase; }
.muted { color: var(--h-muted); } .faint { color: var(--h-faint); } .small { font-size: 12px; }
.chip { display: inline-flex; align-items: center; gap: 4px; font-size: 12px; border-radius: 6px; padding: 3px 8px; border: 1px solid var(--h-line); background: var(--h-sunk); color: var(--h-ink2); }
.pill { display: inline-block; font-size: 12px; font-weight: 600; border-radius: 999px; padding: 3px 10px; border: 1px solid; }
.seg { display: inline-flex; background: #111113; border: 1px solid var(--h-line); border-radius: 10px; padding: 3px; }
.seg button { min-height: 34px; padding: 0 14px; border: 0; border-radius: 8px; background: transparent; color: var(--h-muted); font: inherit; font-weight: 600; cursor: pointer; }
.seg button.on { background: var(--h-accent); color: var(--h-on-accent); }
button.btn { min-height: 40px; padding: 0 14px; border-radius: 8px; border: 1px solid var(--h-ctl); background: transparent; color: var(--h-ink); font: inherit; cursor: pointer; }
button.btn:hover:not(:disabled) { border-color: var(--h-accent); }
button.btn.primary { border-color: var(--h-accent); background: var(--h-accent); color: var(--h-on-accent); font-weight: 700; }
button.btn.ghost-accent { border-color: var(--h-accent); color: var(--h-accent-ink); font-weight: 600; }
button.btn:disabled { opacity: .45; cursor: not-allowed; }
select, input { font: inherit; min-height: 40px; border: 1px solid var(--h-ctl); border-radius: 8px; padding: 0 8px; background: var(--h-sunk); color: var(--h-ink); }
select option { background: var(--h-sunk); color: var(--h-ink); }
input::placeholder { color: var(--h-faint); }
label.lbl { font-size: 12px; color: var(--h-muted); }
.note { border-radius: 10px; padding: 10px 14px; font-size: 13px; border: 1px solid; }
.note.accent { background: rgba(249,115,22,.10); border-color: rgba(249,115,22,.40); color: #FED7AA; }
.note.warn { background: rgba(251,191,36,.10); border-color: rgba(251,191,36,.40); color: #FDE68A; }
.note.ok { background: rgba(94,234,212,.08); border-color: rgba(94,234,212,.35); color: #99F6E4; }
.note.bad { background: rgba(248,113,113,.10); border-color: rgba(248,113,113,.40); color: var(--h-bad); }
.mockless { font-size: 12px; color: var(--h-accent-soft); }
`;
