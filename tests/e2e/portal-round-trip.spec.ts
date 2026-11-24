import type { Browser, Locator, Page } from '@playwright/test';
import { buttonContrastViolations } from './lib/accessibility';
import { persona, personaKeys, personaName, readFoundationState, type FoundationPersona } from './lib/foundation-state';
import { journeyStep, loadJourney } from './lib/journey-contract';
import { expect, test } from './lib/page-health';
import { editDialog, expectSettledPageAccessible, gotoReady, loginAs, waitForReady, waitForSettled } from './lib/session';
import { shot } from './lib/shots';
import { candidateRow, chooseEveryWeek, staffInPlace } from './lib/jobs';

const journey = loadJourney('portal-round-trip');
const area = 'portal-round-trip';
const weekdays = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];

const cancelSteps: Record<string, string> = {
  Registration: 'job-cancel-registration',
  AwaitingCoordination: 'job-cancel-awaiting',
  Coordinating: 'job-cancel-coordinating',
  Confirmed: 'job-cancel-confirmed',
  InProgress: 'job-cancel-in-progress',
};

test.use({ viewport: { width: 1280, height: 800 } });

function occurrenceDay(): number {
  const day = new Date();
  day.setDate(day.getDate() + 2);
  while (day.getDay() === 0 || day.getDay() === 6) day.setDate(day.getDate() + 1);
  return day.getDay();
}

async function capture(page: Page, name: string): Promise<void> {
  await page.mouse.move(0, 0);
  await expectSettledPageAccessible(page);
  expect(await buttonContrastViolations(page)).toEqual([]);
  const style = await page.addStyleTag({
    content: '.app-shell { height: auto !important; min-height: 100dvh; overflow: visible !important; } .app-shell .app-shell__body { overflow: visible !important; }',
  });
  try {
    await shot(page, area, name);
  } finally {
    await style.evaluate((element) => element.remove());
  }
}

async function takeStep(page: Page, dialog: Locator, key: string): Promise<void> {
  const button = dialog.locator(`button[data-transition="${key}"]`);
  await button.focus();
  await expect(button).toBeFocused();
  await page.keyboard.press('Enter');
}

async function confirmStep(page: Page, dialog: Locator, key: string, label: string): Promise<void> {
  await takeStep(page, dialog, key);
  const confirm = page.locator('.form-dialog').filter({ has: page.getByRole('button', { name: label, exact: true }) });
  await confirm.getByRole('button', { name: label, exact: true }).click();
  await expect(confirm).toHaveCount(0);
}

const pill = (dialog: Locator) => dialog.locator('.state-pill__button');

async function signedIn(browser: Browser, who: FoundationPersona): Promise<Page> {
  const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  const page = await context.newPage();
  await loginAs(page, who);
  return page;
}

async function writeRequest(page: Page, name: string, service?: string, people?: number): Promise<Locator> {
  await gotoReady(page, '/portal/orders/new');
  const fresh = page.getByRole('dialog', { name: 'New order', exact: true });
  await chooseEveryWeek(fresh);
  await fresh.locator('[name="order-name"]').fill(name);
  await fresh.locator('[name="order-description"]').fill('A staff member for the spring period.');
  await fresh.locator('[name="order-description"]').press('Tab');
  await takeStep(page, fresh, 'order-save-draft');
  const dialog = page.getByRole('dialog', { name: name, exact: true });
  await expect(pill(dialog)).toHaveAttribute('data-state', 'Registration');
  if (service && people) {
    await editDialog(dialog);
    await dialog.locator('.rz-dropdown').filter({ has: page.locator('[name="order-line-service"]') }).click();
    await page.getByRole('option', { name: service, exact: true }).click();
    const line = dialog.locator(`[data-line="${service}"]`);
    await expect(line).toHaveAttribute('data-count', String(people));
    await takeStep(page, dialog, 'order-save');
    await expect(pill(dialog)).toHaveAttribute('data-state', 'Registration');
    await expect(dialog.locator(`[data-line="${service}"]`)).toBeVisible();
  }
  return dialog;
}

async function send(page: Page, dialog: Locator): Promise<void> {
  await confirmStep(page, dialog, 'order-send', 'Send');
  await expect(pill(dialog)).toHaveAttribute('data-state', 'Sent');
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(page.locator('[data-order-details="ready"]')).toBeVisible();
}

