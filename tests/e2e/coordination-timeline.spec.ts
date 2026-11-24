import type { Browser, Locator, Page } from '@playwright/test';
import { buttonContrastViolations } from './lib/accessibility';
import { persona, personaKeys, personaName, readFoundationState, type FoundationPersona } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { expectSettledPageAccessible, gotoReady, loginAs, waitForSettled } from './lib/session';
import { shot } from './lib/shots';
import { showEveryStaff, staffRow as boardRow, timeline, timelineReady as boardReady, toStaffOn } from './lib/workspace';
import { cancelJob, runSuffix, staffInPlace, startedJob } from './lib/jobs';

function occurrenceDay(): Date {
  const day = new Date();
  day.setDate(day.getDate() + 2);
  while (day.getDay() === 0 || day.getDay() === 6) day.setDate(day.getDate() + 1);
  return day;
}

function isoDay(day: Date): string {
  return `${day.getFullYear()}-${String(day.getMonth() + 1).padStart(2, '0')}-${String(day.getDate()).padStart(2, '0')}`;
}

function mondayOf(day: Date): Date {
  const monday = new Date(day);
  monday.setDate(day.getDate() - ((day.getDay() + 6) % 7));
  return monday;
}

async function capture(page: Page, name: string, width: number): Promise<void> {
  await page.mouse.move(0, 0);
  await expectSettledPageAccessible(page);
  expect(await buttonContrastViolations(page)).toEqual([]);
  await shot(page, 'coordination', `${name}-${width}`, [page.locator('.state-entity-dialog-footer__notice')]);
}

async function takeStep(page: Page, dialog: Locator, key: string): Promise<void> {
  const button = dialog.locator(`button[data-transition="${key}"]`);
  await expect(button).toBeVisible();
  await expect(button).not.toHaveAttribute('aria-disabled', 'true');
  await button.focus();
  await expect(button).toBeFocused();
  await button.press('Enter');
}

async function confirmStep(page: Page, dialog: Locator, key: string, label: string): Promise<void> {
  await takeStep(page, dialog, key);
  const confirm = page.locator('.form-dialog').filter({ has: page.getByRole('button', { name: label, exact: true }) });
  await confirm.getByRole('button', { name: label, exact: true }).click();
  await expect(confirm).toBeHidden();
}

const pill = (dialog: Locator) => dialog.locator('.state-pill__button');

async function pickDay(dialog: Locator, name: string, day: Date): Promise<void> {
  const input = dialog.locator(`input[name="${name}"]`);
  await input.fill(isoDay(day));
  await input.press('Tab');
  await expect(input).toHaveValue(isoDay(day));
}

async function publishPattern(page: Page, name: string): Promise<void> {
  await gotoReady(page, '/staffing/work-patterns');
  await page.getByRole('button', { name: 'New pattern', exact: true }).click();
  const draft = page.getByRole('dialog', { name: 'New work pattern', exact: true });
  await draft.locator('[name="pattern-name"]').fill(name);
  for (const day of [0, 1, 2, 3, 4]) await draft.locator(`[data-add-day="${day}"]`).click();
  await draft.locator('button[data-transition="pattern-save"]').click();
  const saved = page.getByRole('dialog', { name, exact: true });
  await expect(pill(saved)).toHaveAttribute('data-state', 'Draft');
  await confirmStep(page, saved, 'pattern-publish', 'Publish');
  await expect(pill(saved)).toHaveAttribute('data-state', 'Published');
  await saved.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
}

async function openSchedule(page: Page, staffMember: string): Promise<Locator> {
  await gotoReady(page, '/staffing/staff');
  await page.locator('tr.list-row').filter({ hasText: staffMember }).first().click();
  const dialog = page.getByRole('dialog', { name: staffMember, exact: true });
  await dialog.getByRole('tab', { name: 'Schedule' }).click();
  await expect(dialog.locator('[data-staff-schedule]')).toBeVisible();
  return dialog;
}

