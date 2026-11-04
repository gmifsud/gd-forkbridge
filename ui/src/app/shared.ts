import { Component, computed, effect, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Finding, PlanFileDiff } from './api';

export interface FindingAction {
  type: 'exclude-files' | 'include-files' | 'override-finding' | 'policy-ignored' | 'policy-product-both' | 'policy-pinned' | 'policy-confirm-components';
  finding: Finding;
  paths?: string[];
}

export interface LibraryGroup {
  name: string;
  glob: string;
  files: string[];
}

export interface DiffRequestEvent {
  path: string;
  commit?: string;
  files?: string[];
}

export function extractLibrary(path: string): string {
  const norm = (path ?? '').replace(/\\/g, '/').replace(/^\/+|\/+$/g, '');
  if (!norm) return '(root)';
  const m1 = norm.match(/^(.+?\/(?:src|test|tests)\/[^/]+)\//i);
  if (m1) return m1[1];
  const m2 = norm.match(/^((?:src|test|tests)\/[^/]+)\//i);
  if (m2) return m2[1];
  const m3 = norm.match(/^((?:libs|apps|packages|projects)\/[^/]+)\//i);
  if (m3) return m3[1];
  const parts = norm.split('/');
  if (parts.length >= 3) return `${parts[0]}/${parts[1]}`;
  if (parts.length === 2) return parts[0];
  return '(root)';
}

export function toLibraryGlob(lib: string): string {
  if (!lib || lib === '(root)') return '*';
  return lib.endsWith('/**') ? lib : `${lib}/**`;
}

export function relativeToLibrary(path: string, lib: string): string {
  const norm = (path ?? '').replace(/\\/g, '/');
  if (!lib || lib === '(root)') return norm;
  if (norm.startsWith(lib + '/')) return norm.slice(lib.length + 1);
  return norm;
}

export function groupFilesByLibrary(files: string[]): LibraryGroup[] {
  const map = new Map<string, string[]>();
  for (const f of files) {
    const lib = extractLibrary(f);
    let list = map.get(lib);
    if (!list) {
      list = [];
      map.set(lib, list);
    }
    list.push(f);
  }
  return Array.from(map.entries())
    .map(([name, list]) => ({ name, glob: toLibraryGlob(name), files: list }))
    .sort((a, b) => a.name.localeCompare(b.name));
}

export async function copyTextToClipboard(text: string): Promise<boolean> {
  try {
    if (navigator?.clipboard?.writeText) {
      await navigator.clipboard.writeText(text);
      return true;
    }
  } catch {}
  try {
    const ta = document.createElement('textarea');
    ta.value = text;
    ta.style.position = 'fixed';
    ta.style.opacity = '0';
    document.body.appendChild(ta);
    ta.select();
    document.execCommand('copy');
    document.body.removeChild(ta);
    return true;
  } catch {
    return false;
  }
}

export function downloadTextFile(filename: string, content: string, mime = 'text/markdown;charset=utf-8') {
  const blob = new Blob([content], { type: mime });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

@Component({
  selector: 'fb-status',
  template: `<span class="chip" [class]="'chip s-' + status()">{{ status() }}</span>`,
})
export class StatusChip {
  status = input.required<string>();
}

@Component({
  selector: 'fb-sha',
  template: `<code class="sha" [title]="sha() ?? ''">{{ (sha() ?? '').slice(0, len()) }}</code>`,
})
export class Sha {
  sha = input<string | undefined | null>();
  len = input(10);
}

@Component({
  selector: 'fb-file-diff-modal',
  imports: [FormsModule],
  template: `
    @if (diff(); as d) {
      <div class="diff-modal-backdrop" (click)="close.emit()">
        <div class="diff-modal-panel" (click)="$event.stopPropagation()">
          <div class="diff-modal-header">
            <div class="diff-modal-title-wrap">
              <div class="row" style="margin: 0">
                <strong>File Diff Inspector</strong>
                @if (d.component) { <span class="chip">{{ d.component }}</span> }
                @if (d.decision) { <span class="chip" [class]="'chip s-' + (d.decision === 'Include' ? 'READY' : d.decision === 'Block' ? 'BLOCKED' : '')">Decision: {{ d.decision }}</span> }
                @if (d.identical) { <span class="chip s-READY">Identical Content</span> }
                @if (!d.existsInTarget) { <span class="chip s-REVIEW_REQUIRED">Absent in {{ toProduct() }}</span> }
                @if (!d.existsInSource) { <span class="chip s-REVIEW_REQUIRED">Absent in {{ fromProduct() }}</span> }
                @if (copiedMsg()) { <span class="copied-badge">{{ copiedMsg() }}</span> }
              </div>
              <div class="small muted" style="margin-top: 4px">
                <span><b>Library:</b> <code>{{ library() }}</code></span>
                · <span><b>{{ fromProduct() }}:</b> <code>{{ d.sourcePath }}</code></span>
                @if (d.targetPath !== d.sourcePath) {
                  · <span><b>{{ toProduct() }}:</b> <code>{{ d.targetPath }}</code></span>
                }
              </div>
              <div class="diff-commit-meta small muted">
                @if (d.sourceLastCommit) {
                  <div><b>Last {{ fromProduct() }} commit:</b> <code>{{ d.sourceLastCommit }}</code></div>
                }
                @if (d.targetLastCommit) {
                  <div><b>Last {{ toProduct() }} commit:</b> <code>{{ d.targetLastCommit }}</code></div>
                }
              </div>
            </div>

            <div class="diff-modal-actions">
              @if (fileList().length > 1) {
                <button type="button" class="small" [disabled]="currentIndex() <= 0" (click)="navigate(-1)">← Prev</button>
                <span class="small muted">{{ currentIndex() + 1 }} / {{ fileList().length }}</span>
                <button type="button" class="small" [disabled]="currentIndex() >= fileList().length - 1" (click)="navigate(1)">Next →</button>
              }
              <button type="button" class="small" (click)="copyActiveDiff()">Copy Diff</button>
              <button type="button" class="small" (click)="close.emit()">✕ Close</button>
            </div>
          </div>

          @if (interactive()) {
            <div class="diff-decision-bar">
              <span class="small muted"><b>Assess &amp; Decide:</b></span>
              <button type="button" class="small primary" (click)="includePaths.emit([d.sourcePath])">
                Include File in Plan
              </button>
              <button type="button" class="small danger" (click)="excludePaths.emit([d.sourcePath])">
                Exclude File in Plan
              </button>
              <button type="button" class="small" (click)="excludePaths.emit([libraryGlob(), d.sourcePath])" title="Exclude all files under {{ libraryGlob() }} in this plan">
                Exclude Library ({{ library() }}) in Plan
              </button>
              <button type="button" class="small" (click)="policyIgnore.emit([d.sourcePath])">
                Policy: Ignore File
              </button>
              <button type="button" class="small" (click)="policyIgnore.emit([libraryGlob()])">
                Policy: Ignore Library
              </button>
              <button type="button" class="small" (click)="policyProductSpecific.emit([libraryGlob()])">
                Policy: Product-Specific Library
              </button>
            </div>
          }

          <nav class="tabs sub-tabs" style="margin: 8px 0">
            <button type="button" [class.active]="mode() === 'targetToSource'" (click)="mode.set('targetToSource')">
              {{ toProduct() }} vs {{ fromProduct() }} Diff
              @if (d.targetToSourceDiff) { <span class="tab-badge">diff</span> }
            </button>
            <button type="button" [class.active]="mode() === 'baselineToTarget'" (click)="mode.set('baselineToTarget')">
              {{ toProduct() }} Divergence (Baseline → {{ toProduct() }})
              @if (d.baselineToTargetDiff) { <span class="tab-badge">changed</span> }
            </button>
            <button type="button" [class.active]="mode() === 'baselineToSource'" (click)="mode.set('baselineToSource')">
              {{ fromProduct() }} Changes (Baseline → {{ fromProduct() }})
              @if (d.baselineToSourceDiff) { <span class="tab-badge">changed</span> }
            </button>
            @if (d.commitDiff) {
              <button type="button" [class.active]="mode() === 'commitDiff'" (click)="mode.set('commitDiff')">
                Planned Commit Patch(es)
              </button>
            }
            <button type="button" [class.active]="mode() === 'sideBySide'" (click)="mode.set('sideBySide')">
              Side-by-Side Content
            </button>
          </nav>

          <div class="diff-modal-body">
            @if (d.isBinary) {
              <p class="warn">Binary file — textual diff is not applicable.</p>
            } @else if (mode() === 'sideBySide') {
              <div class="grid2">
                <div>
                  <h4>{{ toProduct() }} (Target Base): <code>{{ d.targetPath }}</code></h4>
                  <pre class="out diff-side-pre">{{ d.targetContent ?? '(file does not exist in target)' }}</pre>
                </div>
                <div>
                  <h4>{{ fromProduct() }} (Source Head): <code>{{ d.sourcePath }}</code></h4>
                  <pre class="out diff-side-pre">{{ d.sourceContent ?? '(file does not exist in source)' }}</pre>
                </div>
              </div>
            } @else {
              @if (!activeDiffText()) {
                <div class="empty-diff-note">
                  @if (mode() === 'targetToSource' && d.identical) {
                    <p><b>No differences between {{ toProduct() }} and {{ fromProduct() }}</b> — the file content is byte-identical at the current heads.</p>
                    @if (d.baselineToTargetDiff) {
                      <p class="muted small">Tip: Switch to the <b>{{ toProduct() }} Divergence (Baseline → {{ toProduct() }})</b> tab above to see what changed since the baseline.</p>
                    }
                  } @else {
                    <p class="muted">No differences in this comparison view ({{ modeLabel() }}).</p>
                  }
                </div>
              } @else {
                <pre class="diff diff-viewer-pre">@for (line of activeDiffLines(); track $index) {<span [class]="line.cls">{{ line.text }}
</span>}</pre>
              }
            }
          </div>
        </div>
      </div>
    }
  `,
})
export class FileDiffModal {
  diff = input<PlanFileDiff | null>(null);
  fromProduct = input<string>('source');
  toProduct = input<string>('target');
  interactive = input<boolean>(false);
  fileList = input<string[]>([]);

  close = output<void>();
  selectFile = output<string>();
  excludePaths = output<string[]>();
  includePaths = output<string[]>();
  policyIgnore = output<string[]>();
  policyProductSpecific = output<string[]>();

  mode = signal<'targetToSource' | 'baselineToTarget' | 'baselineToSource' | 'commitDiff' | 'sideBySide'>('targetToSource');
  copiedMsg = signal('');

  constructor() {
    effect(() => {
      const d = this.diff();
      if (!d) return;
      if (d.targetToSourceDiff) {
        this.mode.set('targetToSource');
      } else if (d.baselineToTargetDiff) {
        this.mode.set('baselineToTarget');
      } else if (d.commitDiff) {
        this.mode.set('commitDiff');
      } else if (d.baselineToSourceDiff) {
        this.mode.set('baselineToSource');
      } else {
        this.mode.set('targetToSource');
      }
    });
  }

  library = computed(() => extractLibrary(this.diff()?.sourcePath ?? ''));
  libraryGlob = computed(() => toLibraryGlob(this.library()));

  currentIndex = computed(() => {
    const d = this.diff();
    const list = this.fileList();
    if (!d || list.length === 0) return -1;
    let idx = list.indexOf(d.requestedPath);
    if (idx < 0) idx = list.indexOf(d.sourcePath);
    if (idx < 0) idx = list.indexOf(d.targetPath);
    return idx;
  });

  activeDiffText = computed(() => {
    const d = this.diff();
    if (!d) return '';
    switch (this.mode()) {
      case 'targetToSource': return d.targetToSourceDiff;
      case 'baselineToTarget': return d.baselineToTargetDiff;
      case 'baselineToSource': return d.baselineToSourceDiff;
      case 'commitDiff': return d.commitDiff;
      default: return '';
    }
  });

  modeLabel = computed(() => {
    switch (this.mode()) {
      case 'targetToSource': return `${this.toProduct()} vs ${this.fromProduct()}`;
      case 'baselineToTarget': return `Baseline → ${this.toProduct()}`;
      case 'baselineToSource': return `Baseline → ${this.fromProduct()}`;
      case 'commitDiff': return 'Planned Commit Patch';
      case 'sideBySide': return 'Side-by-Side Content';
    }
  });

  activeDiffLines = computed(() => {
    const raw = this.activeDiffText();
    if (!raw) return [];
    return raw.split('\n').map((text) => {
      let cls = '';
      if (text.startsWith('+++') || text.startsWith('---') || text.startsWith('diff --git')) cls = 'dfile';
      else if (text.startsWith('@@')) cls = 'dhunk';
      else if (text.startsWith('+')) cls = 'da';
      else if (text.startsWith('-')) cls = 'dd';
      else if (text.startsWith('commit ') || text.startsWith('Author:') || text.startsWith('Subject:')) cls = 'dh';
      return { text, cls };
    });
  });

  navigate(delta: number) {
    const list = this.fileList();
    const nextIdx = this.currentIndex() + delta;
    if (nextIdx >= 0 && nextIdx < list.length) {
      this.selectFile.emit(list[nextIdx]);
    }
  }

  async copyActiveDiff() {
    const d = this.diff();
    if (!d) return;
    const text =
      this.mode() === 'sideBySide'
        ? `=== ${this.toProduct()}: ${d.targetPath} ===\n${d.targetContent ?? ''}\n\n=== ${this.fromProduct()}: ${d.sourcePath} ===\n${d.sourceContent ?? ''}`
        : this.activeDiffText();
    if (await copyTextToClipboard(text || '(no diff)')) {
      this.copiedMsg.set('Copied diff!');
      setTimeout(() => this.copiedMsg.set(''), 2000);
    }
  }
}

@Component({
  selector: 'fb-file-table',
  imports: [FormsModule],
  template: `
    <div class="fb-file-table-wrap">
      <div class="file-toolbar">
        <input class="file-filter-input" [(ngModel)]="searchQuery" placeholder="Filter files or libraries..." />
        @if (libraries().length > 1) {
          <select class="file-lib-select" [(ngModel)]="selectedLib">
            <option value="">All libraries ({{ libraries().length }})</option>
            @for (lib of libraries(); track lib.name) {
              <option [value]="lib.name">{{ lib.name }} ({{ lib.files.length }})</option>
            }
          </select>
        }
        <button type="button" class="small" (click)="copyAllPaths()">Copy Paths ({{ filteredRows().length }})</button>
        <button type="button" class="small" (click)="copyTableTsv()">Copy Table (TSV)</button>
        @if (libraries().length > 0 && showLibraryGrouping()) {
          <button type="button" class="small" (click)="showLibsPanel.set(!showLibsPanel())">
            {{ showLibsPanel() ? 'Hide' : 'Show' }} Libraries ({{ libraries().length }})
          </button>
        }
        @if (copiedMsg()) {
          <span class="copied-badge">{{ copiedMsg() }}</span>
        }
      </div>

      @if (showLibraryGrouping() && showLibsPanel() && libraries().length > 0) {
        <div class="lib-group-box">
          <div class="lib-group-header">
            <strong>Detected Libraries / Subprojects ({{ libraries().length }})</strong>
            <span class="muted">Exclude an unused library (e.g. Application.Client) in this plan or permanently in policy</span>
          </div>
          <div class="table-scroll">
            <table class="grid compact-grid lib-table">
              <thead>
                <tr>
                  <th class="col-idx">#</th>
                  <th>Library / Subproject</th>
                  <th class="col-count">Files</th>
                  <th>Actions</th>
                </tr>
              </thead>
              <tbody>
                @for (lib of libraries(); track lib.name; let idx = $index) {
                  <tr [class.row-active]="selectedLib() === lib.name">
                    <td class="col-idx">{{ idx + 1 }}</td>
                    <td><code class="copyable-code">{{ lib.name }}</code></td>
                    <td class="col-count"><span class="badge-count">{{ lib.files.length }}</span></td>
                    <td class="action-cell">
                      <button type="button" class="small" (click)="toggleLibFilter(lib.name)">
                        {{ selectedLib() === lib.name ? 'Show All' : 'Filter' }}
                      </button>
                      <button type="button" class="small" (click)="copySingle(lib.name, 'Copied library path')">Copy Lib</button>
                      <button type="button" class="small" (click)="copySingle(lib.files.join('\n'), 'Copied ' + lib.files.length + ' path(s)')">Copy Files</button>
                      @if (interactive()) {
                        <button type="button" class="small primary" (click)="onExcludeLib(lib)" title="Exclude all files under {{ lib.glob }} in this plan">
                          Exclude Library in Plan
                        </button>
                        <button type="button" class="small" (click)="policyIgnore.emit([lib.glob])" title="Add {{ lib.glob }} to ignored in forkbridge.yml">
                          Policy: Ignore Library
                        </button>
                        <button type="button" class="small" (click)="policyProductSpecific.emit([lib.glob])" title="Add {{ lib.glob }} to productSpecific in forkbridge.yml">
                          Policy: Product-Specific
                        </button>
                        <button type="button" class="small" (click)="policyTargetPinned.emit([lib.glob])" title="Add {{ lib.glob }} to targetPinned in forkbridge.yml">
                          Policy: Target-Pin
                        </button>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </div>
      }

      <div class="table-scroll">
        <table class="grid compact-grid file-enum-table">
          <thead>
            <tr>
              <th class="col-idx">#</th>
              <th>Library / Subproject</th>
              <th>File Path</th>
              <th>Actions</th>
            </tr>
          </thead>
          <tbody>
            @for (row of visibleRows(); track row.path; let i = $index) {
              <tr>
                <td class="col-idx">{{ i + 1 }}</td>
                <td><code class="lib-code" [title]="row.lib">{{ row.lib }}</code></td>
                <td>
                  <code class="path-code clickable-path" [title]="'Click to view diff for ' + row.path" (click)="onViewDiff(row.path)">{{ row.path }}</code>
                </td>
                <td class="action-cell">
                  @if (enableDiff()) {
                    <button type="button" class="small primary" (click)="onViewDiff(row.path)" title="View actual differences between Source, Target, and Baseline">
                      Diff
                    </button>
                  }
                  <button type="button" class="small" (click)="copySingle(row.path, 'Copied path')">Copy</button>
                  @if (interactive()) {
                    <button type="button" class="small" (click)="includePaths.emit([row.path])" title="Include this file in this plan">Include</button>
                    <button type="button" class="small" (click)="excludePaths.emit([row.path])" title="Exclude this file in this plan">Exclude File</button>
                    <button type="button" class="small" (click)="excludePaths.emit([row.glob, row.path])" title="Exclude entire library {{ row.lib }} in this plan">Exclude Lib</button>
                    <button type="button" class="small" (click)="policyIgnore.emit([row.path])" title="Add file to ignored in forkbridge.yml">Policy: Ignore</button>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>

      @if (filteredRows().length > pageSize() && !showAll()) {
        <div class="file-table-footer">
          <span class="muted">Showing {{ pageSize() }} of {{ filteredRows().length }} files.</span>
          <button type="button" class="small" (click)="showAll.set(true)">Show all {{ filteredRows().length }} files</button>
        </div>
      } @else if (showAll() && filteredRows().length > pageSize()) {
        <div class="file-table-footer">
          <button type="button" class="small" (click)="showAll.set(false)">Show first {{ pageSize() }} files</button>
        </div>
      }
    </div>
  `,
})
export class FileTable {
  files = input.required<string[]>();
  interactive = input<boolean>(false);
  enableDiff = input<boolean>(true);
  showLibraryGrouping = input<boolean>(true);
  pageSize = input<number>(100);

  excludePaths = output<string[]>();
  includePaths = output<string[]>();
  policyIgnore = output<string[]>();
  policyProductSpecific = output<string[]>();
  policyTargetPinned = output<string[]>();
  viewDiff = output<DiffRequestEvent>();

  searchQuery = signal('');
  selectedLib = signal('');
  showLibsPanel = signal(true);
  showAll = signal(false);
  copiedMsg = signal('');

  libraries = computed(() => groupFilesByLibrary(this.files()));

  rows = computed(() =>
    this.files().map((path) => {
      const lib = extractLibrary(path);
      return {
        path,
        lib,
        glob: toLibraryGlob(lib),
        rel: relativeToLibrary(path, lib),
      };
    })
  );

  filteredRows = computed(() => {
    const q = this.searchQuery().trim().toLowerCase();
    const lib = this.selectedLib();
    return this.rows().filter((r) => {
      if (lib && r.lib !== lib) return false;
      if (q && !r.path.toLowerCase().includes(q) && !r.lib.toLowerCase().includes(q)) return false;
      return true;
    });
  });

  visibleRows = computed(() => {
    const all = this.filteredRows();
    return this.showAll() ? all : all.slice(0, this.pageSize());
  });

  toggleLibFilter(lib: string) {
    this.selectedLib.set(this.selectedLib() === lib ? '' : lib);
  }

  onExcludeLib(lib: LibraryGroup) {
    this.excludePaths.emit([lib.glob, ...lib.files]);
  }

  onViewDiff(path: string) {
    if (!this.enableDiff()) return;
    this.viewDiff.emit({
      path,
      files: this.filteredRows().map((r) => r.path),
    });
  }

  async copySingle(text: string, label: string) {
    if (await copyTextToClipboard(text)) {
      this.flashCopied(label);
    }
  }

  async copyAllPaths() {
    const text = this.filteredRows().map((r) => r.path).join('\n');
    if (await copyTextToClipboard(text)) {
      this.flashCopied(`Copied ${this.filteredRows().length} path(s)`);
    }
  }

  async copyTableTsv() {
    const header = '#\tLibrary\tFile Path';
    const lines = this.filteredRows().map((r, i) => `${i + 1}\t${r.lib}\t${r.path}`);
    if (await copyTextToClipboard([header, ...lines].join('\n'))) {
      this.flashCopied(`Copied ${lines.length} row(s) as TSV`);
    }
  }

  private flashCopied(msg: string) {
    this.copiedMsg.set(msg);
    setTimeout(() => {
      if (this.copiedMsg() === msg) this.copiedMsg.set('');
    }, 2200);
  }
}

@Component({
  selector: 'fb-findings',
  imports: [FormsModule, FileTable],
  template: `
    @if (findings().length === 0) {
      <p class="muted">No findings.</p>
    } @else {
      <div class="findings-filter-bar">
        <div class="subtab-pills">
          <button type="button" class="pill-btn" [class.active]="filterMode() === 'all'" (click)="filterMode.set('all')">
            All ({{ findings().length }})
          </button>
          <button type="button" class="pill-btn" [class.active]="filterMode() === 'blocking'" (click)="filterMode.set('blocking')">
            Blocking ({{ blockingCount() }})
          </button>
          <button type="button" class="pill-btn" [class.active]="filterMode() === 'review'" (click)="filterMode.set('review')">
            Review Required ({{ reviewCount() }})
          </button>
          <button type="button" class="pill-btn" [class.active]="filterMode() === 'info'" (click)="filterMode.set('info')">
            Drift &amp; Info ({{ infoCount() }})
          </button>
        </div>
        <input class="finding-search-input" [(ngModel)]="searchText" placeholder="Search findings by title, code, component, or file path..." />
      </div>

      @if (filtered().length === 0) {
        <p class="muted">No findings match the current filter.</p>
      }

      @for (f of filtered(); track f.id) {
        <details class="finding" [class.block]="f.blocking" [class.review]="!f.blocking && f.requiresReview" [open]="f.blocking || filtered().length <= 3">
          <summary>
            <span class="gate">{{ f.blocking ? 'BLOCKS' : f.requiresReview ? (accepted().includes(f.id) ? 'ACCEPTED' : 'REVIEW') : f.severity }}</span>
            <span class="ftitle">{{ f.title }}</span>
            @if (f.component) { <span class="chip">{{ f.component }}</span> }
            @if (f.files.length) { <span class="chip">{{ f.files.length }} file(s)</span> }
            <code class="fid">{{ f.id }}</code>
          </summary>
          <dl>
            <dt>Category</dt><dd>{{ f.category }} · {{ f.severity }} · {{ f.origin }}</dd>
            @if (f.component) { <dt>Component</dt><dd>{{ f.component }}</dd> }
            @if (f.commits.length) {
              <dt>Commits</dt>
              <dd class="commit-badges">
                @for (c of f.commits; track c) { <code>{{ c.slice(0, 10) }}</code> }
              </dd>
            }
            <dt>Evidence</dt><dd><ul>@for (e of f.evidence; track $index) { <li>{{ e }}</li> }</ul></dd>
            @if (f.suggestedAction) { <dt>Action</dt><dd>{{ f.suggestedAction }}</dd> }
            @if (interactive()) {
              <dt>Resolve</dt>
              <dd class="row">
                @if (f.files.length) {
                  <button type="button" class="small primary" (click)="action.emit({ type: 'exclude-files', finding: f })">
                    Exclude All {{ f.files.length }} File(s) in Plan
                  </button>
                  <button type="button" class="small" (click)="action.emit({ type: 'include-files', finding: f })">
                    {{ f.code === 'MODIFY_DELETE' ? 'Restore All File(s) in Target' : 'Force Include All File(s)' }}
                  </button>
                  <button type="button" class="small" (click)="action.emit({ type: 'policy-ignored', finding: f })">
                    Policy: Ignore All ({{ f.files.length }})
                  </button>
                  <button type="button" class="small" (click)="action.emit({ type: 'policy-product-both', finding: f })">
                    Policy: Product-Specific (Both)
                  </button>
                  <button type="button" class="small" (click)="action.emit({ type: 'policy-pinned', finding: f })">
                    Policy: Target-Pinned
                  </button>
                }
                @if (f.blocking) {
                  <button type="button" class="small" (click)="action.emit({ type: 'override-finding', finding: f })">
                    Override &amp; Unblock Finding
                  </button>
                }
                @if (f.code === 'PROPOSED_COMPONENTS') {
                  <button type="button" class="small primary" (click)="action.emit({ type: 'policy-confirm-components', finding: f })">
                    Policy: Confirm Component(s)
                  </button>
                }
              </dd>
            }
            @if (f.files.length) {
              <dt>Files ({{ f.files.length }})</dt>
              <dd class="files-table-dd">
                <fb-file-table
                  [files]="f.files"
                  [interactive]="interactive()"
                  (viewDiff)="viewDiff.emit($event)"
                  (excludePaths)="action.emit({ type: 'exclude-files', finding: f, paths: $event })"
                  (includePaths)="action.emit({ type: 'include-files', finding: f, paths: $event })"
                  (policyIgnore)="action.emit({ type: 'policy-ignored', finding: f, paths: $event })"
                  (policyProductSpecific)="action.emit({ type: 'policy-product-both', finding: f, paths: $event })"
                  (policyTargetPinned)="action.emit({ type: 'policy-pinned', finding: f, paths: $event })" />
              </dd>
            }
          </dl>
        </details>
      }
    }
  `,
})
export class Findings {
  findings = input.required<Finding[]>();
  accepted = input<string[]>([]);
  interactive = input<boolean>(false);
  action = output<FindingAction>();
  viewDiff = output<DiffRequestEvent>();

  filterMode = signal<'all' | 'blocking' | 'review' | 'info'>('all');
  searchText = signal('');

  sorted = computed(() =>
    [...this.findings()].sort(
      (a, b) =>
        Number(b.blocking) - Number(a.blocking) ||
        Number(b.requiresReview) - Number(a.requiresReview) ||
        a.code.localeCompare(b.code)
    )
  );

  blockingCount = computed(() => this.findings().filter((f) => f.blocking).length);
  reviewCount = computed(() => this.findings().filter((f) => !f.blocking && f.requiresReview).length);
  infoCount = computed(() => this.findings().filter((f) => !f.blocking && !f.requiresReview).length);

  filtered = computed(() => {
    const mode = this.filterMode();
    const q = this.searchText().trim().toLowerCase();
    return this.sorted().filter((f) => {
      if (mode === 'blocking' && !f.blocking) return false;
      if (mode === 'review' && (f.blocking || !f.requiresReview)) return false;
      if (mode === 'info' && (f.blocking || f.requiresReview)) return false;
      if (q) {
        const hitTitle = f.title.toLowerCase().includes(q) || f.code.toLowerCase().includes(q) || f.id.toLowerCase().includes(q);
        const hitComp = (f.component ?? '').toLowerCase().includes(q);
        const hitFile = f.files.some((x) => x.toLowerCase().includes(q));
        if (!hitTitle && !hitComp && !hitFile) return false;
      }
      return true;
    });
  });
}
