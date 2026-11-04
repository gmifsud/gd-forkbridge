import { Component, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api } from '../api';
import { StatusChip } from '../shared';

@Component({
  selector: 'fb-policy',
  imports: [FormsModule, StatusChip],
  template: `
    <div class="row" style="justify-content: space-between">
      <h2>Policy & Configuration Editor</h2>
      <div class="row">
        <button class="small" (click)="reloadFromDisk()" [disabled]="!!api.busy()">Reload from disk</button>
      </div>
    </div>

    @if (cfg(); as c) {
      <p class="muted small">Config file: <code>{{ c.sourceFile }}</code> · State dir: <code>{{ c.stateDir }}</code></p>

      @for (pair of c.pairs; track pair.name) {
        <section class="card">
          <div class="row" style="justify-content: space-between">
            <h3>Pair: <code>{{ pair.name }}</code></h3>
            <div class="row">
              <button class="small primary" (click)="confirmAllComponents(pair.name)" [disabled]="!!api.busy()">Confirm all components</button>
            </div>
          </div>

          <h4>Validation Profiles</h4>
          <div class="row">
            @for (vp of pair.validationProfiles; track vp.name) {
              <div class="card" style="margin: 4px 0; padding: 8px 12px">
                <b>{{ vp.name }}</b> · trusted: <span class="chip" [class.s-READY]="vp.trusted" [class.s-REVIEW_REQUIRED]="!vp.trusted">{{ vp.trusted ? 'yes' : 'no' }}</span>
                <button class="small" style="margin-left: 8px" (click)="toggleTrust(pair.name, vp.name, !vp.trusted)" [disabled]="!!api.busy()">
                  {{ vp.trusted ? 'Untrust' : 'Trust' }}
                </button>
              </div>
            }
          </div>

          <h4>Components</h4>
          <table class="tbl">
            <tr><th>ID</th><th>Status</th><th>Paths</th><th>Description</th><th>Action</th></tr>
            @for (comp of pair.components; track comp.id) {
              <tr>
                <td><code>{{ comp.id }}</code></td>
                <td><fb-status [status]="comp.status" /></td>
                <td>@for (pt of comp.paths; track pt) { <code>{{ pt }}</code> }</td>
                <td class="small muted">{{ comp.description }}</td>
                <td>
                  <div class="row" style="margin: 0; gap: 4px">
                    @if (comp.status !== 'confirmed') {
                      <button class="small primary" (click)="setComponentStatus(pair.name, comp.id, 'confirmed')" [disabled]="!!api.busy()">Confirm</button>
                    }
                    @if (comp.status !== 'proposed') {
                      <button class="small" (click)="setComponentStatus(pair.name, comp.id, 'proposed')" [disabled]="!!api.busy()">Proposed</button>
                    }
                  </div>
                </td>
              </tr>
            }
          </table>

          <h4>Quick Add Path Rule ({{ pair.name }})</h4>
          <div class="row">
            <select [(ngModel)]="ruleAction">
              <option value="add-ignored">Ignored (never transfer)</option>
              <option value="add-product-specific">Product-Specific (never overwrite)</option>
              <option value="add-target-pinned">Target-Pinned (preserve target version)</option>
            </select>
            @if (ruleAction !== 'add-ignored') {
              <select [(ngModel)]="ruleProduct">
                <option value="both">Both (qisl & isl)</option>
                <option value="qisl">qisl only</option>
                <option value="isl">isl only</option>
              </select>
            }
            <input style="flex: 1; min-width: 260px" [(ngModel)]="rulePattern" placeholder="Glob pattern, e.g. **/QiSL.Platform.Web.ISL.Host/** or pipelines/**">
            <button class="primary small" (click)="addRule(pair.name)" [disabled]="!rulePattern.trim() || !!api.busy()">Add Rule</button>
          </div>

          <div class="grid2" style="margin-top: 8px">
            <div>
              <h4>Ignored Globs ({{ pair.ignored?.length ?? 0 }})</h4>
              <div class="scroll">
                @for (g of pair.ignored; track g) {
                  <div class="row" style="margin: 2px 0; justify-content: space-between">
                    <code>{{ g }}</code>
                    <button class="small danger" (click)="removeGlob(pair.name, 'remove-ignored', '', g)">×</button>
                  </div>
                }
              </div>
            </div>
            <div>
              <h4>Target-Pinned & Product-Specific</h4>
              <div class="scroll">
                @for (prod of ['qisl', 'isl']; track prod) {
                  <div><b>productSpecific.{{ prod }}</b></div>
                  @for (g of pair.productSpecific?.[prod] ?? []; track g) {
                    <div class="row" style="margin: 2px 0; justify-content: space-between">
                      <code>{{ g }}</code>
                      <button class="small danger" (click)="removeGlob(pair.name, 'remove-product-specific', prod, g)">×</button>
                    </div>
                  }
                  <div><b>targetPinned.{{ prod }}</b></div>
                  @for (g of pair.targetPinned?.[prod] ?? []; track g) {
                    <div class="row" style="margin: 2px 0; justify-content: space-between">
                      <code>{{ g }}</code>
                      <button class="small danger" (click)="removeGlob(pair.name, 'remove-target-pinned', prod, g)">×</button>
                    </div>
                  }
                }
              </div>
            </div>
          </div>
        </section>
      }
    }

    <section class="card">
      <div class="row" style="justify-content: space-between">
        <h3>Raw <code>forkbridge.yml</code> Editor</h3>
        <button class="primary" (click)="saveYaml()" [disabled]="!!api.busy()">Save & Hot-Reload YAML</button>
      </div>
      <textarea class="code-editor" rows="26" [(ngModel)]="rawYaml"></textarea>
    </section>
  `,
})
export class PolicyPage implements OnInit {
  api = inject(Api);
  cfg = signal<any | null>(null);
  rawYaml = '';
  ruleAction = 'add-product-specific';
  ruleProduct = 'both';
  rulePattern = '';