async function clearSchedule(page: Page, schedule: Locator): Promise<void> {
  for (;;) {
    const active = schedule.locator('tr.list-row').filter({ hasText: /\bActive\b/ });
    const prepared = schedule.locator('tr.list-row').filter({ hasText: 'Registration' });
    const isActive = (await active.count()) > 0;
    const target = isActive ? active.first() : (await prepared.count()) > 0 ? prepared.first() : null;
    if (!target) return;
    const key = await target.getAttribute('data-row');
    await target.click();
    const assignment = page.getByRole('dialog').last();
    await expect(pill(assignment)).toHaveAttribute('data-state', isActive ? 'Active' : 'Registration');
    if (isActive) {
      await confirmStep(page, assignment, 'pattern-assignment-deactivate', 'Withdraw');
      await expect(pill(assignment)).toHaveAttribute('data-state', 'Inactive');
      await assignment.getByRole('button', { name: 'Close', exact: true }).click();
    } else {
      await confirmStep(page, assignment, 'pattern-assignment-delete', 'Delete');
    }

    await expect(page.getByRole('dialog')).toHaveCount(1);
    const row = schedule.locator(`tr.list-row[data-row="${key}"]`);
    if (isActive) await expect(row).toContainText('Inactive');
    else await expect(row).toHaveCount(0);
  }
}

async function givePattern(page: Page, staffMember: string, name: string): Promise<void> {
  const schedule = await openSchedule(page, staffMember);
  await clearSchedule(page, schedule);
  await schedule.getByRole('button', { name: 'Assign work pattern', exact: true }).click();
  const fresh = page.getByRole('dialog', { name: 'Assign work pattern', exact: true });
  const pattern = fresh.locator('.rz-dropdown').filter({ has: page.locator('[name="assignment-pattern"]') });
  await pattern.click();
  await page.getByRole('option', { name, exact: true }).click();
  await expect(page.getByRole('option', { name, exact: true }).filter({ visible: true })).toHaveCount(0);
  await expect(pattern.locator('.rz-dropdown-label')).toContainText(name);
  await waitForSettled(page);
  await fresh.locator('button[data-transition="pattern-assignment-save"]').click();
  const prepared = page.getByRole('dialog').last();
  await expect(pill(prepared)).toHaveAttribute('data-state', 'Registration');
  await waitForSettled(page);
  await takeStep(page, prepared, 'pattern-assignment-activate');
  await expect(pill(prepared)).toHaveAttribute('data-state', 'Active');
  await prepared.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(page.getByRole('dialog')).toHaveCount(1);
  await schedule.getByRole('button', { name: 'Close', exact: true }).first().click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
}

async function clearApprovedAbsences(browser: Browser, staff: FoundationPersona): Promise<void> {
  const context = await browser.newContext();
  try {
    const page = await context.newPage();
    await loginAs(page, staff);
    await gotoReady(page, '/staffing/absence?status=approved');
    for (;;) {
      const rows = page.locator('tr.list-row');
      if ((await rows.count()) === 0) return;
      const id = await rows.first().getAttribute('data-row');
      await rows.first().click();
      const dialog = page.getByRole('dialog').last();
      await confirmStep(page, dialog, 'absence-cancel-approved', 'Cancel absence');
      await expect(pill(dialog)).toHaveAttribute('data-state', 'Canceled');
      await dialog.getByRole('button', { name: 'Close', exact: true }).first().click();
      await expect(page.getByRole('dialog')).toHaveCount(0);
      await expect(page.locator(`tr.list-row[data-row="${id}"]`)).toHaveCount(0);
    }
  } finally {
    await context.close();
  }
}

