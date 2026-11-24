import type { Browser, Locator, Page } from '@playwright/test';
import { buttonContrastViolations } from './lib/accessibility';
import { persona, personaKeys, personaName, readFoundationState } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { expectSettledPageAccessible, gotoReady, loginAs, waitForSettled } from './lib/session';
import { shot } from './lib/shots';
import { cancelJob, candidateRow, runSuffix, startedJob, workdayAhead } from './lib/jobs';

async function capture(page: Page, name: string): Promise<void> {
  await page.mouse.move(0, 0);
  await expectSettledPageAccessible(page);
  expect(await buttonContrastViolations(page)).toEqual([]);
  await shot(page, 'staffing', name, [page.locator('.state-entity-dialog-footer__notice')]);
}

async function takeStep(page: Page, dialog: Locator, key: string): Promise<void> {
  await dialog.locator(`button[data-transition="${key}"]`).focus();
  await page.keyboard.press('Enter');
}

const pill = (dialog: Locator) => dialog.locator('.state-pill__button');

async function openGroup(page: Page, jobName: string, view: string): Promise<{ job: Locator; slot: Locator }> {
  await gotoReady(page, `/staffing/jobs?q=${encodeURIComponent(jobName)}`);
  await page.locator('tr.list-row').filter({ hasText: jobName }).click();
  const job = page.getByRole('dialog', { name: jobName, exact: true });
  await job.getByRole('tab', { name: 'Slots' }).click();
  await job.locator('tr.list-row').filter({ hasText: 'Cleaner' }).locator('td').first().click();
  const slot = page.getByRole('dialog', { name: 'Cleaner', exact: true });
  await slot.getByRole('tab', { name: view }).click();
  await waitForSettled(page);
  return { job, slot };
}

async function acceptAsStaff(browser: Browser, key: string, jobName: string, shotName?: string): Promise<void> {
  const state = readFoundationState();
  const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  try {
    const page = await context.newPage();
    await loginAs(page, persona(key, state));
    await gotoReady(page, '/me/work');
    const request = page.locator('[data-own-request]').filter({ hasText: jobName });
    await expect(request).toHaveCount(1);
    await request.click();
    const dialog = page.getByRole('dialog').last();
    await expect(pill(dialog)).toHaveAttribute('data-state', 'Requested');
    await expect(dialog.locator('[data-round-rule]')).toHaveText('The first to accept gets a slot.');
    await takeStep(page, dialog, 'assignment-accept');
    await expect(pill(dialog)).toHaveAttribute('data-state', 'Assigned');
    if (shotName) await capture(page, shotName);
    await dialog.getByRole('button', { name: 'Close', exact: true }).first().click();
  } finally {
    await context.close();
  }
}

test('a coordinator sends a request round for a slot group and the first two to accept take its two slots', { tag: '@staffing' }, async ({ page, browser }) => {
  test.setTimeout(300_000);
  await page.setViewportSize({ width: 1280, height: 800 });
  const state = readFoundationState();
  const staff = [personaName(persona(personaKeys.staff, state)), personaName(persona(personaKeys.secondStaff, state))];
  const jobName = `Office cleaning ${runSuffix()}`;

  await loginAs(page, persona(personaKeys.coordinator, state));
  const job = await test.step('set-up: a started job of the run', () => startedJob(page, jobName, { day: workdayAhead(), start: '09:00', end: '10:00', slots: [{ name: 'Cleaner', count: 2 }], keepOpen: true }));
  await expect(job.locator('tr.list-row')).toHaveCount(1);
  await expect(job.locator('tr.list-row').filter({ hasText: 'Cleaner' })).toContainText('0 of 2');
  await job.getByRole('button', { name: 'Close', exact: true }).first().click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  try {
    let { slot } = await openGroup(page, jobName, 'Staffing');
    await expect(slot.locator('[data-open-slots]')).toHaveText('0 of 2 held');
    await slot.locator('button[data-request-several]').click();
    for (const name of staff) await slot.locator(`[data-candidate="${name}"] .rz-chkbox`).click();
    await expect(slot.locator('[data-round-chosen]')).toHaveText('2 chosen');
    await capture(page, '01-round-form');
    await slot.locator('button[data-send-round]').click();
    for (const name of staff) await expect(candidateRow(slot, name)).toHaveCount(0);
    await expect(page.getByRole('dialog')).toHaveCount(2);

    await slot.getByRole('tab', { name: 'Request rounds' }).click();
    await slot.locator('[data-view="rounds"] tr.list-row').first().locator('td').first().click();
    let round = page.getByRole('dialog', { name: 'Request round', exact: true });
    await expect(pill(round)).toHaveAttribute('data-state', 'Open');
    await expect(round.locator('[data-round-held]')).toHaveText('0 of 2');
    await expect(round.locator('[data-round-rule]')).toHaveText('The first to accept gets a slot.');
    await expect(round.locator('[data-round-counts]')).toContainText('2 asked');
    await capture(page, '02-round-sent');
    await round.getByRole('button', { name: 'Close', exact: true }).first().click();

    await acceptAsStaff(browser, personaKeys.staff, jobName, '03-staff-accepts');
    await acceptAsStaff(browser, personaKeys.secondStaff, jobName);

    const opened = await openGroup(page, jobName, 'Request rounds');
    slot = opened.slot;
    await slot.locator('[data-view="rounds"] tr.list-row').first().locator('td').first().click();
    round = page.getByRole('dialog', { name: 'Request round', exact: true });
    await expect(pill(round)).toHaveAttribute('data-state', 'Filled');
    await expect(round.locator('[data-round-held]')).toHaveText('2 of 2');
    await expect(round.locator('[data-round-counts]')).toContainText('2 assigned');
    await round.getByRole('button', { name: 'Close', exact: true }).first().click();
    await slot.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(opened.job.locator('tr.list-row').filter({ hasText: 'Cleaner' })).toContainText('2 of 2');
    for (const name of staff) await expect(opened.job.locator('tr.list-row').filter({ hasText: 'Cleaner' })).toContainText(name);
    await capture(page, '04-group-filled');
    await opened.job.getByRole('button', { name: 'Close', exact: true }).first().click();
  } finally {
    await cancelJob(page, jobName);
  }
});
