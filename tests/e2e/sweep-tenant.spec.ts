import type { Page } from '@playwright/test';
import { persona, personaKeys, personaName, readFoundationState } from './lib/foundation-state';
import { organizationPage, treeItem, walkOrganizationTree } from './lib/organization-tree';
import { expect, test } from './lib/page-health';
import { impersonate, loginAs, waitForReady, waitForSettled } from './lib/session';
import { shot } from './lib/shots';
import { sweeper, staffPages, tenantPages, type SweepPart } from './lib/sweep';

test.use({ viewport: { width: 1280, height: 800 } });

async function asTenantAdministrator(page: Page, part: SweepPart) {
  const state = readFoundationState();
  const sweep = sweeper(page, part);
  await loginAs(page, persona(personaKeys.platformOwner, state));
  await impersonate(page, state.tenantAdministrator);
  return { state, units: organizationPage, ...sweep };
}

test(`every page of a tenant administrator and the bell's panel are free of serious accessibility violations`, { tag: '@a11y' }, async ({ page }) => {
  test.setTimeout(240_000);
  const { check, visit, finish } = await asTenantAdministrator(page, 'sweep-tenant: pages');
  for (const path of tenantPages) await visit('admin', path);

  await page.goto('/me/overview');
  await waitForReady(page);
  await waitForSettled(page);
  await page.locator('.notice-bell__trigger').click();
  const noticePanel = page.locator('[data-notice-panel]');
  await expect(noticePanel).toBeVisible();
  await check('admin bell panel: unread');
  await noticePanel.getByRole('button', { name: 'All', exact: true }).click();
  await expect(noticePanel.getByRole('button', { name: 'All', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await check('admin bell panel: all');
  await page.keyboard.press('Escape');
  await expect(noticePanel).toBeHidden();

  finish();
});

test('the user, role, settings and service dialogs of a tenant administrator are free of serious accessibility violations', { tag: '@a11y' }, async ({ page }) => {
  test.setTimeout(240_000);
  const { state, check, dialog, fromPage, finish } = await asTenantAdministrator(page, 'sweep-tenant: dialogs');

  await test.step('the organization tree from the keyboard', () => walkOrganizationTree(page, state.tenantAdministrator.tenant));
  await page.goto('/tenant/management/users?group=status');
  await waitForReady(page);
  await waitForSettled(page);
  await check('admin /tenant/management/users: grouped');
  const openOwnUser = (p: Page) => p.getByRole('button', { name: state.tenantAdministrator.name, exact: true }).click();
  const openOwnRoles = async (p: Page) => {
    await openOwnUser(p);
    const shown = p.getByRole('dialog', { name: state.tenantAdministrator.name, exact: true });
    await shown.getByRole('tab', { name: 'Roles' }).click();
    await expect(shown.locator('tr.list-row').first()).toBeVisible();
  };
  const staffFirstName = personaName(persona(personaKeys.staff, state)).split(' ')[0];
  const openSearch = (typed?: string) => async (p: Page) => {
    await p.locator('button[data-global-search]').click();
    const box = p.locator('[data-search-dialog] input[role="combobox"]');
    await expect(box).toBeFocused();
    if (!typed) return;
    await box.fill(typed);
    await expect(p.locator('[data-search-dialog] [role="option"]').first()).toHaveAttribute('aria-selected', 'true');
  };
  for (const width of [1280, 390]) {
    await page.setViewportSize({ width, height: width === 390 ? 844 : 800 });
    await dialog(`admin dialog: search (${width})`, 'GlobalSearchDialog', fromPage('/tenant/management/users', openSearch()));
    await dialog(`admin dialog: search hits (${width})`, 'GlobalSearchDialog', fromPage('/tenant/management/users', openSearch(staffFirstName)));
    await dialog(`admin dialog: new user (${width})`, 'UserDialog', fromPage('/tenant/management/users', (p) => p.getByRole('button', { name: 'New User' }).click()));
    await dialog(`admin dialog: user (${width})`, 'UserDialog', fromPage('/tenant/management/users', openOwnUser));
    await dialog(`admin dialog: user roles (${width})`, 'UserDialog', fromPage('/tenant/management/users', openOwnRoles));
  }
  await page.setViewportSize({ width: 1280, height: 800 });
  await fromPage('/tenant/management/users', openOwnRoles)();
  const userDialog = page.getByRole('dialog', { name: state.tenantAdministrator.name, exact: true });
  await dialog('admin dialog: role holding', 'RoleHoldingDialog', () => userDialog.locator('tr.list-row').first().locator('td').first().click());
  await dialog('admin dialog: add role', 'AddRoleHoldingDialog', () => userDialog.getByRole('button', { name: 'Add role', exact: true }).click());
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await dialog('admin dialog: role', 'TenantRoleDialog', fromPage('/tenant/management/roles', async (p) => {
    const card = p.locator('tr.list-row').first();
    await expect(card.getByRole('button', { name: /^Delete/ })).toHaveCount(0);
    await card.locator('.list-row__open').focus();
    await expect(card.locator('.list-row__open')).toBeFocused();
    await expect(card.locator('.list-row__open')).toHaveRole('button');
    await shot(p, 'roles', '01-role-row-name-focused');
    await p.keyboard.press('Enter');
  }));
  await page.goto('/tenant/management/settings');
  await waitForReady(page);
  await waitForSettled(page);
  await page.locator('.rz-dropdown').filter({ has: page.locator('input[aria-label="Time zone"]') }).click();
  await page.getByRole('textbox', { name: 'Search time zones' }).pressSequentially('stockholm');
  await expect(page.getByRole('option', { name: /\) Europe\/Stockholm$/ })).toHaveCount(1);
  await check('admin /tenant/management/settings: time zone picker open');
  await page.keyboard.press('Escape');
  await expect(page.getByRole('textbox', { name: 'Search time zones' })).toBeHidden();

  await page.goto('/tenant/management/settings/custom-fields');
  await waitForReady(page);
  await check('admin /tenant/management/settings/custom-fields');
  await dialog('admin dialog: custom field', 'UserFieldDialog', fromPage('/tenant/management/settings/custom-fields', (p) => p.locator('tr.list-row').filter({ hasText: 'Team' }).click()), { shotName: '04-custom-field-choice' });
  await dialog('admin dialog: global custom field', 'UserFieldDialog', fromPage('/tenant/management/settings/custom-fields', (p) => p.locator('tr.list-row').filter({ hasText: 'Employee number' }).click()));

  await dialog('admin dialog: new service', 'ServiceDialog', fromPage('/tenant/management/settings/services', (p) => p.getByRole('button', { name: 'New service' }).click()));
  await dialog('admin dialog: new closed day', 'ClosedDayDialog', fromPage('/tenant/management/settings/closed-days', (p) => p.getByRole('button', { name: 'New closed day' }).click()));
  await dialog('admin dialog: new role', 'TenantRoleDialog', fromPage('/tenant/management/roles', (p) => p.getByRole('button', { name: 'New Role' }).click()));

  finish();
});

test(`the organization page and its dialogs of a tenant administrator and a staff member's own pages are free of serious accessibility violations`, { tag: '@a11y' }, async ({ page }) => {
  test.setTimeout(240_000);
  const { state, units, check, visit, dialog, fromPage, finish } = await asTenantAdministrator(page, 'sweep-tenant: organization and staff');

  await fromPage(units, (p) => treeItem(p, 'Head office').click())();
  await page.getByRole('button', { name: 'Open unit' }).click();
  const headOffice = page.getByRole('dialog');
  await expect(headOffice.locator('button[data-transition="unit-deactivate"]')).not.toHaveAttribute('aria-disabled', 'true');
  await dialog('admin dialog: confirm a step', 'ConfirmDialog', () => headOffice.locator('button[data-transition="unit-deactivate"]').click(), {
    shotName: '01-confirm-step',
    close: (shown) => shown.getByRole('button', { name: 'Cancel', exact: true }).click(),
  });
  await expect(headOffice.locator('.state-pill__button'), 'the cancelled step kept the unit active').toHaveAttribute('data-state', 'Active');
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  const animationsDone = (p: Page) => p.evaluate(() => Promise.race([
    Promise.all(document.getAnimations().map((animation) => animation.finished.catch(() => undefined))),
    new Promise((resolve) => setTimeout(resolve, 2000)),
  ]));

  await dialog(`admin ${units}: open the root`, 'OrganizationUnitDialog', fromPage(units, async (p) => {
    await p.getByRole('button', { name: 'Open unit' }).click();
    const shown = p.getByRole('dialog');
    await expect(shown.locator('[name="unit-name"]')).toBeVisible();
    await expect(shown.locator('button[data-transition="unit-deactivate"]')).toHaveAttribute('aria-disabled', 'true');
    await expect(shown.locator('button[data-transition="unit-delete"]')).toHaveAttribute('aria-disabled', 'true');
    await animationsDone(p);
    await shot(p, 'organization', '06-root-dialog-steps-greyed');
  }));
  await dialog(`admin ${units}: add a unit below the root`, 'OrganizationUnitDialog', fromPage(units, (p) => p.getByRole('button', { name: 'Add unit below' }).click()));

  const tenant = state.tenantAdministrator.tenant;
  const expectPicker = async (input: string, shotName: string) => {
    const shown = page.getByRole('dialog').last();
    await shown.locator('.rz-dropdown').filter({ has: page.locator(`[name="${input}"]`) }).click();
    await expect(page.getByRole('option', { name: `${tenant} › Head office`, exact: true })).toBeVisible();
    await expect(page.getByRole('option', { name: tenant, exact: true })).toBeVisible();
    await animationsDone(page);
    await shot(page, 'organization', shotName);
    await page.keyboard.press('Escape');
    await expect(page.getByRole('option', { name: tenant, exact: true })).toBeHidden();
  };
  await test.step('the unit pickers show paths', async () => {
    await fromPage(units, (p) => p.getByRole('button', { name: 'Add unit below' }).click())();
    await expectPicker('unit-parent', '07-parent-picker-paths');
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await fromPage('/tenant/management/roles', (p) => p.locator('tr.list-row .list-row__open').first().click())();
    const roleDialog = page.getByRole('dialog');
    await roleDialog.getByRole('tab', { name: 'Holders' }).click();
    await roleDialog.getByRole('button', { name: 'Add holder', exact: true }).click();
    await expect(page.getByRole('dialog')).toHaveCount(2);
    await expectPicker('user-role-unit', '08-role-unit-picker-paths');
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(1);
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);
  });
  await fromPage(units, (p) => treeItem(p, 'Head office').click())();
  await expect(treeItem(page, 'Head office')).toHaveAttribute('aria-selected', 'true');
  await check(`admin ${units}: unit selected`);
  await dialog(`admin ${units}: open unit`, 'OrganizationUnitDialog', () => page.getByRole('button', { name: 'Open unit' }).click(), { shotName: '03-edit-unit' });
  await dialog(`admin ${units}: add child unit`, 'OrganizationUnitDialog', () => page.getByRole('button', { name: 'Add unit below' }).click());
  await page.getByRole('tab', { name: /^People/ }).click();
  await check(`admin ${units}: unit people`);
  await dialog(`admin ${units}: add person`, 'AddRoleHoldingDialog', () => page.getByRole('button', { name: 'Add person', exact: true }).click(), { shotName: '02-add-person' });

  await page.goto(`${units}?view=list&inactive=true`);
  await waitForReady(page);
  await check(`admin ${units}?view=list`);
  await dialog(`admin ${units}?view=list: unit`, 'OrganizationUnitDialog', fromPage(`${units}?view=list`, (p) => p.locator('tr.list-row').filter({ hasText: 'Head office' }).click()));

  await test.step('a staff member', async () => {
    await page.context().clearCookies();
    await loginAs(page, persona(personaKeys.staff, state));
    for (const path of staffPages) await visit('staff', path);
  });

  finish();
});