test(`a coordinator staffs a slot from the timeline and adds a staff member's absence from her row, shown without a reload`, { tag: '@coordination' }, async ({ page, browser }) => {
  test.setTimeout(300_000);
  const width = 1280;
  await page.setViewportSize({ width, height: 800 });
  const state = readFoundationState();
  const kim = personaName(persona(personaKeys.staff, state));
  const suffix = runSuffix();
  const jobName = `Board algebra ${suffix}`;
  const patternName = `Board hours ${suffix}`;
  const day = occurrenceDay();
  const shownDay = isoDay(day);
  const week = isoDay(mondayOf(day));

  await clearApprovedAbsences(browser, persona(personaKeys.staff, state));
  await loginAs(page, persona(personaKeys.coordinator, state));
  await publishPattern(page, patternName);
  await givePattern(page, kim, patternName);
  await test.step('set-up: a started job of the run', () => startedJob(page, jobName, { day, start: '10:00', end: '11:00', slots: ['Lead', 'Assistant'] }));
  try {
    await gotoReady(page, `/coordination?day=${shownDay}`);
    await boardReady(page);
    await expect(timeline(page)).toHaveAttribute('data-day', shownDay);
    await expect(timeline(page)).toHaveAttribute('data-board-week', week);
    const staffDay = toStaffOn(page, shownDay);
    await expect(staffDay.filter({ hasText: jobName })).toHaveCount(2);
    await showEveryStaff(page);
    await expect(boardRow(page, kim).locator(`[data-band][data-day="${shownDay}"]`)).toHaveAttribute('data-band', '08:00-16:00');

    const mainStaff = staffDay.filter({ hasText: `Lead, ${jobName}` });
    await mainStaff.click();
    const need = page.getByRole('dialog', { name: 'Lead', exact: true });
    await expect(need.getByRole('tab', { name: 'Staffing' })).toHaveAttribute('aria-selected', 'true');
    await staffInPlace(need, kim, 'assign');
    await expect(need.locator('[data-staffing-filled]')).toContainText(`Filled by ${kim}`);
    await page.evaluate(() => { (window as unknown as { boardKept?: boolean }).boardKept = true; });
    await need.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    const kimOccurrence = boardRow(page, kim).locator(`button.timeline-bar--slot[data-day="${shownDay}"]`).filter({ hasText: jobName });
    await expect(kimOccurrence).toContainText('10:00-11:00');
    await expect(kimOccurrence).toContainText('Assigned');
    await expect(staffDay.filter({ hasText: jobName })).toHaveCount(1);
    await boardReady(page);
    await expect(page.locator('#board-row-staff')).toBeFocused();
    await boardRow(page, kim).scrollIntoViewIfNeeded();
    await capture(page, '10-timeline-day', width);

    await boardRow(page, kim).locator(`button[data-open-schedule="${kim}"]`).click();
    const schedule = page.getByRole('dialog', { name: `Schedule of ${kim}`, exact: true });
    await expect(schedule.locator('[data-person-schedule]')).toBeVisible();
    await schedule.getByRole('button', { name: 'Register absence', exact: true }).click();
    const fresh = page.getByRole('dialog').last();
    await expect(fresh.locator('[data-staff]')).toContainText(kim);
    await fresh.locator('.rz-dropdown').filter({ has: page.locator('[name="absence-kind"]') }).click();
    await page.getByRole('option', { name: 'Sick leave', exact: true }).filter({ visible: true }).click();
    await expect(page.getByRole('option', { name: 'Sick leave', exact: true }).filter({ visible: true })).toHaveCount(0);
    await pickDay(fresh, 'absence-first-day', day);
    await pickDay(fresh, 'absence-last-day', day);
    await capture(page, '11-timeline-add-absence', width);
    await takeStep(page, fresh, 'absence-register');
    await expect(pill(fresh)).toHaveAttribute('data-state', 'Approved');
    await fresh.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(1);
    await schedule.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    const kimAbsence = boardRow(page, kim).locator(`button.timeline-bar--absence[data-day="${shownDay}"]`);
    await expect(kimAbsence).toContainText('Sick leave, handled');
    await expect(kimAbsence).toContainText('All day');
    await expect(boardRow(page, kim).locator(`[data-band][data-day="${shownDay}"]`)).toHaveAttribute('data-band', '08:00-16:00');
    await expect(kimOccurrence).toHaveCount(1);
    await boardReady(page);
    expect(await page.evaluate(() => (window as unknown as { boardKept?: boolean }).boardKept), 'the page was not reloaded').toBe(true);
    await boardRow(page, kim).scrollIntoViewIfNeeded();
    await capture(page, '12-timeline-absent-staff', width);
    await page.setViewportSize({ width: 390, height: 844 });
    await boardReady(page);
    await boardRow(page, kim).scrollIntoViewIfNeeded();
    await capture(page, '12-timeline-absent-staff', 390);
    await page.setViewportSize({ width, height: 800 });
    await boardReady(page);

    const find = page.locator('input[data-find-staff]');
    await find.fill(kim);
    await find.press('Enter');
    await expect(page).toHaveURL(/[?&]board\.q=/);
    await boardReady(page);
    await expect(page.locator('.timeline-row[data-staff]')).toHaveCount(1);
    await expect(boardRow(page, kim)).toHaveCount(1);
  } finally {
    await clearApprovedAbsences(browser, persona(personaKeys.staff, state));
    await cancelJob(page, jobName);
    const schedule = await openSchedule(page, kim);
    await clearSchedule(page, schedule);
  }
});
