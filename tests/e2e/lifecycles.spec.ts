import type { Locator, Page } from '@playwright/test';
import { readFoundationState, persona, personaKeys } from './lib/foundation-state';
import { organizationPage, treeItem } from './lib/organization-tree';
import { expect, test } from './lib/page-health';
import { expectSettledPageAccessible, gotoReady, impersonate, loginAs } from './lib/session';
import { shot } from './lib/shots';

const width = 1280;

const step = (dialog: Locator, key: string) => dialog.locator(`button[data-transition="${key}"]`);

async function expectState(dialog: Locator, state: string): Promise<void> {
  await expect(dialog.locator('.state-pill__button')).toHaveAttribute('data-state', state);
  await expect(dialog.locator('.state-entity-dialog-footer__bar')).toHaveAttribute('aria-busy', 'false');
}

async function capture(page: Page, area: string, name: string, width: number): Promise<void> {
  await expectSettledPageAccessible(page);
  await shot(page, area, `${name}-${width}`);
}

async function confirmStep(page: Page, dialog: Locator, key: string, label: string): Promise<void> {
  await step(dialog, key).focus();
  await page.keyboard.press('Enter');
  const confirm = page.locator('.form-dialog').filter({ has: page.getByRole('button', { name: label, exact: true }) });
  await confirm.getByRole('button', { name: label, exact: true }).click();
  await expect(confirm).toBeHidden();
}

async function expectGreyed(dialog: Locator, key: string, reason: string, show = false): Promise<void> {
  const greyed = step(dialog, key);
  await expect(greyed).toHaveAttribute('aria-disabled', 'true');
  const card = dialog.locator(`#${await greyed.getAttribute('aria-describedby')}`);
  await expect(card).toContainText(reason);
  if (show) {
    await greyed.hover();
    await expect(card).toBeVisible();
  }
}

async function openUnit(page: Page, name: string): Promise<Locator> {
  await gotoReady(page, organizationPage);
  await treeItem(page, name).click();
  await page.getByRole('button', { name: 'Open unit', exact: true }).click();
  const dialog = page.getByRole('dialog', { name, exact: true });
  await expect(dialog).toBeVisible();
  return dialog;
}

async function addUnitBelow(page: Page, parent: string, name: string): Promise<Locator> {
  await gotoReady(page, organizationPage);
  await treeItem(page, parent).click();
  await page.getByRole('button', { name: 'Add unit below', exact: true }).click();
  const created = page.getByRole('dialog', { name: 'New unit', exact: true });
  await expect(created).toBeVisible();
  await created.locator('[name="unit-name"]').fill(name);
  await step(created, 'unit-create').click();
  const dialog = page.getByRole('dialog', { name, exact: true });
  await expectState(dialog, 'Active');
  return dialog;
}

test(`a unit is added, deactivated, archived, restored and deleted through its dialog, and the root and a parent keep theirs`, { tag: '@admin' }, async ({ page }) => {
  test.setTimeout(300_000);
  await page.setViewportSize({ width, height: 800 });
  const state = readFoundationState();
  const administrator = state.tenantAdministrator;
  const tenant = administrator.tenant;
  await loginAs(page, persona(personaKeys.platformOwner, state));
  await impersonate(page, administrator);

  const suffix = `${width}${Date.now().toString(36)}`;
  const office = `Pier office ${suffix}`;
  let dialog = await addUnitBelow(page, tenant, office);
  await capture(page, 'organization', '10-unit-active', width);

  const desk = `Night desk ${suffix}`;
  dialog = await addUnitBelow(page, office, desk);
  await dialog.getByRole('button', { name: 'Close', exact: true }).click();
  dialog = await openUnit(page, office);
  await expectState(dialog, 'Active');
  await expectGreyed(dialog, 'unit-delete', 'It has units below it', true);
  await capture(page, 'organization', '11-parent-delete-refused', width);
  await dialog.getByRole('tab', { name: 'Units below' }).click();
  await expect(dialog.locator('tr.list-row').filter({ hasText: desk })).toHaveCount(1);
  await dialog.getByRole('tab', { name: 'Info' }).click();

  await confirmStep(page, dialog, 'unit-deactivate', 'Deactivate');
  await expectState(dialog, 'Inactive');
  await expect(dialog.locator('[name="unit-name"]')).toHaveAttribute('readonly');
  await capture(page, 'organization', '12-unit-inactive', width);
  await dialog.getByRole('button', { name: 'Close', exact: true }).click();

  dialog = await openUnit(page, desk);
  await expectState(dialog, 'Inactive');
  await confirmStep(page, dialog, 'unit-archive-inactive', 'Archive');
  await expectState(dialog, 'Archived');
  await dialog.getByRole('button', { name: 'Close', exact: true }).click();
  dialog = await openUnit(page, office);
  await confirmStep(page, dialog, 'unit-archive-inactive', 'Archive');
  await expectState(dialog, 'Archived');
  await capture(page, 'organization', '13-unit-archived', width);

  await step(dialog, 'unit-restore').click();
  await expectState(dialog, 'Inactive');
  await dialog.getByRole('button', { name: 'Close', exact: true }).click();
  dialog = await openUnit(page, desk);
  await confirmStep(page, dialog, 'unit-delete-archived', 'Delete');
  await expect(dialog).toBeHidden();
  dialog = await openUnit(page, office);
  await confirmStep(page, dialog, 'unit-delete-inactive', 'Delete');
  await expect(dialog).toBeHidden();
  await gotoReady(page, organizationPage);
  await expect(treeItem(page, office)).toHaveCount(0);

  dialog = await openUnit(page, tenant);
  await expectGreyed(dialog, 'unit-delete', 'The Company unit is the whole company and is never deleted.', true);
  await capture(page, 'organization', '14-root-delete-refused', width);
  await expectGreyed(dialog, 'unit-deactivate', 'The Company unit is the whole company and stays active.');
  await dialog.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(dialog).toBeHidden();
});

