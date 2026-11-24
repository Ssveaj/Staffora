import { join } from 'node:path';
import type { Page } from '@playwright/test';
import { expectNoSeriousViolations } from './lib/accessibility';
import { persona, personaKeys, readFoundationState } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { endImpersonation, gotoReady, impersonate, loginAs, waitForSettled } from './lib/session';
import { screenshotRoot, shot } from './lib/shots';

const helpButton = (page: Page) => page.getByRole('button', { name: 'About this page', exact: true });

async function shootBoth(page: Page, name: string): Promise<void> {
  for (const viewport of [{ width: 1500, height: 900 }, { width: 390, height: 844 }]) {
    await page.setViewportSize(viewport);
    await waitForSettled(page);
    await shot(page, 'chrome', `${name}-${viewport.width}`);
  }
  await page.setViewportSize({ width: 1500, height: 900 });
}

test.use({ viewport: { width: 1500, height: 900 } });

test('pages open with the top row as their header, its h1 and help, their actions in the page, and system pages are two-panel cards', { tag: ['@shell', '@smoke'] }, async ({ page, browser }) => {
  const state = readFoundationState();
  await loginAs(page, persona(personaKeys.platformOwner, state));
  await impersonate(page, state.tenantAdministrator);

  await test.step('the users page: the top row as its header, its h1 and help, its actions in the page, and the help popover', async () => {
    await gotoReady(page, '/tenant/management/users');
    const row = page.locator('header.app-topbar');
    const crumbs = page.getByRole('navigation', { name: 'Breadcrumb' });
    await expect(crumbs.locator('li')).toHaveText(['Organization', /^Users/]);
    await expect(page.getByRole('heading', { level: 1 })).toHaveCount(1);
    await expect(crumbs.locator('[aria-current="page"]').getByRole('heading', { name: 'Users', level: 1 })).toBeVisible();
    await expect(crumbs.getByRole('button', { name: 'About this page', exact: true })).toBeVisible();
    await expect(page.locator('#main-content header')).toHaveCount(0);
    const create = page.locator('#main-content .page-header__lead .page-header__actions').getByRole('button', { name: 'New User', exact: true });
    await expect(create).toBeVisible();
    await expect(row.getByRole('button', { name: 'New User' })).toHaveCount(0);
    const createBox = await create.boundingBox();
    const rowBox = await row.boundingBox();
    expect(createBox?.y ?? 0, 'the actions stand below the top row, in the page').toBeGreaterThanOrEqual((rowBox?.y ?? 0) + (rowBox?.height ?? 0));

    const help = helpButton(page);
    const popover = page.locator(`[id="${await help.getAttribute('aria-controls')}"]`);
    await expect(help).toHaveAttribute('aria-expanded', 'false');
    await expect(popover).toBeHidden();
    await shootBoth(page, 'users');

    await help.click();
    await expect(help).toHaveAttribute('aria-expanded', 'true');
    await expect(popover).toBeVisible();
    await expect(popover).toContainText('user accounts');
    await expectNoSeriousViolations(page);
    await shot(page, 'chrome', 'users-help-open-1500');
    await page.locator('#main-content').click({ position: { x: 5, y: 5 } });
    await expect(popover).toBeHidden();

    await help.focus();
    await page.keyboard.press('Enter');
    await expect(popover).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(popover).toBeHidden();
    await expect(help).toHaveAttribute('aria-expanded', 'false');
    await expect(help).toBeFocused();
  });

  await test.step('the settings page', async () => {
    await gotoReady(page, '/tenant/management/settings');
    await expect(page.getByRole('heading', { name: 'Settings', level: 1 })).toBeVisible();
    await shootBoth(page, 'settings');
  });

  await endImpersonation(page);
  await expect(page).toHaveURL(/\/superadmin\/overview/);

  await test.step('the system pages: a two-panel card with the eyebrow, the title and the way on', async () => {
    const context = await browser.newContext({ viewport: { width: 1500, height: 900 } });
    page = await context.newPage();
    await loginAs(page, persona(personaKeys.staff, state));

    await gotoReady(page, '/no-such-page');
    const notFound = page.locator('section.system-page');
    await expect(notFound.getByRole('heading', { name: 'Not found', level: 1 })).toBeVisible();
    await expect(notFound.locator('.system-page__eyebrow')).toHaveText('Error 404');
    await expect(notFound.getByRole('link', { name: 'Go to the start page' })).toBeVisible();
    await shootBoth(page, 'not-found');

    await gotoReady(page, '/Account/AccessDenied');
    const denied = page.locator('section.system-page');
    await expect(denied.getByRole('heading', { name: 'Access denied', level: 1 })).toBeVisible();
    await expect(denied.locator('.system-page__eyebrow')).toHaveText('Error 403');
    await shootBoth(page, 'access-denied');
    await context.close();
  });
});

test('the sidebar\'s brand and the page title name the product Staffora', { tag: '@shell' }, async ({ page }) => {
  await loginAs(page, persona(personaKeys.coordinator, readFoundationState()));
  await gotoReady(page, '/staffing/customers');
  const head = page.locator('.app-sidebar__head');
  await expect(head.locator('.app-sidebar__brand-name')).toHaveText('Staffora');
  await expect(page).toHaveTitle(/ - Staffora$/);
  await waitForSettled(page);
  await head.screenshot({ path: join(screenshotRoot, 'rename', 'sidebar-brand-1500.png') });
  await shot(page, 'rename', 'customers-page-1500');
});
