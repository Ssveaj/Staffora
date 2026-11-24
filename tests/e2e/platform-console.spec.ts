import type { Page } from '@playwright/test';
import { persona, personaKeys, readFoundationState } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { consoleTabPath, openTenantConsole, tenantList } from './lib/platform-console';
import { endImpersonation, expectSettledPageAccessible, gotoReady, impersonate, loginAs, waitForReady, waitForSettled } from './lib/session';
import { shot } from './lib/shots';

async function shotWideToo(page: Page, name: string, width: number): Promise<void> {
  await shot(page, 'platform-console', `${name}-${width}`);
  if (width !== 1280) return;
  await page.setViewportSize({ width: 1500, height: 1000 });
  await expectSettledPageAccessible(page);
  await shot(page, 'platform-console', `${name}-1500`);
  await page.setViewportSize({ width, height: 800 });
}

for (const width of [1280, 390]) {
  test(`the overview counts the tenants by state, the tenant list offers no filter and no platform row, and a row opens the tenant's console (${width} px)`, { tag: '@platform' }, async ({ page }) => {
    await page.setViewportSize({ width, height: width === 390 ? 844 : 800 });
    const state = readFoundationState();
    await loginAs(page, persona(personaKeys.platformOwner, state));

    await gotoReady(page, '/superadmin/overview');
    const counts = page.getByRole('list', { name: 'Tenants by state' });
    await expect(counts.getByRole('listitem')).toHaveCount(5);
    await expect(counts.getByRole('link'), 'the counts are figures, not links').toHaveCount(0);
    await expect(page.locator('.hero-figure[data-figure="errors"] .hero-figure__value')).not.toHaveText('0');
    await expect(page.locator('#main-content').getByRole('link', { name: 'Platform health', exact: true })).toHaveAttribute('href', '/superadmin/errorlog');
    await expect(page.getByRole('button', { name: 'Impersonate User' })).toHaveCount(1);
    await expectSettledPageAccessible(page);
    await shotWideToo(page, '01-overview', width);

    await gotoReady(page, '/superadmin/errorlog');
    await expect(page.getByRole('heading', { name: 'Platform health', level: 1 })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Refresh' })).toHaveCount(0);
    await expect(page.getByRole('heading', { name: 'Retention', level: 2 })).toBeVisible();
    await expect(page.locator('[data-retention-when]')).toHaveText(/^(Last pass on .+ UTC\.|The retention pass has not run since the app started\.)/);
    const errorRow = page.locator('tr.list-row').filter({ hasText: state.errorLog.search }).first();
    await expect(errorRow.locator('.error-log-message__text')).toBeVisible();
    await errorRow.click();
    await expect(page.getByRole('dialog').locator('.error-event__facts')).toBeVisible();
    await expectSettledPageAccessible(page);
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toBeHidden();
    await expectSettledPageAccessible(page);
    await shotWideToo(page, '14-error-log', width);
    await gotoReady(page, '/superadmin/overview');

    await page.getByRole('link', { name: 'All tenants' }).click();
    await expect(page).toHaveURL(/\/superadmin\/tenants$/);
    await waitForReady(page);
    const row = page.locator('tr.list-row').filter({ has: page.getByRole('link', { name: state.tenantAdministrator.tenant, exact: true }) });
    await expect(row).toHaveCount(1);
    await expect(page.locator('.list-chip, .list-quick'), 'no filter beside the search').toHaveCount(0);
    await expect(page.locator('thead th button'), 'no heading sorts').toHaveCount(0);
    await expect(page.locator('tr.list-row').filter({ has: page.getByRole('link', { name: 'Platform', exact: true }) }), 'no platform row').toHaveCount(0);
    await expectSettledPageAccessible(page);
    await shotWideToo(page, '02-tenants', width);

    await row.getByRole('link', { name: state.tenantAdministrator.tenant, exact: true }).click();
    await expect(page).toHaveURL(/\/superadmin\/tenants\/[0-9a-f-]{36}$/);
    await waitForReady(page);
    await expect(page.getByRole('heading', { level: 1, name: state.tenantAdministrator.tenant })).toBeVisible();
    for (const tab of ['Users', 'Roles', 'Custom fields'] as const) {
      await openTenantConsole(page, state.tenantAdministrator.tenant, tab);
      await expect(page.getByRole('navigation', { name: 'Tenant console' }).getByRole('link', { name: tab, exact: true })).toHaveAttribute('aria-current', 'page');
      await expect(page.getByRole('button', { name: /^(New|Add|Create|Save|Delete|Remove)\b/ })).toHaveCount(0);
      await expectSettledPageAccessible(page);
    }
  });
}

for (const width of [1500, 1280, 390]) {
  test(`the tenant list names a staffing company's customer tenants, and each console links the other and lists its dead letters (${width} px)`, { tag: '@platform' }, async ({ page }) => {
    await page.setViewportSize({ width, height: width === 390 ? 844 : width === 1500 ? 1000 : 800 });
    const state = readFoundationState();
    const company = state.tenantAdministrator.tenant;
    const customer = state.staffing.customer;
    await loginAs(page, persona(personaKeys.platformOwner, state));

    await gotoReady(page, `${tenantList}?q=${encodeURIComponent(company)}`);
    const rows = page.locator('tr.list-row');
    const companyRow = rows.filter({ has: page.getByRole('link', { name: company, exact: true }) });
    await expect(companyRow).toHaveCount(1);
    await expect(rows.filter({ has: page.getByRole('link', { name: customer, exact: true }) })).toHaveCount(0);
    await expect(companyRow.locator('td[data-label="Managed customers"]')).toContainText(customer);
    const companyId = await companyRow.getAttribute('data-row');
    await expect(companyRow.locator('[data-kind="company"]')).toHaveCount(1);
    await expectSettledPageAccessible(page);
    await shot(page, 'platform-console', `10-company-names-its-customers-${width}`);

    await companyRow.getByRole('link', { name: company, exact: true }).click();
    await waitForReady(page);
    const managed = page.getByRole('list', { name: 'Managed customers' });
    await expect(managed).toContainText(customer);
    const customerId = (await managed.getByRole('link', { name: customer, exact: true }).getAttribute('href'))!.split('/').pop();
    await expectSettledPageAccessible(page);
    await shot(page, 'platform-console', `11-company-manages-${width}`);

    await managed.getByRole('link').filter({ hasText: customer }).first().click();
    await expect(page).toHaveURL(new RegExp(`/superadmin/tenants/${customerId}$`));
    await waitForReady(page);
    await expect(page.getByRole('list', { name: 'Managed by' }).getByRole('link', { name: company, exact: true })).toHaveAttribute('href', `/superadmin/tenants/${companyId}`);
    await expectSettledPageAccessible(page);
    await shot(page, 'platform-console', `12-customer-managed-by-${width}`);

    await page.getByRole('navigation', { name: 'Tenant console' }).getByRole('link', { name: 'Dead letters', exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`/superadmin/tenants/${customerId}/${consoleTabPath('Dead letters')}$`));
    await waitForSettled(page);
    await expect(page.getByRole('navigation', { name: 'Tenant console' }).getByRole('link', { name: 'Dead letters', exact: true })).toHaveAttribute('aria-current', 'page');
    await expect(page.locator('.empty-state').filter({ hasText: 'No dead letters' })).toBeVisible();
    await expect(page.getByRole('button', { name: /^(New|Add|Create|Save|Delete|Remove|Retry)\b/ })).toHaveCount(0);
    await expectSettledPageAccessible(page);
    await shot(page, 'platform-console', `13-dead-letters-${width}`);
  });
}

test('the platform overview is refused to a platform owner who is impersonating', { tag: ['@platform', '@smoke'] }, async ({ page }) => {
  const state = readFoundationState();
  await loginAs(page, persona(personaKeys.platformOwner, state));
  await impersonate(page, state.impersonationTarget);

  for (const path of ['/superadmin/overview', tenantList]) {
    await page.goto(path);
    await waitForReady(page);
    await expect(page, `${path} is refused while impersonating`).toHaveURL(/\/Account\/AccessDenied/);
  }
  await expectSettledPageAccessible(page);
  await shot(page, 'platform-console', '03-overview-refused-while-impersonating');

  await endImpersonation(page);
  await expect(page).toHaveURL(/\/superadmin\/overview/);
  await waitForReady(page);
  await expect(page.getByRole('heading', { name: 'Platform overview', level: 1 })).toBeVisible();
});
