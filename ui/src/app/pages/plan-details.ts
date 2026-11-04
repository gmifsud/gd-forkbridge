import { Component, computed, effect, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Drift, PathDecision, Plan, PlannedCommit } from '../api';
import {
  copyTextToClipboard,
  DiffRequestEvent,
  extractLibrary,
  FileTable,
  toLibraryGlob,
} from '../shared';

export interface AggregatedLibraryRow {
  library: string;
  glob: string;
  component: string;
  includedCount: number;
  excludedCount: number;
  blockedCount: number;
  divergedCount: number;
  differingCount: number;
  allPaths: string[];
}

export interface FlatChangeRow {
  commitSha: string;
  commitSubject: string;
  decision: PathDecision;
  library: string;
  glob: string;
}

/** Tabbed / modular Commit classification, Library summary, Component Drift, and Dependency Impact views. */
@Component({
  selector: 'fb-plan-details',
  imports: [FormsModule, FileTable],
  template: `
    @let p = plan();

    @if (section() === 'tabbed') {
      <nav class="tabs sub-tabs">
        <button type="button" [class.active]="activeSubTab() === 'commits'" (click)="activeSubTab.set('commits')">
          Changed Files &amp; Commits ({{ flatChanges().length }})
        </button>
        <button type="button" [class.active]="activeSubTab() === 'libraries'" (click)="activeSubTab.set('libraries')">
          Libraries / Subprojects ({{ aggregatedLibraries().length }})
        </button>
        <button type="button" [class.active]="activeSubTab() === 'drift'" (click)="activeSubTab.set('drift')">
          Component Drift ({{ p.drift.length }})
        </button>
        <button type="button" [class.active]="activeSubTab() === 'impact'" (click)="activeSubTab.set('impact')">
          Dependency Impact
        </button>
      </nav>
    }

    @if (effectiveSection() === 'libraries') {
      <section class="card">
        <div class="section-header-row">
          <div>
            <h3>Libraries / Subprojects Across Plan &amp; Drift ({{ filteredLibraries().length }})</h3>
            <p class="muted small">
              Exclude any unused library or subproject (e.g. <code>QiSL.App.WF/src/QiSL.App.WF.Application.Client</code>) from this plan or permanently in <code>forkbridge.yml</code>.
            </p>
          </div>
          <div class="row">
            <button type="button" class="small" (click)="copyLibraryList()">Copy Library Paths ({{ filteredLibraries().length }})</button>
            <button type="button" class="small" (click)="copyLibraryTableTsv()">Copy Table (TSV)</button>
            @if (copiedBanner()) { <span class="copied-badge">{{ copiedBanner() }}</span> }
          </div>
        </div>

        @if (interactive()) {
          <div class="custom-lib-bar">
            <input
              class="file-filter-input"
              [(ngModel)]="customPattern"
              placeholder="Enter library path or glob to exclude (e.g. QiSL.App.WF/src/QiSL.App.WF.Application.Client)..." />
            <button type="button" class="small primary" [disabled]="!customPattern().trim()" (click)="excludeCustomInPlan()">
              Exclude in Plan
            </button>
            <button type="button" class="small" [disabled]="!customPattern().trim()" (click)="policyCustom('add-ignored')">
              Policy: Add to Ignored
            </button>
            <button type="button" class="small" [disabled]="!customPattern().trim()" (click)="policyCustom('add-product-specific')">
              Policy: Product-Specific
            </button>
            <button type="button" class="small" [disabled]="!customPattern().trim()" (click)="policyCustom('add-target-pinned')">
              Policy: Target-Pin
            </button>
          </div>
        }

        <div class="file-toolbar">
          <input class="file-filter-input" [(ngModel)]="libSearch" placeholder="Filter libraries by name or component..." />
        </div>

        <div class="table-scroll">
          <table class="grid compact-grid">
            <thead>
              <tr>
                <th class="col-idx">#</th>
                <th>Component</th>
                <th>Library / Subproject</th>
                <th class="col-count">Included</th>
                <th class="col-count">Excluded</th>
                <th class="col-count">Blocked</th>
                <th class="col-count">Target Diverged</th>
                <th class="col-count">Differing</th>
                <th>Actions</th>
              </tr>
            </thead>
            <tbody>
              @for (lib of filteredLibraries(); track lib.library; let i = $index) {
                <tr>
                  <td class="col-idx">{{ i + 1 }}</td>
                  <td><span class="chip">{{ lib.component }}</span></td>
                  <td><code class="path-code">{{ lib.library }}</code></td>
                  <td class="col-count">{{ lib.includedCount }}</td>
                  <td class="col-count">{{ lib.excludedCount }}</td>
                  <td class="col-count">
                    @if (lib.blockedCount > 0) { <b class="warn">{{ lib.blockedCount }}</b> } @else { 0 }
                  </td>
                  <td class="col-count">{{ lib.divergedCount }}</td>
                  <td class="col-count">{{ lib.differingCount }}</td>
                  <td class="action-cell">
                    <button type="button" class="small" (click)="flashCopy(lib.library, 'Copied library path')">Copy Lib</button>
                    <button type="button" class="small" (click)="flashCopy(lib.allPaths.join('\n'), 'Copied ' + lib.allPaths.length + ' file(s)')">
                      Copy Files ({{ lib.allPaths.length }})
                    </button>
                    @if (interactive()) {
                      <button type="button" class="small primary" (click)="excludeLibraryRow(lib)">
                        Exclude Library in Plan
                      </button>
                      <button type="button" class="small" (click)="includeLibraryRow(lib)">
                        Include Library
                      </button>
                      <button type="button" class="small" (click)="policyAction.emit({ action: 'add-ignored', patterns: [lib.glob] })">
                        Policy: Ignore
                      </button>
                      <button type="button" class="small" (click)="policyAction.emit({ action: 'add-product-specific', patterns: [lib.glob] })">
                        Policy: Product-Specific
                      </button>
                      <button type="button" class="small" (click)="policyAction.emit({ action: 'add-target-pinned', patterns: [lib.glob] })">
                        Policy: Target-Pin
                      </button>
                    }
                  </td>
                </tr>
              } @empty {
                <tr><td colspan="9" class="muted">No libraries match the filter.</td></tr>
              }
            </tbody>
          </table>
        </div>
      </section>
    }

    @if (effectiveSection() === 'commits') {
      <section class="card">
        <div class="section-header-row">
          <div class="subtab-pills">
            <button type="button" class="pill-btn" [class.active]="commitsViewMode() === 'flat'" (click)="commitsViewMode.set('flat')">
              All Changed Files Table ({{ filteredFlatChanges().length }})
            </button>
            <button type="button" class="pill-btn" [class.active]="commitsViewMode() === 'by-commit'" (click)="commitsViewMode.set('by-commit')">
              Grouped by Commit ({{ allCommits().length }})
            </button>
          </div>
          <div class="row">
            <button type="button" class="small" (click)="copyChangedPaths()">Copy Paths ({{ filteredFlatChanges().length }})</button>
            <button type="button" class="small" (click)="copyChangedTableTsv()">Copy Table (TSV)</button>
            @if (copiedBanner()) { <span class="copied-badge">{{ copiedBanner() }}</span> }
          </div>
        </div>

        <div class="file-toolbar">
          <input class="file-filter-input" [(ngModel)]="changeSearch" placeholder="Filter changed files by path, library, reason, or commit..." />
          <select class="file-lib-select" [(ngModel)]="decisionFilter">
            <option value="">All decisions</option>
            <option value="Include">Include</option>
            <option value="Exclude">Exclude</option>
            <option value="Block">Block</option>
          </select>
          <select class="file-lib-select" [(ngModel)]="changeLibFilter">
            <option value="">All libraries ({{ changeLibraries().length }})</option>
            @for (l of changeLibraries(); track l) {
              <option [value]="l">{{ l }}</option>
            }
          </select>
          @if (interactive() && changeLibFilter()) {
            <button type="button" class="small primary" (click)="overridePaths.emit({ paths: [toGlob(changeLibFilter())], decision: 'Exclude' })">
              Exclude Selected Library in Plan
            </button>
            <button type="button" class="small" (click)="policyAction.emit({ action: 'add-ignored', patterns: [toGlob(changeLibFilter())] })">
              Policy: Ignore Selected Library
            </button>
          }
        </div>

        @if (commitsViewMode() === 'flat') {
          <div class="table-scroll">
            <table class="grid compact-grid">
              <thead>
                <tr>
                  <th class="col-idx">#</th>
                  <th>Commit</th>
                  <th>St</th>
                  <th>Library / Subproject</th>
                  <th>Source Path</th>
                  <th>Target Path</th>
                  <th>Decision</th>
                  <th>Reason</th>
                  <th>Actions</th>
                </tr>
              </thead>
              <tbody>
                @for (row of filteredFlatChanges(); track $index; let i = $index) {
                  <tr [class]="'d-' + row.decision.decision">
                    <td class="col-idx">{{ i + 1 }}</td>
                    <td><code [title]="row.commitSubject">{{ row.commitSha.slice(0, 10) }}</code></td>
                    <td>{{ row.decision.status }}</td>
                    <td><code class="lib-code">{{ row.library }}</code></td>
                    <td>
                      <code class="path-code clickable-path" (click)="emitFlatDiff(row)" title="Click to inspect actual file differences">
                        {{ row.decision.sourceOldPath ? row.decision.sourceOldPath + ' → ' : '' }}{{ row.decision.sourcePath }}
                      </code>
                    </td>
                    <td>@if (row.decision.targetPath) { <code class="path-code">{{ row.decision.targetPath }}</code> }</td>
                    <td><b>{{ row.decision.decision }}</b></td>
                    <td class="small">{{ row.decision.reason }}{{ row.decision.binary ? ' (binary)' : '' }}</td>
                    <td class="action-cell">
                      <button type="button" class="small primary" (click)="emitFlatDiff(row)" title="View actual differences between Source, Target, and Baseline">
                        Diff
                      </button>
                      <button type="button" class="small" (click)="flashCopy(row.decision.sourcePath, 'Copied path')">Copy</button>
                      @if (interactive()) {
                        @if (row.decision.decision !== 'Exclude') {
                          <button type="button" class="small" (click)="overridePath.emit({ path: row.decision.sourcePath, decision: 'Exclude' })">Exclude File</button>
                        }
                        @if (row.decision.decision !== 'Include') {
                          <button type="button" class="small" (click)="overridePath.emit({ path: row.decision.sourcePath, decision: 'Include' })">Include File</button>
                        }
                        <button type="button" class="small" (click)="overridePaths.emit({ paths: [row.glob, row.decision.sourcePath], decision: 'Exclude' })" title="Exclude library {{ row.library }}">
                          Exclude Lib
                        </button>
                      }
                    </td>
                  </tr>
                } @empty {
                  <tr><td colspan="9" class="muted">No changed files match the filter.</td></tr>
                }
              </tbody>
            </table>
          </div>
        } @else {
          @for (c of allCommits(); track c.sha) {
            <details class="commit" [open]="c.state === 'Pending' || c.state === 'Blocked'">
              <summary>
                <code>{{ c.sha.slice(0, 10) }}</code> <span class="chip s-{{ c.state }}">{{ c.state }}</span>
                @if (c.mixed) { <span class="chip s-REVIEW_REQUIRED">mixed</span> } @if (c.isMerge) { <span class="chip">merge</span> }
                {{ c.subject }} <span class="muted small">— {{ c.author }} ({{ c.paths.length }} files)</span>
              </summary>
              @if (c.stateEvidence) { <p class="muted small">{{ c.stateEvidence }}</p> }
              <div class="table-scroll">
                <table class="grid compact-grid">
                  <thead>
                    <tr>
                      <th class="col-idx">#</th>
                      <th>St</th>
                      <th>Library / Subproject</th>
                      <th>Source path</th>
                      <th>Target path</th>
                      <th>Class</th>
                      <th>Decision</th>
                      <th>Reason</th>
                      <th>Actions</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (d of filterCommitPaths(c); track $index; let idx = $index) {
                      <tr [class]="'d-' + d.decision">
                        <td class="col-idx">{{ idx + 1 }}</td>
                        <td>{{ d.status }}</td>
                        <td><code class="lib-code">{{ getLib(d.sourcePath) }}</code></td>
                        <td>
                          <code class="path-code clickable-path" (click)="emitCommitDiff(c, d)" title="Click to inspect actual file differences">
                            {{ d.sourceOldPath ? d.sourceOldPath + ' → ' : '' }}{{ d.sourcePath }}
                          </code>
                        </td>
                        <td>@if (d.targetPath) { <code class="path-code">{{ d.targetPath }}</code> }</td>
                        <td>{{ d.sourceClass }}</td>
                        <td><b>{{ d.decision }}</b></td>
                        <td class="small">{{ d.reason }}{{ d.binary ? ' (binary)' : '' }}</td>
                        <td class="action-cell">
                          <button type="button" class="small primary" (click)="emitCommitDiff(c, d)" title="View actual differences">
                            Diff
                          </button>
                          <button type="button" class="small" (click)="flashCopy(d.sourcePath, 'Copied path')">Copy</button>
                          @if (interactive()) {
                            @if (d.decision !== 'Exclude') {
                              <button type="button" class="small" (click)="overridePath.emit({ path: d.sourcePath, decision: 'Exclude' })">Exclude</button>
                            }
                            @if (d.decision !== 'Include') {
                              <button type="button" class="small" (click)="overridePath.emit({ path: d.sourcePath, decision: 'Include' })">Include</button>
                            }
                            <button type="button" class="small" (click)="overridePaths.emit({ paths: [toGlob(getLib(d.sourcePath)), d.sourcePath], decision: 'Exclude' })">
                              Exclude Lib
                            </button>
                          }
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            </details>
          }
        }
      </section>
    }

    @if (effectiveSection() === 'drift') {
      <section class="card">
        <h3>Existing Component Drift (Pinned Snapshots)</h3>
        <p class="muted small">Click any component row below to inspect its diverging, target-changed, or target-only files in an enumerated table and exclude unused libraries.</p>
        <div class="table-scroll">
          <table class="grid compact-grid">
            <thead>
              <tr>
                <th class="col-idx">#</th>
                <th>Component</th>
                <th>{{ p.fromProduct }}</th>
                <th>{{ p.toProduct }}</th>
                <th class="col-count">Identical</th>
                <th class="col-count">Differing</th>
                <th class="col-count">Target diverged</th>
                <th class="col-count">Source changed</th>
                <th class="col-count">Only source</th>
                <th class="col-count">Only target</th>
                <th class="col-count">Preserved</th>
                <th>Inspect</th>
              </tr>
            </thead>
            <tbody>
              @for (d of p.drift; track d.component; let i = $index) {
                <tr [class.row-active]="selectedDrift()?.component === d.component" (click)="selectDriftComponent(d)" style="cursor: pointer">
                  <td class="col-idx">{{ i + 1 }}</td>
                  <td><b>{{ d.component }}</b></td>
                  <td><code>{{ d.sourcePath }}</code></td>
                  <td>
                    <code>{{ d.targetPath }}</code>
                    @if (d.intentionallyAbsentInTarget) { <i>(intentionally absent)</i> }
                    @else if (!d.presentInTarget) { <b class="warn">(missing)</b> }
                  </td>
                  <td class="col-count">{{ d.identical }}</td>
                  <td class="col-count">{{ d.differing.length }}</td>
                  <td class="col-count">{{ d.targetDivergedSinceBaseline.length }}</td>
                  <td class="col-count">{{ d.sourceChangedSinceBaseline.length }}</td>
                  <td class="col-count">{{ d.onlyInSource.length }}</td>
                  <td class="col-count">{{ d.onlyInTarget.length }}</td>
                  <td class="col-count">{{ d.preservedProductDifferences.length }}</td>
                  <td>
                    <button type="button" class="small" [class.primary]="selectedDrift()?.component === d.component" (click)="selectDriftComponent(d); $event.stopPropagation()">
                      Inspect Files
                    </button>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>

        @if (selectedDrift(); as sd) {
          <div class="drift-inspector">
            <div class="section-header-row">
              <h4>Inspecting Component: <code>{{ sd.component }}</code></h4>
              <div class="subtab-pills">
                <button type="button" class="pill-btn" [class.active]="driftBucket() === 'diverged'" (click)="driftBucket.set('diverged')">
                  Target Diverged ({{ sd.targetDivergedSinceBaseline.length }})
                </button>
                <button type="button" class="pill-btn" [class.active]="driftBucket() === 'differing'" (click)="driftBucket.set('differing')">
                  All Differing ({{ sd.differing.length }})
                </button>
                <button type="button" class="pill-btn" [class.active]="driftBucket() === 'srcChanged'" (click)="driftBucket.set('srcChanged')">
                  Source Changed ({{ sd.sourceChangedSinceBaseline.length }})
                </button>
                <button type="button" class="pill-btn" [class.active]="driftBucket() === 'onlySrc'" (click)="driftBucket.set('onlySrc')">
                  Only in {{ p.fromProduct }} ({{ sd.onlyInSource.length }})
                </button>
                <button type="button" class="pill-btn" [class.active]="driftBucket() === 'onlyTgt'" (click)="driftBucket.set('onlyTgt')">
                  Only in {{ p.toProduct }} ({{ sd.onlyInTarget.length }})
                </button>
                <button type="button" class="pill-btn" [class.active]="driftBucket() === 'preserved'" (click)="driftBucket.set('preserved')">
                  Preserved ({{ sd.preservedProductDifferences.length }})
                </button>
              </div>
            </div>

            @if (activeDriftFiles().length === 0) {
              <p class="muted">No files in this category for <code>{{ sd.component }}</code>.</p>
            } @else {
              <fb-file-table
                [files]="activeDriftFiles()"
                [interactive]="interactive()"
                (viewDiff)="viewDiff.emit($event)"
                (excludePaths)="overridePaths.emit({ paths: $event, decision: 'Exclude' })"
                (includePaths)="overridePaths.emit({ paths: $event, decision: 'Include' })"
                (policyIgnore)="policyAction.emit({ action: 'add-ignored', patterns: $event })"
                (policyProductSpecific)="policyAction.emit({ action: 'add-product-specific', patterns: $event })"
                (policyTargetPinned)="policyAction.emit({ action: 'add-target-pinned', patterns: $event })" />
            }
          </div>
        }
      </section>
    }

    @if (effectiveSection() === 'impact') {
      @if (p.impact; as i) {
        <section class="card">
          <div class="section-header-row">
            <h3>Dependency Impact in <code>{{ p.toProduct }}</code></h3>
            <div class="subtab-pills">
              <button type="button" class="pill-btn" [class.active]="impactBucket() === 'affected'" (click)="impactBucket.set('affected')">
                Affected Projects ({{ i.affectedProjects.length }})
              </button>
              <button type="button" class="pill-btn" [class.active]="impactBucket() === 'shared'" (click)="impactBucket.set('shared')">
                Shared Consumers ({{ i.sharedConsumers.length }})
              </button>
              <button type="button" class="pill-btn" [class.active]="impactBucket() === 'product'" (click)="impactBucket.set('product')">
                Product Consumers ({{ i.productConsumers.length }})
              </button>
              <button type="button" class="pill-btn" [class.active]="impactBucket() === 'frontend'" (click)="impactBucket.set('frontend')">
                Frontend Consumers ({{ i.frontendConsumers.length }})
              </button>
            </div>
          </div>

          @if (activeImpactFiles().length === 0) {
            <p class="muted">None in this category.</p>
          } @else {
            <fb-file-table
              [files]="activeImpactFiles()"
              [interactive]="false"
              [enableDiff]="false"
              [showLibraryGrouping]="false" />
          }
        </section>
      } @else {
        <p class="muted">No dependency impact analysis available.</p>
      }
    }
  `,
})
export class PlanDetails {
  plan = input.required<Plan>();
  interactive = input<boolean>(false);
  section = input<'tabbed' | 'commits' | 'libraries' | 'drift' | 'impact'>('tabbed');

