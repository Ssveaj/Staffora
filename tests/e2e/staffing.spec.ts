import type { Browser, Locator, Page } from '@playwright/test';
import { buttonContrastViolations } from './lib/accessibility';
import { persona, personaKeys, personaName, readFoundationState } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { expectSettledPageAccessible, gotoReady, impersonate, loginAs, waitForSettled } from './lib/session';
import { shot } from './lib/shots';
import { cancelJob, candidateRow, runSuffix, staffInPlace, startedJob } from './lib/jobs';

function occurrenceDay(): Date {
  const day = new Date();
  day.setDate(day.getDate() + 2);
  return day;
}

function isoDay(day: Date): string {
  return `${day.getFullYear()}-${String(day.getMonth() + 1).padStart(2, '0')}-${String(day.getDate()).padStart(2, '0')}`;
}

async function pickDay(page: Page, dialog: Locator, name: string, day: Date): Promise<void> {
  const input = dialog.locator(`input[name="${name}"]`);
  await input.fill(isoDay(day));
  await input.press('Tab');
  await expect(input).toHaveValue(isoDay(day));
}

async function capture(page: Page, name: string, width: number): Promise<void> {
  await page.mouse.move(0, 0);
  await expectSettledPageAccessible(page);
  expect(await buttonContrastViolations(page)).toEqual([]);
  await shot(page, 'staffing', `${name}-${width}`, [page.locator('.state-entity-dialog-footer__notice')]);
}

async function takeStep(page: Page, dialog: Locator, key: string): Promise<void> {
  await dialog.locator(`button[data-transition="${key}"]`).focus();
  await page.keyboard.press('Enter');
}

const pill = (dialog: Locator) => dialog.locator('.state-pill__button');

async function openStaffing(page: Page, jobName: string): Promise<Locator> {
  await gotoReady(page, `/staffing/jobs?q=${encodeURIComponent(jobName)}`);
  await page.locator('tr.list-row').filter({ hasText: jobName }).click();
  const jobDialog = page.getByRole('dialog', { name: jobName, exact: true });
  await jobDialog.getByRole('tab', { name: 'Slots' }).click();
  await jobDialog.locator('tr.list-row').filter({ hasText: 'Lead' }).locator('td').first().click();
  const need = page.getByRole('dialog', { name: 'Lead', exact: true });
  await need.getByRole('tab', { name: 'Staffing' }).click();
  await expect(need.locator('[data-staffing-summary]')).toContainText(jobName);
  await waitForSettled(page);
  return need;
}

async function secondStaff(browser: Browser, suffix: string): Promise<string> {
  const state = readFoundationState();
  const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  try {
    const page = await context.newPage();
    await loginAs(page, persona(personaKeys.platformOwner, state));
    await impersonate(page, state.tenantAdministrator);
    const email = `noor.lund.${suffix}@company-a.example`;
    const name = `Noor Lund ${suffix}`;
    await gotoReady(page, '/tenant/management/users');
    await page.getByRole('button', { name: 'New User', exact: true }).click();
    const created = page.getByRole('dialog', { name: 'New user', exact: true });
    await created.locator('[name="user-first-name"]').fill('Noor');
    await created.locator('[name="user-last-name"]').fill(`Lund ${suffix}`);
    await created.locator('[name="user-email"]').fill(email);
    await created.locator('[name="user-username"]').fill(email);
    await created.locator('button[data-transition="user-create"]').click();
    const user = page.getByRole('dialog', { name, exact: true });
    await expect(pill(user)).toHaveAttribute('data-state', 'Active');
    await user.getByRole('button', { name: 'Close', exact: true }).click();
    await gotoReady(page, '/staffing/staff');
    await page.getByRole('button', { name: 'New staff member', exact: true }).click();
    const form = page.getByRole('dialog', { name: 'New staff member', exact: true });
    await form.locator('.rz-dropdown').filter({ has: page.locator('[name="role-holding-person"]') }).click();
    await page.getByRole('option', { name, exact: true }).click();
    await form.locator('.rz-dropdown').filter({ has: page.locator('[name="user-role-unit"]') }).click();
    await page.getByRole('option', { name: `${state.tenantAdministrator.tenant} › Head office`, exact: true }).click();
    await form.getByRole('button', { name: 'Make staff member', exact: true }).click();
    await expect(form).toBeHidden();
    return name;
  } finally {
    await context.close();
  }
}

