import type { Browser, Locator, Page } from '@playwright/test';
import { persona, personaKeys, personaName, readFoundationState } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { expectSettledPageAccessible, gotoReady, loginAs, waitForSettled } from './lib/session';
import { shot } from './lib/shots';
import { cancelJob, openJob, runSuffix, staffInPlace, startedJob } from './lib/jobs';

function occurrenceDay(): Date {
  const day = new Date();
  day.setDate(day.getDate() + 2);
  return day;
}

function isoDay(day: Date): string {
  return `${day.getFullYear()}-${String(day.getMonth() + 1).padStart(2, '0')}-${String(day.getDate()).padStart(2, '0')}`;
}

async function capture(page: Page, name: string, width: number): Promise<void> {
  await page.mouse.move(0, 0);
  await expectSettledPageAccessible(page);
  await shot(page, 'staff', `${name}-${width}`, [page.locator('.state-entity-dialog-footer__notice')]);
}

async function takeStep(page: Page, dialog: Locator, key: string): Promise<void> {
  await dialog.locator(`button[data-transition="${key}"]`).focus();
  await page.keyboard.press('Enter');
}

const pill = (dialog: Locator) => dialog.locator('.state-pill__button');

async function openBell(page: Page): Promise<Locator> {
  await page.locator('.notice-bell__trigger').click();
  const panel = page.locator('[data-notice-panel]');
  await expect(panel).toBeVisible();
  await panel.getByRole('button', { name: 'All', exact: true }).click();
  await waitForSettled(page);
  return panel;
}

async function requestStaff(page: Page, jobName: string, staffMember: string): Promise<void> {
  const jobDialog = (await openJob(page, jobName))!;
  await jobDialog.getByRole('tab', { name: 'Slots' }).click();
  await jobDialog.locator('tr.list-row').filter({ hasText: 'Lead' }).locator('td').first().click();
  const need = page.getByRole('dialog', { name: 'Lead', exact: true });
  await need.getByRole('tab', { name: 'Staffing' }).click();
  await waitForSettled(page);
  await staffInPlace(need, staffMember, 'request');
  for (const open of [need, jobDialog]) await open.getByRole('button', { name: 'Close', exact: true }).first().click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
}

async function coordinatorBrowser(browser: Browser, width: number): Promise<{ page: Page; close: () => Promise<void> }> {
  const context = await browser.newContext({ viewport: { width, height: 800 } });
  const page = await context.newPage();
  await loginAs(page, persona(personaKeys.coordinator, readFoundationState()));
  return { page, close: () => context.close() };
}

