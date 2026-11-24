import type { Page } from '@playwright/test';
import { buttonContrastViolations } from './lib/accessibility';
import { persona, personaKeys, personaName, readFoundationState } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { expectSettledPageAccessible, gotoReady, impersonate, loginAs, waitForReady, waitForSettled } from './lib/session';
import { shot } from './lib/shots';
import { occurrencesOf, pageScroll, priorityItems, searchPriorities, timeline, timelineReady, toStaffOn } from './lib/workspace';
import { cancelJob, candidateRow, runSuffix, staffInPlace, startedJob } from './lib/jobs';

function occurrenceDay(): Date {
  const day = new Date();
  day.setDate(day.getDate() + 2);
  return day;
}

function isoDay(day: Date): string {
  return `${day.getFullYear()}-${String(day.getMonth() + 1).padStart(2, '0')}-${String(day.getDate()).padStart(2, '0')}`;
}

async function capture(page: Page, name: string, width: number): Promise<void> {
  await page.mouse.move(0, 0);
  await expectSettledPageAccessible(page);
  expect(await buttonContrastViolations(page)).toEqual([]);
  await shot(page, 'coordination', `${name}-${width}`, [page.locator('.state-entity-dialog-footer__notice')]);
}

const openSlot = (page: Page, day: string, jobName: string) => toStaffOn(page, day).filter({ hasText: `Lead, ${jobName}` });

async function openSlotDialog(page: Page, day: string, jobName: string): Promise<void> {
  await expect(openSlot(page, day, jobName)).toHaveCount(1);
  await openSlot(page, day, jobName).focus();
  await page.keyboard.press('Enter');
}

const priorityRow = (page: Page, jobName: string) => priorityItems(page, `${jobName}, `);

