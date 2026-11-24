import type { Browser, Locator, Page } from '@playwright/test';
import { buttonContrastViolations } from './lib/accessibility';
import { persona, personaKeys, personaName, readFoundationState } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { expectSettledPageAccessible, gotoReady, loginAs, waitForSettled } from './lib/session';
import { shot } from './lib/shots';
import { chooseEveryWeek, saveRegisteredJob, staffInPlace } from './lib/jobs';

async function capture(page: Page, name: string, width: number): Promise<void> {
  await page.mouse.move(0, 0);
  await expectSettledPageAccessible(page);
  await shot(page, 'jobs', `${name}-${width}`, [page.locator('.state-entity-dialog-footer__notice')]);
}

async function takeStep(page: Page, dialog: Locator, key: string): Promise<void> {
  await dialog.locator(`button[data-transition="${key}"]`).focus();
  await page.keyboard.press('Enter');
}

async function cancelWithReason(page: Page, dialog: Locator, key: string): Promise<void> {
  await dialog.locator(`button[data-transition="${key}"]`).focus();
  await page.keyboard.press('Enter');
  const prompt = page.locator('.form-dialog').filter({ has: page.getByRole('button', { name: 'Cancel job', exact: true }) });
  await prompt.getByLabel('The reason').fill('The run is over.');
  await prompt.getByRole('button', { name: 'Cancel job', exact: true }).click();
  await expect(prompt).toHaveCount(0);
}

async function confirmStep(page: Page, dialog: Locator, key: string, label: string): Promise<void> {
  await takeStep(page, dialog, key);
  const confirm = page.locator('.form-dialog').filter({ has: page.getByRole('button', { name: label, exact: true }) });
  await confirm.getByRole('button', { name: label, exact: true }).click();
  await expect(confirm).toBeHidden();
}

async function choose(page: Page, dialog: Locator, name: string, option: string): Promise<void> {
  await dialog.locator('.rz-dropdown').filter({ has: page.locator(`[name="${name}"]`) }).click();
  await page.getByRole('option', { name: option, exact: true }).click();
}

async function activeCustomer(page: Page, name: string): Promise<void> {
  await gotoReady(page, '/staffing/customers');
  await page.getByRole('button', { name: 'New customer', exact: true }).click();
  const fresh = page.getByRole('dialog', { name: 'New customer', exact: true });
  await fresh.locator('[name="customer-name"]').fill(name);
  await takeStep(page, fresh, 'customer-save-draft');
  const customer = page.getByRole('dialog', { name, exact: true });
  await expect(customer.locator('.state-pill__button')).toHaveAttribute('data-state', 'Create');
  await takeStep(page, customer, 'customer-activate');
  await expect(customer.locator('.state-pill__button')).toHaveAttribute('data-state', 'Active');
  await customer.getByRole('tab', { name: 'Contacts' }).click();
  await customer.getByRole('button', { name: 'Add contact', exact: true }).focus();
  await page.keyboard.press('Enter');
  const contact = page.getByRole('dialog', { name: 'New contact', exact: true });
  await contact.locator('[name="contact-first-name"]').fill('Ada');
  await contact.locator('[name="contact-last-name"]').fill('Moreno');
  await takeStep(page, contact, 'contact-save-draft');
  const saved = page.getByRole('dialog', { name: 'Ada Moreno', exact: true });
  await expect(saved.locator('.state-pill__button')).toHaveAttribute('data-state', 'Create');
  await takeStep(page, saved, 'contact-activate');
  await expect(saved.locator('.state-pill__button')).toHaveAttribute('data-state', 'Active');
  await saved.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(page.getByRole('dialog')).toHaveCount(1);
  await customer.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
}

