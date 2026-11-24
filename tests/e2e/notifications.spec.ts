import type { Locator, Page } from '@playwright/test';
import { persona, personaKeys, readFoundationState } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { expectSettledPageAccessible, gotoReady, impersonate, loginAs } from './lib/session';
import { shot } from './lib/shots';

const width = 1280;

const usersPage = '/tenant/management/users';

const step = (dialog: Locator, key: string) => dialog.locator(`button[data-transition="${key}"]`);

async function expectState(dialog: Locator, state: string): Promise<void> {
  await expect(dialog.locator('.state-pill__button')).toHaveAttribute('data-state', state);
  await expect(dialog.locator('.state-entity-dialog-footer__bar')).toHaveAttribute('aria-busy', 'false');
}

async function deactivateAndReactivate(page: Page, dialog: Locator): Promise<void> {
  await step(dialog, 'user-deactivate').click();
  const confirm = page.locator('.form-dialog').filter({ has: page.getByRole('button', { name: 'Deactivate', exact: true }) });
  await confirm.getByRole('button', { name: 'Deactivate', exact: true }).click();
  await expectState(dialog, 'Inactive');
  await step(dialog, 'user-reactivate').click();
  await expectState(dialog, 'Active');
}

async function expectDrawer(page: Page, panel: Locator, viewport: { width: number; height: number }): Promise<void> {
  const bar = await page.locator('[data-app-topbar]').boundingBox();
  const box = await panel.boundingBox();
  expect(box, 'the drawer has a box').not.toBeNull();
  expect(box!.y, 'the drawer starts under the top bar').toBeGreaterThanOrEqual((bar?.y ?? 0) + (bar?.height ?? 0) - 1);
  expect(box!.x + box!.width, 'the drawer stands at the right edge').toBeGreaterThanOrEqual(viewport.width - 24);
  expect(box!.y + box!.height, 'the drawer runs to the foot').toBeGreaterThanOrEqual(viewport.height - 24);
  if (viewport.width > 480) {
    expect(Math.round(box!.width), 'the drawer is 430 px wide').toBe(430);
  } else {
    expect(box!.x, 'on a phone the drawer takes the whole width').toBeLessThanOrEqual(1);
  }
}

async function capture(page: Page, name: string, width: number): Promise<void> {
  await expectSettledPageAccessible(page);
  await shot(page, 'notifications', `${name}-${width}`, [page.locator('.state-entity-dialog-footer__notice')]);
}