test(`a coordinator finds a slot in Priorities and opens it from the timeline, F5 keeps it, and a second coordinator's assignment shows live`, { tag: '@coordination' }, async ({ page, browser }) => {
  test.setTimeout(300_000);
  const width = 1280;
  const viewport = { width, height: 800 };
  await page.setViewportSize(viewport);
  const state = readFoundationState();
  const kim = personaName(persona(personaKeys.staff, state));
  const jobName = `Workspace algebra ${runSuffix()}`;
  const day = occurrenceDay();
  const shownDay = isoDay(day);

  await loginAs(page, persona(personaKeys.coordinator, state));
  await test.step('set-up: a started job of the run', () => startedJob(page, jobName, { day, start: '06:00', end: '06:45' }));
  try {
    await gotoReady(page, '/coordination');
    await expect(page.getByRole('heading', { name: 'Coordination', level: 1 })).toBeAttached();
    await timelineReady(page);
    await expect(page.locator('table.mini-month')).toHaveCount(3);
    await searchPriorities(page, jobName);
    await expect(priorityRow(page, jobName)).toHaveCount(1);
    await expect(priorityRow(page, jobName)).toHaveAttribute('data-priorities-kind', 'uncovered');
    await expect(priorityRow(page, jobName).locator('.priorities-pill')).toHaveText('Uncovered');
    await expect(page.locator('[data-priorities] [data-priorities-group], [data-priorities] .priorities-group')).toHaveCount(0);
    await expect(priorityRow(page, jobName).locator('button[data-priorities-action]')).toHaveCount(1);
    await expect(priorityRow(page, jobName).getByRole('button', { name: /^Open: / })).toBeVisible();
    await expect(priorityRow(page, jobName).getByRole('button', { name: /^(Request|Assign): / })).toHaveCount(0);
    await capture(page, '01-workspace', width);

    await priorityRow(page, jobName).getByRole('button', { name: /^Open: / }).click();
    const job = page.getByRole('dialog', { name: jobName, exact: true });
    await expect(job.locator('[data-job-staff] [data-staff-slot="Lead"] [data-open]')).toHaveCount(1);
    await expect(job).not.toContainText('Coordinator');
    await expect(job).not.toContainText('Register the job, then');
    await capture(page, '01a-job-from-priorities', width);
    await job.locator('button[data-find-staff="Lead"]').click();
    await expect(job.getByRole('tab', { name: 'Staffing' })).toHaveAttribute('aria-selected', 'true');
    await expect(candidateRow(job, kim).locator('button[data-staff-action="assign"]')).toBeVisible();
    await capture(page, '01b-job-staffing', width);
    await job.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    const marked = page.locator(`button.mini-month__day[data-day="${shownDay}"]`);
    if (await marked.count() === 0) await page.locator('button[data-calendar-move="later"]').click();
    await expect(marked.locator('[data-mark="open"]')).toHaveCount(1);
    await expect(marked).toHaveAttribute('aria-label', /\d+ open/);
    await marked.scrollIntoViewIfNeeded();
    await capture(page, '00-calendar-rail', width);

    await marked.click();
    await expect(marked).toHaveAttribute('aria-pressed', 'true');
    await expect(timeline(page)).toHaveAttribute('data-day', shownDay);
    await timelineReady(page);
    await expect(openSlot(page, shownDay, jobName)).toHaveCount(1);
    await expect(openSlot(page, shownDay, jobName)).toContainText('Uncovered');
    await expect(page).toHaveURL(new RegExp(`[?&]day=${shownDay}(&|$)`));
    await expect(page).not.toHaveURL(/[?&](week|mode|view)=/);
    await expect(page).toHaveURL(/[?&]prio\.q=/);

    await openSlotDialog(page, shownDay, jobName);
    const need = page.getByRole('dialog', { name: 'Lead', exact: true });
    await expect(need.getByRole('tab', { name: 'Staffing' })).toHaveAttribute('aria-selected', 'true');
    await expect(need.locator('[data-staffing-summary]')).toContainText(jobName);
    await expect(page).toHaveURL(/[?&]dlg=slot-[0-9a-f-]{36}/);
    await waitForSettled(page);
    await capture(page, '02-staffing-from-workspace', width);

    await page.reload();
    await waitForReady(page);
    const restored = page.getByRole('dialog', { name: 'Lead', exact: true });
    await expect(restored.getByRole('tab', { name: 'Staffing' })).toHaveAttribute('aria-selected', 'true');
    await expect(restored.locator('[data-staffing-summary]')).toContainText(jobName);
    await expect(page.locator(`button.mini-month__day[data-day="${shownDay}"]`)).toHaveAttribute('aria-pressed', 'true');
    await expect(timeline(page)).toHaveAttribute('data-day', shownDay);
    await expect(page.locator('input[data-priorities-search]')).toHaveValue(jobName);
    await waitForSettled(page);
    await capture(page, '03-after-reload', width);
    await restored.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(0);

    await timelineReady(page);
    await expect(openSlot(page, shownDay, jobName)).toHaveCount(1);
    await page.evaluate(() => { (window as unknown as { workspaceKept?: boolean }).workspaceKept = true; });
    const other = await browser.newContext({ viewport });
    try {
      const second = await other.newPage();
      await loginAs(second, persona(personaKeys.platformOwner, state));
      await impersonate(second, state.tenantAdministrator);
      await gotoReady(second, `/coordination?day=${shownDay}`);
      await timelineReady(second);
      await openSlotDialog(second, shownDay, jobName);
      const staffing = second.getByRole('dialog', { name: 'Lead', exact: true });
      await expect(staffing.getByRole('tab', { name: 'Staffing' })).toHaveAttribute('aria-selected', 'true');
      await staffInPlace(staffing, kim, 'assign');
      await expect(staffing.locator('[data-staffing-filled]')).toContainText(`Filled by ${kim}`);
    } finally {
      await other.close();
    }

    await expect(openSlot(page, shownDay, jobName)).toHaveCount(0, { timeout: 15_000 });
    await expect(occurrencesOf(page, kim, shownDay).filter({ hasText: jobName })).toHaveCount(1);
    await expect(priorityRow(page, jobName)).toHaveCount(0);
    expect(await page.evaluate(() => (window as unknown as { workspaceKept?: boolean }).workspaceKept), 'the page was not reloaded').toBe(true);
    await capture(page, '04-live-update', width);
  } finally {
    await cancelJob(page, jobName);
  }
});

