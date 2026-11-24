import type { Page } from '@playwright/test';
import { persona, personaKeys, readFoundationState } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { expectSettledPageAccessible, gotoReady, loginAs, waitForReady } from './lib/session';
import { shot } from './lib/shots';

async function capture(page: Page, name: string): Promise<void> {
  await page.mouse.move(0, 0);
  await expectSettledPageAccessible(page);
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow, 'the page is no wider than the window').toBeLessThanOrEqual(0);
  const style = await page.addStyleTag({
    content: '.app-shell { height: auto !important; min-height: 100dvh; overflow: visible !important; } .app-shell .app-shell__body { overflow: visible !important; }',
  });
  await shot(page, 'platform-overview', name);
  await style.evaluate((element) => element.remove());
}

for (const width of [1440, 390]) {
  test(`the platform overview names itself, shows the platform in figures and leads to each company's console (${width} px)`, { tag: width === 1440 ? ['@platform', '@smoke'] : '@platform' }, async ({ page }) => {
    await page.setViewportSize({ width, height: width === 390 ? 844 : 1000 });
    const state = readFoundationState();
    const company = state.tenantAdministrator.tenant;
    await loginAs(page, persona(personaKeys.platformOwner, state));
    await gotoReady(page, '/superadmin/overview');

    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Platform overview');
    await expect(page).toHaveTitle(/^Platform overview/);
    const figures = page.locator('dl.hero-figures > .hero-figure');
    await expect(figures).toHaveCount(4);
    await expect(figures.locator('dt')).toContainText(['Errors, last 24 h', 'Failed messages', 'Active users', 'Acting as a user']);
    await expect(page.getByRole('list', { name: 'Tenant health' })).toHaveCount(0);
    await expect(page.locator('.ring')).toHaveCount(0);

    const rows = page.getByRole('list', { name: 'Tenants', exact: true }).getByRole('link');
    const companyRow = rows.filter({ has: page.locator('.tenant-row__name', { hasText: new RegExp(`^${escape(company)}$`) }) });
    await expect(companyRow).toHaveCount(1);
    await expect(companyRow).toHaveAttribute('href', /^\/superadmin\/tenants\/[0-9a-f-]{36}$/);
    await expect(page.locator('a.tenant-row--portal')).toHaveCount(0);
    await expect(rows.filter({ has: page.locator('.tenant-row__name', { hasText: state.staffing.customer }) })).toHaveCount(0);
    await expect(rows.filter({ has: page.locator('.tenant-row__name', { hasText: /^Platform$/ }) })).toHaveCount(0);
    await expect(page.locator('.tenant-row__concern')).toHaveCount(0);
    await expect(page.getByRole('link', { name: 'All tenants' })).toHaveAttribute('href', '/superadmin/tenants');
    await capture(page, `01-overview-${width}`);

    await page.getByLabel('Find a user in any tenant').fill('a');
    await page.getByLabel('Find a user in any tenant').press('Enter');
    const picker = page.getByRole('dialog', { name: 'Impersonate User' });
    await expect(picker).toBeVisible();
    await expect(picker.locator('input[name="impersonate-search"]')).toHaveValue('a');
    await capture(page, `02-act-as-a-user-${width}`);
    await picker.getByRole('button', { name: 'Cancel' }).click();
    await expect(picker).toBeHidden();

    await page.getByRole('button', { name: 'Start onboarding' }).click();
    const onboarding = page.getByRole('dialog', { name: 'New tenant' });
    await expect(onboarding.getByLabel('Tenant name')).toBeVisible();
    await capture(page, `03-start-onboarding-${width}`);
    await page.keyboard.press('Escape');
    await expect(onboarding).toBeHidden();

    await companyRow.focus();
    await expect(companyRow).toBeFocused();
    await capture(page, `04-row-focused-${width}`);
    await page.keyboard.press('Enter');
    await expect(page).toHaveURL(/\/superadmin\/tenants\/[0-9a-f-]{36}$/);
    await waitForReady(page);
    await expect(page.getByRole('heading', { level: 1, name: company })).toBeVisible();
  });
}

function escape(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}
