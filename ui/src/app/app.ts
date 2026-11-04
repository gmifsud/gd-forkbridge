import { Component, computed, inject, signal } from '@angular/core';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { Api } from './api';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <header>
      <span class="brand">Fork<span>Bridge</span></span>
      <nav>
        <a routerLink="/" routerLinkActive="on" [routerLinkActiveOptions]="{ exact: true }">New plan</a>
        <a routerLink="/runs" routerLinkActive="on">History</a>
        <a routerLink="/discover" routerLinkActive="on">Discovery</a>
        <a routerLink="/policy" routerLinkActive="on">Policy & Config</a>
        <a routerLink="/harmony" routerLinkActive="on">Harmony</a>
      </nav>
      <span class="local">local · loopback only</span>
    </header>
    @if (!api.token()) {
      <div class="banner err">No session token. Open the URL printed by <code>forkbridge-web</code> (it ends with <code>#token=…</code>). With <code>ng serve</code>, append the same <code>#token=…</code> to <code>http://localhost:4300/</code>.</div>
    }
    @if (api.busy(); as b) { <div class="banner busy"><span class="spin"></span>{{ b }}… @if (api.currentJob) { <button class="small" (click)="api.cancel()">cancel</button> }</div> }
    @if (api.error(); as e) {
      <div class="banner err" role="alert">
        <span style="flex: 1; min-width: 0">{{ showFull() || !errorLong() ? e : errorHead() }}
          @if (errorLong()) { <button class="small" (click)="showFull.set(!showFull())">{{ showFull() ? 'less' : 'details' }}</button> }</span>
        <button class="small" (click)="api.error.set(null)">dismiss</button>
      </div>
    }
    <main><router-outlet /></main>
  `,
})
export class App {
  api = inject(Api);
  /** Long errors show their first line; the rest is behind "details". */
  readonly showFull = signal(false);
  readonly errorHead = computed(() => { const e = this.api.error() ?? ''; const i = e.search(/[:.]\s|\n/); return (i > 0 && i < 200 ? e.slice(0, i + 1) : e.slice(0, 200)) + ' …'; });
  readonly errorLong = computed(() => (this.api.error() ?? '').length > 220);

  constructor() {
    // An error belongs to the screen that produced it: leaving that screen retires it.
    inject(Router).events.subscribe((ev) => { if (ev instanceof NavigationEnd) { this.api.error.set(null); this.showFull.set(false); } });
  }
}
