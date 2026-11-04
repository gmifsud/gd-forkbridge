import { Component, inject, input } from '@angular/core';
import { Api } from '../api';

/** Loading veil for the Harmony screens: covers the host (which must be position: relative), shows the current
 *  step and keeps a Cancel for long jobs. The host marks its content `inert` while active, so nothing underneath
 *  can be clicked, typed into or focused. The spinner is sticky so it stays visible on long pages. */
@Component({
  selector: 'fb-busy-veil',
  styles: [`
    :host { display: contents; }
    .veil { position: absolute; inset: 0; z-index: 30; background: rgba(28,28,31,.55); backdrop-filter: blur(1px); cursor: progress; animation: in .15s ease-out .12s both; }
    .card { position: sticky; top: 38vh; margin: 0 auto; width: max-content; max-width: calc(100% - 32px); display: flex; align-items: center; gap: 14px;
      padding: 14px 18px; border-radius: 12px; background: var(--panel); border: 1px solid var(--line); box-shadow: 0 16px 40px rgba(0,0,0,.5); cursor: default; }
    .ring { flex: none; width: 22px; height: 22px; border-radius: 50%; border: 3px solid rgba(249,115,22,.25); border-top-color: var(--accent); animation: spin .8s linear infinite; }
    .label { font-size: 14px; color: var(--ink); }
    .sub { font-size: 12px; color: var(--faint); }
    button { min-height: 30px; padding: 0 10px; border-radius: 8px; border: 1px solid var(--ctl); background: transparent; color: var(--ink); font: inherit; font-size: 12px; cursor: pointer; }
    button:hover { border-color: var(--accent); }
    @keyframes spin { to { transform: rotate(360deg); } }
    /* short delay so instant responses don't flash the veil */
    @keyframes in { from { opacity: 0; } to { opacity: 1; } }
    @media (prefers-reduced-motion: reduce) { .ring { animation-duration: 2.4s; } .veil { animation: none; } }
  `],
  template: `
    @if (active()) {
      <div class="veil">
        <div class="card" role="status" aria-live="polite">
          <span class="ring" aria-hidden="true"></span>
          <span><div class="label">{{ (api.busy() ?? label()) }}…</div><div class="sub">Inputs are locked until this finishes.</div></span>
          @if (api.currentJob) { <button type="button" (click)="api.cancel()">Cancel</button> }
        </div>
      </div>
    }
  `,
})
export class BusyVeil {
  readonly api = inject(Api);
  readonly active = input(false);
  readonly label = input('Loading');
}
