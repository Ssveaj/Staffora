import type { Page } from '@playwright/test';
import { persona, personaKeys, personaName, readFoundationState } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { editDialog, endImpersonation, gotoReady, impersonate, loginAs, waitForSettled } from './lib/session';
import { shot } from './lib/shots';
import { sweeper, type SweepPart } from './lib/sweep';
import { chooseEveryWeek, saveRegisteredJob } from './lib/jobs';

test.use({ viewport: { width: 1280, height: 800 } });

async function asTenantAdministrator(page: Page, part: SweepPart) {
  const state = readFoundationState();
  const sweep = sweeper(page, part);
  await loginAs(page, persona(personaKeys.platformOwner, state));
  await impersonate(page, state.tenantAdministrator);
  return { state, ...sweep };
}

test('the staff members, the role editor, the qualifications and the work patterns of a tenant administrator are free of serious accessibility violations', { tag: '@a11y' }, async ({ page }) => {
  test.setTimeout(240_000);
  const { state, visit, dialog, fromPage, finish } = await asTenantAdministrator(page, 'sweep-staffing: staff and catalogues');

  await test.step('the staff members and the role editor', async () => {
    const staffName = personaName(persona(personaKeys.staff, state));
    for (const width of [390, 1280]) {
      await page.setViewportSize({ width, height: width === 390 ? 844 : 800 });
      await visit(`admin (${width})`, '/staffing/staff');
      await shot(page, 'staff', `05-sweep-staff-list-${width}`);
      await dialog(`admin dialog: staff view (${width})`, 'UserDialog', fromPage('/staffing/staff', async (p) => {
        await p.locator('tr.list-row').filter({ hasText: staffName }).click();
        const shown = p.getByRole('dialog', { name: staffName, exact: true });
        await shown.getByRole('tab', { name: /^Staff(?!ing)/ }).click();
        await expect(shown.locator('[data-staff-status]')).toContainText('Active');
        await expect(shown.locator('tr.list-row').filter({ hasText: 'Head office' })).toHaveCount(1);
      }), { shotName: `staff-view-${width}` });
      await dialog(`admin dialog: new staff member (${width})`, 'AddRoleHoldingDialog', fromPage('/staffing/staff', (p) => p.getByRole('button', { name: 'New staff member', exact: true }).click()));
      await dialog(`admin dialog: role permissions grouped (${width})`, 'TenantRoleDialog', fromPage('/tenant/management/roles', async (p) => {
        await p.locator('.list-row__open').filter({ hasText: /^Staff$/ }).click();
        const shown = p.getByRole('dialog', { name: 'Staff', exact: true });
        await shown.getByRole('tab', { name: 'Permissions' }).click();
        await expect(shown.locator('.state-permission-matrix fieldset').filter({ has: p.locator('legend', { hasText: 'Coordination' }) })).toHaveCount(1);
      }), { shotName: `staff-role-permissions-${width}` });
    }
    await page.setViewportSize({ width: 1280, height: 800 });
  });

  await test.step('qualifications and work patterns', async () => {
    const staffName = personaName(persona(personaKeys.staff, state));
    const suffix = Date.now().toString(36);
    const qualification = `Sweep qualification ${suffix}`;
    const pattern = `Sweep pattern ${suffix}`;
    await fromPage('/staffing/qualifications', (p) => p.getByRole('button', { name: 'New qualification', exact: true }).click())();
    const newQualification = page.getByRole('dialog', { name: 'New qualification', exact: true });
    await newQualification.locator('[name="qualification-name"]').fill(qualification);
    await newQualification.locator('button[data-transition="qualification-create"]').click();
    await expect(page.getByRole('dialog', { name: qualification, exact: true }).locator('.state-pill__button')).toHaveAttribute('data-state', 'Active');
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);
    const openStaffQualifications = async (p: Page) => {
      await p.locator('tr.list-row').filter({ hasText: staffName }).click();
      const shown = p.getByRole('dialog', { name: staffName, exact: true });
      await shown.getByRole('tab', { name: 'Qualifications' }).click();
      await expect(shown.getByRole('button', { name: 'Add qualification', exact: true })).toBeVisible();
    };
    await fromPage('/staffing/staff', openStaffQualifications)();
    const staffDialog = page.getByRole('dialog', { name: staffName, exact: true });
    await dialog('admin dialog: add qualification', 'AddQualificationDialog', async () => {
      await staffDialog.getByRole('button', { name: 'Add qualification', exact: true }).click();
      await expect(page.getByRole('dialog').last().locator('[name="staff-qualification"]')).toHaveCount(1);
    });
    await staffDialog.getByRole('button', { name: 'Add qualification', exact: true }).click();
    const addForm = page.getByRole('dialog').last();
    await addForm.locator('.rz-dropdown').filter({ has: page.locator('[name="staff-qualification"]') }).click();
    await page.getByRole('option', { name: qualification, exact: true }).click();
    await addForm.getByRole('button', { name: 'Add qualification', exact: true }).click();
    await expect(page.getByRole('dialog')).toHaveCount(1);
    await expect(staffDialog.locator('tr.list-row').filter({ hasText: qualification })).toHaveCount(1);
    await dialog('admin dialog: staff qualification', 'StaffQualificationDialog', () => staffDialog.locator('tr.list-row').filter({ hasText: qualification }).locator('td').first().click());
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await fromPage('/staffing/work-patterns', (p) => p.getByRole('button', { name: 'New pattern', exact: true }).click())();
    const newPattern = page.getByRole('dialog', { name: 'New work pattern', exact: true });
    await newPattern.locator('[name="pattern-name"]').fill(pattern);
    await newPattern.locator('[data-add-day="0"]').click();
    await newPattern.locator('button[data-transition="pattern-save"]').click();
    await expect(page.getByRole('dialog', { name: pattern, exact: true }).locator('.state-pill__button')).toHaveAttribute('data-state', 'Draft');
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);

    for (const width of [390, 1280]) {
      await page.setViewportSize({ width, height: width === 390 ? 844 : 800 });
      await visit(`admin (${width})`, '/staffing/qualifications');
      await visit(`admin (${width})`, '/staffing/work-patterns');
      await dialog(`admin dialog: new qualification (${width})`, 'QualificationDialog', fromPage('/staffing/qualifications', (p) => p.getByRole('button', { name: 'New qualification', exact: true }).click()));
      await dialog(`admin dialog: qualification (${width})`, 'QualificationDialog', fromPage('/staffing/qualifications', (p) => p.locator('tr.list-row').filter({ hasText: qualification }).click()));
      await dialog(`admin dialog: staff qualifications (${width})`, 'UserDialog', fromPage('/staffing/staff', async (p) => {
        await openStaffQualifications(p);
        await expect(p.getByRole('dialog').locator('tr.list-row').filter({ hasText: qualification })).toHaveCount(1);
      }));
      await dialog(`admin dialog: new work pattern (${width})`, 'WorkPatternDialog', fromPage('/staffing/work-patterns', (p) => p.getByRole('button', { name: 'New pattern', exact: true }).click()));
      await dialog(`admin dialog: work pattern (${width})`, 'WorkPatternDialog', fromPage('/staffing/work-patterns', async (p) => {
        await p.locator('tr.list-row').filter({ hasText: pattern }).click();
        await editDialog(p.getByRole('dialog'));
        await expect(p.getByRole('dialog').locator('[name="pattern-segment-0-1-start"]')).toBeVisible();
      }));
    }
    await page.setViewportSize({ width: 1280, height: 800 });
  });

  finish();
});

