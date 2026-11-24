import type { Browser, Locator, Page } from '@playwright/test';
import { buttonContrastViolations } from './lib/accessibility';
import { persona, personaKeys, personaName, readFoundationState } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { expectSettledPageAccessible, gotoReady, loginAs, waitForSettled } from './lib/session';
import { shot } from './lib/shots';
import { cancelJob, candidateRow, runSuffix, staffInPlace, startedJob, workdayAhead } from './lib/jobs';

async function capture(page: Page, name: string, width: number): Promise<void> {
  await page.mouse.move(0, 0);
  await expectSettledPageAccessible(page);
  expect(await buttonContrastViolations(page)).toEqual([]);
  await shot(page, 'staffing', `${name}-${width}`, [page.locator('.state-entity-dialog-footer__notice')]);
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

async function staffSeesTheDeadline(browser: Browser, jobName: string, width: number): Promise<void> {
  const context = await browser.newContext({ viewport: { width, height: 800 } });
  try {
    const page = await context.newPage();
    await loginAs(page, persona(personaKeys.staff, readFoundationState()));
    await gotoReady(page, '/me/work');
    const request = page.locator('[data-own-request]').filter({ hasText: jobName });
    await expect(request).toContainText(/Answer by \w{3} \d{1,2} \w{3} \d{4}, \d{2}:\d{2}/);
    await request.click();
    const dialog = page.getByRole('dialog').last();
    await expect(dialog.locator('[data-assignment-status] [data-answer-by]')).toHaveText(/^Answer by \w{3} \d{1,2} \w{3} \d{4}, \d{2}:\d{2}$/);
    await capture(page, '02-staff-answer-by', width);
  } finally {
    await context.close();
  }
}

test('a coordinator chooses the time to answer a request in the Staffing view and the staff member reads its deadline', { tag: '@staffing' }, async ({ page, browser }) => {
  test.setTimeout(240_000);
  const state = readFoundationState();
  const staffMember = personaName(persona(personaKeys.staff, state));
  const jobName = `Deadline cleaning ${runSuffix()}`;

  await page.setViewportSize({ width: 1280, height: 800 });
  await loginAs(page, persona(personaKeys.coordinator, state));
  const job = await test.step('set-up: a started job of the run', () => startedJob(page, jobName, { day: workdayAhead(9), start: '06:00', end: '06:30', slots: ['Lead'], keepOpen: true }));
  await job.getByRole('button', { name: 'Close', exact: true }).first().click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  try {
    const slot = await openLeadStaffing(page, jobName);
    const within = slot.locator('[data-respond-within]');
    await expect(within).toContainText('2 days');
    await within.locator('.rz-dropdown').click();
    await page.getByRole('option', { name: '4 hours', exact: true }).click();
    await expect(within).toContainText('4 hours');
    await expect(candidateRow(slot, staffMember).locator('button[data-staff-action="request"]')).toBeEnabled();
    await staffInPlace(slot, staffMember, 'request');
    await expect(slot.locator(`[data-asked="${staffMember}"] [data-answer-by]`)).toHaveText(/^Answer by \w{3} \d{1,2} \w{3} \d{4}, \d{2}:\d{2}$/);
    for (const width of [1280, 390]) {
      await page.setViewportSize({ width, height: 800 });
      await waitForSettled(page);
      await capture(page, '01-asked-answer-by', width);
    }

    await page.setViewportSize({ width: 1280, height: 800 });
    for (const width of [1280, 390]) await staffSeesTheDeadline(browser, jobName, width);
  } finally {
    await cancelJob(page, jobName);
  }
});
