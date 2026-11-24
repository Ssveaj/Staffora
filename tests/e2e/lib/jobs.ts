import type { Locator, Page } from '@playwright/test';
import { readFoundationState } from './foundation-state';
import { expect } from './page-health';
import { editDialog, gotoReady, waitForSettled } from './session';

export const jobsPage = '/staffing/jobs';

export const weekdays = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];

export function workdayAhead(ahead = 2): Date {
  const day = new Date();
  day.setDate(day.getDate() + ahead);
  while (day.getDay() === 0 || day.getDay() === 6) day.setDate(day.getDate() + 1);
  return day;
}

export function isoDay(day: Date): string {
  const pad = (value: number) => value.toString().padStart(2, '0');
  return `${day.getFullYear()}-${pad(day.getMonth() + 1)}-${pad(day.getDate())}`;
}

export async function chooseEveryWeek(dialog: Locator): Promise<void> {
  const page = dialog.page();
  const info = dialog.getByRole('tab', { name: 'Info' });
  if ((await info.count()) > 0) await info.click();
  await dialog.locator('.rz-dropdown').filter({ has: page.locator('[name="job-schedule"], [name="order-schedule"]') }).click();
  await page.getByRole('option', { name: 'Every week', exact: true }).click();
  await expect(page.getByRole('option', { name: 'Every week', exact: true })).toBeHidden();
  await expect(dialog.locator('[name="job-period-start"], [name="order-period-start"]')).toBeVisible();
}

export async function fillSingleTimes(dialog: Locator, day: Date, start: string, end: string): Promise<void> {
  const dayField = dialog.locator('input[name="job-day"]');
  await dayField.fill(isoDay(day));
  await dayField.press('Tab');
  await expect(dayField).toHaveValue(isoDay(day));
  const startField = dialog.locator('[name="job-start-time"]');
  await startField.fill(start);
  await startField.press('Tab');
  const endField = dialog.locator('[name="job-end-time"]');
  await endField.fill(end);
  await endField.press('Tab');
  await expect(endField).toHaveValue(end);
}

export async function addWeeklyTime(dialog: Locator, day: Date = workdayAhead(), start = '19:00', end = '20:00'): Promise<void> {
  await chooseEveryWeek(dialog);
  const weekday = day.getDay();
  await dialog.getByRole('tab', { name: 'Timetable' }).click();
  await dialog.locator(`[data-add-day="${weekday}"]`).click();
  await dialog.getByLabel(`Start, ${weekdays[weekday]}, time 1`, { exact: true }).fill(start);
  await dialog.getByLabel(`End, ${weekdays[weekday]}, time 1`, { exact: true }).fill(end);
  await expect(dialog.getByLabel(`End, ${weekdays[weekday]}, time 1`, { exact: true })).toHaveValue(end);
  const info = dialog.getByRole('tab', { name: 'Info' });
  if ((await info.count()) > 0) await info.click();
}

export const candidateRow = (dialog: Locator, name: string): Locator => dialog.locator(`[data-slot-staffing] [data-candidate="${name}"]`);

export async function staffInPlace(dialog: Locator, name: string, action: 'request' | 'assign'): Promise<void> {
  const row = candidateRow(dialog, name);
  await expect(row).toBeVisible();
  await row.locator(`button[data-staff-action="${action}"]`).click();
  await expect(row).toHaveCount(0);
}

export async function saveRegisteredJob(dialog: Locator, fill: () => Promise<void>): Promise<void> {
  await editDialog(dialog);
  await fill();
  await waitForSettled(dialog.page());
  await dialog.locator('button[data-transition="job-save-registration"]').focus();
  await dialog.page().keyboard.press('Enter');
  await expect(dialog.locator('.state-entity-dialog-footer button[data-dialog-action="edit"]')).toBeVisible();
}

export const pill = (dialog: Locator): Locator => dialog.locator('.state-pill__button');

export async function takeStep(dialog: Locator, key: string): Promise<void> {
  const button = dialog.locator(`button[data-transition="${key}"]`);
  await expect(button).toBeVisible();
  await expect(button).not.toHaveAttribute('aria-disabled', 'true');
  await button.focus();
  await expect(button).toBeFocused();
  await button.press('Enter');
}

export const runSuffix = (): string => `${Date.now().toString(36)}${Math.floor(Math.random() * 1000)}`;

export interface SlotOfTheRun {
  name: string;
  count?: number;
  qualification?: string;
}

export interface JobOfTheRun {
  customer?: string;
  day?: Date;
  start?: string;
  end?: string;
  slots?: (string | SlotOfTheRun)[];
  fields?: Record<string, string>;
  keepOpen?: boolean;
}