  overridePath = output<{ path: string; decision: 'Include' | 'Exclude' }>();
  overridePaths = output<{ paths: string[]; decision: 'Include' | 'Exclude' }>();
  policyAction = output<{ action: 'add-ignored' | 'add-product-specific' | 'add-target-pinned'; patterns: string[] }>();
  viewDiff = output<DiffRequestEvent>();

  activeSubTab = signal<'commits' | 'libraries' | 'drift' | 'impact'>('commits');
  commitsViewMode = signal<'flat' | 'by-commit'>('flat');

  changeSearch = signal('');
  decisionFilter = signal('');
  changeLibFilter = signal('');

  libSearch = signal('');
  customPattern = signal('');
  copiedBanner = signal('');

  selectedDriftComp = signal<string>('');
  driftBucket = signal<'diverged' | 'differing' | 'srcChanged' | 'onlySrc' | 'onlyTgt' | 'preserved'>('diverged');
  impactBucket = signal<'affected' | 'shared' | 'product' | 'frontend'>('affected');

  constructor() {
    effect(() => {
      const p = this.plan();
      if (p.drift.length > 0 && !this.selectedDriftComp()) {
        const firstWithFiles =
          p.drift.find((d) => d.targetDivergedSinceBaseline.length > 0 || d.differing.length > 0) ?? p.drift[0];
        this.selectedDriftComp.set(firstWithFiles.component);
      }
    });
  }

