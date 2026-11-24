import { expect, type Page } from '@playwright/test';
import { gotoReady, waitForReady, waitForSettled } from './session';

export const tenantList = '/superadmin/tenants';

export type ConsoleTab = 'Overview' | 'Users' | 'Roles' | 'Custom fields' | 'Dead letters';

export const consoleTabPath = (tab: ConsoleTab): string => tab.toLowerCase().replace(/ /g, '-');

export async function openTenantConsole(page: Page, tenant: string, tab: ConsoleTab = 'Overview'): Promise<void> {
  await gotoReady(page, `${tenantList}?q=${encodeURIComponent(tenant)}`);
  await page.locator('tr.list-row').getByRole('link', { name: tenant, exact: true }).click();
  await expect(page).toHaveURL(/\/superadmin\/tenants\/[0-9a-f-]{36}$/);
  await waitForReady(page);
  await expect(page.getByRole('heading', { level: 1, name: tenant, exact: true })).toBeVisible();
  if (tab !== 'Overview') {
    await page.getByRole('navigation', { name: 'Tenant console' }).getByRole('link', { name: tab, exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`/superadmin/tenants/[0-9a-f-]{36}/${consoleTabPath(tab)}$`));
  }
  await waitForSettled(page);
}

export async function openTenantDialog(page: Page, tenant: string): Promise<void> {
  await openTenantConsole(page, tenant);
  await page.getByRole('button', { name: 'Tenant details', exact: true }).click();
  await expect(page.getByRole('dialog', { name: tenant })).toBeVisible();
}
