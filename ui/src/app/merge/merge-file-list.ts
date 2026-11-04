import { Component, computed, input, output } from '@angular/core';
import { counts, FILTERS, matches, MergeFilter, resolutionLabel, Row, statusLabel } from './merge-logic';

/** Changed-file / conflict list with the five filters. Each row shows path (+ old path), what Git did, what can be
 *  done here, the resolution state and the reason. Selecting a row opens the inspector (when a session exists). */
@Component({
  selector: 'fb-merge-file-list',
  styles: [`
    .bar { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; margin: 6px 0; }
    .q { min-width: 240px; flex: 1; max-width: 420px; }
    td.path code { background: none; padding: 0; color: var(--ink); }
    .old { display: block; font-size: 11px; color: var(--faint); }
    .r-unresolved { background: var(--warnbg); color: var(--warn); } .r-draft { background: var(--warnbg); color: var(--accent-soft); }
    .r-resolved, .r-clean { background: var(--okbg); color: var(--ok); } .r-unsupported { background: var(--badbg); color: var(--bad); }
    tr.sel td { background: var(--infobg); }
    tr.clickable { cursor: pointer; }
    .why { color: var(--muted); font-size: 12px; max-width: 420px; }
    .caps { font-size: 11px; color: var(--faint); white-space: nowrap; }
  `],
  template: `
    <div class="bar" role="toolbar" aria-label="File filters">
      @for (f of filters; track f.key) {
        <button type="button" class="pill-btn" [class.active]="filter() === f.key" (click)="filterChange.emit(f.key)" [attr.data-filter]="f.key">
          {{ f.label }} <span class="badge-count">{{ tally()[f.key] }}</span></button>
      }
      <input class="q" type="search" placeholder="Filter by path…" [value]="query()" (input)="queryChange.emit($any($event.target).value)" aria-label="Filter by path" />
    </div>
    <div class="table-scroll">
      <table class="tbl compact-grid" aria-label="Changed files">
        <thead><tr><th>Path</th><th>Change</th><th>Can do here</th><th>State</th><th>Why</th><th></th></tr></thead>
        <tbody>
          @for (r of visible(); track r.id) {
            <tr [class.sel]="r.id === selected()" [class.clickable]="openable()" (click)="openable() && open.emit(r.id)" [attr.data-entry]="r.path">
              <td class="path"><code>{{ r.path }}</code>@if (r.oldPath) { <span class="old">was {{ r.oldPath }}</span> }</td>
              <td class="nowrap">{{ status(r) }}</td>
              <td class="caps">{{ caps(r) }}</td>
              <td><span class="chip" [class]="'chip r-' + r.resolution">{{ resLabel(r.resolution) }}</span></td>
              <td class="why">{{ r.entry.capabilities.reason }}</td>
              <td>@if (openable()) { <button type="button" class="small" (click)="$event.stopPropagation(); open.emit(r.id)">Inspect</button> }</td>
            </tr>
          } @empty { <tr><td colspan="6" class="muted">No files match this filter.</td></tr> }
        </tbody>
      </table>
    </div>
  `,
})
export class MergeFileList {
  readonly rows = input<Row[]>([]);
  readonly filter = input<MergeFilter>('all');
  readonly query = input('');
  readonly selected = input<string | null>(null);
  readonly openable = input(true);
  readonly filterChange = output<MergeFilter>();
  readonly queryChange = output<string>();
  readonly open = output<string>();

  readonly filters = FILTERS;
  readonly tally = computed(() => counts(this.rows()));
  readonly visible = computed(() => this.rows().filter((r) => matches(r, this.filter(), this.query())));
  status = statusLabel;
  resLabel = resolutionLabel;

  caps(r: Row): string {
    const c = r.entry.capabilities;
    if (r.status !== 'Conflicted') return 'review';
    if (!c.supported) return 'blocked';
    return [c.editText && 'edit', c.takeTarget && 'target', c.takeSource && 'source', c.keepModified && 'keep', c.delete && 'delete'].filter(Boolean).join(' · ');
  }
}