  effectiveSection = computed(() => {
    const s = this.section();
    return s === 'tabbed' ? this.activeSubTab() : s;
  });

  allCommits = computed<PlannedCommit[]>(() => {
    const p = this.plan();
    return p.reconciliation ? [...p.commits, p.reconciliation] : p.commits;
  });

  flatChanges = computed<FlatChangeRow[]>(() => {
    const rows: FlatChangeRow[] = [];
    for (const c of this.allCommits()) {
      for (const d of c.paths) {
        const lib = extractLibrary(d.sourcePath);
        rows.push({
          commitSha: c.sha,
          commitSubject: c.subject,
          decision: d,
          library: lib,
          glob: toLibraryGlob(lib),
        });
      }
    }
    return rows;
  });

  changeLibraries = computed(() => {
    const set = new Set<string>();
    for (const r of this.flatChanges()) set.add(r.library);
    return Array.from(set).sort();
  });

  filteredFlatChanges = computed(() => {
    const q = this.changeSearch().trim().toLowerCase();
    const dec = this.decisionFilter();
    const lib = this.changeLibFilter();
    return this.flatChanges().filter((r) => {
      if (dec && r.decision.decision !== dec) return false;
      if (lib && r.library !== lib) return false;
      if (q) {
        const hay = `${r.decision.sourcePath} ${r.decision.targetPath ?? ''} ${r.library} ${r.decision.reason} ${r.commitSha} ${r.commitSubject}`.toLowerCase();
        if (!hay.includes(q)) return false;
      }
      return true;
    });
  });

