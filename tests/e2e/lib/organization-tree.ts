import { expect, type Locator, type Page } from '@playwright/test';
import { expectSettledPageAccessible, gotoReady, waitForSettled } from './session';
import { shot } from './shots';

export const organizationPage = '/tenant/management/organization-units';

export const treeItem = (scope: Page | Locator, name: string): Locator =>
  scope.getByRole('tree').getByRole('treeitem', { name: new RegExp(`^${name} `) });

async function ownedGroup(page: Page, item: Locator): Promise<Locator> {
  const id = await item.getAttribute('aria-owns');
  expect(id, 'an open item owns the group of its children').toBeTruthy();
  return page.locator(`[id="${id}"]`);
}

export async function expectTenantConsoleTree(page: Page, tenant: string): Promise<void> {
  await expect(page.getByRole('tree', { name: `Organization units of ${tenant}` })).toBeVisible();
  const root = treeItem(page, tenant);
  await expect(root).toHaveAttribute('aria-selected', 'false');
  await expect(page.locator('.console-unit')).toHaveCount(0);
  await root.click();
  await expect(root).toHaveAttribute('aria-selected', 'true');
  await expect(page.locator('.console-unit')).toContainText(tenant);
  await expect(root).toHaveAccessibleName(`${tenant} Company`);
  await expect(page.getByRole('tree')).toHaveCount(1);
  await waitForSettled(page);
  await shot(page, 'platform-console', '03-tenant-overview-tree-1280');

  await page.setViewportSize({ width: 390, height: 844 });
  await waitForSettled(page);
  await root.scrollIntoViewIfNeeded();
  await expect(root).toBeInViewport();
  await shot(page, 'platform-console', '04-tenant-overview-tree-390');
  await page.setViewportSize({ width: 1280, height: 800 });
  await waitForSettled(page);
}

export async function walkOrganizationTree(page: Page, tenant: string): Promise<void> {
  await gotoReady(page, organizationPage);
  const tree = page.getByRole('tree', { name: 'Organization units' });
  const root = treeItem(page, tenant);
  const headOffice = treeItem(page, 'Head office');

  await expect(page.getByRole('tree')).toHaveCount(1);
  await expect(tree.getByRole('treeitem')).toHaveCount(2);
  await expect(root).toHaveAccessibleName(`${tenant} Company`);
  await expect(root).toHaveAttribute('aria-level', '1');
  await expect(root).toHaveAttribute('aria-expanded', 'true');
  await expect(root).toHaveAttribute('aria-selected', 'true');
  await expect((await ownedGroup(page, root)).getByRole('treeitem')).toHaveCount(1);
  await expect(headOffice).toHaveAccessibleName('Head office Branch');
  await expect(headOffice).toHaveAttribute('aria-level', '2');
  await expect(headOffice).toHaveAttribute('aria-setsize', '1');
  await expect(headOffice).toHaveAttribute('aria-posinset', '1');
  await expect(headOffice).not.toHaveAttribute('aria-expanded', /.*/);
  await expect(page.getByRole('heading', { name: tenant, level: 2 })).toBeVisible();
  await shot(page, 'organization', '01-organization-page-1280');

  await expect(tree.locator('[role="treeitem"][tabindex="0"]')).toHaveCount(1);
  await root.focus();
  await expect(root).toBeFocused();
  const scroll = () => page.evaluate(() => document.querySelector('.app-shell__body')?.scrollTop ?? window.scrollY);
  const scrolledBefore = await scroll();

  await page.keyboard.press('ArrowDown');
  await expect(headOffice).toBeFocused();
  await expect(headOffice).toHaveAttribute('tabindex', '0');
  await expect(root).toHaveAttribute('tabindex', '-1');
  await page.keyboard.press('ArrowUp');
  await expect(root).toBeFocused();
  await page.keyboard.press('End');
  await expect(headOffice).toBeFocused();
  await page.keyboard.press('Home');
  await expect(root).toBeFocused();

  await page.keyboard.press('ArrowRight');
  await expect(headOffice).toBeFocused();
  await page.keyboard.press('ArrowLeft');
  await expect(root).toBeFocused();
  await page.keyboard.press('ArrowLeft');
  await expect(root).toHaveAttribute('aria-expanded', 'false');
  await expect(root).not.toHaveAttribute('aria-owns', /.*/);
  await expect(headOffice).toHaveCount(0);
  await page.keyboard.press('ArrowDown');
  await expect(root, 'Down from the last visible item stays where it is').toBeFocused();
  await page.keyboard.press('ArrowRight');
  await expect(root).toHaveAttribute('aria-expanded', 'true');
  await expect(root).toBeFocused();
  await page.keyboard.press('ArrowRight');
  await expect(headOffice).toBeFocused();

  await page.keyboard.press('Enter');
  await expect(headOffice).toHaveAttribute('aria-selected', 'true');
  await expect(root).toHaveAttribute('aria-selected', 'false');
  await expect(page.getByRole('heading', { name: 'Head office', level: 2 })).toBeVisible();
  const facts = page.locator('.org-unit-head__fact');
  await expect(facts.nth(0)).toHaveText('Branch');
  await expect(facts.nth(1)).toHaveText(`Below ${tenant}`);
  await expect(page.getByRole('tab', { name: /^People/ })).toHaveAttribute('aria-selected', 'true');
  await expect(headOffice).toBeFocused();
  await page.keyboard.press('ArrowUp');
  await expect(root).toBeFocused();
  await page.keyboard.press(' ');
  await expect(root).toHaveAttribute('aria-selected', 'true');
  expect(await scroll(), 'the tree keys do not scroll the page').toBe(scrolledBefore);
  await page.keyboard.press('ArrowDown');
  await expect(headOffice).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(headOffice).toHaveAttribute('aria-selected', 'true');
  await expectSettledPageAccessible(page);
  await shot(page, 'organization', '02-head-office-selected-1280');
  await page.getByRole('tab', { name: 'Information', exact: true }).click();
  await expect(page.getByRole('tabpanel').getByText('No description provided.', { exact: true }), 'the details are the head office\'s, not the root\'s').toBeVisible();

  await page.setViewportSize({ width: 390, height: 844 });
  await gotoReady(page, organizationPage);
  await expect(treeItem(page, tenant)).toHaveAttribute('aria-selected', 'true');
  await shot(page, 'organization', '04-organization-page-390');
  await page.setViewportSize({ width: 1280, height: 800 });
}
