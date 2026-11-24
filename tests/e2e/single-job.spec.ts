import type { Locator, Page } from '@playwright/test';
import { persona, personaKeys, personaName, readFoundationState } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { expectSettledPageAccessible, gotoReady, loginAs, waitForSettled } from './lib/session';
import { shot } from './lib/shots';
import { candidateRow, fillSingleTimes, isoDay, jobsPage, staffInPlace, workdayAhead } from './lib/jobs';
import { occurrencesOf, showEveryStaff, timelineReady, toStaffOn } from './lib/workspace';

const area = 'single-job';

test.use({ viewport: { width: 1280, height: 800 } });

async function capture(page: Page, name: string): Promise<void> {
  await page.mouse.move(0, 0);
  await expectSettledPageAccessible(page);
  await shot(page, area, name, [page.locator('.state-entity-dialog-footer__notice')]);
}

async function takeStep(page: Page, dialog: Locator, key: string): Promise<void> {
  const button = dialog.locator(`button[data-transition="${key}"]`);
  await expect(button).toBeVisible();
  await expect(button).not.toHaveAttribute('aria-disabled', 'true');
  await button.focus();
  await expect(button).toBeFocused();
  await page.keyboard.press('Enter');
}

const pill = (dialog: Locator) => dialog.locator('.state-pill__button');

async function cancelJob(page: Page, jobName: string): Promise<void> {
  await gotoReady(page, `${jobsPage}?q=${encodeURIComponent(jobName)}`);
  const row = page.locator('tr.list-row').filter({ hasText: jobName });
  if ((await row.count()) === 0) return;
  await row.click();
  const job = page.getByRole('dialog', { name: jobName, exact: true });
  const state = await pill(job).getAttribute('data-state');
  const step = state === 'Coordinating' ? 'job-cancel-coordinating' : state === 'Registration' ? 'job-cancel-registration' : null;
  if (step) {
    await job.locator(`button[data-transition="${step}"]`).focus();
    await page.keyboard.press('Enter');
    const prompt = page.locator('.form-dialog').filter({ has: page.getByLabel('The reason') });
    await prompt.getByLabel('The reason').fill('The run is over.');
    await prompt.getByRole('button').filter({ hasText: /^Cancel / }).click();
    await expect(prompt).toHaveCount(0);
    await expect(pill(job)).toHaveAttribute('data-state', 'CanceledBySupplier');
  }
  await job.getByRole('button', { name: 'Close', exact: true }).first().click();
}

test('a coordinator registers a single job in the evening and staffs it from the timeline', { tag: ['@staffing', '@coordination', '@smoke'] }, async ({ page }) => {
  test.setTimeout(300_000);
  const state = readFoundationState();
  const staff = personaName(persona(personaKeys.staff, state));
  const { customer } = state.staffing;
  const suffix = `${Date.now().toString(36)}${Math.floor(Math.random() * 1000)}`;
  const jobName = `Evening cleaning ${suffix}`;
  const day = workdayAhead();
  const shown = isoDay(day);
  let registered = false;

  try {
    await loginAs(page, persona(personaKeys.coordinator, state));
    await gotoReady(page, jobsPage);
    await page.getByRole('button', { name: 'New job', exact: true }).click();
    const fresh = page.getByRole('dialog', { name: 'New job', exact: true });

    await expect(fresh.locator('.rz-dropdown').filter({ has: page.locator('[name="job-schedule"]') })).toContainText('Single');
    await fresh.locator('[name="job-name"]').fill(jobName);
    await fresh.locator('.rz-dropdown').filter({ has: page.locator('[name="job-customer"]') }).click();
    await page.getByRole('option', { name: customer, exact: true }).click();
    await fillSingleTimes(fresh, day, '18:00', '23:00');
    await capture(page, '01-new-single-job');

    await takeStep(page, fresh, 'job-register');
    const job = page.getByRole('dialog', { name: jobName, exact: true });
    await expect(pill(job)).toHaveAttribute('data-state', 'Registration');
    registered = true;
    await job.getByRole('tab', { name: 'Timetable' }).click();
    await expect(job.locator('[data-single-job-note]')).toBeVisible();
    await expect(job.locator('[data-add-day]')).toHaveCount(0);

    await job.getByRole('tab', { name: 'Slots' }).click();
    await waitForSettled(page);
    await job.getByRole('button', { name: 'Add slot', exact: true }).focus();
    await page.keyboard.press('Enter');
    const slot = page.getByRole('dialog', { name: 'New slot', exact: true });
    await slot.locator('[name="slot-name"]').fill('Cleaner');
    await takeStep(page, slot, 'slot-add');
    const saved = page.getByRole('dialog', { name: 'Cleaner', exact: true });
    await expect(pill(saved)).toHaveAttribute('data-state', 'Active');
    await saved.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(1);
    await waitForSettled(page);
    await takeStep(page, job, 'job-start-from-registration');
    await expect(pill(job)).toHaveAttribute('data-state', 'Coordinating');
    await job.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    await gotoReady(page, `/coordination?day=${shown}`);
    await timelineReady(page);
    const toStaff = toStaffOn(page, shown).filter({ hasText: jobName });
    await expect(toStaff).toHaveCount(1);
    await expect(toStaff).toContainText('18:00-23:00');
    await showEveryStaff(page);
    await capture(page, '02-day-view-to-staff');

    await toStaff.click();
    const opened = page.getByRole('dialog', { name: 'Cleaner', exact: true });
    await expect(opened.getByRole('tab', { name: 'Staffing' })).toHaveAttribute('aria-selected', 'true');
    await waitForSettled(page);
    await expect(candidateRow(opened, staff)).toBeVisible();
    await capture(page, '03-candidate');
    await staffInPlace(opened, staff, 'assign');
    await opened.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    await timelineReady(page);
    const held = occurrencesOf(page, staff, shown).filter({ hasText: jobName });
    await expect(held).toHaveCount(1);
    await expect(held).toContainText('18:00-23:00');
    await expect(toStaffOn(page, shown).filter({ hasText: jobName })).toHaveCount(0);
    await held.scrollIntoViewIfNeeded();
    await capture(page, '04-day-view-staffed');
  } finally {
    if (registered) await cancelJob(page, jobName);
  }
});