  aggregatedLibraries = computed<AggregatedLibraryRow[]>(() => {
    const p = this.plan();
    const map = new Map<string, {
      component: string;
      included: Set<string>;
      excluded: Set<string>;
      blocked: Set<string>;
      diverged: Set<string>;
      differing: Set<string>;
      all: Set<string>;
    }>();

    const ensure = (lib: string, comp: string) => {
      let item = map.get(lib);
      if (!item) {
        item = {
          component: comp || 'shared',
          included: new Set(),
          excluded: new Set(),
          blocked: new Set(),
          diverged: new Set(),
          differing: new Set(),
          all: new Set(),
        };
        map.set(lib, item);
      } else if ((!item.component || item.component === 'shared') && comp) {
        item.component = comp;
      }
      return item;
    };

    for (const r of this.flatChanges()) {
      const entry = ensure(r.library, r.decision.component ?? 'unclassified');
      entry.all.add(r.decision.sourcePath);
      if (r.decision.decision === 'Include') entry.included.add(r.decision.sourcePath);
      else if (r.decision.decision === 'Exclude') entry.excluded.add(r.decision.sourcePath);
      else if (r.decision.decision === 'Block') entry.blocked.add(r.decision.sourcePath);
    }

    for (const d of p.drift) {
      for (const f of d.targetDivergedSinceBaseline) {
        const lib = extractLibrary(f);
        const entry = ensure(lib, d.component);
        entry.diverged.add(f);
        entry.all.add(f);
      }
      for (const f of d.differing) {
        const lib = extractLibrary(f);
        const entry = ensure(lib, d.component);
        entry.differing.add(f);
        entry.all.add(f);
      }
      for (const f of d.onlyInTarget) {
        const lib = extractLibrary(f);
        const entry = ensure(lib, d.component);
        entry.all.add(f);
      }
    }

    return Array.from(map.entries())
      .map(([library, v]) => ({
        library,
        glob: toLibraryGlob(library),
        component: v.component,
        includedCount: v.included.size,
        excludedCount: v.excluded.size,
        blockedCount: v.blocked.size,
        divergedCount: v.diverged.size,
        differingCount: v.differing.size,
        allPaths: Array.from(v.all),
      }))
      .sort((a, b) => a.library.localeCompare(b.library));
  });