export async function addSlot(job: Locator, slot: string | SlotOfTheRun): Promise<void> {
  const page = job.page();
  const { name, count, qualification } = typeof slot === 'string' ? { name: slot } as SlotOfTheRun : slot;
  await waitForSettled(page);
  await job.getByRole('button', { name: 'Add slot', exact: true }).focus();
  await page.keyboard.press('Enter');
  const fresh = page.getByRole('dialog', { name: 'New slot', exact: true });
  await fresh.locator('[name="slot-name"]').fill(name);
  if (count) {
    await fresh.locator('input[name="slot-count"]').fill(String(count));
    await fresh.locator('input[name="slot-count"]').press('Tab');
  }
  if (qualification) {
    await fresh.locator('.rz-dropdown').filter({ has: page.locator('[name="slot-qualification"]') }).click();
    await page.getByRole('option', { name: qualification, exact: true }).click();
    await expect(page.getByRole('option', { name: qualification, exact: true })).toBeHidden();
    await expect(fresh.locator(`[data-qualification="${qualification}"]`)).toBeVisible();
  }
  await waitForSettled(page);
  await takeStep(fresh, 'slot-add');
  const saved = page.getByRole('dialog', { name, exact: true });
  await expect(pill(saved)).toHaveAttribute('data-state', 'Active');
  await saved.getByRole('button', { name: 'Close', exact: true }).first().click();
  await expect(page.getByRole('dialog')).toHaveCount(1);
  await waitForSettled(page);
}

export async function startedJob(page: Page, name: string, options: JobOfTheRun = {}): Promise<Locator> {
  const customer = options.customer ?? readFoundationState().staffing.customer;
  const day = options.day ?? workdayAhead();
  const weekday = day.getDay();
  await gotoReady(page, jobsPage);
  await page.getByRole('button', { name: 'New job', exact: true }).click();
  const fresh = page.getByRole('dialog', { name: 'New job', exact: true });
  await chooseEveryWeek(fresh);
  await fresh.locator('[name="job-name"]').fill(name);
  await fresh.locator('.rz-dropdown').filter({ has: page.locator('[name="job-customer"]') }).click();
  await page.getByRole('option', { name: customer, exact: true }).click();
  for (const [label, value] of Object.entries(options.fields ?? {})) await fresh.getByLabel(label, { exact: true }).fill(value);
  await waitForSettled(page);
  await takeStep(fresh, 'job-register');
  const job = page.getByRole('dialog', { name, exact: true });
  await expect(pill(job)).toHaveAttribute('data-state', 'Registration');
  await saveRegisteredJob(job, async () => {
    await job.getByRole('tab', { name: 'Timetable' }).click();
    await job.locator(`[data-add-day="${weekday === 0 ? 7 : weekday}"]`).click();
    if (options.start) await job.getByLabel(`Start, ${weekdays[weekday]}, time 1`, { exact: true }).fill(options.start);
    if (options.end) {
      const end = job.getByLabel(`End, ${weekdays[weekday]}, time 1`, { exact: true });
      await end.fill(options.end);
      await expect(end).toHaveValue(options.end);
    }
  });
  await job.getByRole('tab', { name: 'Slots' }).click();
  for (const slot of options.slots ?? ['Lead']) await addSlot(job, slot);
  await takeStep(job, 'job-start-from-registration');
  await expect(pill(job)).toHaveAttribute('data-state', 'Coordinating');
  if (!options.keepOpen) {
    await job.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
  }
  return job;
}

export async function openJob(page: Page, name: string): Promise<Locator | null> {
  await gotoReady(page, `${jobsPage}?q=${encodeURIComponent(name)}`);
  const row = page.locator('tr.list-row').filter({ hasText: name });
  if ((await row.count()) === 0) return null;
  await row.click();
  const job = page.getByRole('dialog', { name, exact: true });
  await expect(pill(job)).toBeVisible();
  return job;
}

const cancelSteps: Record<string, string> = {
  Registration: 'job-cancel-registration',
  AwaitingCoordination: 'job-cancel-awaiting',
  Coordinating: 'job-cancel-coordinating',
  Confirmed: 'job-cancel-confirmed',
  InProgress: 'job-cancel-in-progress',
};

export async function cancelJob(page: Page, name: string): Promise<void> {
  const job = await openJob(page, name);
  if (!job) return;
  const step = cancelSteps[(await pill(job).getAttribute('data-state')) ?? ''];
  if (step) {
    await takeStep(job, step);
    const prompt = page.locator('.form-dialog').filter({ has: page.getByRole('button', { name: 'Cancel job', exact: true }) });
    await prompt.getByLabel('The reason').fill('The run is over.');
    await prompt.getByRole('button', { name: 'Cancel job', exact: true }).click();
    await expect(prompt).toHaveCount(0);
    await expect(pill(job)).toHaveAttribute('data-state', 'CanceledBySupplier');
  }
  await job.getByRole('button', { name: 'Close', exact: true }).first().click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
}