test(`a role is created with its permissions, its delete refused with the reason while held, and deleted once it is not`, { tag: '@admin' }, async ({ page }) => {
  test.setTimeout(300_000);
  await page.setViewportSize({ width, height: 800 });
  const state = readFoundationState();
  const administrator = state.tenantAdministrator;
  const viewer = state.tenantViewer;
  await loginAs(page, persona(personaKeys.platformOwner, state));
  await impersonate(page, administrator);
  await gotoReady(page, '/tenant/management/roles');

  const name = `Planner ${width}${Date.now().toString(36)}`;
  await page.getByRole('button', { name: 'New Role', exact: true }).click();
  const created = page.getByRole('dialog', { name: 'New role', exact: true });
  await expect(created).toBeVisible();
  await created.locator('[name="role-name"]').fill(name);
  await created.getByRole('tab', { name: 'Permissions' }).click();
  await created.getByRole('button', { name: 'Show Users' }).click();
  await created.locator('[data-permission="UsersView"] .rz-chkbox-box').click();
  await expect(step(created, 'role-create')).toBeVisible();
  await capture(page, 'roles', '10-new-role-permissions', width);
  await step(created, 'role-create').click();
  const dialog = page.getByRole('dialog', { name, exact: true });
  await expectState(dialog, 'Active');
  await capture(page, 'roles', '11-role-active', width);

  await dialog.getByRole('tab', { name: 'Holders' }).click();
  await expect(dialog.getByText('Nobody holds this role')).toBeVisible();
  await dialog.getByRole('button', { name: 'Add holder', exact: true }).click();
  const add = page.locator('.form-dialog').filter({ has: page.getByRole('button', { name: 'Add role', exact: true }) });
  await expect(add).toBeVisible();
  await add.locator('.rz-dropdown').first().click();
  await page.getByRole('option', { name: viewer.name, exact: true }).click();
  await add.getByRole('button', { name: 'Add role', exact: true }).click();
  await expect(add).toBeHidden();
  await expect(dialog.locator('tr.list-row').filter({ hasText: viewer.name })).toHaveCount(1);
  await capture(page, 'roles', '12-role-holders', width);

  await expectGreyed(dialog, 'role-delete', 'Someone holds this role: remove it from them first.', true);
  await capture(page, 'roles', '13-role-delete-refused', width);

  await dialog.getByRole('tab', { name: 'Holders' }).click();
  await dialog.locator('tr.list-row').filter({ hasText: viewer.name }).locator('td').first().click();
  const person = page.getByRole('dialog', { name: viewer.name, exact: true });
  await expect(person).toBeVisible();
  await person.getByRole('tab', { name: 'Roles' }).click();
  await person.locator('tr.list-row').filter({ hasText: name }).locator('td').first().click();
  const holding = page.locator('.form-dialog').filter({ has: page.getByRole('button', { name: 'Remove role', exact: true }) });
  await holding.getByRole('button', { name: 'Remove role', exact: true }).click();
  await expect(holding).toBeHidden();
  await expect(person.locator('tr.list-row').filter({ hasText: name })).toHaveCount(0);
  await page.keyboard.press('Escape');
  await expect(person).toBeHidden();
  await expect(dialog.getByText('Nobody holds this role')).toBeVisible();
  await expect(step(dialog, 'role-delete')).not.toHaveAttribute('aria-disabled', 'true');
  await confirmStep(page, dialog, 'role-delete', 'Delete');
  await expect(dialog).toBeHidden();
  await expect(page.locator('tr.list-row .list-row__open').filter({ hasText: name })).toHaveCount(0);
});
