import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { Api } from './api';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <header>
      <span class="brand">ForkBridge</span>
      <nav>
        <a routerLink="/" routerLinkActive="on" [routerLinkActiveOptions]="{ exact: true }">New plan</a>
        <a routerLink="/runs" routerLinkActive="on">History</a>
        <a routerLink="/discover" routerLinkActive="on">Discovery</a>
      </nav>
      <span class="local">local · loopback only</span>
    </header>
    @if (!api.token()) {
      <div class="banner err">No session token. Open the URL printed by <code>forkbridge-web</code> (it ends with <code>#token=…</code>). With <code>ng serve</code>, append the same <code>#token=…</code> to <code>http://localhost:4300/</code>.</div>
    }
    @if (api.busy(); as b) { <div class="banner busy"><span class="spin"></span>{{ b }}… @if (api.currentJob) { <button class="small" (click)="api.cancel()">cancel</button> }</div> }
    @if (api.error(); as e) { <div class="banner err">{{ e }} <button class="small" (click)="api.error.set(null)">dismiss</button></div> }
    <main><router-outlet /></main>
  `,
})
export class App {
  api = inject(Api);
}
