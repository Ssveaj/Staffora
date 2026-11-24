import type { Locator, Page } from '@playwright/test';
import { persona, personaKeys, personaName, readFoundationState } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { expectSettledPageAccessible, gotoReady, impersonate, loginAs } from './lib/session';
import { shot } from './lib/shots';

const width = 1280;

const staffMembersPage = '/staffing/staff';

async function capture(page: Page, area: string, name: string, width: number): Promise<void> {
  await expectSettledPageAccessible(page);
  await shot(page, area, `${name}-${width}`, [page.locator('.state-entity-dialog-footer__notice')]);
}

const row = (page: Page, name: string): Locator => page.locator('tr.list-row').filter({ has: page.locator('td[data-label="Name"]', { hasText: name }) });

test(`a coordinator finds the staff members and opens a staff member's record in the user dialog`, { tag: ['@staffing', '@admin'] }, async ({ page }) => {
  test.setTimeout(120_000);
  await page.setViewportSize({ width, height: 800 });
  const state = readFoundationState();
  const staffMember = persona(personaKeys.staff, state);
  await loginAs(page, persona(personaKeys.coordinator, state));
  await gotoReady(page, staffMembersPage);

  const kim = row(page, personaName(staffMember));
  await expect(kim).toHaveCount(1);
  await expect(kim).toContainText('Head office');
  await expect(kim).toContainText('Active');
  await expect(page.getByRole('button', { name: 'New staff member', exact: true })).toHaveCount(0);
  await capture(page, 'staff', '01-staff-list', width);

  await kim.click();
  const dialog = page.getByRole('dialog', { name: personaName(staffMember), exact: true });
  await expect(dialog).toBeVisible();
  await expect(dialog.locator('[name="user-email"]')).toHaveValue(staffMember.email);
  await dialog.getByRole('tab', { name: /^Staff(?!ing)/ }).click();
  await expect(dialog.locator('[data-staff-status]')).toContainText('Active');
  await expect(dialog.locator('tr.list-row').filter({ hasText: 'Head office' })).toHaveCount(1);
  await capture(page, 'staff', '02-staff-view', width);
  await dialog.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(dialog).toBeHidden();
});

test(`the role editor shows the Staff role's permissions grouped by area with its holders staff members`, { tag: ['@staffing', '@admin'] }, async ({ page }) => {
  test.setTimeout(120_000);
  await page.setViewportSize({ width, height: 800 });
  const state = readFoundationState();
  await loginAs(page, persona(personaKeys.platformOwner, state));
  await impersonate(page, state.tenantAdministrator);

  await gotoReady(page, '/tenant/management/roles');
  await page.locator('.list-row__open').filter({ hasText: /^Staff$/ }).click();
  const staffRole = page.getByRole('dialog', { name: 'Staff', exact: true });
  await expect(staffRole).toBeVisible();
  await expect(staffRole.locator('[data-field="ApplyResourceMembershipToUser"][data-access="read-only"] input')).toHaveValue('Yes');
  await capture(page, 'roles', '02-staff-role-info', width);

  await staffRole.getByRole('tab', { name: 'Permissions' }).click();
  const groups = staffRole.locator('.state-permission-matrix fieldset');
  for (const area of ['Personal', 'Staff', 'Schedules and absence', 'Customers', 'Jobs', 'Coordination']) {
    await expect(groups.filter({ has: page.locator('legend', { hasText: area }) }), `a group for ${area}`).toHaveCount(1);
  }
  const personal = groups.filter({ has: page.locator('legend', { hasText: 'Personal' }) });
  for (const permission of ['ViewOwnAssignments', 'RespondToAssignment', 'ViewOwnSchedule', 'ReportOwnAbsence']) {
    await expect(personal.locator(`[data-permission="${permission}"] .rz-chkbox-box`)).toHaveClass(/rz-state-active/);
  }
  await capture(page, 'roles', '03-staff-role-permissions', width);
  await staffRole.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(staffRole).toBeHidden();

  await page.locator('.list-row__open').filter({ hasText: /^Coordinator$/ }).click();
  const coordinatorRole = page.getByRole('dialog', { name: 'Coordinator', exact: true });
  await expect(coordinatorRole.locator('[data-field="ApplyResourceMembershipToUser"][data-access="read-only"] input')).toHaveValue('No');
  await coordinatorRole.getByRole('tab', { name: 'Permissions' }).click();
  const coordination = coordinatorRole.locator('.state-permission-matrix fieldset').filter({ has: page.locator('legend', { hasText: 'Coordination' }) });
  await expect(coordination.locator('[data-permission="PerformCoordination"] .rz-chkbox-box')).toHaveClass(/rz-state-active/);
  await capture(page, 'roles', '04-coordinator-role-permissions', width);
  await coordinatorRole.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(coordinatorRole).toBeHidden();
});

test(`the system owner makes a new person a staff member of the head office with "New staff member"`, { tag: ['@staffing', '@admin'] }, async ({ page }) => {
  test.setTimeout(120_000);
  await page.setViewportSize({ width, height: 800 });
  const state = readFoundationState();
  await loginAs(page, persona(personaKeys.platformOwner, state));
  await impersonate(page, state.tenantAdministrator);

  const suffix = `${Date.now().toString(36)}${Math.floor(Math.random() * 1000)}`;
  const email = `new.staff.${suffix}@company-a.example`;
  const name = `Tove Ek ${suffix}`;
  await gotoReady(page, '/tenant/management/users');
  await page.getByRole('button', { name: 'New User', exact: true }).click();
  const created = page.getByRole('dialog', { name: 'New user', exact: true });
  await created.locator('[name="user-first-name"]').fill('Tove');
  await created.locator('[name="user-last-name"]').fill(`Ek ${suffix}`);
  await created.locator('[name="user-email"]').fill(email);
  await created.locator('[name="user-username"]').fill(email);
  await created.locator('button[data-transition="user-create"]').click();
  const user = page.getByRole('dialog', { name, exact: true });
  await expect(user.locator('.state-pill__button')).toHaveAttribute('data-state', 'Active');
  await user.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(user).toBeHidden();

  await gotoReady(page, staffMembersPage);
  await page.getByRole('button', { name: 'New staff member', exact: true }).click();
  const form = page.getByRole('dialog', { name: 'New staff member', exact: true });
  await expect(form).toBeVisible();
  await expect(form.locator('[name="user-role-role"]')).toHaveCount(0);
  await form.locator('.rz-dropdown').filter({ has: page.locator('[name="role-holding-person"]') }).click();
  await page.getByRole('option', { name, exact: true }).click();
  await form.locator('.rz-dropdown').filter({ has: page.locator('[name="user-role-unit"]') }).click();
  await page.getByRole('option', { name: `${state.tenantAdministrator.tenant} › Head office`, exact: true }).click();
  await capture(page, 'staff', '03-new-staff', width);
  await form.getByRole('button', { name: 'Make staff member', exact: true }).click();
  await expect(form).toBeHidden();

  const added = row(page, name);
  await expect(added).toHaveCount(1);
  await expect(added).toContainText('Head office');
  await expect(added).toContainText('Active');
  await capture(page, 'staff', '04-new-staff-listed', width);
});