  filteredLibraries = computed(() => {
    const q = this.libSearch().trim().toLowerCase();
    if (!q) return this.aggregatedLibraries();
    return this.aggregatedLibraries().filter(
      (l) => l.library.toLowerCase().includes(q) || l.component.toLowerCase().includes(q)
    );
  });

  selectedDrift = computed<Drift | null>(() => {
    const p = this.plan();
    const id = this.selectedDriftComp();
    return p.drift.find((d) => d.component === id) ?? p.drift[0] ?? null;
  });

  activeDriftFiles = computed<string[]>(() => {
    const d = this.selectedDrift();
    if (!d) return [];
    switch (this.driftBucket()) {
      case 'diverged': return d.targetDivergedSinceBaseline;
      case 'differing': return d.differing;
      case 'srcChanged': return d.sourceChangedSinceBaseline;
      case 'onlySrc': return d.onlyInSource;
      case 'onlyTgt': return d.onlyInTarget;
      case 'preserved': return d.preservedProductDifferences;
    }
  });

  activeImpactFiles = computed<string[]>(() => {
    const i = this.plan().impact;
    if (!i) return [];
    switch (this.impactBucket()) {
      case 'affected': return i.affectedProjects;
      case 'shared': return i.sharedConsumers;
      case 'product': return i.productConsumers;
      case 'frontend': return i.frontendConsumers;
    }
  });