test(`the schedules and the absence of a tenant administrator's staff are free of serious accessibility violations`, { tag: '@a11y' }, async ({ page }) => {
  test.setTimeout(240_000);
  const { state, visit, dialog, fromPage, finish } = await asTenantAdministrator(page, 'sweep-staffing: schedules and absence');

  await test.step('schedules', async () => {
    const staffName = personaName(persona(personaKeys.staff, state));
    const pattern = `Sweep schedule ${Date.now().toString(36)}`;
    await fromPage('/staffing/work-patterns', (p) => p.getByRole('button', { name: 'New pattern', exact: true }).click())();
    const draft = page.getByRole('dialog', { name: 'New work pattern', exact: true });
    await draft.locator('[name="pattern-name"]').fill(pattern);
    await draft.locator('[data-add-day="0"]').click();
    await draft.locator('button[data-transition="pattern-save"]').click();
    const saved = page.getByRole('dialog', { name: pattern, exact: true });
    await expect(saved.locator('.state-pill__button')).toHaveAttribute('data-state', 'Draft');
    await saved.locator('button[data-transition="pattern-publish"]').focus();
    await page.keyboard.press('Enter');
    await page.locator('.form-dialog').getByRole('button', { name: 'Publish', exact: true }).click();
    await expect(saved.locator('.state-pill__button')).toHaveAttribute('data-state', 'Published');
    await saved.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    const openPatternStaff = async (p: Page) => {
      await p.locator('tr.list-row').filter({ hasText: pattern }).click();
      const shown = p.getByRole('dialog', { name: pattern, exact: true });
      await shown.getByRole('tab', { name: 'Staff' }).click();
      await expect(shown.getByRole('button', { name: 'Assign to staff', exact: true })).toBeVisible();
    };
    for (const width of [390, 1280]) {
      await page.setViewportSize({ width, height: width === 390 ? 844 : 800 });
      await dialog(`admin dialog: work pattern staff (${width})`, 'WorkPatternDialog', fromPage('/staffing/work-patterns', openPatternStaff));
      await fromPage('/staffing/work-patterns', openPatternStaff)();
      const patternDialog = page.getByRole('dialog', { name: pattern, exact: true });
      await dialog(`admin dialog: assign pattern to staff (${width})`, 'AssignPatternToStaffDialog', async () => {
        await patternDialog.getByRole('button', { name: 'Assign to staff', exact: true }).click();
        await expect(page.getByRole('dialog').last().locator('[name="assign-pattern-staff"]')).toHaveCount(1);
      });
      await patternDialog.getByRole('button', { name: 'Close', exact: true }).first().click();
      await expect(page.getByRole('dialog')).toHaveCount(0);
    }
    await page.setViewportSize({ width: 1280, height: 800 });
    const openSchedule = async (p: Page) => {
      await p.locator('tr.list-row').filter({ hasText: staffName }).click();
      const shown = p.getByRole('dialog', { name: staffName, exact: true });
      await shown.getByRole('tab', { name: 'Schedule' }).click();
      await expect(shown.locator('[data-staff-schedule] tbody tr')).toHaveCount(20);
    };
    await fromPage('/staffing/staff', openSchedule)();
    const staffDialog = page.getByRole('dialog', { name: staffName, exact: true });
    await staffDialog.getByRole('button', { name: 'Assign work pattern', exact: true }).click();
    const fresh = page.getByRole('dialog', { name: 'Assign work pattern', exact: true });
    await fresh.locator('.rz-dropdown').filter({ has: page.locator('[name="assignment-pattern"]') }).click();
    await page.getByRole('option', { name: pattern, exact: true }).click();
    await fresh.locator('button[data-transition="pattern-assignment-save"]').click();
    const created = page.getByRole('dialog').last();
    await expect(created.locator('.state-pill__button')).toHaveAttribute('data-state', 'Registration');
    await created.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(1);
    const prepared = staffDialog.locator('tr.list-row').filter({ hasText: pattern });
    await expect(prepared).toHaveCount(1);

    for (const width of [390, 1280]) {
      await page.setViewportSize({ width, height: width === 390 ? 844 : 800 });
      await dialog(`admin dialog: new pattern assignment (${width})`, 'PatternAssignmentDialog', () => staffDialog.getByRole('button', { name: 'Assign work pattern', exact: true }).click());
      await dialog(`admin dialog: pattern assignment (${width})`, 'PatternAssignmentDialog', () => prepared.locator('td').first().click());
    }
    await page.setViewportSize({ width: 1280, height: 800 });
    await staffDialog.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    for (const width of [390, 1280]) {
      await page.setViewportSize({ width, height: width === 390 ? 844 : 800 });
      await dialog(`admin dialog: staff schedule (${width})`, 'UserDialog', fromPage('/staffing/staff', openSchedule), { shotName: `staff-schedule-${width}` });
    }
    await page.setViewportSize({ width: 1280, height: 800 });

    await fromPage('/staffing/staff', openSchedule)();
    await staffDialog.locator('tr.list-row').filter({ hasText: pattern }).locator('td').first().click();
    const assignment = page.getByRole('dialog').last();
    await assignment.locator('button[data-transition="pattern-assignment-delete"]').focus();
    await page.keyboard.press('Enter');
    await page.locator('.form-dialog').getByRole('button', { name: 'Delete', exact: true }).click();
    await expect(page.getByRole('dialog')).toHaveCount(1);
    await staffDialog.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
  });

  await test.step('absence', async () => {
    const staffName = personaName(persona(personaKeys.staff, state));
    const openAbsence = async (p: Page) => {
      await p.locator('tr.list-row').filter({ hasText: staffName }).click();
      const shown = p.getByRole('dialog', { name: staffName, exact: true });
      await shown.getByRole('tab', { name: 'Absence' }).click();
      await expect(shown.getByRole('button', { name: 'Register absence', exact: true })).toBeVisible();
      await p.mouse.move(0, 0);
    };
    await fromPage('/staffing/staff', openAbsence)();
    const staffDialog = page.getByRole('dialog', { name: staffName, exact: true });
    await staffDialog.getByRole('button', { name: 'Register absence', exact: true }).focus();
    await page.keyboard.press('Enter');
    const fresh = page.getByRole('dialog').last();
    await expect(fresh.locator('[data-absence-note]')).toBeVisible();
    await fresh.locator('button[data-transition="absence-register"]').click();
    const created = page.getByRole('dialog').last();
    await expect(created.locator('.state-pill__button')).toHaveAttribute('data-state', 'Approved');
    await created.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(1);
    const registered = staffDialog.locator('tr.list-row').filter({ hasText: /\bApproved\b/ }).first();
    await expect(registered).toBeVisible();

    for (const width of [390, 1280]) {
      await page.setViewportSize({ width, height: width === 390 ? 844 : 800 });
      await dialog(`admin dialog: new absence (${width})`, 'AbsenceDialog', async () => {
        await staffDialog.getByRole('button', { name: 'Register absence', exact: true }).focus();
        await page.keyboard.press('Enter');
      }, { shotName: `absence-new-${width}`, wide: true });
      await dialog(`admin dialog: absence (${width})`, 'AbsenceDialog', () => registered.locator('td').first().click(), { shotName: `absence-${width}`, wide: true });
    }
    await page.setViewportSize({ width: 1280, height: 800 });
    await staffDialog.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    for (const width of [390, 1280]) {
      await page.setViewportSize({ width, height: width === 390 ? 844 : 800 });
      await visit(`admin (${width})`, '/staffing/absence');
      await dialog(`admin dialog: register absence (${width})`, 'ChooseAbsenceStaffDialog', fromPage('/staffing/absence', (p) => p.getByRole('button', { name: 'Register absence', exact: true }).click()));
      await dialog(`admin dialog: staff absence (${width})`, 'UserDialog', fromPage('/staffing/staff', openAbsence), { shotName: `staff-absence-${width}` });
    }
    await page.setViewportSize({ width: 1280, height: 800 });

    const staffMember = persona(personaKeys.staff, state);
    await endImpersonation(page);
    await impersonate(page, { tenant: state.tenantAdministrator.tenant, name: staffName, email: staffMember.email });
    await gotoReady(page, '/staffing/absence?status=approved');
    await page.locator('tr.list-row').first().click();
    const absence = page.getByRole('dialog').last();
    await absence.locator('button[data-transition="absence-cancel-approved"]').focus();
    await page.keyboard.press('Enter');
    await page.locator('.form-dialog').getByRole('button', { name: 'Cancel absence', exact: true }).click();
    await expect(absence.locator('.state-pill__button')).toHaveAttribute('data-state', 'Canceled');
    await absence.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await endImpersonation(page);
    await impersonate(page, state.tenantAdministrator);
  });

  finish();
});

