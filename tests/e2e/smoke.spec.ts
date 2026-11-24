import type { Locator, Page } from '@playwright/test';
import { errorLogEntry, persona, personaKeys, readFoundationState, type FoundationErrorLogEntry } from './lib/foundation-state';
import { journeyStep, loadJourney } from './lib/journey-contract';
import { expect, test } from './lib/page-health';
import { openTenantDialog } from './lib/platform-console';
import { expectSettledPageAccessible, gotoReady, loginAs, waitForReady } from './lib/session';
import { shot } from './lib/shots';

const chip = (page: Page, label: string): Locator => page.getByRole('button', { name: new RegExp(`^${label}:`) });

async function choose(page: Page, label: string, option: string): Promise<void> {
  await chip(page, label).click();
  const listbox = page.getByRole('listbox', { name: label, exact: true });
  await expect(listbox).toBeVisible();
  await listbox.getByRole('option', { name: option, exact: true }).click();
  await expect(chip(page, label)).toHaveAccessibleName(`${label}: ${option}`);
}

const queryValue = (page: Page, key: string): string | null => new URL(page.url()).searchParams.get(key);

async function expectRows(page: Page, rows: { shown: FoundationErrorLogEntry[]; hidden: FoundationErrorLogEntry[] }): Promise<void> {
  const message = (entry: FoundationErrorLogEntry) => page.locator('.error-log-message__text').getByText(entry.message, { exact: true });
  for (const entry of rows.shown) await expect(message(entry), `the error log lists "${entry.message}"`).toBeVisible();
  for (const entry of rows.hidden) await expect(message(entry), `the error log leaves out "${entry.message}"`).toHaveCount(0);
}

async function followLink(page: Page, path: string): Promise<void> {
  await page.evaluate((target) => (window as unknown as { Blazor: { navigateTo(url: string): void } }).Blazor.navigateTo(target), path);
  await expect(page).toHaveURL((url) => url.pathname + url.search === path);
}

const journey = loadJourney('platform-owner-sign-in');

test('the app reports healthy with its database reachable', { tag: ['@platform', '@smoke'] }, async ({ request }) => {
  const response = await request.get('/health');

  expect(response.status()).toBe(200);
  expect(await response.text()).toBe('Healthy');
});

test('the platform owner signs in and reaches the platform console', { tag: ['@platform', '@smoke'] }, async ({ page }) => {
  const owner = persona(personaKeys.platformOwner);

  await journeyStep(journey, 'sign-in', async () => {
    await gotoReady(page, '/Account/Login');
    await shot(page, journey.id, '01-login');
    await loginAs(page, owner);
  });

  await journeyStep(journey, 'open-console', async () => {
    await gotoReady(page, '/superadmin/overview');
    await expect(page.getByRole('heading', { name: 'Platform overview', level: 1 })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Impersonate User' })).toBeVisible();
    await shot(page, journey.id, '02-platform-console');
  });

  await page.getByRole('button', { name: 'Impersonate User' }).click();
  const dialog = page.getByRole('dialog', { name: 'Impersonate User' });
  await expect(dialog).toBeVisible();
  await expect
    .poll(() => dialog.evaluate((element) => element.contains(document.activeElement)), { message: 'the focus is inside the dialog' })
    .toBe(true);
  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();

  const criticalToday = errorLogEntry('criticalToday');
  const errorToday = errorLogEntry('errorToday');
  const errorLongAgo = errorLogEntry('errorLongAgo');
  const unmatchedErrorToday = errorLogEntry('unmatchedErrorToday');
  const searchWord = readFoundationState().errorLog.search;

  await journeyStep(journey, 'filter-survives-reload', async () => {
    await gotoReady(page, '/superadmin/errorlog?ref=smoke');
    await expectRows(page, { shown: [criticalToday, errorToday, unmatchedErrorToday], hidden: [errorLongAgo] });

    await choose(page, 'Level', 'Error');
    await expectRows(page, { shown: [errorToday, unmatchedErrorToday], hidden: [criticalToday, errorLongAgo] });
    await choose(page, 'Period', 'Last 30 days');
    await expectRows(page, { shown: [errorToday, unmatchedErrorToday, errorLongAgo], hidden: [criticalToday] });
    const search = page.getByRole('searchbox', { name: 'Search' });
    await search.fill(searchWord);
    await search.press('Tab');
    await expectRows(page, { shown: [errorToday, errorLongAgo], hidden: [criticalToday, unmatchedErrorToday] });
    await expect.poll(() => [queryValue(page, 'level'), queryValue(page, 'days'), queryValue(page, 'q'), queryValue(page, 'ref')], { message: 'the filters are in the address' })
      .toEqual(['error', '30', searchWord, 'smoke']);
    await shot(page, 'url-state', '01-error-log-filtered');

    await page.reload();
    await waitForReady(page);
    await expectSettledPageAccessible(page);
    await expect(chip(page, 'Level')).toHaveAccessibleName('Level: Error');
    await expect(chip(page, 'Period')).toHaveAccessibleName('Period: Last 30 days');
    await expect(page.getByRole('searchbox', { name: 'Search' })).toHaveValue(searchWord);
    await expectRows(page, { shown: [errorToday, errorLongAgo], hidden: [criticalToday, unmatchedErrorToday] });
    expect([queryValue(page, 'level'), queryValue(page, 'days'), queryValue(page, 'q'), queryValue(page, 'ref')]).toEqual(['error', '30', searchWord, 'smoke']);
    await shot(page, 'url-state', '02-error-log-after-reload');

    await choose(page, 'Level', 'All');
    await expect.poll(() => page.url(), { message: 'the default level is left out of the address' }).not.toContain('level=');
    expect(queryValue(page, 'days')).toBe('30');
    await expectRows(page, { shown: [criticalToday, errorToday, errorLongAgo], hidden: [unmatchedErrorToday] });
  });

  await journeyStep(journey, 'link-to-other-filters', async () => {
    await followLink(page, '/superadmin/errorlog?level=critical&days=30');
    await expect(chip(page, 'Level')).toHaveAccessibleName('Level: Critical');
    await expect(page.getByRole('searchbox', { name: 'Search' })).toHaveValue('');
    await expectRows(page, { shown: [criticalToday], hidden: [errorToday, errorLongAgo, unmatchedErrorToday] });

    await followLink(page, '/superadmin/errorlog?level=error');
    await expect(chip(page, 'Level')).toHaveAccessibleName('Level: Error');
    await expect(chip(page, 'Period')).toHaveAccessibleName('Period: Last 7 days');
    await expectRows(page, { shown: [errorToday, unmatchedErrorToday], hidden: [criticalToday, errorLongAgo] });
    await expect.poll(() => [queryValue(page, 'level'), queryValue(page, 'days')], { message: 'the address is the link\'s' }).toEqual(['error', null]);
    await expectSettledPageAccessible(page);
    await shot(page, 'url-state', '03-error-log-after-link');
  });

  await journeyStep(journey, 'dialog-in-address', async () => {
    await openTenantDialog(page, readFoundationState().tenantAdministrator.tenant);
    const view = page.getByRole('dialog');
    await expect.poll(() => queryValue(page, 'dlg'), { message: 'the open tenant is named in the address' }).toMatch(/^tenant-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/);
    await shot(page, 'url-state', '04-tenant-dialog-in-address');

    await page.keyboard.press('Escape');
    await expect(view).toBeHidden();
    await expect.poll(() => queryValue(page, 'dlg'), { message: 'a closed dialog leaves the address' }).toBeNull();
  });
});
