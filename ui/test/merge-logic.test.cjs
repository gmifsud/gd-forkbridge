// Pure UI logic of the Merge tab and the shared diff/keyboard helpers. Run: npm test (tsc + node --test, no extra deps).
const test = require('node:test');
const assert = require('node:assert/strict');
const L = require('../out-spec/app/merge/merge-logic.js');
const D = require('../out-spec/app/shared/diff/diff-lines.js');
const K = require('../out-spec/app/shared/keyboard.js');

const caps = (o = {}) => ({ supported: true, editText: false, takeTarget: false, takeSource: false, keepModified: false, delete: false, restriction: 'None', ...o });
const entry = (id, path, status, conflict = 'None', c = caps(), binary = false) => ({ id, path, status, conflict, binary, messages: [], relatedPaths: [], capabilities: c });
const plan = [
  entry('a', 'src/incoming.txt', 'Incoming'),
  entry('b', 'src/auto.txt', 'AutoMerged'),
  entry('c', 'src/conflict.txt', 'Conflicted', 'Content', caps({ editText: true, takeTarget: true, takeSource: true })),
  entry('d', 'src/gone.txt', 'Conflicted', 'DeletedInSource', caps({ keepModified: true, delete: true })),
  entry('e', 'src/renamed.txt', 'Conflicted', 'RenameDelete', caps({ supported: false, reason: 'not supported' })),
  entry('f', 'img/logo.png', 'Conflicted', 'Binary', caps({ takeTarget: true, takeSource: true }), true),
];

test('analysis rows: conflicts are unresolved, unsupported is explicit', () => {
  const r = L.rows(plan);
  assert.deepEqual(r.map((x) => x.resolution), ['clean', 'clean', 'unresolved', 'unresolved', 'unsupported', 'unresolved']);
});

test('session state overrides analysis state; filters count correctly', () => {
  const session = [
    { entryId: 'c', resolution: 'resolved' }, { entryId: 'd', resolution: 'draft' }, { entryId: 'e', resolution: 'unsupported' }, { entryId: 'f', resolution: 'unresolved' },
  ];
  const r = L.rows(plan, session);
  const c = L.counts(r);
  assert.deepEqual(c, { all: 6, unresolved: 2, resolved: 1, auto: 1, special: 3 });
  assert.deepEqual(r.filter((x) => L.matches(x, 'unresolved')).map((x) => x.id), ['d', 'f']); // drafts are still unresolved
  assert.deepEqual(r.filter((x) => L.matches(x, 'special')).map((x) => x.id), ['d', 'e', 'f']);
});

test('path query matches every word, case-insensitively', () => {
  const r = L.rows(plan);
  assert.deepEqual(r.filter((x) => L.matches(x, 'all', 'SRC conf')).map((x) => x.id), ['c']);
  assert.equal(r.filter((x) => L.matches(x, 'all', 'nothing-here')).length, 0);
});

test('labels name source and target explicitly', () => {
  assert.equal(L.conflictLabel('DeletedInSource'), 'Modified in target, deleted in source');
  assert.equal(L.conflictLabel('DeletedInTarget'), 'Deleted in target, modified in source');
  assert.equal(L.namespaceLabel('Local'), 'local branch (your checkout)');
  assert.equal(L.outcomeLabel('UpToDate').label, 'Already up to date');
  assert.match(L.outcomeLabel('FastForward').text, /no fast-forward/);
  assert.equal(L.resolutionLabel('draft'), 'Draft saved (not resolved)');
});

test('stale-save errors are recognised and get a reload hint', () => {
  const h = L.errorHint('The session changed since you loaded it (revision 3 → 4), possibly in another tab. (exit 13)');
  assert.equal(h.stale, true);
  assert.match(h.text, /click Reload file/);
  assert.doesNotMatch(h.text, /exit 13/);
  assert.equal(L.errorHint('boom (exit 20)').stale, false);
  assert.equal(L.isDirty('a', 'a'), false);
  assert.equal(L.isDirty('a', 'b'), true);
});

test('shared diff helpers classify, count and invert', () => {
  const diff = '--- target\n+++ result\n@@ -1,2 +1,2 @@\n ctx\n-old\n+new';
  assert.deepEqual(D.classifyDiffLines(diff).map((l) => l.cls), ['dfile', 'dfile', 'dhunk', '', 'dd', 'da']);
  assert.deepEqual(D.diffStat(diff), { added: 1, removed: 1 });
  assert.equal(D.invertDiff(diff), '--- result\n+++ target\n@@ -1,2 +1,2 @@\n ctx\n-new\n+old');
  assert.deepEqual(D.classifyDiffLines(''), []);
});

test('editor keystrokes never trigger inspector shortcuts', () => {
  const k = (target, mods = {}) => ({ key: 'ArrowRight', ctrlKey: false, metaKey: false, altKey: false, target, ...mods });
  assert.equal(K.shortcutAllowed(k({ tagName: 'TEXTAREA' })), false);
  assert.equal(K.shortcutAllowed(k({ tagName: 'INPUT' })), false);
  assert.equal(K.shortcutAllowed(k({ tagName: 'DIV', isContentEditable: true })), false);
  assert.equal(K.shortcutAllowed(k({ tagName: 'DIV' }, { ctrlKey: true })), false);
  assert.equal(K.shortcutAllowed(k({ tagName: 'DIV' })), true);
  assert.equal(K.shortcutAllowed(k(null)), true);
});