test('a staff member answers a request from the bell, finds it on My assignments and My schedule, and reports absence; the coordinator is told of both', { tag: ['@staffing', '@homes', '@smoke'] }, async ({ page, browser }) => {
  test.setTimeout(300_000);
  const width = 1280;
  await page.setViewportSize({ width, height: 800 });
  const state = readFoundationState();
  const staffMember = personaName(persona(personaKeys.staff, state));
  const jobName = `Geometry ${runSuffix()}`;
  const day = isoDay(occurrenceDay());

  const coordinatorSide = await coordinatorBrowser(browser, width);
  const coordinator = coordinatorSide.page;
  let jobLive = false;
  try {
    jobLive = true;
    await test.step('set-up: a started job of the run', () => startedJob(coordinator, jobName, { day: occurrenceDay(), start: '09:00', end: '10:00' }));
    await requestStaff(coordinator, jobName, staffMember);

    await loginAs(page, persona(personaKeys.staff, state));
    await gotoReady(page, '/me/overview');
    const navigation = page.getByRole('navigation', { name: 'Main navigation' });
    const myWork = navigation.getByRole('list', { name: 'My work', exact: true });
    await expect(myWork.getByRole('link', { name: 'My assignments', exact: true })).toBeVisible();
    await expect(myWork.getByRole('link', { name: 'My schedule', exact: true })).toBeVisible();
    await page.keyboard.press('Escape');

    await gotoReady(page, '/me/assignments');
    await expect(page.locator('.list-row').filter({ hasText: jobName })).toContainText('Your answer is needed');
    const sections = page.getByRole('navigation', { name: 'Sections of my assignments' });
    const requests = sections.locator('a[data-section="requests"]');
    await expect(requests.locator('[data-count]')).toHaveText(/^\s*[1-9]/);
    await requests.click();
    await expect(page).toHaveURL(/[?&]status=requested/);
    await expect(requests).toHaveAttribute('aria-current', 'page');
    await expect(page.locator('#my-assignments-title')).toHaveText('Requests');
    await expect(page.locator('.list-row').filter({ hasText: jobName })).toContainText('Your answer is needed');
    await capture(page, '01-my-assignments-request', width);
    const bell = await openBell(page);
    const notice = bell.locator('.notice-row').filter({ hasText: `You are asked to work ${jobName}` });
    await expect(notice).toHaveCount(1);
    await capture(page, '02-bell-request', width);
    await notice.click();
    const request = page.getByRole('dialog').last();
    await expect(request.locator('[data-job]')).toHaveText(jobName);
    await expect(pill(request)).toHaveAttribute('data-state', 'Requested');
    await expect(request.locator('button[data-transition="assignment-accept"]')).toBeVisible();
    await expect(request.locator('button[data-transition="assignment-decline"]')).toBeVisible();
    await expect(request.locator('button[data-transition="assignment-ask-coordinator"]')).toBeVisible();
    await expect(request.locator('button[data-transition]')).toHaveCount(3);
    await capture(page, '03-request-dialog', width);
    await takeStep(page, request, 'assignment-accept');
    await expect(pill(request)).toHaveAttribute('data-state', 'Accepted');
    await expect(request.locator('button[data-transition="assignment-withdraw"]')).toBeVisible();
    await capture(page, '04-accepted', width);
    await request.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    await gotoReady(page, '/me/assignments');
    const row = page.locator('.list-row').filter({ hasText: jobName });
    await expect(row).toContainText('Accepted');
    await expect(row).toContainText('the coordinator picks who works');
    await capture(page, '05-my-assignments-accepted', width);
    await page.setViewportSize({ width: 390, height: 844 });
    await capture(page, '05-my-assignments-accepted', 390);
    await page.setViewportSize({ width, height: 800 });
    await row.locator('.list-card__date').click();
    const opened = page.getByRole('dialog').last();
    await expect(opened.locator('[data-job]')).toHaveText(jobName);
    await expect(pill(opened)).toHaveAttribute('data-state', 'Accepted');
    await opened.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    const month = day.slice(0, 7);
    await gotoReady(page, `/me/schedule?month=${month}`);
    await expect(page.locator('[data-schedule-month]')).toHaveAttribute('data-schedule-month', month);
    const occurrenceDayRow = page.locator(`li[data-date="${day}"]`);
    await expect(occurrenceDayRow).toHaveCount(1);
    await expect(page.locator('.person-schedule__days')).not.toContainText(jobName);
    await expect(page).toHaveURL(new RegExp(`[?&]month=${month}`));
    await occurrenceDayRow.scrollIntoViewIfNeeded();
    await capture(page, '06-my-schedule-month', width);

    await page.getByRole('button', { name: 'Next month', exact: true }).click();
    await expect(page).not.toHaveURL(new RegExp(`[?&]month=${month}`));
    await expect(page).toHaveURL(/[?&]month=\d{4}-\d{2}/);
    await page.getByRole('button', { name: 'Previous month', exact: true }).click();
    await expect(page.locator('[data-schedule-month]')).toHaveAttribute('data-schedule-month', month);

    await gotoReady(page, '/me/schedule');
    await page.getByRole('button', { name: 'Report absence', exact: true }).click();
    const fresh = page.getByRole('dialog').last();
    await expect(fresh.locator('[data-absence-note]')).toContainText('You report your own absence');
    await expect(fresh.locator('button[data-transition]')).toHaveCount(1);
    await fresh.locator('[name="absence-start-time"]').fill('13:00');
    await capture(page, '07-report-absence', width);
    await takeStep(page, fresh, 'absence-report');
    const reported = page.getByRole('dialog').last();
    await expect(pill(reported)).toHaveAttribute('data-state', 'Requested');
    await expect(page).toHaveURL(/dlg=absence-[0-9a-f-]{36}/);
    const absenceId = /dlg=absence-([0-9a-f-]{36})/.exec(page.url())![1];
    await reported.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    const absence = page.locator(`li[data-absence="${absenceId}"]`);
    await expect(absence).toContainText('Planned leave from 13:00');
    await expect(absence).toContainText('Waiting for approval');
    await absence.scrollIntoViewIfNeeded();
    await capture(page, '08-my-schedule-absence', width);

    await absence.locator('button').click();
    await expect(pill(page.getByRole('dialog').last())).toHaveAttribute('data-state', 'Requested');
    await page.getByRole('dialog').last().getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await expect(absence.locator('button')).toBeFocused();

    await gotoReady(coordinator, '/staffing/jobs');
    const coordinatorBell = await openBell(coordinator);
    await expect(coordinatorBell.locator('.notice-row').filter({ hasText: `${staffMember} accepted ${jobName}` })).toHaveCount(1);
    const leave = coordinatorBell.locator('.notice-row').filter({ hasText: `Leave requested by ${staffMember}` }).first();
    await expect(leave).toBeVisible();
    await capture(coordinator, '09-coordinator-bell', width);
    await leave.click();
    const shown = coordinator.getByRole('dialog').last();
    await expect(pill(shown)).toHaveAttribute('data-state', 'Requested');
    await expect(coordinator).toHaveURL(new RegExp(`dlg=absence-${absenceId}`));
    await shown.getByRole('button', { name: 'Close', exact: true }).first().click();

    await absence.locator('button').click();
    const own = page.getByRole('dialog').last();
    await takeStep(page, own, 'absence-cancel');
    await page.locator('.form-dialog').getByRole('button', { name: 'Cancel absence', exact: true }).click();
    await expect(pill(own)).toHaveAttribute('data-state', 'Canceled');
    await own.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.locator(`li[data-absence="${absenceId}"]`)).toHaveCount(0);
    await cancelJob(coordinator, jobName);
    jobLive = false;
  } finally {
    if (jobLive) {
      await cancelJob(coordinator, jobName).catch(() => undefined);
    }

    await coordinatorSide.close();
  }
});
