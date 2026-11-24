import type { Locator, Page } from '@playwright/test';
import { persona, personaKeys, personaName, readFoundationState } from './lib/foundation-state';
import { journeyStep, loadJourney } from './lib/journey-contract';
import { expect, test } from './lib/page-health';
import { endImpersonation, expectSettledPageAccessible, gotoReady, impersonate, loginAs, waitForReady, waitForSettled } from './lib/session';
import { shot } from './lib/shots';

const journey = loadJourney('tenant-administrator-finds-in-lists');

const queryValue = (page: Page, key: string): string | null => new URL(page.url()).searchParams.get(key);

const row = (page: Page, name: string): Locator => page.locator('tr.list-row').filter({ has: page.locator('td[data-label="Name"]', { hasText: name }) });

const names = (page: Page): Promise<string[]> =>
  page.locator('tr.list-row td[data-label="Name"]').evaluateAll((cells) =>
    cells.map((cell) =>
      (cell.querySelector('.list-row__open')?.textContent
        ?? Array.from(cell.childNodes)
          .filter((node) => !(node instanceof Element && node.classList.contains('list-cell__label')))
          .map((node) => node.textContent ?? '')
          .join(''))
        .trim(),
    ),
  );

const chip = (page: Page, label: string): Locator => page.getByRole('button', { name: new RegExp(`^${label}:`) });

async function choose(page: Page, label: string, option: string): Promise<void> {
  await chip(page, label).click();
  const listbox = page.getByRole('listbox', { name: label, exact: true });
  await expect(listbox).toBeVisible();
  await listbox.getByRole('option', { name: option, exact: true }).click();
  await expect(listbox).toBeHidden();
}

async function unrolled<T>(page: Page, capture: () => Promise<T>): Promise<T> {
  const style = await page.addStyleTag({
    content: '.app-shell { height: auto !important; min-height: 100dvh; overflow: visible !important; } .app-shell .app-shell__body { overflow: visible !important; }',
  });
  try {
    return await capture();
  } finally {
    await style.evaluate((element) => element.remove());
  }
}

async function expectNoSidewaysScroll(page: Page, name: string, width: number): Promise<void> {
  const overflowing = await page.evaluate(() =>
    [document.documentElement, ...Array.from(document.querySelectorAll<HTMLElement>('.app-shell .app-shell__body'))]
      .filter((element) => element.scrollWidth > element.clientWidth)
      .map((element) => `${element.tagName.toLowerCase()}.${element.className}: ${element.scrollWidth}px wide in ${element.clientWidth}px`),
  );
  expect(overflowing, `${name} scrolls sideways at ${width} px`).toEqual([]);
}

async function shootBothWidths(page: Page, name: string): Promise<void> {
  for (const viewport of [{ width: 1500, height: 900 }, { width: 1280, height: 800 }, { width: 390, height: 844 }]) {
    await page.setViewportSize(viewport);
    await waitForSettled(page);
    await expectNoSidewaysScroll(page, name, viewport.width);
    await unrolled(page, () => shot(page, 'lists', `${name}-${viewport.width}`));
  }
  await page.setViewportSize({ width: 1280, height: 800 });
  await waitForSettled(page);
}

async function closeDialog(page: Page): Promise<void> {
  const dialog = page.getByRole('dialog');
  await page.keyboard.press('Escape');
  const closed = await dialog.waitFor({ state: 'hidden', timeout: 1500 }).then(() => true, () => false);
  if (!closed) await dialog.getByRole('button', { name: 'Close', exact: true }).first().click();
  await expect(dialog).toBeHidden();
}

test.use({ viewport: { width: 1280, height: 800 } });

