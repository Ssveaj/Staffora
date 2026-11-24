import type { Locator, Page } from '@playwright/test';
import { persona, personaKeys, personaName, readFoundationState } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { expectSettledPageAccessible, gotoReady, loginAs } from './lib/session';
import { shot } from './lib/shots';

const width = 1280;

const staffMembersPage = '/staffing/staff';
const patternsPage = '/staffing/work-patterns';

async function capture(page: Page, name: string, width: number): Promise<void> {
  await expectSettledPageAccessible(page);
  await shot(page, 'schedules', `${name}-${width}`, [page.locator('.state-entity-dialog-footer__notice')]);
}

async function confirmStep(page: Page, dialog: Locator, key: string, label: string): Promise<void> {
  await dialog.locator(`button[data-transition="${key}"]`).focus();
  await page.keyboard.press('Enter');
  const confirm = page.locator('.form-dialog').filter({ has: page.getByRole('button', { name: label, exact: true }) });
  await confirm.getByRole('button', { name: label, exact: true }).click();
  await expect(confirm).toBeHidden();
}

async function takeStep(page: Page, dialog: Locator, key: string): Promise<void> {
  await dialog.locator(`button[data-transition="${key}"]`).focus();
  await page.keyboard.press('Enter');
}

async function publishPattern(page: Page, name: string): Promise<void> {
  await gotoReady(page, patternsPage);
  await page.getByRole('button', { name: 'New pattern', exact: true }).click();
  const draft = page.getByRole('dialog', { name: 'New work pattern', exact: true });
  await draft.locator('[name="pattern-name"]').fill(name);
  for (const day of [0, 1, 2, 3, 4]) await draft.locator(`[data-add-day="${day}"]`).click();
  await draft.locator('button[data-transition="pattern-save"]').click();
  const saved = page.getByRole('dialog', { name, exact: true });
  await expect(saved.locator('.state-pill__button')).toHaveAttribute('data-state', 'Draft');
  await confirmStep(page, saved, 'pattern-publish', 'Publish');
  await expect(saved.locator('.state-pill__button')).toHaveAttribute('data-state', 'Published');
  await saved.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
}

async function openSchedule(page: Page, staffMember: string): Promise<Locator> {
  await gotoReady(page, staffMembersPage);
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
    const target = (await active.count()) > 0 ? active.first() : (await prepared.count()) > 0 ? prepared.first() : null;
    if (!target) return;
    const isActive = (await active.count()) > 0;
    const key = await target.getAttribute('data-row');
    await target.click();
    const assignment = page.getByRole('dialog').last();
    await expect(assignment.locator('.state-pill__button')).toHaveAttribute('data-state', isActive ? 'Active' : 'Registration');
    if (isActive) {
      await confirmStep(page, assignment, 'pattern-assignment-deactivate', 'Withdraw');
      await expect(assignment.locator('.state-pill__button')).toHaveAttribute('data-state', 'Inactive');
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

async function prepareAssignment(page: Page, schedule: Locator, name: string, width: number, shotName?: string): Promise<Locator> {
  await schedule.getByRole('button', { name: 'Assign work pattern', exact: true }).click();
  const fresh = page.getByRole('dialog', { name: 'Assign work pattern', exact: true });
  await expect(fresh.locator('[data-staff]')).toBeVisible();
  await fresh.locator('.rz-dropdown').filter({ has: page.locator('[name="assignment-pattern"]') }).click();
  await page.getByRole('option', { name, exact: true }).click();
  if (shotName) await capture(page, shotName, width);
  await fresh.locator('button[data-transition="pattern-assignment-save"]').click();
  const prepared = page.getByRole('dialog').last();
  await expect(prepared.locator('.state-pill__button')).toHaveAttribute('data-state', 'Registration');
  await expect(prepared.getByRole('heading')).toContainText(`${name} from `);
  return prepared;
}

test(`a coordinator gives a staff member a work pattern and the Schedule view shows it across a period; an overlapping one is refused`, { tag: '@staffing' }, async ({ page }) => {
  test.setTimeout(180_000);
  await page.setViewportSize({ width, height: 800 });
  const state = readFoundationState();
  const staffMember = personaName(persona(personaKeys.staff, state));
  const pattern = `Period hours ${Date.now().toString(36)}${Math.floor(Math.random() * 1000)}`;
  await loginAs(page, persona(personaKeys.coordinator, state));
  await publishPattern(page, pattern);

  let schedule = await openSchedule(page, staffMember);
  await clearSchedule(page, schedule);

  const assignment = await prepareAssignment(page, schedule, pattern, width, '01-new-assignment');
  await capture(page, '02-assignment-prepared', width);
  await takeStep(page, assignment, 'pattern-assignment-activate');
  await expect(assignment.locator('.state-pill__button')).toHaveAttribute('data-state', 'Active');
  await expect(assignment.locator('button[data-transition="pattern-assignment-deactivate"]')).toBeVisible();
  await capture(page, '03-assignment-active', width);
  await assignment.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(page.getByRole('dialog')).toHaveCount(1);

  const weeks = schedule.locator('[data-staff-schedule] tbody tr[data-week]');
  await expect(weeks).toHaveCount(20);
  for (const week of [1, 10, 19]) {
    const row = weeks.nth(week);
    await expect(row.locator('td[data-date]').first()).toHaveText('08:00–16:00');
    await expect(row.locator('td[data-date]').nth(5)).toHaveText('Off');
    await expect(row.locator('td[data-worked]')).toHaveText('40 h');
  }
  await expect(schedule.locator('tr.list-row').filter({ hasText: pattern }).filter({ hasText: /\bActive\b/ })).toHaveCount(1);
  await capture(page, '04-schedule-across-a-period', width);

  const overlapping = await prepareAssignment(page, schedule, pattern, width);
  const activate = overlapping.locator('button[data-transition="pattern-assignment-activate"]');
  await expect(activate).toHaveAttribute('aria-disabled', 'true');
  await activate.focus();
  await expect(overlapping.locator(`#${await activate.getAttribute('aria-describedby')}`)).toContainText(`It overlaps the active assignment of ${pattern}`);
  await takeStep(page, overlapping, 'pattern-assignment-activate');
  await expect(overlapping.locator('.state-pill__button')).toHaveAttribute('data-state', 'Registration');
  await capture(page, '05-overlap-refused', width);
  await confirmStep(page, overlapping, 'pattern-assignment-delete', 'Delete');
  await expect(page.getByRole('dialog')).toHaveCount(1);

  schedule = page.getByRole('dialog', { name: staffMember, exact: true });
  await clearSchedule(page, schedule);
  await expect(schedule.locator('tr.list-row').filter({ hasText: /\bActive\b/ })).toHaveCount(0);
});