test('the customers and their contacts of a tenant administrator are free of serious accessibility violations', { tag: '@a11y' }, async ({ page }) => {
  test.setTimeout(240_000);
  const { visit, dialog, fromPage, finish } = await asTenantAdministrator(page, 'sweep-staffing: customers');

  await test.step('customers', async () => {
    const customer = `Sweep customer ${Date.now().toString(36)}`;
    const customersOfTheSweep = `/staffing/customers?q=${encodeURIComponent(customer)}`;
    const openContacts = async (p: Page) => {
      await p.locator('tr.list-row').filter({ hasText: customer }).click();
      const shown = p.getByRole('dialog', { name: customer, exact: true });
      await shown.getByRole('tab', { name: 'Contacts' }).click();
      await expect(shown.getByRole('button', { name: 'Add contact', exact: true })).toBeVisible();
      await p.mouse.move(0, 0);
    };
    await fromPage('/staffing/customers', (p) => p.getByRole('button', { name: 'New customer', exact: true }).click())();
    const fresh = page.getByRole('dialog', { name: 'New customer', exact: true });
    await fresh.locator('[name="customer-name"]').fill(customer);
    await fresh.locator('button[data-transition="customer-save-draft"]').click();
    const saved = page.getByRole('dialog', { name: customer, exact: true });
    await expect(saved.locator('.state-pill__button')).toHaveAttribute('data-state', 'Create');
    await saved.getByRole('tab', { name: 'Contacts' }).click();
    await saved.getByRole('button', { name: 'Add contact', exact: true }).focus();
    await page.keyboard.press('Enter');
    const newContact = page.getByRole('dialog', { name: 'New contact', exact: true });
    await newContact.locator('[name="contact-first-name"]').fill('Jordan');
    await newContact.locator('[name="contact-last-name"]').fill('Sweep');
    await newContact.locator('button[data-transition="contact-save-draft"]').click();
    const contact = page.getByRole('dialog', { name: 'Jordan Sweep', exact: true });
    await expect(contact.locator('.state-pill__button')).toHaveAttribute('data-state', 'Create');
    await contact.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(1);
    const row = saved.locator('tr.list-row').filter({ hasText: 'Jordan Sweep' });
    await expect(row).toHaveCount(1);
    await saved.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    for (const width of [390, 1280]) {
      await page.setViewportSize({ width, height: width === 390 ? 844 : 800 });
      await visit(`admin (${width})`, '/staffing/customers');
      await dialog(`admin dialog: new customer (${width})`, 'CustomerDialog', fromPage('/staffing/customers', (p) => p.getByRole('button', { name: 'New customer', exact: true }).click()));
      await dialog(`admin dialog: customer (${width})`, 'CustomerDialog', fromPage(customersOfTheSweep, (p) => p.locator('tr.list-row').filter({ hasText: customer }).click()), { shotName: `customer-${width}`, wide: true });
      await dialog(`admin dialog: customer contacts (${width})`, 'CustomerDialog', fromPage(customersOfTheSweep, openContacts), { shotName: `customer-contacts-${width}` });
      await fromPage(customersOfTheSweep, openContacts)();
      const customerDialog = page.getByRole('dialog', { name: customer, exact: true });
      await dialog(`admin dialog: new contact (${width})`, 'CustomerContactDialog', async () => {
        await customerDialog.getByRole('button', { name: 'Add contact', exact: true }).focus();
        await page.keyboard.press('Enter');
      });
      await dialog(`admin dialog: contact (${width})`, 'CustomerContactDialog', () => customerDialog.locator('tr.list-row').filter({ hasText: 'Jordan Sweep' }).locator('td').first().click(), { shotName: `customer-contact-${width}`, wide: true });
      await customerDialog.getByRole('button', { name: 'Close', exact: true }).first().click();
      await expect(page.getByRole('dialog')).toHaveCount(0);
    }
    await page.setViewportSize({ width: 1280, height: 800 });

    await fromPage(customersOfTheSweep, (p) => p.locator('tr.list-row').filter({ hasText: customer }).click())();
    const draft = page.getByRole('dialog', { name: customer, exact: true });
    await draft.locator('button[data-transition="customer-delete-draft"]').focus();
    await page.keyboard.press('Enter');
    await page.locator('.form-dialog').getByRole('button', { name: 'Delete', exact: true }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await expect(page.locator('tr.list-row').filter({ hasText: customer })).toHaveCount(0);
  });

  finish();
});

test('the jobs, slots, assignments and request rounds of a tenant administrator are free of serious accessibility violations', { tag: '@a11y' }, async ({ page }) => {
  test.setTimeout(240_000);
  const { state, visit, dialog, fromPage, check, finish } = await asTenantAdministrator(page, 'sweep-staffing: jobs');

  await test.step('jobs', async () => {
    const staffMember = personaName(persona(personaKeys.staff, state));
    const suffix = Date.now().toString(36);
    const customer = `Sweep job customer ${suffix}`;
    const jobName = `Sweep job ${suffix}`;
    await fromPage('/staffing/customers', (p) => p.getByRole('button', { name: 'New customer', exact: true }).click())();
    const freshCustomer = page.getByRole('dialog', { name: 'New customer', exact: true });
    await freshCustomer.locator('[name="customer-name"]').fill(customer);
    await freshCustomer.locator('button[data-transition="customer-save-draft"]').click();
    const activeCustomer = page.getByRole('dialog', { name: customer, exact: true });
    await expect(activeCustomer.locator('.state-pill__button')).toHaveAttribute('data-state', 'Create');
    await activeCustomer.locator('button[data-transition="customer-activate"]').focus();
    await page.keyboard.press('Enter');
    await expect(activeCustomer.locator('.state-pill__button')).toHaveAttribute('data-state', 'Active');
    await dialog('admin dialog: enable portal', 'EnableCustomerPortalDialog', () => activeCustomer.locator('button[data-transition="customer-enable-portal"]').click(), {
      close: (shown) => shown.getByRole('button', { name: 'Cancel', exact: true }).click(),
    });
    await expect(activeCustomer.locator('[data-customer-portal]')).toHaveAttribute('data-customer-portal', 'none');
    await activeCustomer.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    await fromPage('/staffing/jobs', (p) => p.getByRole('button', { name: 'New job', exact: true }).click())();
    const freshJob = page.getByRole('dialog', { name: 'New job', exact: true });
    await chooseEveryWeek(freshJob);
    await freshJob.locator('[name="job-name"]').fill(jobName);
    await freshJob.locator('.rz-dropdown').filter({ has: page.locator('[name="job-customer"]') }).click();
    await page.getByRole('option', { name: customer, exact: true }).click();
    await waitForSettled(page);
    await freshJob.locator('button[data-transition="job-register"]').click();
    const saved = page.getByRole('dialog', { name: jobName, exact: true });
    await expect(saved.locator('.state-pill__button')).toHaveAttribute('data-state', 'Registration');
    await saveRegisteredJob(saved, async () => {
      await saved.getByRole('tab', { name: 'Timetable' }).click();
      await saved.locator('[data-add-day="2"]').click();
    });
    await saved.getByRole('tab', { name: 'Slots' }).click();
    await saved.getByRole('button', { name: 'Add slot', exact: true }).focus();
    await page.keyboard.press('Enter');
    const newSlot = page.getByRole('dialog', { name: 'New slot', exact: true });
    await newSlot.locator('[name="slot-name"]').fill('Lead');
    await newSlot.locator('button[data-transition="slot-add"]').click();
    const need = page.getByRole('dialog', { name: 'Lead', exact: true });
    await expect(need.locator('.state-pill__button')).toHaveAttribute('data-state', 'Active');
    await need.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(1);

    await saved.locator('button[data-transition="job-start-from-registration"]').focus();
    await page.keyboard.press('Enter');
    await expect(saved.locator('.state-pill__button')).toHaveAttribute('data-state', 'Coordinating');
    await saved.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    const jobsOfTheSweep = `/staffing/jobs?q=${encodeURIComponent(jobName)}`;
    const openJob = (view?: string) => async (p: Page) => {
      await p.locator('tr.list-row').filter({ hasText: jobName }).click();
      const shown = p.getByRole('dialog', { name: jobName, exact: true });
      if (view) await shown.getByRole('tab', { name: view }).click();
      if (view === 'Slots') await expect(shown.getByRole('button', { name: 'Add slot', exact: true })).toBeVisible();
      await p.mouse.move(0, 0);
    };
    for (const width of [390, 1280]) {
      await page.setViewportSize({ width, height: width === 390 ? 844 : 800 });
      await visit(`admin (${width})`, '/staffing/jobs');
      await dialog(`admin dialog: new job (${width})`, 'JobDialog', fromPage('/staffing/jobs', (p) => p.getByRole('button', { name: 'New job', exact: true }).click()));
      await dialog(`admin dialog: job (${width})`, 'JobDialog', fromPage(jobsOfTheSweep, openJob()), { shotName: `job-info-${width}`, wide: true });
      await dialog(`admin dialog: job timetable (${width})`, 'JobDialog', fromPage(jobsOfTheSweep, openJob('Timetable')), { shotName: `job-timetable-${width}` });
      await dialog(`admin dialog: job needs (${width})`, 'JobDialog', fromPage(jobsOfTheSweep, openJob('Slots')), { shotName: `job-slots-${width}` });
      await dialog(`admin dialog: job staffing (${width})`, 'JobDialog', fromPage(jobsOfTheSweep, async (p) => {
        await openJob('Staffing')(p);
        await expect(p.getByRole('dialog', { name: jobName, exact: true }).locator(`[data-slot-staffing] [data-candidate="${staffMember}"]`)).toHaveCount(1);
      }), { shotName: `job-staffing-${width}` });
      await fromPage(jobsOfTheSweep, openJob('Slots'))();
      const jobDialog = page.getByRole('dialog', { name: jobName, exact: true });
      await dialog(`admin dialog: new slot (${width})`, 'JobSlotDialog', async () => {
        await jobDialog.getByRole('button', { name: 'Add slot', exact: true }).focus();
        await page.keyboard.press('Enter');
      });
      await dialog(`admin dialog: need (${width})`, 'JobSlotDialog', () => jobDialog.locator('tr.list-row').filter({ hasText: 'Lead' }).locator('td').first().click(), { shotName: `slot-${width}` });
      await dialog(`admin dialog: need staffing (${width})`, 'JobSlotDialog', async () => {
        await jobDialog.locator('tr.list-row').filter({ hasText: 'Lead' }).locator('td').first().click();
        const slotDialog = page.getByRole('dialog', { name: 'Lead', exact: true });
        await slotDialog.getByRole('tab', { name: 'Staffing' }).click();
        await expect(slotDialog.locator(`[data-candidate="${staffMember}"]`)).toHaveCount(1);
      }, { shotName: `slot-staffing-${width}` });
      await jobDialog.locator('tr.list-row').filter({ hasText: 'Lead' }).locator('td').first().click();
      const slotDialog = page.getByRole('dialog', { name: 'Lead', exact: true });
      await slotDialog.getByRole('tab', { name: 'Staffing' }).click();

      await slotDialog.locator(`[data-candidate="${staffMember}"] button[data-staff-action="request"]`).click();
      await expect(slotDialog.locator(`[data-candidate="${staffMember}"]`)).toHaveCount(0);
      await slotDialog.getByRole('tab', { name: /^Staff(?!ing)/ }).click();
      const requested = slotDialog.locator('tr.list-row').filter({ hasText: staffMember }).filter({ hasText: 'Requested' });
      await expect(requested).toHaveCount(1);
      await check(`admin dialog: need staff members (${width})`);
      await shot(page, 'dialogs', `slot-staff-${width}`);
      await dialog(`admin dialog: assignment (${width})`, 'SlotAssignmentDialog', () => requested.locator('td').first().click(), { shotName: `assignment-${width}` });

      await requested.locator('td').first().click();
      const request = page.getByRole('dialog').nth(2);
      await request.locator('button[data-transition="assignment-cancel-request"]').focus();
      await page.keyboard.press('Enter');
      await page.locator('.form-dialog').getByRole('button', { name: 'Withdraw request', exact: true }).click();
      await expect(request.locator('.state-pill__button')).toHaveAttribute('data-state', 'Canceled');
      await request.getByRole('button', { name: 'Close', exact: true }).first().click();

      await slotDialog.getByRole('tab', { name: 'Staffing' }).click();
      await slotDialog.locator('button[data-request-several]').click();
      await slotDialog.locator(`[data-candidate="${staffMember}"] .rz-chkbox`).click();
      await check(`admin dialog: need staffing, request several (${width})`);
      await shot(page, 'dialogs', `slot-request-several-${width}`);
      await slotDialog.locator('button[data-send-round]').click();
      await expect(slotDialog.locator(`[data-candidate="${staffMember}"]`)).toHaveCount(0);
      await slotDialog.getByRole('tab', { name: 'Request rounds' }).click();
      await dialog(`admin dialog: request round (${width})`, 'RequestRoundDialog', () => slotDialog.locator('[data-view="rounds"] tr.list-row').first().locator('td').first().click(), { shotName: `request-round-${width}` });
      await slotDialog.locator('[data-view="rounds"] tr.list-row').first().locator('td').first().click();
      const round = page.getByRole('dialog').nth(2);
      await round.locator('button[data-transition="round-close"]').focus();
      await page.keyboard.press('Enter');
      await page.locator('.form-dialog').getByRole('button', { name: 'Close round', exact: true }).click();
      await expect(round.locator('.state-pill__button')).toHaveAttribute('data-state', 'Closed');
      await round.getByRole('button', { name: 'Close', exact: true }).first().click();
      await slotDialog.getByRole('button', { name: 'Close', exact: true }).first().click();
      await expect(page.getByRole('dialog')).toHaveCount(1);
      await jobDialog.getByRole('button', { name: 'Close', exact: true }).first().click();
      await expect(page.getByRole('dialog')).toHaveCount(0);
    }
    await page.setViewportSize({ width: 1280, height: 800 });
  });

  finish();
});
