import type { Browser, Page } from '@playwright/test';
import { persona, personaKeys, personaName, readFoundationState } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { expectSettledPageAccessible, gotoReady, impersonate, loginAs, waitForSettled } from './lib/session';
import { shot } from './lib/shots';
import { cancelJob, candidateRow, isoDay, runSuffix, startedJob } from './lib/jobs';

const generalSettings = '/tenant/management/settings';

const fixedHolidays: Record<string, string> = {
  '01-01': "New Year's Day",
  '01-06': 'Epiphany',
  '05-01': 'May Day',
  '06-06': 'National Day',
  '12-24': 'Christmas Eve',
  '12-25': 'Christmas Day',
  '12-26': 'Boxing Day',
  '12-31': "New Year's Eve",
};

function weekdayHolidayAhead(): { day: Date; name: string } {
  const day = new Date();
  day.setDate(day.getDate() + 3);
  for (let tries = 0; tries < 400; tries++) {
    const name = fixedHolidays[isoDay(day).slice(5)];
    if (name && day.getDay() !== 0 && day.getDay() !== 6) return { day, name };
    day.setDate(day.getDate() + 1);
  }
  throw new Error('No fixed public holiday on a weekday within a year.');
}

async function bothWidths(page: Page, name: string, settle: () => Promise<void> = async () => {}): Promise<void> {
  for (const viewport of [{ width: 1280, height: 800 }, { width: 390, height: 844 }]) {
    await page.setViewportSize(viewport);
    await settle();
    await page.mouse.move(0, 0);
    await expectSettledPageAccessible(page);
    await shot(page, 'public-holidays', `${name}-${viewport.width}`);
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

async function setClosedOnHolidays(page: Page, closed: boolean, shots: boolean): Promise<void> {
  await gotoReady(page, generalSettings);
  const section = page.locator('[data-setting-public-holidays]');
  await expect(section.getByRole('heading', { name: 'Public holidays', exact: true })).toBeVisible();
  await expect(section.locator('.rz-dropdown')).toContainText('Sweden');
  const toggle = section.locator('[data-closed-on-holidays] .rz-switch');
  const isOn = ((await toggle.getAttribute('class')) ?? '').includes('rz-switch-checked');
  if (isOn === closed) return;
  await toggle.click();
  if (closed) await expect(toggle).toHaveClass(/rz-switch-checked/);
  else await expect(toggle).not.toHaveClass(/rz-switch-checked/);
  await section.locator('[data-save-public-holidays]').click();
  await expect(page.locator('.general-settings__status')).toHaveText(
    closed ? 'Saved: the public holidays of Sweden are marked and block staffing.' : 'Saved: the public holidays of Sweden are marked, and staffing goes on.',
  );
  if (!shots) return;
  await gotoReady(page, generalSettings);
  await expect(section.locator('[data-closed-on-holidays] .rz-switch')).toHaveClass(/rz-switch-checked/);
  await bothWidths(page, '01-general-settings-closed-on-holidays');
}

test('a tenant administrator closes the company on public holidays and a coordinator finds a candidate blocked on a holiday', { tag: ['@staffing', '@admin'] }, async ({ page, browser }) => {
  test.setTimeout(300_000);
  const state = readFoundationState();
  const staffMember = personaName(persona(personaKeys.staff, state));
  const { day, name } = weekdayHolidayAhead();
  const jobName = `Holiday cleaning ${runSuffix()}`;

  const administrator = await asAdministrator(browser);
  try {
    await test.step('the administrator turns on closed on public holidays', () => setClosedOnHolidays(administrator, true, true));

    await page.setViewportSize({ width: 1280, height: 800 });
    await loginAs(page, persona(personaKeys.coordinator, state));
    const job = await test.step('set-up: a started job of the run on the holiday', () => startedJob(page, jobName, { day, start: '06:00', end: '06:30', slots: ['Lead'], keepOpen: true }));
    await job.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    try {
      await test.step('the staff member is blocked on the holiday in the Staffing view', async () => {
        await gotoReady(page, `/staffing/jobs?q=${encodeURIComponent(jobName)}`);
        await page.locator('tr.list-row').filter({ hasText: jobName }).click();
        const jobDialog = page.getByRole('dialog', { name: jobName, exact: true });
        await jobDialog.getByRole('tab', { name: 'Slots' }).click();
        await jobDialog.locator('tr.list-row').filter({ hasText: 'Lead' }).locator('td').first().click();
        const slot = page.getByRole('dialog', { name: 'Lead', exact: true });
        await slot.getByRole('tab', { name: 'Staffing' }).click();
        await expect(slot.locator('[data-staffing-summary]')).toContainText(jobName);
        await waitForSettled(page);
        const candidate = candidateRow(slot, staffMember);
        await expect(candidate).toHaveClass(/slot-staffing__candidate--blocked/);
        await expect(candidate.locator('button[data-staff-action="assign"]')).toBeDisabled();
        const verdict = candidate.locator('button[data-verdict][data-blocked="true"]');
        await verdict.click();
        await expect(candidate.locator('[data-block]').filter({ hasText: `Closed for ${name}` })).toContainText(isoDay(day));
        await bothWidths(page, '02-candidate-blocked-on-a-holiday');
      });

      await test.step('the workspace names the holiday on the rail, in the day title and on the staff row', async () => {
        await gotoReady(page, `/coordination?day=${isoDay(day)}`);
        await expect(page.locator('[data-holiday-note]')).toHaveAttribute('data-holiday-note', `Closed for ${name}`);
        const railDay = page.locator(`.calendar-rail button.mini-month__day[data-day="${isoDay(day)}"]`);
        await expect(railDay).toHaveClass(/mini-month__day--holiday/);
        await expect(railDay).toHaveAttribute('aria-label', new RegExp(`Closed for ${name}`));
        const band = page.locator(`.timeline-row[data-staff="${staffMember}"] [data-closed][data-day="${isoDay(day)}"]`);
        await expect(band).toHaveAttribute('data-closed', `Closed for ${name}`);
        await bothWidths(page, '03-workspace-holiday', async () => {
          await waitForSettled(page);
        });
      });
    } finally {
      await cancelJob(page, jobName);
    }
  } finally {
    await setClosedOnHolidays(administrator, false, false);
    await administrator.context().close();
  }
});
