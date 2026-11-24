import type { Locator, Page } from '@playwright/test';
import { persona, personaKeys, personaName, readFoundationState } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { expectSettledPageAccessible, gotoReady, impersonate, loginAs } from './lib/session';
import { shot } from './lib/shots';

test.use({ viewport: { width: 1440, height: 900 } });

async function capture(page: Page, name: string): Promise<void> {
  await expectSettledPageAccessible(page);
  await shot(page, 'work-patterns', name, [page.locator('.state-entity-dialog-footer__notice')]);
}

async function confirmStep(page: Page, dialog: Locator, key: string, label: string): Promise<void> {
  await dialog.locator(`button[data-transition="${key}"]`).focus();
  await page.keyboard.press('Enter');
  const confirm = page.locator('.form-dialog').filter({ has: page.getByRole('button', { name: label, exact: true }) });
  await confirm.getByRole('button', { name: label, exact: true }).click();
  await expect(confirm).toBeHidden();
}

async function toggleStaff(page: Page, form: Locator, names: string[]): Promise<void> {
  await form.locator('.rz-dropdown').filter({ has: page.locator('[name="assign-pattern-staff"]') }).click();
  for (const name of names) {
    await page.getByRole('option').filter({ hasText: name }).first().click();
  }
  await page.keyboard.press('Escape');
}

test('an administrator sees who has a work pattern and gives one pattern to several staff members at once', { tag: ['@staffing', '@homes'] }, async ({ page }) => {
  test.setTimeout(180_000);
  const state = readFoundationState();
  const staff = personaName(persona(personaKeys.staff, state));
  const second = personaName(persona(personaKeys.secondStaff, state));
  const pattern = `Bulk ${Date.now().toString(36)}${Math.floor(Math.random() * 1000)}`;
  await loginAs(page, persona(personaKeys.platformOwner, state));
  await impersonate(page, state.tenantAdministrator);

  await gotoReady(page, '/staffing/staff');
  await expect(page.locator('th').filter({ hasText: 'Work pattern' }).first()).toBeVisible();
  await expect(page.locator('tr.list-row [data-work-pattern="current"]').first()).toBeVisible();
  await capture(page, '09-staff-list-work-pattern-column');
  await gotoReady(page, '/staffing/staff?pattern=none');
  await expect(page.locator('.list-chip--on').filter({ hasText: 'No work pattern' })).toBeVisible();
  await expect(page.locator('tr.list-row [data-work-pattern]:not([data-work-pattern="none"])')).toHaveCount(0);
  await capture(page, '10-staff-list-no-pattern-filter');

  await gotoReady(page, '/staffing/work-patterns');
  await page.getByRole('button', { name: 'New pattern', exact: true }).click();
  const draft = page.getByRole('dialog', { name: 'New work pattern', exact: true });
  await draft.locator('[name="pattern-name"]').fill(pattern);
  for (const day of [0, 1, 2, 3, 4]) await draft.locator(`[data-add-day="${day}"]`).click();
  await draft.locator('button[data-transition="pattern-save"]').click();
  const dialog = page.getByRole('dialog', { name: pattern, exact: true });
  await expect(dialog.locator('.state-pill__button')).toHaveAttribute('data-state', 'Draft');
  await confirmStep(page, dialog, 'pattern-publish', 'Publish');
  await expect(dialog.locator('.state-pill__button')).toHaveAttribute('data-state', 'Published');

  await dialog.getByRole('tab', { name: 'Staff' }).click();
  await expect(dialog.getByText('Nobody works by this pattern')).toBeVisible();
  await dialog.getByRole('button', { name: 'Assign to staff', exact: true }).click();
  const form = page.locator('[data-form-dialog="AssignPatternToStaffDialog"]');
  await expect(form.locator('[name="assign-pattern-staff"]')).toHaveCount(1);

  await form.locator('.rz-dropdown').filter({ has: page.locator('[name="assign-pattern-staff"]') }).click();
  const busyName = ((await page.getByRole('option').filter({ hasText: '· since' }).first().textContent()) ?? '').split(' (')[0].trim();
  await page.keyboard.press('Escape');
  await toggleStaff(page, form, [busyName, staff]);
  await expect(form.locator('[data-chosen-count]')).toHaveAttribute('data-chosen-count', '2');
  await capture(page, '12-assign-form');
  await form.getByRole('button', { name: 'Assign', exact: true }).click();
  const refused = form.locator('[data-assign-error] [data-refused]');
  await expect(refused).toHaveCount(1);
  await expect(refused).toContainText(busyName);
  await expect(refused).toContainText('overlaps the active assignment');
  await capture(page, '13-assign-form-refused');

  await toggleStaff(page, form, [busyName, second]);
  await expect(form.locator('[data-chosen-count]')).toHaveAttribute('data-chosen-count', '2');
  await form.getByRole('button', { name: 'Assign', exact: true }).click();
  await expect(form).toBeHidden();
  const rows = dialog.locator('tr.list-row');
  await expect(rows).toHaveCount(2);
  await expect(rows.filter({ hasText: staff })).toContainText('Current');
  await expect(rows.filter({ hasText: second })).toContainText('Current');
  await capture(page, '11-pattern-staff-view');

  await dialog.getByRole('button', { name: 'Close', exact: true }).first().click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(page.locator('tr.list-row').filter({ hasText: pattern }).locator('[data-pattern-staff]')).toHaveText('2 today');

  await page.locator('tr.list-row').filter({ hasText: pattern }).click();
  await dialog.getByRole('tab', { name: 'Staff' }).click();
  for (const name of [staff, second]) {
    await dialog.locator('tr.list-row').filter({ hasText: name }).locator('td').first().click();
    const assignment = page.getByRole('dialog').last();
    await expect(assignment.locator('.state-pill__button')).toHaveAttribute('data-state', 'Active');
    await confirmStep(page, assignment, 'pattern-assignment-deactivate', 'Withdraw');
    await expect(assignment.locator('.state-pill__button')).toHaveAttribute('data-state', 'Inactive');
    await assignment.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(1);
  }
});
