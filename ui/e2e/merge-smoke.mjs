// Browser smoke test of the Merge tab against a live forkbridge-web serving the merge demo
// (examples/demo/New-ForkBridgeMergeDemo.ps1). Playwright is NOT a project dependency: run with a Playwright
// installation available to Node (e.g. `npm i -g playwright && npx playwright install chromium`).
//
//   NODE_PATH=$(npm root -g) FB_URL=http://127.0.0.1:5199 FB_TOKEN=<hex> [FB_SHOTS=<dir>] node ui/e2e/merge-smoke.mjs
//
// Covers: navigation, repository/branch selection with provenance, analysis outcome + filters, session start,
// editor shortcut isolation, draft save (Ctrl+S), reload restoration, stale-save error from a second tab,
// mark resolved, keep-modified and binary choices, finalize, trusted validation, approval and publication.
import { createRequire } from 'node:module';
import assert from 'node:assert/strict';

// Resolved through CommonJS so a global installation works (NODE_PATH=$(npm root -g)).
const { chromium } = createRequire(import.meta.url)('playwright');

const url = process.env.FB_URL ?? 'http://127.0.0.1:5199';
const token = process.env.FB_TOKEN;
const shots = process.env.FB_SHOTS;
if (!token) throw new Error('FB_TOKEN is required');

const browser = await chromium.launch();
const ctx = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
const page = await ctx.newPage();
const errors = [];
page.on('pageerror', (e) => errors.push(e.message));
const shot = async (p, name) => { if (shots) await p.screenshot({ path: `${shots}/${name}.png`, fullPage: false }); };
const step = (s) => console.log('✓', s);

await page.goto(`${url}/#token=${token}`);
await page.getByRole('link', { name: 'Merge', exact: true }).click();
await page.waitForURL(/\/merge$/);
step('navigation: Merge tab');

await page.selectOption('select[aria-label="Repository"]', 'demo-shop');
await page.getByRole('button', { name: 'Refresh refs', exact: true }).click();
// Searchable lookup: type to narrow, pick with Enter (keyboard) or a click; scrolling never selects.
const pickRef = async (id, terms, ref) => {
  const box = page.locator(`#${id}`);
  await box.click();
  await box.fill(terms);
  await page.locator(`#${id}-list li[data-ref="${ref}"]`).waitFor();
  await page.locator(`#${id}-list li[data-ref="${ref}"]`).click();
  await page.waitForFunction(([i, v]) => document.getElementById(i)?.value === v, [id, ref.split(':')[1]]);
};
await page.locator('#merge-source:not([disabled])').waitFor();
await page.locator('#merge-source').click();
assert.ok(await page.locator('#merge-source-list li.hdr', { hasText: 'REMOTE (FETCHED BY FORKBRIDGE)' }).count() > 0);
await page.keyboard.press('Escape');
await pickRef('merge-source', 'checkout', 'Remote:feature/checkout');
// keyboard path: type, Enter picks the first match
await page.locator('#merge-target').click();
await page.keyboard.type('develop');
await page.locator('#merge-target-list li.active[data-ref="Remote:develop"]').waitFor();
await page.keyboard.press('Enter');
await page.waitForFunction(() => document.getElementById('merge-target')?.value === 'develop');
assert.match(await page.locator('.prov').first().innerText(), /remote \(fetched by ForkBridge\)/);
step('selection with provenance');

await page.getByRole('button', { name: 'Analyze merge' }).click();
await page.getByTestId('outcome').waitFor();
assert.equal(await page.getByTestId('outcome').innerText(), 'Conflicts need resolution');
assert.match(await page.getByTestId('analysis').innerText(), /Merging feature\/checkout @ [0-9a-f]{10} into develop @ [0-9a-f]{10}/);
await page.locator('[data-filter="auto"]').click();
await page.waitForFunction(() => document.querySelectorAll('tr[data-entry]').length === 1);
assert.deepEqual(await page.locator('tr[data-entry]').evaluateAll((rs) => rs.map((r) => r.getAttribute('data-entry'))), ['src/appsettings.json']);
await page.locator('[data-filter="all"]').click();
await page.locator('tr[data-entry="src/Checkout.cs"]').waitFor();
await shot(page, '1-analysis');
step('analysis outcome, header SHAs and filters');

await page.getByTestId('start-session').click();
await page.waitForURL(/\/merge\/sessions\/msess-/);
await page.getByTestId('unresolved-count').waitFor();
assert.equal(await page.getByTestId('unresolved-count').innerText(), '3');
const sessionUrl = page.url();
step('session created');