async function addSlot(page: Page, jobDialog: Locator, jobName: string, name: string): Promise<void> {
  await jobDialog.getByRole('button', { name: 'Add slot', exact: true }).focus();
  await page.keyboard.press('Enter');
  const fresh = page.getByRole('dialog', { name: 'New slot', exact: true });
  await expect(fresh.locator('[data-job]')).toContainText(jobName);
  await fresh.locator('[name="slot-name"]').fill(name);
  await takeStep(page, fresh, 'slot-add');
  const saved = page.getByRole('dialog', { name, exact: true });
  await expect(saved.locator('.state-pill__button')).toHaveAttribute('data-state', 'Active');
  await saved.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(page.getByRole('dialog')).toHaveCount(1);
  await expect(jobDialog.locator('tr.list-row').filter({ hasText: name })).toHaveCount(1);
}

test('a coordinator registers a job with a timetable and two slots and starts its coordination, uncovered', { tag: '@staffing' }, async ({ page }) => {
  test.setTimeout(180_000);
  const width = 1280;
  await page.setViewportSize({ width, height: 800 });
  const state = readFoundationState();
  const coordinator = persona(personaKeys.coordinator, state);
  const suffix = `${Date.now().toString(36)}${Math.floor(Math.random() * 1000)}`;
  const customerName = `Brightpath Learning ${suffix}`;
  const jobName = `Algebra for adults ${suffix}`;
  await loginAs(page, coordinator);
  await test.step('set-up: a customer of the run', () => activeCustomer(page, customerName));

  await gotoReady(page, '/staffing/jobs');
  await page.getByRole('button', { name: 'New job', exact: true }).click();
  const fresh = page.getByRole('dialog', { name: 'New job', exact: true });
  await chooseEveryWeek(fresh);
  await fresh.locator('[name="job-name"]').fill(jobName);
  await choose(page, fresh, 'job-customer', customerName);
  await choose(page, fresh, 'job-contact', 'Ada Moreno');
  await expect(fresh.getByRole('tab')).toHaveCount(0);
  await expect(fresh.locator('.state-entity-dialog-side')).toHaveCount(0);
  await capture(page, '01-new-job', width);

  await takeStep(page, fresh, 'job-register');
  const jobDialog = page.getByRole('dialog', { name: jobName, exact: true });
  await expect(jobDialog.locator('.state-pill__button')).toHaveAttribute('data-state', 'Registration');
  await expect(jobDialog.locator('.state-entity-dialog-tabs [role=tab]')).toHaveText(['Info', 'Place', 'Timetable', 'Slots']);
  await expect(jobDialog.locator('.state-entity-dialog-side')).toBeVisible();
  await capture(page, '02-registered', width);

  await saveRegisteredJob(jobDialog, async () => {
    await jobDialog.getByRole('tab', { name: 'Timetable' }).click();
    await jobDialog.locator('[data-add-day="1"]').click();
    await jobDialog.locator('[data-add-day="3"]').click();
    const wednesdayStart = jobDialog.getByLabel('Start, Wednesday, time 1', { exact: true });
    await wednesdayStart.fill('10:00');
    await wednesdayStart.press('Tab');
    const wednesdayEnd = jobDialog.getByLabel('End, Wednesday, time 1', { exact: true });
    await wednesdayEnd.fill('11:30');
    await wednesdayEnd.press('Tab');
    await jobDialog.locator('[data-add-day="1"]').click();
    const secondMonday = jobDialog.getByLabel('Start, Monday, time 2', { exact: true });
    await secondMonday.fill('08:30');
    await secondMonday.press('Tab');
    await expect(jobDialog.locator('[data-problems]')).toContainText('Monday: 08:00-09:00 and 08:30-10:00 overlap.');
    await capture(page, '03-timetable-overlap', width);
    await jobDialog.getByRole('button', { name: 'Remove the time 08:30–10:00 on Monday', exact: true }).click();
    await expect(jobDialog.locator('[data-problems]')).toHaveCount(0);

    await expect(jobDialog.locator('[data-add-day]')).toHaveCount(7);
    expect(await buttonContrastViolations(page)).toEqual([]);
    await capture(page, '04-timetable', width);
  });

  await jobDialog.getByRole('tab', { name: 'Slots' }).click();
  await expect(jobDialog.getByText('No slot yet')).toBeVisible();
  await addSlot(page, jobDialog, jobName, 'Lead');
  await addSlot(page, jobDialog, jobName, 'Assistant');
  await expect(jobDialog.locator('tr.list-row').filter({ hasText: 'Uncovered' })).toHaveCount(2);
  await capture(page, '05-slots', width);

  await takeStep(page, jobDialog, 'job-hand-over');
  await expect(jobDialog.locator('.state-pill__button')).toHaveAttribute('data-state', 'AwaitingCoordination');
  await takeStep(page, jobDialog, 'job-start');
  await expect(jobDialog.locator('.state-pill__button')).toHaveAttribute('data-state', 'Coordinating');
  await jobDialog.getByRole('tab', { name: 'Info' }).click();
  await expect(jobDialog.locator('[data-job-staff] [data-staff-slot]')).toHaveCount(2);
  await expect(jobDialog.locator('[data-job-staff] [data-open]')).toHaveCount(2);
  await expect(jobDialog.locator('[data-coordinator], [data-coverage]')).toHaveCount(0);
  await expect(jobDialog.locator('[name="job-customer"]')).toHaveCount(1);
  await capture(page, '06-coordinating', width);
  await jobDialog.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  const row = page.locator('tr.list-row').filter({ hasText: jobName });
  await expect(row).toContainText('Uncovered (2 slots)');
  await expect(row).toContainText(personaName(coordinator));
  await capture(page, '07-jobs', width);

  await gotoReady(page, '/staffing/customers');
  await page.locator('tr.list-row').filter({ hasText: customerName }).click();
  const customer = page.getByRole('dialog', { name: customerName, exact: true });
  await confirmStep(page, customer, 'customer-deactivate', 'Deactivate');
  await expect(customer.locator('.state-pill__button')).toHaveAttribute('data-state', 'Inactive');
  const remove = customer.locator('button[data-transition="customer-delete"]');
  await expect(remove).toHaveAttribute('aria-disabled', 'true');
  await remove.focus();
  const card = customer.locator('.transition-button').filter({ has: page.locator('button[data-transition="customer-delete"]') }).locator('.transition-button__card');
  await expect(card).toContainText('1 job is registered for this customer');
  await page.mouse.move(0, 0);
  await expectSettledPageAccessible(page);
  await shot(page, 'jobs', `08-customer-delete-refused-${width}`, [page.locator('.state-entity-dialog-footer__notice')]);
  for (const other of [{ width: 1500, height: 900 }, { width: 390, height: 844 }]) {
    await page.setViewportSize(other);
    await remove.focus();
    await expect(card).toBeVisible();
    await page.mouse.move(0, 0);
    await expectSettledPageAccessible(page);
    await shot(page, 'jobs', `08-customer-delete-refused-${other.width}`, [page.locator('.state-entity-dialog-footer__notice')]);
  }
  await page.setViewportSize({ width, height: 800 });
});