  async ngOnInit() {
    await this.load();
  }

  async load() {
    const [c, raw] = await Promise.all([
      this.api.get<any>('/config'),
      this.api.get<{ sourceFile: string; yaml: string }>('/config/raw'),
    ]);
    this.cfg.set(c);
    this.rawYaml = raw.yaml;
  }

  async reloadFromDisk() {
    await this.api.run('Reloading config', () => this.api.post('/config/reload'));
    await this.load();
  }

  async saveYaml() {
    await this.api.run('Saving forkbridge.yml', () => this.api.post('/config/raw', { yaml: this.rawYaml }));
    await this.load();
  }

  async confirmAllComponents(pair: string) {
    await this.api.run('Confirming all components', () =>
      this.api.post('/config/policy-action', { pair, action: 'set-component-status', componentId: '*', status: 'confirmed' })
    );
    await this.load();
  }

  async setComponentStatus(pair: string, compId: string, status: string) {
    await this.api.run(`Setting ${compId} to ${status}`, () =>
      this.api.post('/config/policy-action', { pair, action: 'set-component-status', componentId: compId, status })
    );
    await this.load();
  }

  async toggleTrust(pair: string, profile: string, trusted: boolean) {
    await this.api.run(`Updating validation trust for ${profile}`, () =>
      this.api.post('/config/policy-action', { pair, action: 'set-validation-trusted', profile, trusted })
    );
    await this.load();
  }

  async addRule(pair: string) {
    const pat = this.rulePattern.trim();
    if (!pat) return;
    await this.api.run(`Adding ${pat}`, () =>
      this.api.post('/config/policy-action', {
        pair,
        action: this.ruleAction,
        product: this.ruleProduct,
        patterns: [pat],
      })
    );
    this.rulePattern = '';
    await this.load();
  }

  async removeGlob(pair: string, action: string, product: string, pat: string) {
    await this.api.run(`Removing ${pat}`, () =>
      this.api.post('/config/policy-action', {
        pair,
        action,
        product,
        patterns: [pat],
      })
    );
    await this.load();
  }
}
