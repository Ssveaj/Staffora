import { join } from 'node:path';
import type { Browser, Locator, Page } from '@playwright/test';
import { buttonContrastViolations } from './lib/accessibility';
import { persona, personaKeys, personaName, readFoundationState, type FoundationPersona } from './lib/foundation-state';
import { journeyStep, loadJourney } from './lib/journey-contract';
import { expect, test } from './lib/page-health';
import { expectSettledPageAccessible, gotoReady, loginAs, waitForSettled } from './lib/session';
import { screenshotRoot, shot } from './lib/shots';
import { addSlot, cancelJob, candidateRow, chooseEveryWeek, openJob, pill, runSuffix, saveRegisteredJob, staffInPlace, takeStep, weekdays, workdayAhead } from './lib/jobs';

const journey = loadJourney('job-staffed');
const area = 'job-staffed';
const wideToo = new Set(['06b-request-for-the-staff', '07b-decline-prompt']);

test.use({ viewport: { width: 1280, height: 800 }, video: { mode: 'on', size: { width: 1280, height: 800 } } });

async function capture(page: Page, name: string): Promise<void> {
  await page.mouse.move(0, 0);
  await expectSettledPageAccessible(page);
  expect(await buttonContrastViolations(page)).toEqual([]);
  const style = await page.addStyleTag({
    content: '.app-shell { height: auto !important; min-height: 100dvh; overflow: visible !important; } .app-shell .app-shell__body { overflow: visible !important; }',
  });
  try {
    await shot(page, area, name, [page.locator('.state-entity-dialog-footer__notice')]);
  } finally {
    await style.evaluate((element) => element.remove());
  }
  if (wideToo.has(name)) {
    await page.setViewportSize({ width: 1500, height: 900 });
    await expectSettledPageAccessible(page);
    await shot(page, area, `${name}-1500`, [page.locator('.state-entity-dialog-footer__notice')]);
    await page.setViewportSize({ width: 1280, height: 800 });
    await waitForSettled(page);
  }
}

async function confirmStep(page: Page, dialog: Locator, key: string, label: string): Promise<void> {
  await takeStep(dialog, key);
  const confirm = page.locator('.form-dialog').filter({ has: page.getByRole('button', { name: label, exact: true }) });
  await confirm.getByRole('button', { name: label, exact: true }).click();
  await expect(confirm).toHaveCount(0);
}

async function openSlot(page: Page, job: Locator, name: string, view: 'Staffing' | 'Staff'): Promise<Locator> {
  await job.getByRole('tab', { name: 'Slots' }).click();
  await job.locator('tr.list-row').filter({ hasText: name }).locator('td').first().click();
  const slot = page.getByRole('dialog', { name, exact: true });
  await slot.getByRole('tab', { name: view, exact: true }).click();
  await waitForSettled(page);
  return slot;
}

async function openNotice(page: Page, text: string): Promise<Locator> {
  await page.locator('[data-notice-bell] .notice-bell__trigger').click();
  const panel = page.locator('[data-notice-panel]');
  await expect(panel).toBeVisible();
  await panel.getByRole('button', { name: 'All', exact: true }).click();
  await waitForSettled(page);
  await panel.locator('.notice-row').filter({ hasText: text }).first().click();
  const dialog = page.getByRole('dialog').last();
  await expect(dialog.locator('[data-job]')).toBeVisible();
  return dialog;
}

async function signedIn(browser: Browser, who: FoundationPersona): Promise<Page> {
  const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  const page = await context.newPage();
  await loginAs(page, who);
  return page;
}