async function expectRequestState(page: Page, state: string): Promise<void> {
  await expect(async () => {
    await page.reload();
    await waitForReady(page);
    await expect(page.locator('[data-order-state]')).toHaveAttribute('data-order-state', state, { timeout: 2_000 });
  }).toPass({ timeout: 90_000, intervals: [1_000, 2_000, 3_000] });
}

async function openJob(page: Page, name: string): Promise<Locator | null> {
  await gotoReady(page, `/staffing/jobs?q=${encodeURIComponent(name)}`);
  const row = page.locator('tr.list-row').filter({ hasText: name });
  if ((await row.count()) === 0) return null;
  await row.click();
  const dialog = page.getByRole('dialog', { name: name, exact: true });
  await expect(pill(dialog)).toBeVisible();
  return dialog;
}

async function cancelJob(page: Page, name: string): Promise<void> {
  const dialog = await openJob(page, name);
  if (!dialog) return;
  const step = cancelSteps[(await pill(dialog).getAttribute('data-state')) ?? ''];
  if (step) {
    await takeStep(page, dialog, step);
    const prompt = page.locator('.form-dialog').filter({ has: page.getByRole('button', { name: 'Cancel job', exact: true }) });
    await prompt.getByLabel('The reason').fill('The run is over.');
    await prompt.getByRole('button', { name: 'Cancel job', exact: true }).click();
    await expect(prompt).toHaveCount(0);
    await expect(pill(dialog)).toHaveAttribute('data-state', 'CanceledBySupplier');
  }
  await page.keyboard.press('Escape');
}