  getLib(path: string): string {
    return extractLibrary(path);
  }

  toGlob(lib: string): string {
    return toLibraryGlob(lib);
  }

  emitFlatDiff(row: FlatChangeRow) {
    this.viewDiff.emit({
      path: row.decision.sourcePath,
      commit: row.commitSha,
      files: this.filteredFlatChanges().map((r) => r.decision.sourcePath),
    });
  }

  emitCommitDiff(c: PlannedCommit, d: PathDecision) {
    this.viewDiff.emit({
      path: d.sourcePath,
      commit: c.sha,
      files: this.filterCommitPaths(c).map((x) => x.sourcePath),
    });
  }

  filterCommitPaths(c: PlannedCommit): PathDecision[] {
    const q = this.changeSearch().trim().toLowerCase();
    const dec = this.decisionFilter();
    const lib = this.changeLibFilter();
    return c.paths.filter((d) => {
      if (dec && d.decision !== dec) return false;
      const dLib = extractLibrary(d.sourcePath);
      if (lib && dLib !== lib) return false;
      if (q) {
        const hay = `${d.sourcePath} ${d.targetPath ?? ''} ${dLib} ${d.reason}`.toLowerCase();
        if (!hay.includes(q)) return false;
      }
      return true;
    });
  }

  selectDriftComponent(d: Drift) {
    this.selectedDriftComp.set(d.component);
    if (d.targetDivergedSinceBaseline.length > 0) this.driftBucket.set('diverged');
    else if (d.differing.length > 0) this.driftBucket.set('differing');
  }

