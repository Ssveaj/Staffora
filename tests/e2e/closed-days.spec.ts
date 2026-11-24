import type { Browser, Locator, Page } from '@playwright/test';
import { persona, personaKeys, personaName, readFoundationState } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { expectSettledPageAccessible, gotoReady, impersonate, loginAs, waitForSettled } from './lib/session';
import { shot } from './lib/shots';
import { cancelJob, candidateRow, isoDay, runSuffix, startedJob, workdayAhead } from './lib/jobs';

const closedDaysPage = '/tenant/management/settings/closed-days';

async function bothWidths(page: Page, name: string, settle: () => Promise<void> = async () => {}): Promise<void> {
  for (const viewport of [{ width: 1280, height: 800 }, { width: 390, height: 844 }]) {
    await page.setViewportSize(viewport);
    await settle();
    await page.mouse.move(0, 0);
    await expectSettledPageAccessible(page);
    await shot(page, 'closed-days', `${name}-${viewport.width}`);
  }
  await page.setViewportSize({ width: 1280, height: 800 });
  await waitForSettled(page);
}

async function asAdministrator(browser: Browser): Promise<Page> {
  const state = readFoundationState();
  const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  const page = await context.newPage();
  await loginAs(page, persona(personaKeys.platformOwner, state));
  await impersonate(page, state.tenantAdministrator);
  return page;
}

async function addClosedDay(page: Page, name: string, day: Date): Promise<void> {
  await gotoReady(page, closedDaysPage);
  await expect(page.getByRole('navigation', { name: 'Settings' }).getByRole('link', { name: 'Closed days' })).toHaveAttribute('aria-current', 'page');
  await page.getByRole('button', { name: 'New closed day', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'New closed day', exact: true });
  await dialog.locator('[name="closed-day-name"]').fill(name);
  const first = dialog.locator('input[name="closed-day-first"]');
  await first.fill(isoDay(day));
  await first.press('Tab');
  await expect(first).toHaveValue(isoDay(day));
  await expect(dialog.locator('input[name="closed-day-last"]')).toHaveValue(isoDay(day));
  await expect(dialog.locator('[data-closed-day-scope]')).toHaveText('Every staff member of the organization.');
  await bothWidths(page, '01-new-closed-day');
  await dialog.locator('[data-closed-day-save]').click();
  await expect(dialog).toHaveCount(0);
  const row = page.locator('tr.list-row').filter({ hasText: name });
  await expect(row).toBeVisible();
  await expect(row).toContainText('The whole organization');
  await bothWidths(page, '02-closed-days-list');
}

async function deleteClosedDay(page: Page, name: string): Promise<void> {
  await gotoReady(page, `${closedDaysPage}?closed.q=${encodeURIComponent(name)}`);
  const row = page.locator('tr.list-row').filter({ hasText: name });
  if ((await row.count()) === 0) return;
  await row.locator('.list-row__open').click();
  const dialog = page.getByRole('dialog', { name, exact: true });
  await dialog.locator('[data-closed-day-delete]').click();
  await page.getByRole('dialog', { name: 'Delete closed day', exact: true }).getByRole('button', { name: 'Delete', exact: true }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(page.locator('tr.list-row').filter({ hasText: name })).toHaveCount(0);
}

async function openLeadStaffing(page: Page, jobName: string): Promise<Locator> {
  await gotoReady(page, `/staffing/jobs?q=${encodeURIComponent(jobName)}`);
  await page.locator('tr.list-row').filter({ hasText: jobName }).click();
  const job = page.getByRole('dialog', { name: jobName, exact: true });
  await job.getByRole('tab', { name: 'Slots' }).click();
  await job.locator('tr.list-row').filter({ hasText: 'Lead' }).locator('td').first().click();
  const slot = page.getByRole('dialog', { name: 'Lead', exact: true });
  await slot.getByRole('tab', { name: 'Staffing' }).click();
  await expect(slot.locator('[data-staffing-summary]')).toContainText(jobName);
  await waitForSettled(page);
  return slot;
}

test('a tenant administrator adds a closed day and a coordinator finds a candidate blocked on it and the day marked on the workspace', { tag: ['@staffing', '@admin'] }, async ({ page, browser }) => {
  test.setTimeout(300_000);
  const state = readFoundationState();
  const staffMember = personaName(persona(personaKeys.staff, state));
  const suffix = runSuffix();
  const closedName = `Founders' Day ${suffix}`;
  const jobName = `Closed day cleaning ${suffix}`;
  const day = workdayAhead(45);

  const administrator = await asAdministrator(browser);
  try {
    await test.step('the administrator adds a closed day for the whole organization', () => addClosedDay(administrator, closedName, day));

    await page.setViewportSize({ width: 1280, height: 800 });
    await loginAs(page, persona(personaKeys.coordinator, state));
    const job = await test.step('set-up: a started job of the run on that weekday', () => startedJob(page, jobName, { day, start: '06:00', end: '06:30', slots: ['Lead'], keepOpen: true }));
    await job.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    try {
      await test.step('the staff member is blocked on the closed day in the Staffing view', async () => {
        const slot = await openLeadStaffing(page, jobName);
        const candidate = candidateRow(slot, staffMember);
        await expect(candidate).toBeVisible();
        await expect(candidate).toHaveClass(/slot-staffing__candidate--blocked/);
        const verdict = candidate.locator('button[data-verdict][data-blocked="true"]');
        await expect(verdict).toContainText('Blocked');
        await expect(candidate.locator('button[data-staff-action="request"]')).toBeDisabled();
        await expect(candidate.locator('button[data-staff-action="assign"]')).toBeDisabled();
        await verdict.click();
        await expect(candidate.locator('[data-block]').filter({ hasText: `Closed for ${closedName}` })).toContainText(isoDay(day));
        await bothWidths(page, '03-candidate-blocked');
      });

      await test.step('the coordination workspace marks the day on the rail and on the timeline', async () => {
        await gotoReady(page, `/coordination?day=${isoDay(day)}`);
        await expect(page.locator('[data-closed-day-note]')).toHaveAttribute('data-closed-day-note', new RegExp(`Closed for ${closedName}`));
        const railDay = page.locator(`.calendar-rail button.mini-month__day[data-day="${isoDay(day)}"]`);
        await expect(railDay).toHaveClass(/mini-month__day--closed/);
        await expect(railDay).toHaveAttribute('aria-label', new RegExp(`Closed for ${closedName}`));
        const band = page.locator(`.timeline-row[data-staff="${staffMember}"] [data-closed][data-day="${isoDay(day)}"]`);
        await expect(band).toHaveAttribute('data-closed', new RegExp(`Closed for ${closedName}`));
        await bothWidths(page, '04-workspace-closed-day', async () => {
          await waitForSettled(page);
        });
      });
    } finally {
      await cancelJob(page, jobName);
    }
  } finally {
    await deleteClosedDay(administrator, closedName);
    await administrator.context().close();
  }
});