async function staffBrowser(browser: Browser, width: number): Promise<{ page: Page; close: () => Promise<void> }> {
  const context = await browser.newContext({ viewport: { width, height: 800 } });
  const page = await context.newPage();
  await loginAs(page, persona(personaKeys.staff, readFoundationState()));
  return { page, close: () => context.close() };
}

async function registeredJob(page: Page, customerName: string, name: string): Promise<Locator> {
  await gotoReady(page, '/staffing/jobs');
  await page.getByRole('button', { name: 'New job', exact: true }).click();
  const fresh = page.getByRole('dialog', { name: 'New job', exact: true });
  await chooseEveryWeek(fresh);
  await fresh.locator('[name="job-name"]').fill(name);
  await choose(page, fresh, 'job-customer', customerName);
  await waitForSettled(page);
  await takeStep(page, fresh, 'job-register');
  const jobDialog = page.getByRole('dialog', { name, exact: true });
  await expect(jobDialog.locator('.state-pill__button')).toHaveAttribute('data-state', 'Registration');
  await saveRegisteredJob(jobDialog, async () => {
    await jobDialog.getByRole('tab', { name: 'Timetable' }).click();
    await jobDialog.locator('[data-add-day="1"]').click();
  });
  await jobDialog.getByRole('tab', { name: 'Slots' }).click();
  await addSlot(page, jobDialog, name, 'Lead');
  return jobDialog;
}

