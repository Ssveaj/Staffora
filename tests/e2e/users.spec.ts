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

async function capture(page: Page, name: string, width: number): Promise<void> {
  await expectSettledPageAccessible(page);
  await shot(page, 'users', `${name}-${width}`, [page.locator('.state-entity-dialog-footer__notice')]);
}

test(`a tenant administrator creates, deactivates and reactivates a user through the user dialog`, { tag: ['@admin', '@dialogs', '@smoke'] }, async ({ page }) => {
  test.setTimeout(120_000);
  await page.setViewportSize({ width, height: 800 });
  const state = readFoundationState();
  const administrator = state.tenantAdministrator;
  await loginAs(page, persona(personaKeys.platformOwner, state));
  await impersonate(page, administrator);
  await gotoReady(page, usersPage);

  const suffix = `${width}${Date.now().toString(36)}${Math.floor(Math.random() * 1000)}`;
  const email = `new.user.${suffix}@company-a.example`;
  await page.getByRole('button', { name: 'New User', exact: true }).click();
  const created = page.getByRole('dialog', { name: 'New user', exact: true });
  await expect(created).toBeVisible();
  await expect(created.locator('[name="user-first-name"]')).toHaveAttribute('aria-required', 'true');
  await expect(created.locator('button[data-transition]')).toHaveCount(0);
  await created.locator('[name="user-first-name"]').fill('Noor');
  await created.locator('[name="user-last-name"]').fill(`Lindqvist ${suffix}`);
  await created.locator('[name="user-email"]').fill(email);
  await created.locator('[name="user-username"]').fill(email);
  await expect(step(created, 'user-create')).toBeVisible();
  await capture(page, '01-new-user', width);

  await step(created, 'user-create').click();
  const name = `Noor Lindqvist ${suffix}`;
  const dialog = page.getByRole('dialog', { name, exact: true });
  await expectState(dialog, 'Active');
  await expect(dialog.locator('.state-entity-dialog-footer__notice')).toContainText('Their initial password is ');
  await capture(page, '02-active-info', width);

  await dialog.getByRole('tab', { name: 'Roles' }).click();
  await expect(dialog.getByText('No roles yet')).toBeVisible();
  await dialog.getByRole('button', { name: 'Add role', exact: true }).click();
  const add = page.locator('.form-dialog').filter({ has: page.getByRole('button', { name: 'Add role', exact: true }) });
  await expect(add).toBeVisible();
  await add.locator('.rz-dropdown').first().click();
  await page.getByRole('option', { name: 'Viewer', exact: true }).click();
  await add.getByRole('button', { name: 'Add role', exact: true }).click();
  await expect(add).toBeHidden();
  await expect(dialog.locator('tr.list-row').filter({ hasText: 'Viewer' })).toHaveCount(1);
  await capture(page, '03-roles', width);

  await dialog.getByRole('tab', { name: 'Info' }).click();
  await step(dialog, 'user-deactivate').click();
  const confirm = page.locator('.form-dialog').filter({ has: page.getByRole('button', { name: 'Deactivate', exact: true }) });
  await confirm.getByRole('button', { name: 'Deactivate', exact: true }).click();
  await expectState(dialog, 'Inactive');
  for (const field of ['user-first-name', 'user-last-name', 'user-email', 'user-username', 'user-phone']) {
    await expect(dialog.locator(`[name="${field}"]`), `${field} is read-only while inactive`).toHaveAttribute('readonly');
  }
  await expect(dialog.locator('button[data-transition]')).toHaveCount(1);
  await capture(page, '04-inactive', width);

  await step(dialog, 'user-reactivate').click();
  await expectState(dialog, 'Active');

  await step(dialog, 'user-new-password').focus();
  await page.keyboard.press('Enter');
  const reset = page.locator('.form-dialog').filter({ has: page.getByRole('button', { name: 'New password', exact: true }) });
  await expect(reset).toContainText('signed out everywhere');
  await reset.getByRole('button', { name: 'New password', exact: true }).click();
  await expect(dialog.locator('.state-entity-dialog-footer__notice')).toContainText('The new password is ');
  await capture(page, '05-new-password', width);
  await dialog.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(dialog).toBeHidden();

  await page.getByRole('button', { name: administrator.name, exact: true }).click();
  const own = page.getByRole('dialog', { name: administrator.name, exact: true });
  await expectState(own, 'Active');
  const deactivate = step(own, 'user-deactivate');
  await expect(deactivate).toHaveAttribute('aria-disabled', 'true');
  await deactivate.hover();
  const card = own.locator(`#${await deactivate.getAttribute('aria-describedby')}`);
  await expect(card).toBeVisible();
  await expect(card).toContainText('You cannot deactivate your own account');
  await expect(step(own, 'user-new-password')).not.toHaveAttribute('aria-disabled', 'true');
  await capture(page, '06-refused-deactivate', width);
  await own.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(own).toBeHidden();
});
