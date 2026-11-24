import type { Browser, Locator, Page } from '@playwright/test';
import { buttonContrastViolations } from './lib/accessibility';
import { persona, personaKeys, personaName, readFoundationState } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { expectSettledPageAccessible, gotoReady, loginAs, waitForSettled } from './lib/session';
import { shot } from './lib/shots';
import { cancelJob, isoDay, pill, runSuffix, staffInPlace, startedJob, takeStep, workdayAhead } from './lib/jobs';
import { priorityItems, searchPriorities, timelineReady } from './lib/workspace';

const area = 'absence-impact';

const widths = [1280, 390];

async function capture(page: Page, name: string, width: number): Promise<void> {
  await page.mouse.move(0, 0);
  await expectSettledPageAccessible(page);
  expect(await buttonContrastViolations(page)).toEqual([]);
  await shot(page, area, `${name}-${width}`, [page.locator('.state-entity-dialog-footer__notice')]);
}

async function captureAtBothWidths(page: Page, name: string): Promise<void> {
  for (const width of widths) {
    await page.setViewportSize({ width, height: width === 1280 ? 800 : 844 });
    await waitForSettled(page);
    await capture(page, name, width);
  }

  await page.setViewportSize({ width: 1280, height: 800 });
  await waitForSettled(page);
}

async function pickDay(dialog: Locator, name: string, day: Date): Promise<void> {
  const input = dialog.locator(`input[name="${name}"]`);
  await input.fill(isoDay(day));
  await input.press('Tab');
  await expect(input).toHaveValue(isoDay(day));
}

async function registerSickLeave(page: Page, staffMember: string, day: Date): Promise<string> {
  await gotoReady(page, '/staffing/absence');
  await page.getByRole('button', { name: 'Register absence', exact: true }).click();
  const chooser = page.locator('[data-form-dialog="ChooseAbsenceStaffDialog"]');
  await chooser.locator('.rz-dropdown').filter({ has: page.locator('[name="absence-staff"]') }).click();
  await page.getByRole('option', { name: staffMember, exact: true }).click();
  await chooser.getByRole('button', { name: 'Continue', exact: true }).click();
  const fresh = page.getByRole('dialog').last();
  await fresh.locator('.rz-dropdown').filter({ has: page.locator('[name="absence-kind"]') }).click();
  await page.getByRole('option', { name: 'Sick leave', exact: true }).filter({ visible: true }).click();
  await expect(page.getByRole('option', { name: 'Sick leave', exact: true }).filter({ visible: true })).toHaveCount(0);
  await pickDay(fresh, 'absence-first-day', day);
  await pickDay(fresh, 'absence-last-day', day);
  await waitForSettled(page);
  await takeStep(fresh, 'absence-register');
  await expect(pill(fresh)).toHaveAttribute('data-state', 'Approved');
  await expect(page).toHaveURL(/dlg=absence-[0-9a-f-]{36}/);
  const id = /dlg=absence-([0-9a-f-]{36})/.exec(page.url())![1];
  await fresh.getByRole('button', { name: 'Close', exact: true }).first().click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  return id;
}

async function cancelAbsence(browser: Browser, id: string): Promise<void> {
  const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  try {
    const page = await context.newPage();
    await loginAs(page, persona(personaKeys.staff, readFoundationState()));
    await gotoReady(page, `/staffing/absence?dlg=absence-${id}`);
    const absence = page.getByRole('dialog').last();
    await expect(absence).toBeVisible();
    for (const key of ['absence-cancel', 'absence-cancel-approved', 'absence-cancel-question', 'absence-cancel-staff-question']) {
      if ((await absence.locator(`button[data-transition="${key}"]`).count()) === 0) continue;
      await takeStep(absence, key);
      await page.locator('.form-dialog').getByRole('button', { name: 'Cancel absence', exact: true }).click();
      await expect(pill(absence)).toHaveAttribute('data-state', 'Canceled');
      break;
    }
  } finally {
    await context.close();
  }
}

async function staffIsTold(browser: Browser, jobName: string): Promise<void> {
  for (const width of widths) {
    const context = await browser.newContext({ viewport: { width, height: width === 1280 ? 800 : 844 } });
    try {
      const page = await context.newPage();
      await loginAs(page, persona(personaKeys.staff, readFoundationState()));
      await gotoReady(page, '/me/work');
      await page.locator('.notice-bell__trigger').click();
      const panel = page.locator('[data-notice-panel]');
      await expect(panel).toBeVisible();
      await panel.getByRole('button', { name: 'All', exact: true }).click();
      const notice = panel.locator('.notice-row').filter({ hasText: `You are taken off ${jobName}` }).first();
      await expect(notice).toBeVisible();
      await expect(notice).toContainText('because of your sick leave on');
      await capture(page, '07-staff-member-told', width);
    } finally {
      await context.close();
    }
  }
}