test('a coordinator deletes a draft job and cancels a staffed one, its staff member told', { tag: '@staffing' }, async ({ page, browser }) => {
  test.setTimeout(240_000);
  const width = 1280;
  await page.setViewportSize({ width, height: 800 });
  const state = readFoundationState();
  const staffMember = personaName(persona(personaKeys.staff, state));
  const suffix = `${Date.now().toString(36)}${Math.floor(Math.random() * 1000)}`;
  const customerName = state.staffing.customer;
  const draftName = `Draft geometry ${suffix}`;
  const staffedName = `Staffed biology ${suffix}`;
  await loginAs(page, persona(personaKeys.coordinator, state));

  const draft = await registeredJob(page, customerName, draftName);
  await expect(draft.locator('button[data-transition="job-delete"]')).toBeVisible();
  await capture(page, '09-draft-delete-offered', width);
  await confirmStep(page, draft, 'job-delete', 'Delete');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await gotoReady(page, `/staffing/jobs?q=${encodeURIComponent(draftName)}`);
  await expect(page.locator('tr.list-row').filter({ hasText: draftName })).toHaveCount(0);

  const jobDialog = await registeredJob(page, customerName, staffedName);
  await takeStep(page, jobDialog, 'job-start-from-registration');
  await expect(jobDialog.locator('.state-pill__button')).toHaveAttribute('data-state', 'Coordinating');
  await expect(jobDialog.locator('button[data-transition="job-delete"]')).toHaveCount(0);
  await jobDialog.locator('tr.list-row').filter({ hasText: 'Lead' }).locator('td').first().click();
  const need = page.getByRole('dialog', { name: 'Lead', exact: true });
  await need.getByRole('tab', { name: 'Staffing' }).click();
  await staffInPlace(need, staffMember, 'assign');
  await need.getByRole('tab', { name: /^Staff(?!ing)/ }).click();
  await expect(need.locator('tr.list-row').filter({ hasText: staffMember }).filter({ hasText: 'Assigned' })).toHaveCount(1);
  await need.getByRole('button', { name: 'Close', exact: true }).first().click();
  await expect(page.getByRole('dialog')).toHaveCount(1);

  await expect(jobDialog.locator('button[data-transition="job-cancel-coordinating"]')).toBeVisible();
  await capture(page, '10-cancel-offered', width);
  await cancelWithReason(page, jobDialog, 'job-cancel-coordinating');
  await expect(jobDialog.locator('.state-pill__button')).toHaveAttribute('data-state', 'CanceledBySupplier');
  await jobDialog.getByRole('tab', { name: 'Slots' }).click();
  await expect(jobDialog.locator('tr.list-row').filter({ hasText: 'Lead' })).toContainText('Canceled');
  await capture(page, '11-canceled', width);
  await jobDialog.getByRole('button', { name: 'Close', exact: true }).first().click();

  const staffSide = await staffBrowser(browser, width);
  try {
    const staffPage = staffSide.page;
    await gotoReady(staffPage, '/me/overview');
    await staffPage.locator('.notice-bell__trigger').click();
    const bell = staffPage.locator('[data-notice-panel]');
    await expect(bell).toBeVisible();
    await bell.getByRole('button', { name: 'All', exact: true }).click();
    await bell.locator('.notice-row').filter({ hasText: `${staffedName} is canceled for you` }).first().click();
    const assignment = staffPage.getByRole('dialog').last();
    await expect(assignment.locator('.state-pill__button')).toHaveAttribute('data-state', 'Canceled');
    await expect(assignment.locator('[data-job]')).toHaveText(staffedName);
    await capture(staffPage, '12-staff-told', width);
  } finally {
    await staffSide.close();
  }
});