test('a coordinator registers a job, asks two qualified staff members, assigns the one who says yes and confirms the job', { tag: '@staffing' }, async ({ page, browser }) => {
  test.setTimeout(420_000);
  const state = readFoundationState();
  const kim = personaName(persona(personaKeys.staff, state));
  const jamie = personaName(persona(personaKeys.secondStaff, state));
  const { customer, qualification } = state.staffing;
  const suffix = runSuffix();
  const jobName = `Staffed geometry ${suffix}`;
  const reason = `Fully booked ${suffix}`;
  const weekday = workdayAhead().getDay();
  const staffPages: Page[] = [];
  let registered = false;

  try {
    await journeyStep(journey, 'register-job', async () => {
      await loginAs(page, persona(personaKeys.coordinator, state));
      await expect(page).toHaveURL(/\/staffing\/overview$/);

      await gotoReady(page, '/staffing/jobs');
      await page.getByRole('button', { name: 'New job', exact: true }).click();
      const fresh = page.getByRole('dialog', { name: 'New job', exact: true });
      await chooseEveryWeek(fresh);
      await fresh.locator('[name="job-name"]').fill(jobName);
      await fresh.locator('.rz-dropdown').filter({ has: page.locator('[name="job-customer"]') }).click();
      await page.getByRole('option', { name: customer, exact: true }).click();
      await fresh.getByLabel('Room', { exact: true }).fill('B12');
      await fresh.getByLabel('Budget code', { exact: true }).fill('BX-4200');
      await capture(page, '01-new-job');
      await takeStep(fresh, 'job-register');
      const job = page.getByRole('dialog', { name: jobName, exact: true });
      await expect(pill(job)).toHaveAttribute('data-state', 'Registration');
      registered = true;
      await saveRegisteredJob(job, async () => {
        await job.getByRole('tab', { name: 'Timetable' }).click();
        await job.locator(`[data-add-day="${weekday}"]`).click();
        await job.getByLabel(`Start, ${weekdays[weekday]}, time 1`, { exact: true }).fill('14:00');
        await job.getByLabel(`End, ${weekdays[weekday]}, time 1`, { exact: true }).fill('15:00');
      });

      await job.getByRole('tab', { name: 'Slots' }).click();
      await waitForSettled(page);
      await job.getByRole('button', { name: 'Add slot', exact: true }).focus();
      await page.keyboard.press('Enter');
      const slot = page.getByRole('dialog', { name: 'New slot', exact: true });
      await slot.locator('[name="slot-name"]').fill('Lead');
      await slot.locator('.rz-dropdown').filter({ has: page.locator('[name="slot-qualification"]') }).click();
      await page.getByRole('option', { name: qualification, exact: true }).click();
      await expect(slot.locator(`[data-qualification="${qualification}"]`)).toBeVisible();
      await capture(page, '02-slot-requires-the-qualification');
      await takeStep(slot, 'slot-add');
      const saved = page.getByRole('dialog', { name: 'Lead', exact: true });
      await expect(pill(saved)).toHaveAttribute('data-state', 'Active');
      await saved.getByRole('button', { name: 'Close', exact: true }).first().click();
      await expect(page.getByRole('dialog')).toHaveCount(1);
      await addSlot(job, 'Assistant');

      await takeStep(job, 'job-start-from-registration');
      await expect(pill(job)).toHaveAttribute('data-state', 'Coordinating');
      await expect(job.locator('tr.list-row').filter({ hasText: 'Lead' })).toBeVisible();
      await capture(page, '03-job-being-staffed');
    });

    await journeyStep(journey, 'request-staff', async () => {
      const job = page.getByRole('dialog', { name: jobName, exact: true });
      const lead = await openSlot(page, job, 'Lead', 'Staffing');
      await expect(lead.locator('[data-staffing-summary]')).toContainText(jobName);
      for (const name of [kim, jamie]) {
        await expect(candidateRow(lead, name).locator('[data-held]')).not.toHaveCount(0);
        await expect(candidateRow(lead, name).locator('[data-missing]')).toHaveCount(0);
      }
      await capture(page, '04-both-staff-qualified');

      for (const name of [kim, jamie]) {
        await staffInPlace(lead, name, 'request');
      }

      await lead.getByRole('tab', { name: /^Staff(?!ing)/ }).click();
      for (const name of [kim, jamie]) {
        await expect(lead.locator('tr.list-row').filter({ hasText: name }).filter({ hasText: 'Requested' })).toHaveCount(1);
      }
      await capture(page, '05-both-asked');
      await lead.getByRole('button', { name: 'Close', exact: true }).first().click();

      const assistant = await openSlot(page, job, 'Assistant', 'Staffing');
      await staffInPlace(assistant, kim, 'request');
      await assistant.getByRole('button', { name: 'Close', exact: true }).first().click();
      await job.getByRole('button', { name: 'Close', exact: true }).first().click();
      await expect(page.getByRole('dialog')).toHaveCount(0);
    });

    await journeyStep(journey, 'staff-accepts', async () => {
      const kimPage = await signedIn(browser, persona(personaKeys.staff, state));
      staffPages.push(kimPage);
      await expect(kimPage).toHaveURL(/\/me\/work$/);
      const request = kimPage.locator('[data-own-request]').filter({ hasText: `${jobName}: Lead` });
      await expect(request).toHaveCount(1);
      await expect(request).toContainText('Your answer is needed');
      await capture(kimPage, '06-staff-home-with-the-request');
      await request.click();
      const dialog = kimPage.getByRole('dialog').last();
      await expect(pill(dialog)).toHaveAttribute('data-state', 'Requested');
      await expect(dialog.locator('[data-job]')).toHaveText(jobName);
      await expect(dialog.locator('[data-slot]')).toHaveText('Lead');
      await expect(dialog.locator('[data-staff]')).toHaveCount(0);
      await expect(dialog.locator('button[data-transition]')).toHaveCount(3);
      await capture(kimPage, '06b-request-for-the-staff');
      await takeStep(dialog, 'assignment-accept');
      await expect(pill(dialog)).toHaveAttribute('data-state', 'Accepted');
      await capture(kimPage, '07-staff-accepted');
      await dialog.getByRole('button', { name: 'Close', exact: true }).first().click();

      const second = kimPage.locator('[data-own-request]').filter({ hasText: `${jobName}: Assistant` });
      await second.click();
      const declined = kimPage.getByRole('dialog').last();
      await expect(declined.locator('[data-slot]')).toHaveText('Assistant');
      await takeStep(declined, 'assignment-decline');
      const prompt = kimPage.locator('.form-dialog').filter({ hasText: 'The coordinator reads your reason' });
      await prompt.getByLabel('The reason').fill(reason);
      await capture(kimPage, '07b-decline-prompt');
      await prompt.getByRole('button', { name: 'Decline', exact: true }).click();
      await expect(pill(declined)).toHaveAttribute('data-state', 'Declined');
      await expect.poll(() => declined.evaluate((element) => element.contains(document.activeElement)), { message: 'the focus comes back into the assignment\'s dialog' }).toBe(true);
      await expect(declined.locator('.state-entity-dialog-side')).toContainText(reason);
      await declined.getByRole('button', { name: 'Close', exact: true }).first().click();
      await expect(kimPage.locator('[data-own-request]').filter({ hasText: jobName })).toHaveCount(0);
    });

    await journeyStep(journey, 'assign-staff', async () => {
      const job = (await openJob(page, jobName))!;
      const lead = await openSlot(page, job, 'Lead', 'Staff');
      await expect(lead.locator('tr.list-row').filter({ hasText: kim }).filter({ hasText: 'Accepted' })).toHaveCount(1);
      await lead.locator('tr.list-row').filter({ hasText: kim }).locator('td').first().click();
      const yes = page.getByRole('dialog').last();
      await expect(pill(yes)).toHaveAttribute('data-state', 'Accepted');
      await takeStep(yes, 'assignment-pick');
      await expect(pill(yes)).toHaveAttribute('data-state', 'Assigned');
      const fields = yes.locator('[data-job-fields]');
      await expect(fields.locator('[data-job-field="Room"]')).toContainText('B12');
      await expect(fields.locator('[data-job-field="Room"]')).toContainText('Also the assigned staff member');
      await expect(fields.locator('[data-job-field="Budget code"]')).toContainText('BX-4200');
      await capture(page, '08-staff-assigned');
      await yes.getByRole('button', { name: 'Close', exact: true }).first().click();

      await expect(lead.locator('tr.list-row').filter({ hasText: kim }).filter({ hasText: 'Assigned' })).toHaveCount(1);
      await expect(lead.locator('tr.list-row').filter({ hasText: jamie }).filter({ hasText: 'Not selected' })).toHaveCount(1);
      await capture(page, '09-other-request-closed');
      await lead.getByRole('button', { name: 'Close', exact: true }).first().click();
      await expect(job.locator('tr.list-row').filter({ hasText: 'Lead' })).toContainText(`${kim} (assigned)`);
      await expect(job.locator('tr.list-row').filter({ hasText: 'Lead' })).toContainText('Covered');
      await job.getByRole('button', { name: 'Close', exact: true }).first().click();

      const declined = await openNotice(page, `${kim} declined ${jobName}`);
      await expect(pill(declined)).toHaveAttribute('data-state', 'Declined');
      await expect(declined.locator('.state-entity-dialog-side')).toContainText(reason);
      await capture(page, '09b-decline-reason-for-the-coordinator');
      await declined.getByRole('button', { name: 'Close', exact: true }).first().click();
      await expect(page.getByRole('dialog')).toHaveCount(0);
    });

    await journeyStep(journey, 'confirm-job', async () => {
      const job = (await openJob(page, jobName))!;
      await confirmStep(page, job, 'job-confirm', 'Confirm');
      await expect(pill(job)).toHaveAttribute('data-state', 'Confirmed');
      await capture(page, '10-job-confirmed');
      await job.getByRole('button', { name: 'Close', exact: true }).first().click();
      await expect(page.getByRole('dialog')).toHaveCount(0);
    });

    await journeyStep(journey, 'staff-see-the-outcome', async () => {
      const kimPage = staffPages[0];
      await gotoReady(kimPage, '/me/work');
      await expect(kimPage.locator('[data-own-next], [data-week-time][data-state="Assigned"]').filter({ hasText: jobName }).first()).toBeVisible();
      await expect(kimPage.locator('[data-own-request]').filter({ hasText: jobName })).toHaveCount(0);
      await capture(kimPage, '11-staff-next-assignment');
      const assigned = await openNotice(kimPage, `You are assigned to ${jobName}`);
      await expect(pill(assigned)).toHaveAttribute('data-state', 'Assigned');
      const fields = assigned.locator('[data-job-fields]');
      await expect(fields.locator('[data-job-field="Room"]')).toContainText('B12');
      await expect(fields).not.toContainText('Budget code');
      await expect(fields).not.toContainText('BX-4200');
      await expect(fields).not.toContainText('assigned staff member');
      await capture(kimPage, '11b-job-details-for-the-staff');
      await assigned.getByRole('button', { name: 'Close', exact: true }).first().click();

      const jamiePage = await signedIn(browser, persona(personaKeys.secondStaff, state));
      staffPages.push(jamiePage);
      await expect(jamiePage).toHaveURL(/\/me\/work$/);
      await waitForSettled(jamiePage);
      await expect(jamiePage.locator('[data-own-request]').filter({ hasText: jobName })).toHaveCount(0);
      await jamiePage.locator('[data-notice-bell] .notice-bell__trigger').click();
      const panel = jamiePage.locator('[data-notice-panel]');
      await expect(panel).toBeVisible();
      await expect(panel.locator('.notice-row').filter({ hasText: `Another staff member was picked for ${jobName}` })).toHaveCount(1);
      await capture(jamiePage, '12-other-staff-told');
    });
  } finally {
    for (const staffPage of staffPages) await staffPage.context().close();
    if (registered) await cancelJob(page, jobName);
  }

  const video = page.video();
  await page.close();
  if (video) await video.saveAs(join(screenshotRoot, area, `${area}.webm`));
});