test(`a reactivated person's bell rises without a reload and its panel opens the notice's page`, { tag: '@shell' }, async ({ page, browser }) => {
  test.setTimeout(180_000);
  const viewport = { width, height: 800 };
  await page.setViewportSize(viewport);
  const state = readFoundationState();
  const owner = persona(personaKeys.platformOwner, state);
  const administrator = state.tenantAdministrator;

  await loginAs(page, owner);
  await gotoReady(page, '/superadmin/overview');
  const accountTrigger = page.locator('.account-menu__trigger');
  await expect(accountTrigger).toBeVisible();
  await expect(page.locator('[data-notice-bell]')).toHaveCount(0);
  const toggleBox = await page.locator('.app-topbar__lead').boundingBox();
  const accountBox = await accountTrigger.boundingBox();
  expect(Math.abs((toggleBox?.y ?? 0) + (toggleBox?.height ?? 0) / 2 - ((accountBox?.y ?? 0) + (accountBox?.height ?? 0) / 2)), 'the console bar is one line').toBeLessThan(8);
  await capture(page, '00-console-no-bell', width);
  await page.setViewportSize({ width: 390, height: 844 });
  const phoneToggle = await page.locator('.app-topbar__lead').boundingBox();
  const phoneAccount = await accountTrigger.boundingBox();
  expect(Math.abs((phoneToggle?.y ?? 0) + (phoneToggle?.height ?? 0) / 2 - ((phoneAccount?.y ?? 0) + (phoneAccount?.height ?? 0) / 2)), 'the console bar is one line on a phone').toBeLessThan(8);
  await capture(page, '00-console-no-bell', 390);
  await page.setViewportSize(viewport);

  await impersonate(page, administrator);
  await gotoReady(page, usersPage);
  const suffix = `${width}${Date.now().toString(36)}${Math.floor(Math.random() * 1000)}`;
  const email = `notice.reader.${suffix}@company-a.example`;
  const name = `Rae Notice ${suffix}`;
  await page.getByRole('button', { name: 'New User', exact: true }).click();
  const created = page.getByRole('dialog', { name: 'New user', exact: true });
  await created.locator('[name="user-first-name"]').fill('Rae');
  await created.locator('[name="user-last-name"]').fill(`Notice ${suffix}`);
  await created.locator('[name="user-email"]').fill(email);
  await created.locator('[name="user-username"]').fill(email);
  await step(created, 'user-create').click();
  const dialog = page.getByRole('dialog', { name, exact: true });
  await expectState(dialog, 'Active');
  await deactivateAndReactivate(page, dialog);

  const pill = dialog.locator('.state-pill__button');
  await page.mouse.move(0, 0);
  await pill.focus();
  if ((await pill.getAttribute('aria-expanded')) !== 'true') await page.keyboard.press('Enter');
  await expect(dialog.locator('.state-pill__card [data-history-notice="Delivered"]').first()).toContainText('Notice: Account reactivated · delivered to 1 person');
  await capture(page, '01-history-delivered', width);
  await page.keyboard.press('Escape');

  const readerContext = await browser.newContext({ viewport });
  try {
    const reader = await readerContext.newPage();
    await loginAs(reader, owner);
    await impersonate(reader, { tenant: administrator.tenant, name, email });
    await gotoReady(reader, '/me/profile');
    const bell = reader.locator('[data-notice-bell]');
    const trigger = bell.locator('.notice-bell__trigger');
    await expect(trigger).toHaveAttribute('aria-label', 'Notifications, 1 unread');
    await expect(bell.locator('.notice-bell__badge')).toHaveText('1');
    await capture(reader, '02-bell-badge', width);

    await deactivateAndReactivate(page, dialog);
    await expect(trigger).toHaveAttribute('aria-label', 'Notifications, 2 unread');
    await expect(bell.locator('.notice-bell__badge')).toHaveText('2');
    await expect(bell.locator('[data-notice-announcement]')).toHaveText('New notice. 2 unread.');

    await trigger.focus();
    await reader.keyboard.press('Enter');
    const panel = reader.locator('[data-notice-panel]');
    await expect(panel).toBeVisible();
    await expect(trigger).toHaveAttribute('aria-expanded', 'true');
    await expect(panel.locator('[data-notice-summary]')).toHaveText('2 unread');
    await expectDrawer(reader, panel, viewport);
    await expect(panel.locator('.notice-row--unread')).toHaveCount(2);
    await expect(panel.getByRole('button', { name: 'Unread', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await expect(panel.getByRole('button', { name: 'Mark all read', exact: true })).toHaveCount(0);
    await expect(panel).toContainText('Notices stay unread for the person you are signed in as');
    await capture(reader, '03-panel-unread', width);
    await panel.getByRole('button', { name: 'All', exact: true }).click();
    await expect(panel.getByRole('button', { name: 'All', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await expect(panel.locator('.notice-row')).toHaveCount(2);
    await capture(reader, '04-panel-all', width);
    await reader.keyboard.press('Escape');
    await expect(panel).toBeHidden();
    await expect(trigger).toBeFocused();

    await reader.keyboard.press('Enter');
    await expect(panel).toBeVisible();
    await panel.getByRole('button', { name: 'Close notifications', exact: true }).click();
    await expect(panel).toBeHidden();
    await expect(trigger).toBeFocused();

    await trigger.click();
    await panel.locator('.notice-row').first().click();
    await expect(reader).toHaveURL(/\/me\/overview$/);
    await expect(reader.getByRole('heading', { name: 'My overview' })).toBeVisible();
    await expect(trigger).toHaveAttribute('aria-label', 'Notifications, 2 unread');
    await capture(reader, '05-notice-opened-overview', width);

    const phone = { width: 390, height: 844 };
    await reader.setViewportSize(phone);
    await trigger.click();
    await expect(panel).toBeVisible();
    await expectDrawer(reader, panel, phone);
    await capture(reader, '06-panel-on-a-phone', 390);
    await reader.keyboard.press('Escape');
    await expect(panel).toBeHidden();
  } finally {
    await readerContext.close();
  }
});
