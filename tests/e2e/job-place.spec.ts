import type { Browser, Locator, Page } from '@playwright/test';
import { persona, personaKeys, personaName, readFoundationState, type FoundationPersona } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { expectSettledPageAccessible, gotoReady, loginAs, waitForSettled } from './lib/session';
import { shot } from './lib/shots';
import { addWeeklyTime, chooseEveryWeek, jobsPage, saveRegisteredJob, staffInPlace } from './lib/jobs';

const area = 'place';

test.use({ viewport: { width: 1280, height: 800 } });

async function capture(page: Page, name: string): Promise<void> {
  await page.mouse.move(0, 0);
  await expectSettledPageAccessible(page);
  await shot(page, area, name, [page.locator('.state-entity-dialog-footer__notice')]);
}

async function takeStep(page: Page, dialog: Locator, key: string): Promise<void> {
  const button = dialog.locator(`button[data-transition="${key}"]`);
  await button.focus();
  await expect(button).toBeFocused();
  await page.keyboard.press('Enter');
}

const pill = (dialog: Locator) => dialog.locator('.state-pill__button');

async function signedIn(browser: Browser, who: FoundationPersona): Promise<Page> {
  const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  const page = await context.newPage();
  await loginAs(page, who);
  return page;
}

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

test('a coordinator registers a job with its place and the assigned staff member finds the address and access notes', { tag: '@staffing' }, async ({ page, browser }) => {
  test.setTimeout(300_000);
  const state = readFoundationState();
  const staff = personaName(persona(personaKeys.staff, state));
  const { customer } = state.staffing;
  const suffix = `${Date.now().toString(36)}${Math.floor(Math.random() * 1000)}`;
  const jobName = `Office cleaning ${suffix}`;
  const street = `Main Street ${suffix}`;
  const notes = `Door code ${suffix}, the key box by the gate.`;
  let registered = false;
  let staffPage: Page | undefined;

  try {
    await loginAs(page, persona(personaKeys.coordinator, state));
    await gotoReady(page, jobsPage);
    await page.getByRole('button', { name: 'New job', exact: true }).click();
    const fresh = page.getByRole('dialog', { name: 'New job', exact: true });
    await fresh.locator('[name="job-name"]').fill(jobName);
    await fresh.locator('.rz-dropdown').filter({ has: page.locator('[name="job-customer"]') }).click();
    await page.getByRole('option', { name: customer, exact: true }).click();
    await chooseEveryWeek(fresh);
    await waitForSettled(page);
    await takeStep(page, fresh, 'job-register');
    const job = page.getByRole('dialog', { name: jobName, exact: true });
    await expect(pill(job)).toHaveAttribute('data-state', 'Registration');
    registered = true;

    await saveRegisteredJob(job, async () => {
      await job.getByRole('tab', { name: 'Place' }).click();
      await job.locator('[name="job-place-name"]').fill('Hillview School');
      await job.locator('[name="job-street"]').fill(street);
      await job.locator('[name="job-postal-code"]').fill('111 22');
      await job.locator('[name="job-city"]').fill('Stockholm');
      await job.locator('[name="job-access-notes"]').fill(notes);
      await capture(page, '01-place-view');
      await addWeeklyTime(job);
    });

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

    await job.locator('tr.list-row').filter({ hasText: 'Cleaner' }).locator('td').first().click();
    const opened = page.getByRole('dialog', { name: 'Cleaner', exact: true });
    await opened.getByRole('tab', { name: 'Staffing' }).click();
    await waitForSettled(page);
    await staffInPlace(opened, staff, 'assign');
    await opened.getByRole('button', { name: 'Close', exact: true }).first().click();
    await job.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    staffPage = await signedIn(browser, persona(personaKeys.staff, state));
    await gotoReady(staffPage, '/me/assignments');
    const card = staffPage.locator('.list-row').filter({ hasText: jobName });
    await expect(card).toContainText('Hillview School');
    await expect(card).not.toContainText(notes);
    await capture(staffPage, '02-my-assignments-card');
    await card.locator('.list-card__date').click();
    const assignment = staffPage.getByRole('dialog').last();
    await expect(pill(assignment)).toHaveAttribute('data-state', 'Assigned');
    await expect(assignment.locator('[data-place]')).toHaveText(`Hillview School, ${street}, 111 22 Stockholm`);
    await expect(assignment.locator('[data-access-notes]')).toHaveText(notes);
    await capture(staffPage, '03-assignment-where');
  } finally {
    if (staffPage) await staffPage.context().close();
    if (registered) await cancelJob(page, jobName);
  }
});