  excludeLibraryRow(lib: AggregatedLibraryRow) {
    this.overridePaths.emit({ paths: [lib.glob, ...lib.allPaths], decision: 'Exclude' });
  }

  includeLibraryRow(lib: AggregatedLibraryRow) {
    this.overridePaths.emit({ paths: [lib.glob, ...lib.allPaths], decision: 'Include' });
  }

  excludeCustomInPlan() {
    const raw = this.customPattern().trim();
    if (!raw) return;
    const glob = toLibraryGlob(raw.replace(/\/+\*\*$/, ''));
    this.overridePaths.emit({ paths: [glob, raw], decision: 'Exclude' });
    this.customPattern.set('');
  }

  policyCustom(action: 'add-ignored' | 'add-product-specific' | 'add-target-pinned') {
    const raw = this.customPattern().trim();
    if (!raw) return;
    const glob = toLibraryGlob(raw.replace(/\/+\*\*$/, ''));
    this.policyAction.emit({ action, patterns: [glob] });
    this.customPattern.set('');
  }

  async flashCopy(text: string, msg: string) {
    if (await copyTextToClipboard(text)) {
      this.copiedBanner.set(msg);
      setTimeout(() => {
        if (this.copiedBanner() === msg) this.copiedBanner.set('');
      }, 2200);
    }
  }

