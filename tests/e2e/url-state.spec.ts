import { expect, test } from './lib/page-health';

test('the module merges the named keys into the address, in place, and only on the page they belong to', { tag: '@lists' }, async ({ page }) => {
  const response = await page.goto('/health');
  expect(response?.status()).toBe(200);

  const result = await page.evaluate(`(async () => {
    const { apply } = await import('/scripts/url-state.js');
    history.replaceState({ blazor: 7 }, '', '/health?keep=1&q=old&status=active#part');
    const length = history.length;
    const address = () => location.pathname + location.search + location.hash;

    apply('/health', { q: 'ann lee', status: null, days: '30' });
    const merged = address();
    apply('/health', { q: 'ann lee' });
    const unchanged = address();
    apply('/elsewhere', { q: 'late', keep: null });
    const elsewhere = address();
    apply('/health', { q: '', days: null });
    const removed = address();

    return { merged, unchanged, elsewhere, removed, state: history.state, added: history.length - length };
  })()`);

  expect(result).toEqual({
    merged: '/health?keep=1&q=ann+lee&days=30#part',
    unchanged: '/health?keep=1&q=ann+lee&days=30#part',
    elsewhere: '/health?keep=1&q=ann+lee&days=30#part',
    removed: '/health?keep=1#part',
    state: { blazor: 7 },
    added: 0,
  });
});

test('a write reaches an address whose path is spelled with percent-escapes', { tag: '@lists' }, async ({ page }) => {
  const response = await page.goto('/health');
  expect(response?.status()).toBe(200);

  const result = await page.evaluate(`(async () => {
    const { apply } = await import('/scripts/url-state.js');
    history.replaceState(null, '', '/superadmin/error%6Cog?keep=1');
    apply('/superadmin/errorlog', { q: 'boom' });
    const decodedFromServer = location.pathname + location.search;
    history.replaceState(null, '', '/superadmin/errorlog?keep=1');
    apply('/superadmin/error%6Cog', { level: 'critical' });
    const escapedFromServer = location.pathname + location.search;
    apply('/superadmin/other', { q: 'late' });
    const elsewhere = location.pathname + location.search;
    return { decodedFromServer, escapedFromServer, elsewhere };
  })()`);

  expect(result).toEqual({
    decodedFromServer: '/superadmin/error%6Cog?keep=1&q=boom',
    escapedFromServer: '/superadmin/errorlog?keep=1&level=critical',
    elsewhere: '/superadmin/errorlog?keep=1&level=critical',
  });
});
