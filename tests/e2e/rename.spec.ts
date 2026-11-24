import type { Page } from '@playwright/test';
import { persona, personaKeys, readFoundationState } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { expectSettledPageAccessible, gotoReady, loginAs } from './lib/session';
import { shot } from './lib/shots';
import { timelineReady } from './lib/workspace';

const oldWords = /\b(teachers?|courses?|lessons?|terms?)\b/i;

async function visibleWords(page: Page): Promise<string> {
  return page.locator('body').innerText();
}

async function capture(page: Page, name: string): Promise<void> {
  await page.mouse.move(0, 0);
  await expectSettledPageAccessible(page);
  expect(await visibleWords(page)).not.toMatch(oldWords);
  await shot(page, 'rename', name);
}

test('the workspace, the jobs list and a job dialog speak of jobs, slots and staff', { tag: '@shell' }, async ({ page }) => {
  test.setTimeout(180_000);
  await page.setViewportSize({ width: 1500, height: 900 });
  await loginAs(page, persona(personaKeys.coordinator, readFoundationState()));

  await gotoReady(page, '/coordination');
  await timelineReady(page);
  await expect(page.getByRole('navigation', { name: 'Main navigation' })).toContainText('Jobs');
  await capture(page, 'workspace-1500');

  await gotoReady(page, '/staffing/jobs');
  await expect(page.getByRole('button', { name: 'New job', exact: true })).toBeVisible();
  await expect(page.getByRole('columnheader', { name: /Period/ }).first()).toBeVisible();
  await capture(page, 'jobs-list-1500');

  await page.locator('tr.list-row').first().click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByRole('tab', { name: /Slots/ })).toBeVisible();
  await expect(dialog.getByText('Period starts', { exact: true }).first()).toBeVisible();
  await capture(page, 'job-dialog-1500');
  await dialog.getByRole('tab', { name: /Slots/ }).click();
  await expect(dialog.getByRole('tab', { name: /Slots/ })).toHaveAttribute('aria-selected', 'true');
  await capture(page, 'job-dialog-slots-1500');
});

test('the portal speaks of orders', { tag: ['@shell', '@portal'] }, async ({ page }) => {
  test.setTimeout(120_000);
  await page.setViewportSize({ width: 1500, height: 900 });
  await loginAs(page, persona(personaKeys.customerAdmin, readFoundationState()));

  await gotoReady(page, '/portal');
  await expect(page.getByRole('link', { name: /My orders/ }).first()).toBeVisible();
  await capture(page, 'portal-home-1500');

  await gotoReady(page, '/portal/orders');
  await expect(page.getByRole('button', { name: 'New order', exact: true })).toBeVisible();
  await capture(page, 'portal-orders-1500');
});
