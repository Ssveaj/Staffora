import type { Page } from '@playwright/test';
import { persona, personaKeys, personaName, readFoundationState } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { expectSettledPageAccessible, gotoReady, loginAs, waitForReady } from './lib/session';
import { shot } from './lib/shots';

const searchButton = (page: Page) => page.locator('button[data-global-search]');
const searchDialog = (page: Page) => page.getByRole('dialog', { name: 'Search', exact: true });
const searchBox = (page: Page) => searchDialog(page).getByRole('combobox');
const options = (page: Page) => searchDialog(page).getByRole('option');

async function capture(page: Page, name: string, width: number): Promise<void> {
  await expectSettledPageAccessible(page);
  await shot(page, 'search', `${name}-${width}`);
}

for (const width of [1280, 390]) {
  test(`a coordinator finds a customer and a staff member from "/" and opens the hit's dialog with the keyboard (${width})`, { tag: '@shell' }, async ({ page }) => {
    test.setTimeout(120_000);
    await page.setViewportSize({ width, height: width === 390 ? 844 : 800 });
    const state = readFoundationState();
    const staffName = personaName(persona(personaKeys.staff, state));
    const customer = state.staffing.customer;
    await loginAs(page, persona(personaKeys.coordinator, state));
    await gotoReady(page, '/staffing/jobs');
    await expect(searchButton(page)).toHaveAttribute('aria-label', 'Search');

    const listSearch = page.getByRole('searchbox', { name: 'Search' });
    await listSearch.click();
    await page.keyboard.press('/');
    await expect(listSearch).toHaveValue('/');
    await expect(searchDialog(page)).toHaveCount(0);
    await listSearch.fill('');
    await page.locator('main').click({ position: { x: 4, y: 4 } });

    await page.keyboard.press('/');
    await expect(searchDialog(page)).toBeVisible();
    await expect(searchBox(page)).toBeFocused();
    await expect(searchBox(page)).toHaveAttribute('aria-expanded', 'false');
    await capture(page, '01-empty', width);

    await page.keyboard.type(staffName.split(' ')[0]);
    await expect(options(page).first()).toHaveAttribute('aria-selected', 'true');
    await expect(searchDialog(page).getByRole('group', { name: 'Staff members' })).toBeVisible();
    await expect(searchDialog(page).getByRole('group', { name: 'Staff members' }).getByRole('option').filter({ hasText: staffName })).toHaveCount(1);
    await capture(page, '02-staff-hits', width);

    await page.keyboard.press('Escape');
    await expect(searchDialog(page)).toHaveCount(0);
    await expect(searchButton(page)).toBeFocused();

    await searchButton(page).click();
    await expect(searchBox(page)).toBeFocused();
    await page.keyboard.type(customer);
    const customers = searchDialog(page).getByRole('group', { name: 'Customers' });
    await expect(customers.getByRole('option').filter({ hasText: customer }).first()).toBeVisible();
    const count = await options(page).count();
    const target = await options(page).evaluateAll((all, name) => all.findIndex((option) => option.closest('[role="group"]')?.getAttribute('data-search-group') === 'Customers' && option.textContent?.includes(name)), customer);
    expect(target, 'the customer is among the hits').toBeGreaterThanOrEqual(0);
    for (let press = 0; press < target; press++) await page.keyboard.press('ArrowDown');
    await expect(options(page).nth(target)).toHaveAttribute('aria-selected', 'true');
    await expect(searchBox(page)).toHaveAttribute('aria-activedescendant', (await options(page).nth(target).getAttribute('id'))!);
    await page.keyboard.press('ArrowUp');
    await expect(options(page).nth((target - 1 + count) % count)).toHaveAttribute('aria-selected', 'true');
    await page.keyboard.press('ArrowDown');
    await capture(page, '03-customer-chosen', width);

    await page.keyboard.press('Enter');
    await expect(searchDialog(page)).toHaveCount(0);
    const record = page.getByRole('dialog', { name: customer, exact: true });
    await expect(record).toBeVisible();
    await expect(page).toHaveURL(/[?&]dlg=customer-[0-9a-f-]{36}/);
    await page.reload();
    await waitForReady(page);
    await expect(page.getByRole('dialog', { name: customer, exact: true }), 'F5 reopens the dialog the search opened').toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);
  });
}

test('the platform owner finds a tenant on the console and opens its console page', { tag: '@shell' }, async ({ page }) => {
  test.setTimeout(120_000);
  const state = readFoundationState();
  const tenant = state.tenantAdministrator.tenant;
  await loginAs(page, persona(personaKeys.platformOwner, state));
  for (const width of [1280, 390]) {
    await page.setViewportSize({ width, height: width === 390 ? 844 : 800 });
    await gotoReady(page, '/superadmin/overview');
    await searchButton(page).click();
    await expect(searchBox(page)).toHaveAttribute('placeholder', 'Search tenants');
    await expect(searchBox(page)).toBeFocused();
    await page.keyboard.type(tenant);
    const tenants = searchDialog(page).getByRole('group', { name: 'Tenants' });
    await expect(tenants.getByRole('option').filter({ hasText: tenant }).first()).toHaveAttribute('aria-selected', 'true');
    await capture(page, '04-console-tenants', width);
    await page.keyboard.press('Enter');
    await expect(page).toHaveURL(/\/superadmin\/tenants\/[0-9a-f-]{36}$/);
    await waitForReady(page);
    await expect(page.getByRole('heading', { level: 1, name: tenant })).toBeVisible();
  }
});