test('an orderer\'s order is staffed and confirmed through the portal, shows its staff member, and a second order is canceled from the portal', { tag: '@portal' }, async ({ page, browser }) => {
  test.setTimeout(480_000);
  const state = readFoundationState();
  const orderer = persona(personaKeys.orderer, state);
  const staffMember = personaName(persona(personaKeys.secondStaff, state));
  const supplier = state.tenantAdministrator.tenant;
  const { customer, service } = state.staffing;
  const run = `${Date.now().toString(36)}${Math.floor(Math.random() * 1000)}`;
  const staffed = `Round trip geometry ${run}`;
  const canceled = `Round trip biology ${run}`;
  const weekday = occurrenceDay();
  let coordinator: Page | undefined;
  const sent: string[] = [];

  try {
    await journeyStep(journey, 'send-order', async () => {
      await loginAs(page, orderer);
      await expect(page).toHaveURL(/\/portal$/);
      await expect(page.locator('[data-portal-supplier]')).toContainText(supplier);

      const dialog = await writeRequest(page, staffed, service, 2);
      await expect(dialog.locator('[data-order-orderer]')).toHaveText(personaName(orderer));
      await capture(page, '01-order-written');
      sent.push(staffed);
      await send(page, dialog);
      await expect(page.locator('[data-order-state]')).toHaveAttribute('data-order-state', 'Sent');
      await expect(page.locator('[data-order-staff]')).toHaveCount(0);
      await capture(page, '02-order-sent');
    });

    await journeyStep(journey, 'staff-order', async () => {
      coordinator = await signedIn(browser, persona(personaKeys.coordinator, state));
      const jobDialog = await openJob(coordinator, staffed);
      expect(jobDialog, 'the staffing company has the job the order became').not.toBeNull();
      await expect(pill(jobDialog!)).toHaveAttribute('data-state', 'AwaitingCoordination');
      await expect(jobDialog!.locator('input[name="job-customer"]')).toHaveValue(customer);
      await expect(jobDialog!.locator('input[name="job-contact"]')).toHaveValue(personaName(orderer));
      await capture(coordinator, '03-job-received');

      await editDialog(jobDialog!);
      await jobDialog!.getByRole('tab', { name: 'Timetable' }).click();
      await jobDialog!.locator(`[data-add-day="${weekday}"]`).click();
      await jobDialog!.getByLabel(`Start, ${weekdays[weekday]}, time 1`, { exact: true }).fill('12:00');
      await jobDialog!.getByLabel(`End, ${weekdays[weekday]}, time 1`, { exact: true }).fill('12:45');
      await takeStep(coordinator, jobDialog!, 'job-save-awaiting');
      await waitForSettled(coordinator);
      await jobDialog!.getByRole('tab', { name: 'Slots' }).click();
      await waitForSettled(coordinator);
      await expect(jobDialog!.locator('tr.list-row').filter({ hasText: service })).toHaveCount(1);
      await expect(jobDialog!.locator('tr.list-row').filter({ hasText: service })).toContainText('0 of 2');
      await takeStep(coordinator, jobDialog!, 'job-start');
      await expect(pill(jobDialog!)).toHaveAttribute('data-state', 'Coordinating');

      await jobDialog!.getByRole('tab', { name: 'Slots' }).click();
      await jobDialog!.locator('tr.list-row').filter({ hasText: service }).first().locator('td').first().click();
      const staffing = coordinator.getByRole('dialog', { name: service, exact: true });
      await staffing.getByRole('tab', { name: 'Staffing' }).click();
      await waitForSettled(coordinator);
      await expect(candidateRow(staffing, staffMember).locator('[data-held]')).not.toHaveCount(0);
      await expect(candidateRow(staffing, staffMember).locator('[data-missing]')).toHaveCount(0);
      await staffInPlace(staffing, staffMember, 'assign');
      await staffing.getByRole('button', { name: 'Close', exact: true }).first().click();
      await expect(coordinator.getByRole('dialog')).toHaveCount(1);
      await waitForSettled(coordinator);
      await expect(jobDialog!.locator('tr.list-row').filter({ hasText: `${staffMember} (assigned)` })).toHaveCount(1);
      await expect(jobDialog!.locator('tr.list-row').filter({ hasText: service })).toContainText('1 of 2');
      await capture(coordinator, '04-staff-assigned');
    });

    await journeyStep(journey, 'confirm-job', async () => {
      const jobDialog = coordinator!.getByRole('dialog', { name: staffed, exact: true });
      await confirmStep(coordinator!, jobDialog, 'job-confirm', 'Confirm');
      await expect(pill(jobDialog)).toHaveAttribute('data-state', 'Confirmed');
      await capture(coordinator!, '05-job-confirmed');
      await coordinator!.keyboard.press('Escape');
      await expect(coordinator!.getByRole('dialog')).toHaveCount(0);
    });

    await journeyStep(journey, 'customer-sees-staff', async () => {
      await expectRequestState(page, 'Confirmed');
      await expect(page.locator('[data-order-staff]')).toHaveText(staffMember);
      await capture(page, '06-order-confirmed-with-staff');
    });

    await journeyStep(journey, 'cancel-second-order', async () => {
      const dialog = await writeRequest(page, canceled);
      sent.push(canceled);
      await send(page, dialog);
      await page.getByRole('button', { name: 'Open order', exact: true }).click();
      const request = page.getByRole('dialog', { name: canceled, exact: true });
      await confirmStep(page, request, 'order-cancel-sent', 'Cancel order');
      await expect(pill(request)).toHaveAttribute('data-state', 'CancellationSent');
      await capture(page, '07-second-order-cancellation-sent');
      await page.keyboard.press('Escape');
      await expect(page.getByRole('dialog')).toHaveCount(0);

      await expect(async () => {
        const jobDialog = await openJob(coordinator!, canceled);
        expect(jobDialog).not.toBeNull();
        await expect(pill(jobDialog!)).toHaveAttribute('data-state', 'CanceledByCustomer', { timeout: 2_000 });
      }).toPass({ timeout: 90_000, intervals: [1_000, 2_000, 3_000] });
      await capture(coordinator!, '08-job-canceled-by-the-customer');

      await expectRequestState(page, 'CanceledByUs');
      await coordinator!.keyboard.press('Escape');
      await coordinator!.locator('[data-notice-bell] .notice-bell__trigger').click();
      const panel = coordinator!.locator('[data-notice-panel]');
      await panel.getByRole('button', { name: 'All', exact: true }).click();
      await waitForSettled(coordinator!);
      await expect(panel.locator('.notice-row').filter({ hasText: `${customer} canceled ${canceled}` })).toHaveCount(1);
      await capture(coordinator!, '09-coordinator-told');
    });
  } finally {
    if (sent.length > 0) {
      const cleaner = coordinator ?? (await signedIn(browser, persona(personaKeys.coordinator, state)));
      const failures: unknown[] = [];
      for (const name of sent) await cancelJob(cleaner, name).catch((failure: unknown) => failures.push(failure));
      await cleaner.context().close();
      if (failures.length > 0) throw failures[0];
    }
  }
});