  async copyChangedPaths() {
    const text = this.filteredFlatChanges().map((r) => r.decision.sourcePath).join('\n');
    await this.flashCopy(text, `Copied ${this.filteredFlatChanges().length} path(s)`);
  }

  async copyChangedTableTsv() {
    const header = '#\tCommit\tStatus\tLibrary\tSource Path\tTarget Path\tDecision\tReason';
    const rows = this.filteredFlatChanges().map(
      (r, i) =>
        `${i + 1}\t${r.commitSha.slice(0, 10)}\t${r.decision.status}\t${r.library}\t${r.decision.sourcePath}\t${r.decision.targetPath ?? ''}\t${r.decision.decision}\t${r.decision.reason}`
    );
    await this.flashCopy([header, ...rows].join('\n'), `Copied ${rows.length} row(s) as TSV`);
  }

  async copyLibraryList() {
    const text = this.filteredLibraries().map((l) => l.library).join('\n');
    await this.flashCopy(text, `Copied ${this.filteredLibraries().length} library path(s)`);
  }

  async copyLibraryTableTsv() {
    const header = '#\tComponent\tLibrary\tIncluded\tExcluded\tBlocked\tTarget Diverged\tDiffering';
    const rows = this.filteredLibraries().map(
      (l, i) =>
        `${i + 1}\t${l.component}\t${l.library}\t${l.includedCount}\t${l.excludedCount}\t${l.blockedCount}\t${l.divergedCount}\t${l.differingCount}`
    );
    await this.flashCopy([header, ...rows].join('\n'), `Copied ${rows.length} library row(s) as TSV`);
  }
}