test('a coordinator finds a sick leave over an assigned job in Priorities, takes the staff member off it and staffs the slot open again', { tag: '@coordination' }, async ({ page, browser }) => {
  test.setTimeout(420_000);
  const state = readFoundationState();
  const kim = personaName(persona(personaKeys.staff, state));
  const jamie = personaName(persona(personaKeys.secondStaff, state));
  const jobName = `Absent cleaning ${runSuffix()}`;
  const day = workdayAhead(16);

  await page.setViewportSize({ width: 1280, height: 800 });
  await loginAs(page, persona(personaKeys.coordinator, state));
  const job = await test.step('set-up: a started job of the run with the staff member assigned', async () => {
    const started = await startedJob(page, jobName, { day, start: '05:00', end: '05:30', slots: ['Lead'], keepOpen: true });
    await started.getByRole('tab', { name: 'Staffing' }).click();
    await staffInPlace(started, kim, 'assign');
    await started.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    return started;
  });
  expect(job).toBeTruthy();

  let absenceId: string | null = null;
  let passed = false;
  try {
    absenceId = await test.step('the staff member calls in sick and the coordinator registers it', () => registerSickLeave(page, kim, day));

    await gotoReady(page, '/coordination');
    await timelineReady(page);
    await searchPriorities(page, kim);
    const waiting = priorityItems(page, jobName).and(page.locator('[data-priorities-kind="absent"]'));
    await expect(waiting).toHaveCount(1);
    await expect(waiting.locator('.priorities-pill')).toHaveText('Absent');
    await expect(waiting).toContainText(kim);
    await expect(waiting).toContainText(`Sick leave on ${isoDay(day)}, Lead, ${jobName}`);
    await expect(waiting).toContainText('1 assignment to settle');
    await captureAtBothWidths(page, '01-priorities-absence-to-settle');

    await waiting.getByRole('button', { name: /^Open: / }).click();
    const absence = page.getByRole('dialog').last();
    await expect(absence.getByRole('tab', { name: 'Affected jobs' })).toHaveAttribute('aria-selected', 'true');
    const row = absence.locator(`[data-impact-row="${jobName}"]`);
    await expect(row).toHaveCount(1);
    await expect(row.locator('[data-impact-state]')).toHaveText('Assigned');
    await expect(row.locator('[data-impact-when]')).toContainText(`${isoDay(day)} 05:00-05:30`);
    await captureAtBothWidths(page, '02-absence-affected-jobs');

    await row.locator('button[data-impact-action="open"]').click();
    const assignment = page.getByRole('dialog').last();
    await expect(pill(assignment)).toHaveAttribute('data-state', 'Assigned');
    await expect(assignment.locator('button[data-transition="assignment-take-off"]')).toHaveText(/Take off for absence/);
    await takeStep(assignment, 'assignment-take-off');
    const prompt = page.locator('.form-dialog').filter({ hasText: 'no longer work this slot because of their absence' });
    await expect(prompt).toBeVisible();
    await prompt.getByLabel('A word for the staff member (optional)').fill('Get well soon.');
    await captureAtBothWidths(page, '03-take-off-prompt');
    await prompt.getByRole('button', { name: 'Take off for absence', exact: true }).click();
    await expect(prompt).toHaveCount(0);
    await expect(pill(assignment)).toHaveAttribute('data-state', 'Canceled');
    await assignment.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(1);

    await expect(row).toHaveAttribute('data-settled', 'true');
    await expect(row.locator('[data-impact-state]')).toHaveText('Taken off');
    await expect(row.locator('[data-open-again]')).toHaveText('Open again');
    await captureAtBothWidths(page, '04-taken-off-open-again');

    await row.locator('button[data-impact-action="staffing"]').click();
    const staffing = page.getByRole('dialog', { name: jobName, exact: true });
    await expect(staffing.getByRole('tab', { name: 'Staffing' })).toHaveAttribute('aria-selected', 'true');
    await expect(staffing.locator('[data-staffing-slot="Lead"]')).toHaveAttribute('data-open-again', 'true');
    await expect(staffing.locator('[data-staffing-slot="Lead"]')).toContainText('Open again');
    await expect(staffing.locator('[data-slot-staffing] p[data-open-again]')).toContainText('taken off for absence');
    await captureAtBothWidths(page, '05-staffing-open-again');

    await staffInPlace(staffing, jamie, 'assign');
    await expect(staffing.locator('[data-staffing-slot="Lead"]')).not.toHaveAttribute('data-open-again', 'true');
    await expect(staffing.locator('[data-staffing-slot="Lead"]')).toContainText('Filled');
    await expect(staffing.locator('[data-slot-staffing] p[data-open-again]')).toHaveCount(0);
    await captureAtBothWidths(page, '06-held-again');
    await staffing.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(1);
    await expect(row.locator('[data-open-again]')).toHaveCount(0);
    await absence.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    await expect(priorityItems(page, jobName).and(page.locator('[data-priorities-kind="absent"]'))).toHaveCount(0);
    await expect(page.locator('[data-priorities] [data-open-again]').filter({ hasText: jobName })).toHaveCount(0);

    await test.step('the staff member is told', () => staffIsTold(browser, jobName));
    passed = true;
  } finally {
    await test.step('clean-up: the sick leave and the job of the run are canceled', async () => {
      try {
        await page.keyboard.press('Escape');
        if (absenceId) await cancelAbsence(browser, absenceId);
        await cancelJob(page, jobName);
      } catch (cleanup) {
        if (passed) throw cleanup;
      }
    });
  }
});