test('the workspace fills the window without a page scroll, folds its Priorities dock into the address and keeps it folded after a reload', { tag: ['@coordination', '@smoke'] }, async ({ page }) => {
  test.setTimeout(240_000);
  const state = readFoundationState();
  await page.setViewportSize({ width: 1440, height: 900 });
  await loginAs(page, persona(personaKeys.coordinator, state));
  for (const [width, height] of [[1440, 900], [1280, 800]] as const) {
    await page.setViewportSize({ width, height });
    await gotoReady(page, '/coordination');
    await timelineReady(page);
    expect(await pageScroll(page), `no page scroll at ${width} px`).toEqual({ document: 0, body: 0 });
    const frame = await page.locator('[data-workspace]').boundingBox();
    expect(Math.round(frame!.y + frame!.height), 'the workspace reaches the window\'s foot').toBe(height);
    if (width === 1440) await capture(page, '05-workspace', 1440);
  }

  await page.setViewportSize({ width: 1440, height: 900 });
  await gotoReady(page, '/coordination');
  await timelineReady(page);
  await page.locator('button[data-priorities-fold]').click();
  await expect(page).toHaveURL(/[?&]dock=closed(&|$)/);
  await page.reload();
  await waitForReady(page);
  await timelineReady(page);
  await expect(page.locator('[data-priorities]')).toHaveAttribute('data-dock', 'closed');
  await expect(page.locator('button[data-priorities-fold]')).toHaveAttribute('aria-expanded', 'false');
  expect(await pageScroll(page)).toEqual({ document: 0, body: 0 });
  await capture(page, '06-dock-folded', 1440);

  await page.locator('button[data-priorities-fold]').focus();
  await page.keyboard.press('Enter');
  await expect(page.locator('[data-priorities]')).toHaveAttribute('data-dock', 'open');
  await expect(page).not.toHaveURL(/[?&]dock=/);

  await page.setViewportSize({ width: 390, height: 844 });
  await gotoReady(page, '/coordination');
  await timelineReady(page);
  const body = await page.locator('.timeline-body').boundingBox();
  const dock = await page.locator('[data-priorities]').boundingBox();
  const rail = await page.locator('.timeline-rail').boundingBox();
  expect(body!.y).toBeLessThan(dock!.y);
  expect(dock!.y).toBeLessThan(rail!.y);
  expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0);
  await capture(page, '07-stacked', 390);
});

for (const [width, height] of [[1440, 900], [390, 844]] as const) {
  test(`the day from 00 to 24 scrolls sideways inside the timeline, never the page, with today's red now line in view (${width})`, { tag: '@coordination' }, async ({ page }) => {
    test.setTimeout(120_000);
    const state = readFoundationState();
    await page.setViewportSize({ width, height });
    await loginAs(page, persona(personaKeys.coordinator, state));
    await gotoReady(page, '/coordination');
    await timelineReady(page);

    const marks = page.locator('.timeline-hours__tick');
    await expect(marks).toHaveCount(25);
    await expect(marks.first()).toHaveAttribute('data-hour', '00');
    await expect(marks.last()).toHaveAttribute('data-hour', '24');
    const scroller = page.locator('.timeline-scroll');
    const room = await scroller.evaluate((element) => element.scrollWidth - element.clientWidth);
    expect(room, 'the day is wider than the timeline').toBeGreaterThan(0);
    if (width === 1440) expect(await pageScroll(page)).toEqual({ document: 0, body: 0 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth), 'the page never scrolls sideways').toBeLessThanOrEqual(0);

    const line = page.locator('[data-now-line]');
    await expect(line).toHaveCount(1);
    const said = await line.getAttribute('data-now-line');
    expect(said).toMatch(/^\d{2}:\d{2}$/);
    await expect(page.locator('[data-now-time]')).toHaveText(said!);
    await expect.poll(async () => {
      const [area, mark] = await Promise.all([scroller.boundingBox(), page.locator('.timeline-hours__now').boundingBox()]);
      return area !== null && mark !== null && mark.x > area.x && mark.x < area.x + area.width;
    }, { message: 'the now line is in view as today opens' }).toBe(true);
    expect(await line.evaluate((element) => getComputedStyle(element).backgroundColor)).toBe(await page.evaluate(() => {
      const probe = document.createElement('span');
      probe.style.color = 'var(--color-danger-dot)';
      document.body.append(probe);
      const colour = getComputedStyle(probe).color;
      probe.remove();
      return colour;
    }));
    await capture(page, '11-now-line', width);

    const name = page.locator('.timeline-row[data-staff] .timeline-name').first();
    const open = page.locator('.timeline-name--staff');
    const [nameBefore, openBefore] = await Promise.all([name.boundingBox(), open.boundingBox()]);
    await scroller.evaluate((element) => element.scrollTo({ left: element.scrollWidth }));
    await expect.poll(() => scroller.evaluate((element) => Math.round(element.scrollLeft + element.clientWidth - element.scrollWidth))).toBeGreaterThanOrEqual(-1);
    const end = await page.locator('.timeline-hours__tick--end .timeline-hours__label').boundingBox();
    const area = await scroller.boundingBox();
    expect(end!.x + end!.width, 'the 24 label is in view at the end').toBeLessThanOrEqual(area!.x + area!.width + 1);
    expect((await name.boundingBox())!.x).toBeCloseTo(nameBefore!.x, 0);
    expect((await open.boundingBox())!.x).toBeCloseTo(openBefore!.x, 0);
    await capture(page, '12-day-end', width);
  });
}