test('a tenant administrator finds people and units in lists', { tag: ['@lists', '@admin'] }, async ({ page }) => {
  test.setTimeout(180_000);
  const state = readFoundationState();
  const sam = state.impersonationTarget;
  const alex = state.tenantAdministrator;
  const robin = state.tenantViewer;

  await journeyStep(journey, 'impersonate-administrator', async () => {
    await loginAs(page, persona(personaKeys.platformOwner, state));
    await impersonate(page, alex);
  });

  await journeyStep(journey, 'search-users', async () => {
    await gotoReady(page, '/tenant/management/users');
    const showcaseStaffMembers = ['Ada Holm', 'Ben Carter', 'Chloe Park', 'Dev Patel', 'Erin Walsh', 'Felix Grant'];
    const people = [alex.name, robin.name, sam.name, ...[personaKeys.coordinator, personaKeys.staff, personaKeys.secondStaff].map((key) => personaName(persona(key, state))), ...showcaseStaffMembers].sort((a, b) => a.localeCompare(b));
    expect(await names(page), 'the list opens with Company A\'s people and no system account').toEqual(people);
    await shootBothWidths(page, '00-users');
    const search = page.getByRole('searchbox', { name: 'Search' });
    await search.fill('Sam');
    await search.press('Enter');
    await expect(row(page, alex.name), 'the search leaves out Alex Morgan').toHaveCount(0);
    expect(await names(page)).toEqual([sam.name]);
    await expect.poll(() => queryValue(page, 'q'), { message: 'the search is in the address' }).toBe('Sam');
    await expect(page.locator('#users-summary')).toHaveText('1–1 of 1 user');
  });

  await journeyStep(journey, 'reload-keeps-search', async () => {
    await page.reload();
    await waitForReady(page);
    await expectSettledPageAccessible(page);
    await expect(page.getByRole('searchbox', { name: 'Search' })).toHaveValue('Sam');
    expect(await names(page), 'F5 shows the same row, read from the address').toEqual([sam.name]);
  });

  await journeyStep(journey, 'read-user-summary', async () => {
    await expect(row(page, sam.name).getByRole('button')).toHaveCount(1);
    await expect(row(page, sam.name).getByRole('button', { name: sam.name, exact: true })).toBeVisible();
    await expectSettledPageAccessible(page);
    await shootBothWidths(page, '01-users-searched');
  });

  await journeyStep(journey, 'open-user', async () => {
    const name = row(page, sam.name).getByRole('button', { name: sam.name, exact: true });
    await name.focus();
    await expect(name).toBeFocused();
    await page.keyboard.press('Enter');
    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible();
    await expect(dialog.locator('[name="user-first-name"]')).toHaveValue(sam.name.split(' ')[0]);
    await expect(dialog.locator('.state-entity-dialog-header__title')).toBeFocused();
    await expectSettledPageAccessible(page);
    for (const viewport of [{ width: 1500, height: 900 }, { width: 1280, height: 800 }, { width: 390, height: 844 }]) {
      await page.setViewportSize(viewport);
      await waitForSettled(page);
      await expectSettledPageAccessible(page);
      await shot(page, 'dialogs', `user-dialog-${viewport.width}`);
    }
    await page.setViewportSize({ width: 1280, height: 800 });
    await waitForSettled(page);
    await dialog.locator('.state-entity-dialog-header__title').focus();
    await page.keyboard.press('Escape');
    await expect(dialog).toBeHidden();
    expect(await names(page), 'the list reads its page again and keeps its search').toEqual([sam.name]);
    await expect(row(page, sam.name).getByRole('button', { name: sam.name, exact: true }), 'the row\'s name has the focus again').toBeFocused();
  });

  await journeyStep(journey, 'find-users-on-a-phone', async () => {
    await page.setViewportSize({ width: 390, height: 844 });
    await gotoReady(page, '/tenant/management/users');
    await waitForSettled(page);
    await expectNoSidewaysScroll(page, 'the user list', 390);

    const filters = page.getByRole('button', { name: /^Filters/ });
    await expect(filters).toHaveAttribute('aria-expanded', 'false');
    await expect(page.getByRole('searchbox', { name: 'Search' })).toBeVisible();
    await expect(page.getByRole('group', { name: 'Status', exact: true }), 'the quick filter stays in view').toBeVisible();
    await expect(chip(page, 'Sort')).toBeHidden();
    const first = page.locator('tr.list-row').first();
    const box = await first.boundingBox();
    expect(box, 'the first row is laid out').not.toBeNull();
    expect(box!.y + box!.height, 'the first row is above the fold').toBeLessThanOrEqual(844);
    await expectSettledPageAccessible(page);
    await shot(page, 'lists', '04-users-phone-filters-folded-390');

    await filters.click();
    await expect(filters).toHaveAttribute('aria-expanded', 'true');
    await expect(chip(page, 'Group')).toBeVisible();
    const sort = chip(page, 'Sort');
    await sort.focus();
    await page.keyboard.press('Tab');
    await expect(page.locator('tr.list-row').first().locator('.list-row__open'), 'Tab from the sort reaches the first row').toBeFocused();
    const inHeadings = await page.evaluate(() => document.activeElement?.closest('thead') !== null);
    expect(inHeadings, 'the focus is never in the hidden headings').toBe(false);

    await choose(page, 'Group', 'Status');
    await expect(page.locator('.list-heading__label')).toHaveText(['Active']);
    await expect(page.getByRole('button', { name: 'Filters (1)' }), 'the button counts the choice made').toBeVisible();
    await expectSettledPageAccessible(page);
    await unrolled(page, () => shot(page, 'lists', '05-users-phone-filters-unfolded-390'));

    await choose(page, 'Group', 'None');
    await expect.poll(() => queryValue(page, 'group')).toBeNull();
    await page.setViewportSize({ width: 1280, height: 800 });
    await waitForSettled(page);
  });

  await journeyStep(journey, 'filter-units', async () => {
    await gotoReady(page, '/tenant/management/organization-units?view=list');
    await expect(page.getByRole('tree'), 'the list view shows no tree').toHaveCount(0);
    await expect(page.getByRole('button', { name: 'List', exact: true })).toHaveAttribute('aria-pressed', 'true');
    expect(await names(page)).toEqual([alex.tenant, 'Head office']);
    await expect(page.locator('.list-heading__label')).toHaveText(['Top of the tree', `Below ${alex.tenant}`]);
    await shootBothWidths(page, '02-units-grouped');

    const type = chip(page, 'Type');
    await expect(type).toHaveAccessibleName('Type: All');
    await type.focus();
    await page.keyboard.press('Enter');
    const types = page.getByRole('listbox', { name: 'Type', exact: true });
    await expect(types).toBeVisible();
    await expect(type).toHaveAttribute('aria-expanded', 'true');
    await expect(types.getByRole('option', { name: 'All', exact: true }), 'the chosen option has the focus').toBeFocused();
    await expectSettledPageAccessible(page);
    await shot(page, 'lists', '07-units-chip-open-1280');
    await page.setViewportSize({ width: 1500, height: 900 });
    await shot(page, 'lists', '07-units-chip-open-1500');
    await page.setViewportSize({ width: 1280, height: 800 });
    await expect(types.getByRole('option', { name: 'All', exact: true }), 'the listbox stays open with its focus').toBeFocused();
    const branch = types.getByRole('option', { name: 'Branch', exact: true });
    for (let step = 0; step < 6 && !(await branch.evaluate((element) => element === document.activeElement)); step++) {
      await page.keyboard.press('ArrowDown');
    }
    await expect(branch).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(types).toBeHidden();
    await expect(type, 'the focus is back on the chip').toBeFocused();
    await expect(type).toHaveAccessibleName('Type: Branch');
    await expect(page.getByRole('button', { name: 'Clear Type', exact: true }), 'a set chip can be cleared').toBeVisible();
    await expect(row(page, alex.tenant), 'the type filter leaves out the Company root').toHaveCount(0);
    expect(await names(page)).toEqual(['Head office']);
    await expect.poll(() => queryValue(page, 'type'), { message: 'the filter is in the address' }).toBe('branch');
    expect(queryValue(page, 'view')).toBe('list');

    await page.reload();
    await waitForReady(page);
    await expectSettledPageAccessible(page);
    await expect(chip(page, 'Type')).toHaveAccessibleName('Type: Branch');
    await expect(chip(page, 'Type')).toHaveAttribute('value', 'branch');
    expect(await names(page), 'F5 keeps the filter and its rows').toEqual(['Head office']);
  });

  await journeyStep(journey, 'open-unit', async () => {
    await expectSettledPageAccessible(page);
    await shootBothWidths(page, '03-units-filtered');

    await row(page, 'Head office').locator('td[data-label="Type"]').click();
    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible();
    await expect(dialog.locator('[name="unit-name"]')).toHaveValue('Head office');
    await expectSettledPageAccessible(page);
    await closeDialog(page);
    expect(await names(page)).toEqual(['Head office']);
    await expect.poll(() => row(page, 'Head office').evaluate((element) => element.contains(document.activeElement)), { message: 'the focus is back on the row' }).toBe(true);

    const clear = page.getByRole('button', { name: 'Clear Type', exact: true });
    await clear.focus();
    await page.keyboard.press('Enter');
    await expect(chip(page, 'Type')).toHaveAccessibleName('Type: All');
    await expect(chip(page, 'Type'), 'the focus goes back to the chip').toBeFocused();
    await expect.poll(() => queryValue(page, 'type'), { message: 'the filter leaves the address' }).toBeNull();
    await expect.poll(() => names(page), { message: 'the list reloads without the filter' }).toEqual([alex.tenant, 'Head office']);
  });

  await journeyStep(journey, 'impersonate-viewer', async () => {
    await endImpersonation(page);
    await expect(page).toHaveURL(/\/superadmin\//);
    await impersonate(page, robin);
  });

  await journeyStep(journey, 'view-user-read-only', async () => {
    await gotoReady(page, '/tenant/management/users');
    await expect(page.getByRole('button', { name: 'New User' }), 'a viewer is offered no new user').toHaveCount(0);
    await row(page, sam.name).getByRole('button', { name: sam.name, exact: true }).click();
    const dialog = page.getByRole('dialog', { name: sam.name, exact: true });
    await expect(dialog).toBeVisible();
    await expect(dialog.locator('[name="user-first-name"]')).toHaveValue(sam.name.split(' ')[0]);
    for (const name of ['user-first-name', 'user-last-name', 'user-email', 'user-username', 'user-phone']) {
      await expect(dialog.locator(`[name="${name}"]`), `${name} is read-only`).toHaveAttribute('readonly');
    }
    await expect(dialog.locator('button[data-transition]'), 'a viewer is offered no step').toHaveCount(0);
    await expectSettledPageAccessible(page);
    await shot(page, 'lists', '06-user-dialog-read-only-1280');
    await dialog.getByRole('button', { name: 'Close', exact: true }).click();
    await expect(dialog).toBeHidden();
    await expect(row(page, sam.name).getByRole('button', { name: sam.name, exact: true }), 'the row\'s name has the focus again').toBeFocused();
  });
});
