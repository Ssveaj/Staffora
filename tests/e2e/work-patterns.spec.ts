import type { Locator, Page } from '@playwright/test';
import { persona, personaKeys, readFoundationState } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { expectSettledPageAccessible, gotoReady, impersonate, loginAs } from './lib/session';
import { shot } from './lib/shots';

const width = 1280;

const patternsPage = '/staffing/work-patterns';

async function capture(page: Page, name: string, width: number): Promise<void> {
  await expectSettledPageAccessible(page);
  await shot(page, 'work-patterns', `${name}-${width}`, [page.locator('.state-entity-dialog-footer__notice')]);
}

async function confirmStep(page: Page, dialog: Locator, key: string, label: string): Promise<void> {
  await dialog.locator(`button[data-transition="${key}"]`).focus();
  await page.keyboard.press('Enter');
  const confirm = page.locator('.form-dialog').filter({ has: page.getByRole('button', { name: label, exact: true }) });
  await confirm.getByRole('button', { name: label, exact: true }).click();
  await expect(confirm).toBeHidden();
}

async function newDraft(page: Page, name: string, days: number[]): Promise<Locator> {
  await page.getByRole('button', { name: 'New pattern', exact: true }).click();
  const draft = page.getByRole('dialog', { name: 'New work pattern', exact: true });
  await draft.locator('[name="pattern-name"]').fill(name);
  for (const day of days) await draft.locator(`[data-add-day="${day}"]`).click();
  return page.getByRole('dialog').last();
}

test(`an administrator publishes a weekly pattern, copies it to a new draft, and a deleted draft leaves the list`, { tag: '@staffing' }, async ({ page }) => {
  test.setTimeout(150_000);
  await page.setViewportSize({ width, height: 800 });
  const state = readFoundationState();
  const suffix = `${Date.now().toString(36)}${Math.floor(Math.random() * 1000)}`;
  const fullTime = `Full time ${suffix}`;
  const discarded = `Discarded ${suffix}`;
  await loginAs(page, persona(personaKeys.platformOwner, state));
  await impersonate(page, state.tenantAdministrator);
  await gotoReady(page, patternsPage);

  let dialog = await newDraft(page, fullTime, [0, 1, 2, 3, 4]);
  const fridayEnd = dialog.getByLabel('End, Friday, segment 1', { exact: true });
  await fridayEnd.fill('12:00');
  await fridayEnd.press('Tab');
  await dialog.locator('[data-add-day="0"]').click();
  const secondStart = dialog.getByLabel('Start, Monday, segment 2', { exact: true });
  await secondStart.fill('15:00');
  await secondStart.press('Tab');
  await expect(dialog.locator('[data-problems]')).toContainText('Monday: 08:00-16:00 and 15:00-17:00 overlap.');
  await capture(page, '02-overlap-listed', width);
  await dialog.getByRole('button', { name: 'Remove 15:00–17:00 on Monday', exact: true }).click();
  await expect(dialog.locator('[data-problems]')).toHaveCount(0);
  await capture(page, '03-new-weekly-pattern', width);

  await dialog.locator('button[data-transition="pattern-save"]').click();
  dialog = page.getByRole('dialog', { name: fullTime, exact: true });
  await expect(dialog.locator('.state-pill__button')).toHaveAttribute('data-state', 'Draft');
  await confirmStep(page, dialog, 'pattern-publish', 'Publish');
  await expect(dialog.locator('.state-pill__button')).toHaveAttribute('data-state', 'Published');
  await expect(dialog.locator('.state-pattern-segments')).toHaveAttribute('data-access', 'read-only');
  await expect(dialog.locator('.state-pattern-segments__day[data-day="4"] [data-hours]')).toHaveText('08:00–12:00');
  await expect(dialog.locator('button[data-transition="pattern-delete"]')).toHaveCount(0);
  await capture(page, '04-published-pattern', width);

  await dialog.getByRole('button', { name: 'Copy to new draft', exact: true }).click();
  const copy = page.getByRole('dialog', { name: `${fullTime} (copy)`, exact: true });
  await expect(copy.getByLabel('End, Friday, segment 1', { exact: true })).toHaveValue('12:00');
  await capture(page, '05-copy-to-new-draft', width);
  await copy.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(copy).toBeHidden();
  await dialog.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  const published = page.locator('tr.list-row').filter({ hasText: fullTime });
  await expect(published).toContainText('Published');
  await expect(published).toContainText('36 h per week');
  await expect(page.locator('tr.list-row').filter({ hasText: `${fullTime} (copy)` })).toHaveCount(0);
  await capture(page, '01-patterns-list', width);

  dialog = await newDraft(page, discarded, [0, 2]);
  await dialog.locator('button[data-transition="pattern-save"]').click();
  dialog = page.getByRole('dialog', { name: discarded, exact: true });
  await expect(dialog.locator('.state-pill__button')).toHaveAttribute('data-state', 'Draft');
  await confirmStep(page, dialog, 'pattern-delete', 'Delete');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(page.locator('tr.list-row').filter({ hasText: discarded })).toHaveCount(0);
  await capture(page, '06-draft-deleted', width);
});
