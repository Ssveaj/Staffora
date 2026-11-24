import type { Locator, Page } from '@playwright/test';
import { persona, personaKeys, personaName, readFoundationState } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { editDialog, expectSettledPageAccessible, gotoReady, loginAs } from './lib/session';
import { shot } from './lib/shots';

const width = 1280;

async function capture(page: Page, name: string, width: number): Promise<void> {
  await expectSettledPageAccessible(page);
  await shot(page, 'qualifications', `${name}-${width}`, [page.locator('.state-entity-dialog-footer__notice')]);
}

async function confirmStep(page: Page, dialog: Locator, key: string, label: string): Promise<void> {
  await dialog.locator(`button[data-transition="${key}"]`).focus();
  await page.keyboard.press('Enter');
  const confirm = page.locator('.form-dialog').filter({ has: page.getByRole('button', { name: label, exact: true }) });
  await confirm.getByRole('button', { name: label, exact: true }).click();
  await expect(confirm).toBeHidden();
}

async function createQualification(page: Page, name: string): Promise<Locator> {
  await page.getByRole('button', { name: 'New qualification', exact: true }).click();
  const created = page.getByRole('dialog', { name: 'New qualification', exact: true });
  await created.locator('[name="qualification-name"]').fill(name);
  await created.locator('button[data-transition="qualification-create"]').click();
  const dialog = page.getByRole('dialog', { name, exact: true });
  await expect(dialog.locator('.state-pill__button')).toHaveAttribute('data-state', 'Active');
  return dialog;
}

test(`a coordinator maintains the catalogue and gives a staff member two qualifications`, { tag: '@staffing' }, async ({ page }) => {
  test.setTimeout(150_000);
  await page.setViewportSize({ width, height: 800 });
  const state = readFoundationState();
  const staffName = personaName(persona(personaKeys.staff, state));
  const suffix = `${Date.now().toString(36)}${Math.floor(Math.random() * 1000)}`;
  const maths = `Mathematics ${suffix}`;
  const physics = `Physics ${suffix}`;
  const latin = `Latin ${suffix}`;
  await loginAs(page, persona(personaKeys.coordinator, state));
  await gotoReady(page, '/staffing/qualifications');

  let dialog = await createQualification(page, maths);
  await capture(page, '02-qualification-created', width);
  await dialog.getByRole('button', { name: 'Close', exact: true }).click();
  dialog = await createQualification(page, physics);
  await editDialog(dialog);
  await dialog.locator('[name="qualification-description"]').fill('Upper secondary physics');
  await dialog.locator('button[data-transition="qualification-save"]').click();
  await expect(dialog.locator('.state-entity-dialog-footer__status')).toContainText('Save: done.');
  await dialog.getByRole('button', { name: 'Close', exact: true }).click();
  dialog = await createQualification(page, latin);
  await confirmStep(page, dialog, 'qualification-archive', 'Archive');
  await expect(dialog.locator('.state-pill__button')).toHaveAttribute('data-state', 'Archived');
  await expect(dialog.locator('[name="qualification-name"]')).toHaveAttribute('readonly', '');
  await capture(page, '03-qualification-archived', width);
  await dialog.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  for (const name of [maths, physics, latin]) await expect(page.locator('tr.list-row').filter({ hasText: name })).toHaveCount(1);
  await expect(page.locator('tr.list-row').filter({ hasText: physics })).toContainText('Upper secondary physics');
  await capture(page, '01-catalogue', width);

  await gotoReady(page, '/staffing/staff');
  await page.locator('tr.list-row').filter({ hasText: staffName }).click();
  const staffMember = page.getByRole('dialog', { name: staffName, exact: true });
  await staffMember.getByRole('tab', { name: 'Qualifications' }).click();
  for (const name of [maths, physics]) {
    await staffMember.getByRole('button', { name: 'Add qualification', exact: true }).click();
    const form = page.getByRole('dialog').last();
    await form.locator('.rz-dropdown').filter({ has: page.locator('[name="staff-qualification"]') }).click();
    await expect(page.getByRole('option', { name: latin, exact: true }), 'the archived qualification is never offered').toHaveCount(0);
    await page.getByRole('option', { name, exact: true }).click();
    if (name === maths) await capture(page, '04-add-qualification', width);
    await form.getByRole('button', { name: 'Add qualification', exact: true }).click();
    await expect(page.getByRole('dialog')).toHaveCount(1);
    await expect(staffMember.locator('tr.list-row').filter({ hasText: name })).toHaveCount(1);
  }
  await capture(page, '05-staff-qualifications', width);
  await staffMember.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(staffMember).toBeHidden();

  await gotoReady(page, '/staffing/qualifications');
  await page.locator('tr.list-row').filter({ hasText: maths }).click();
  const qualification = page.getByRole('dialog', { name: maths, exact: true });
  await qualification.getByRole('tab', { name: 'Staff', exact: true }).click();
  const holder = qualification.locator('tr.list-row').filter({ hasText: staffName });
  await expect(holder).toHaveCount(1);
  await expect(holder).toContainText('Active');
  await capture(page, '06-qualification-staff', width);
  await holder.click();
  const holderDialog = page.getByRole('dialog', { name: staffName, exact: true });
  await expect(holderDialog.locator('.state-pill__button')).toBeVisible();
  await holderDialog.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(holderDialog).toBeHidden();
  await expect(qualification.locator('tr.list-row').filter({ hasText: staffName })).toHaveCount(1);
  await qualification.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
});
