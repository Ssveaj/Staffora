import type { Page } from '@playwright/test';
import { buttonContrastViolations } from './lib/accessibility';
import { persona, personaKeys, readFoundationState } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { expectSettledPageAccessible, gotoReady, loginAs } from './lib/session';
import { shot } from './lib/shots';
import { staffRow, timelineReady } from './lib/workspace';

const heights: Record<number, number> = { 1440: 900, 1280: 800, 390: 844 };

function isoDay(day: Date): string {
  return `${day.getFullYear()}-${String(day.getMonth() + 1).padStart(2, '0')}-${String(day.getDate()).padStart(2, '0')}`;
}

function workday(day: Date): Date {
  const next = new Date(day);
  while (next.getDay() === 0 || next.getDay() === 6) next.setDate(next.getDate() + 1);
  return next;
}

function showcaseDays(): { today: Date; tomorrow: Date; isReal: boolean } {
  const real = new Date();
  const today = workday(real);
  const after = new Date(today);
  after.setDate(after.getDate() + 1);
  return { today, tomorrow: workday(after), isReal: today.getDay() === real.getDay() };
}

async function capture(page: Page, name: string, width: number, keepPointer = false): Promise<void> {
  if (!keepPointer) await page.mouse.move(0, 0);
  await expectSettledPageAccessible(page);
  expect(await buttonContrastViolations(page)).toEqual([]);
  await shot(page, 'coordination', `showcase-${name}-${width}`);
}

async function open(page: Page, path: string): Promise<void> {
  await gotoReady(page, path);
  await timelineReady(page);
}

for (const width of [1440, 1280, 390]) {
  test(`the busy week day by day, a hover card, the folded dock and a schedule (${width})`, { tag: ['@coordination', '@visual'] }, async ({ page }) => {
    test.setTimeout(300_000);
    await page.setViewportSize({ width, height: heights[width] });
    await loginAs(page, persona(personaKeys.coordinator, readFoundationState()));
    const { today, tomorrow, isReal } = showcaseDays();

    await open(page, isReal ? '/coordination' : `/coordination?day=${isoDay(today)}`);
    await expect(page.locator('.timeline-row--staff')).toBeVisible();
    await expect(staffRow(page, 'Ada Holm')).toBeVisible();
    await capture(page, 'day', width);

    await open(page, `/coordination?day=${isoDay(tomorrow)}`);
    await capture(page, 'day-tomorrow', width);

    await open(page, `/coordination?day=${isoDay(tomorrow)}`);
    const slotBar = page.locator('button.timeline-bar--slot').first();
    await slotBar.scrollIntoViewIfNeeded();
    await slotBar.hover();
    await expect(slotBar.locator('.timeline-card')).toBeVisible();
    await capture(page, 'hover-card', width, true);

    await open(page, isReal ? '/coordination?dock=closed' : `/coordination?dock=closed&day=${isoDay(today)}`);
    await expect(page.locator('[data-priorities]')).toHaveAttribute('data-dock', 'closed');
    await capture(page, 'dock-folded', width);

    await open(page, isReal ? '/coordination' : `/coordination?day=${isoDay(today)}`);
    await staffRow(page, 'Ada Holm').locator('button[data-open-schedule]').click();
    await expect(page.getByRole('dialog', { name: 'Schedule of Ada Holm', exact: true }).locator('[data-person-schedule]')).toBeVisible();
    await capture(page, 'person-schedule', width);
    await page.getByRole('dialog').getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
  });
}
