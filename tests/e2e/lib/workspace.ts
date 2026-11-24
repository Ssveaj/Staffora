import type { Locator, Page } from '@playwright/test';
import { expect } from './page-health';
import { waitForSettled } from './session';

export const timeline = (page: Page): Locator => page.locator('.coordination-timeline');

export async function timelineReady(page: Page): Promise<void> {
  await expect(timeline(page)).toHaveAttribute('data-board-state', 'ready', { timeout: 15_000 });
  await waitForSettled(page);
}

export const staffRow = (page: Page, name: string): Locator => page.locator(`.timeline-row[data-staff="${name}"]`);

export const toStaffOn = (page: Page, day: string): Locator => page.locator(`.timeline-row--staff button.timeline-bar--open[data-day="${day}"]`);

export const occurrencesOf = (page: Page, name: string, day: string): Locator => staffRow(page, name).locator(`button.timeline-bar--slot[data-day="${day}"]`);

export async function showEveryStaff(page: Page): Promise<void> {
  await waitForSettled(page);
}

export const priorityItems = (page: Page, text: string): Locator => page.locator('[data-priorities] [data-priorities-item]').filter({ hasText: text });

export async function searchPriorities(page: Page, text: string): Promise<void> {
  const open = page.locator('button[data-priorities-search-open]');
  if (await open.isVisible()) await open.click();
  const box = page.locator('input[data-priorities-search]');
  await box.fill(text);
  await box.press('Enter');
}

export async function pageScroll(page: Page): Promise<{ document: number; body: number }> {
  return page.evaluate(() => {
    const body = document.querySelector<HTMLElement>('.app-shell__body');
    return {
      document: document.documentElement.scrollHeight - document.documentElement.clientHeight,
      body: body ? body.scrollHeight - body.clientHeight : 0,
    };
  });
}