test(`a coordinator staffs a job from the slot's Staffing view: ranked, an absent staff member blocked, an open request warned`, { tag: '@staffing' }, async ({ page, browser }) => {
  test.setTimeout(300_000);
  const day = occurrenceDay();
  const width = 1280;
  const viewport = { width, height: 800 };
  await page.setViewportSize(viewport);
  const state = readFoundationState();
  const kim = personaName(persona(personaKeys.staff, state));
  const suffix = runSuffix();
  const jobName = `Staffed algebra ${suffix}`;
  const otherName = `Overlapping physics ${suffix}`;
  const noor = await secondStaff(browser, suffix);

  await loginAs(page, persona(personaKeys.coordinator, state));
  const times = { day, start: '09:00', end: '10:00' };
  await test.step('set-up: a started job of the run', () => startedJob(page, jobName, times));
  await test.step('set-up: a second started job of the run', () => startedJob(page, otherName, times));

  const other = await openStaffing(page, otherName);
  await staffInPlace(other, kim, 'request');

  let need = await openStaffing(page, jobName);
  await expect(need.locator('[data-staffing-summary]')).toContainText('09:00-10:00');
  await expect(need.locator('[data-staffing-summary]')).toContainText('None');
  for (const staffMember of [kim, noor]) await expect(candidateRow(need, staffMember)).toHaveCount(1);
  await capture(page, '01-ranked', width);

  let candidate = candidateRow(need, kim);
  await expect(candidate.locator('[data-warning]').filter({ hasText: 'Asked to work a job' })).toContainText(`Asked to work a job, not answered yet ${otherName} · Lead`);
  await expect(candidate.locator('[data-block]')).toHaveCount(0);
  await expect(candidate.locator('button[data-staff-action="assign"]')).toBeEnabled();
  await capture(page, '03-warning-open-request', width);

  const staffContext = await browser.newContext({ viewport });
  try {
    const staffPage = await staffContext.newPage();
    await loginAs(staffPage, persona(personaKeys.staff, state));
    await gotoReady(staffPage, '/staffing/absence');
    await staffPage.getByRole('button', { name: 'Report absence', exact: true }).click();
    const fresh = staffPage.getByRole('dialog').last();
    await fresh.locator('.rz-dropdown').filter({ has: staffPage.locator('[name="absence-kind"]') }).click();
    await staffPage.getByRole('option', { name: 'Sick leave', exact: true }).filter({ visible: true }).click();
    await expect(fresh.locator('.rz-dropdown').filter({ has: staffPage.locator('[name="absence-kind"]') })).toContainText('Sick leave');
    await pickDay(staffPage, fresh, 'absence-last-day', day);
    await waitForSettled(staffPage);
    await takeStep(staffPage, fresh, 'absence-report');
    const sick = staffPage.getByRole('dialog').last();
    await expect(pill(sick)).toHaveAttribute('data-state', 'Requested');
    await expect(sick).toContainText(`to ${isoDay(day)}`);

    need = await openStaffing(page, jobName);
    candidate = candidateRow(need, kim);
    await expect(candidate.locator('[data-blocked]')).toHaveCount(1);
    await expect(candidate.locator('[data-block]').filter({ hasText: 'On sick leave' })).toContainText(`On sick leave (reported) ${isoDay(day)}`);
    await expect(candidate.locator('button[data-staff-action="request"]')).toBeDisabled();
    await expect(candidate.locator('button[data-staff-action="assign"]')).toBeDisabled();
    await capture(page, '02-blocked-absent', width);

    await staffInPlace(need, noor, 'assign');
    await expect(need.locator('[data-staffing-filled]')).toContainText(`Filled by ${noor}`);
    await need.getByRole('button', { name: 'Close', exact: true }).first().click();
    const jobDialog = page.getByRole('dialog', { name: jobName, exact: true });
    await expect(jobDialog.locator('tr.list-row').filter({ hasText: 'Lead' })).toContainText(`${noor} (assigned)`);
    await expect(jobDialog.locator('tr.list-row').filter({ hasText: 'Lead' })).toContainText('Covered');
    await capture(page, '04-fully-staffed', width);
    await page.setViewportSize({ width: 390, height: 844 });
    await capture(page, '04-fully-staffed', 390);
    await page.setViewportSize(viewport);

    await takeStep(staffPage, sick, 'absence-cancel');
    await staffPage.locator('.form-dialog').getByRole('button', { name: 'Cancel absence', exact: true }).click();
    await expect(pill(sick)).toHaveAttribute('data-state', 'Canceled');
  } finally {
    await staffContext.close();
  }

  await cancelJob(page, jobName);
  await cancelJob(page, otherName);
});
