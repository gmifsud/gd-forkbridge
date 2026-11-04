/** Neutral unified-diff helpers shared by Harmony and Merge. No Angular imports: unit-tested with `node --test`. */

export interface DiffLine { text: string; cls: '' | 'dfile' | 'dhunk' | 'da' | 'dd'; }

/** Classifies each line of a unified diff for display (file header, hunk header, addition, deletion, context). */
export function classifyDiffLines(raw: string): DiffLine[] {
  if (!raw) return [];
  return raw.split('\n').map((text) => ({
    text,
    cls: text.startsWith('+++') || text.startsWith('---') ? 'dfile' : text.startsWith('@@') ? 'dhunk' : text.startsWith('+') ? 'da' : text.startsWith('-') ? 'dd' : '',
  }));
}

/** Counts added / removed lines (headers excluded). */
export function diffStat(raw: string): { added: number; removed: number } {
  let added = 0, removed = 0;
  for (const l of classifyDiffLines(raw)) { if (l.cls === 'da') added++; else if (l.cls === 'dd') removed++; }
  return { added, removed };
}

/** Reverses a unified diff (old ↔ new): swaps the file headers, the hunk ranges and the +/- lines. */
export function invertDiff(diff: string): string {
  const out = diff.split('\n').map((l) => {
    if (l.startsWith('--- ')) return '+++ ' + l.slice(4);
    if (l.startsWith('+++ ')) return '--- ' + l.slice(4);
    const h = /^@@ -(\S+) \+(\S+) @@(.*)$/.exec(l);
    if (h) return `@@ -${h[2]} +${h[1]} @@${h[3]}`;
    if (l.startsWith('+')) return '-' + l.slice(1);
    if (l.startsWith('-')) return '+' + l.slice(1);
    return l;
  });
  if (out.length > 1 && out[0].startsWith('+++ ') && out[1].startsWith('--- ')) [out[0], out[1]] = [out[1], out[0]];
  // Within each run of changed lines, list removals before additions (git's own order), keeping relative order.
  const res: string[] = [];
  for (let i = 0; i < out.length;) {
    const l = out[i];
    const changed = (x: string) => (x.startsWith('+') && !x.startsWith('+++ ')) || (x.startsWith('-') && !x.startsWith('--- '));
    if (!changed(l)) { res.push(l); i++; continue; }
    const run: string[] = [];
    while (i < out.length && changed(out[i])) run.push(out[i++]);
    res.push(...run.filter((x) => x.startsWith('-')), ...run.filter((x) => x.startsWith('+')));
  }
  return res.join('\n');
}
