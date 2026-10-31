/** Dark grey + orange theme for the Harmony screens, scoped to the components (the rest of ForkBridge keeps its
 *  own theme). Tokens are CSS custom properties on :host so a global dark theme can adopt them later. */
export const HARMONY_THEME = `
:host {
  /* Aliases of the app-wide tokens in styles.css, so the whole app shares one palette. */
  --h-bg: var(--bg); --h-panel: var(--panel); --h-sunk: var(--sunk); --h-head: var(--head); --h-line: var(--line); --h-line2: var(--line2); --h-ctl: var(--ctl);
  --h-ink: var(--ink); --h-ink2: var(--ink2); --h-ink3: var(--ink3); --h-muted: var(--muted); --h-faint: var(--faint); --h-white: var(--white);
  --h-accent: var(--accent); --h-accent-ink: var(--accent-ink); --h-accent-soft: var(--accent-soft); --h-on-accent: var(--on-accent);
  --h-qisl: #7DD3FC; --h-qisl-rgb: 56,189,248; --h-isl: #FCD34D; --h-isl-rgb: 251,191,36; --h-ok: var(--ok); --h-bad: var(--bad);
  --h-mono: var(--mono);
  display: block; position: relative; color: var(--h-ink); padding: 4px 4px 0;
  min-height: calc(100vh - 120px);   /* the loading veil must cover the viewport even before data arrives */
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
.muted { color: var(--h-muted); } .faint { color: var(--h-faint); } .small { font-size: 12px; padding: 0; }  /* the global .small also pads buttons */
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
label.lbl { display: inline; margin: 0; font-size: 12px; color: var(--h-muted); }
.note { border-radius: 10px; padding: 10px 14px; font-size: 13px; border: 1px solid; }
.note.accent { background: rgba(249,115,22,.10); border-color: rgba(249,115,22,.40); color: #FED7AA; }
.note.warn { background: rgba(251,191,36,.10); border-color: rgba(251,191,36,.40); color: #FDE68A; }
.note.ok { background: rgba(94,234,212,.08); border-color: rgba(94,234,212,.35); color: #99F6E4; }
.note.bad { background: rgba(248,113,113,.10); border-color: rgba(248,113,113,.40); color: var(--h-bad); }
`;