await page.locator('tr[data-entry="src/Greeting.cs"]').click();
const editor = page.locator('textarea[aria-label="Merge result editor"]');
await editor.waitFor();
assert.match(await editor.inputValue(), /<<<<<<< /);
// Keys typed in the editor are text, not shortcuts.
await editor.focus();
await page.keyboard.press('End');
await page.keyboard.type('jk');
await page.keyboard.press('ArrowRight');
assert.match(await editor.inputValue(), /jk/);
assert.match(await page.locator('.diff-modal-panel').innerText(), /Path:\s*src\/Greeting\.cs/);
const merged = 'namespace Shop;\n\npublic static class Greeting\n{\n    public static string Hello(string name) => $"Welcome to the shop, {name} - good to see you!";\n}\n';
await editor.fill(merged);
await page.keyboard.press('Control+s');
await page.locator('.diff-modal-panel .chip', { hasText: 'Draft saved' }).waitFor();
await shot(page, '2-inspector-draft');
step('editor shortcut isolation + draft saved with Ctrl+S');

await page.reload();
await editor.waitFor();
assert.equal(await editor.inputValue(), merged);
assert.ok(await page.locator('.diff-modal-panel .chip', { hasText: 'Draft saved' }).isVisible());
step('reload restores the open file and the server-side draft');

// A second tab saves first; the first tab's save is rejected, not merged silently.
const page2 = await ctx.newPage();
await page2.goto(`${page.url()}#token=${token}`); // sessionStorage (token) is per tab
const editor2 = page2.locator('textarea[aria-label="Merge result editor"]');
await editor2.waitFor();
await editor2.fill(merged.replace('good to see you', 'from tab two'));
await page2.keyboard.press('Control+s');
await page2.locator('.diff-modal-panel .chip', { hasText: 'Draft saved' }).waitFor();
await page2.locator('.diff-modal-panel .chip', { hasText: 'unsaved edits' }).waitFor({ state: 'detached' });
await editor.fill(merged.replace('good to see you', 'from tab one'));
await page.keyboard.press('Control+s');
const alert = page.locator('.diff-modal-panel [role="alert"]');
await alert.waitFor();
assert.match(await alert.innerText(), /changed since you loaded it/);
await shot(page, '3-stale-save');
await page.getByRole('button', { name: 'Reload file' }).click();
await page.waitForFunction(() => document.querySelector('textarea[aria-label="Merge result editor"]')?.value.includes('from tab two'));
await page2.close();
step('stale save from another tab is refused with a reload action');

await page.getByRole('button', { name: 'Mark resolved' }).click();
await page.locator('.diff-modal-panel .chip', { hasText: 'Resolved' }).waitFor();
await page.getByRole('button', { name: '✕ Close' }).click();
step('text conflict marked resolved');

await page.locator('tr[data-entry="src/Legacy.cs"]').click();
await page.locator('[data-act="KeepModified"]').click();
await page.locator('.diff-modal-panel .chip', { hasText: 'Resolved' }).waitFor();
await page.getByRole('button', { name: '✕ Close' }).click();
await page.locator('tr[data-entry="assets/logo.bin"]').click();
assert.equal(await page.locator('textarea[aria-label="Merge result editor"]').count(), 0);
await page.locator('[data-act="TakeSource"]').click();
await page.locator('.diff-modal-panel .chip', { hasText: 'Resolved' }).waitFor();
await page.keyboard.press('Escape');
await page.waitForFunction(() => document.querySelector('[data-testid="unresolved-count"]')?.textContent?.trim() === '0');
step('modify/delete kept and binary source chosen');

await page.getByTestId('finalize').click();
await page.getByTestId('candidate').waitFor();
const candidate = (await page.getByTestId('candidate').innerText()).trim();
assert.match(candidate, /^[0-9a-f]{40}$/);
await page.getByLabel('Trust validation commands for this run').check();
await page.getByTestId('validate').click();
await page.getByTestId('validation').waitFor();
assert.match(await page.getByTestId('validation').innerText(), /^PASSED/);
await page.getByTestId('approve').click();
await page.locator('.step', { hasText: 'Approved by' }).waitFor();
await page.getByTestId('publish').click();
await page.locator('.step .ok', { hasText: 'Pushed' }).waitFor();
assert.equal(await page.getByTestId('session-state').innerText(), 'Published');
await shot(page, '4-published');
step('finalize → validate → approve → publish');

assert.deepEqual(errors, []);
console.log(JSON.stringify({ sessionUrl, candidate }));
await browser.close();
