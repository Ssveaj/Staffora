import type { Locator, Page } from '@playwright/test';
import { persona, personaKeys, readFoundationState } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { editDialog, expectSettledPageAccessible, gotoReady, loginAs } from './lib/session';
import { shot } from './lib/shots';

const width = 1280;

async function capture(page: Page, name: string, width: number): Promise<void> {
  await page.mouse.move(0, 0);
  await expectSettledPageAccessible(page);
  await shot(page, 'customers', `${name}-${width}`, [page.locator('.state-entity-dialog-footer__notice')]);
}

async function expectStepCardInside(page: Page, dialog: Locator, key: string): Promise<void> {
  await dialog.locator(`button[data-transition="${key}"]`).focus();
  const card = dialog.locator('.transition-button').filter({ has: page.locator(`button[data-transition="${key}"]`) }).locator('.transition-button__card');
  await expect(card).toBeVisible();
  const box = await card.boundingBox();
  const frame = await dialog.boundingBox();
  const screen = page.viewportSize();
  expect(box && frame && screen).toBeTruthy();
  expect(box!.x).toBeGreaterThanOrEqual(Math.max(frame!.x, 0) - 0.5);
  expect(box!.x + box!.width).toBeLessThanOrEqual(Math.min(frame!.x + frame!.width, screen!.width) + 0.5);
}

async function confirmStep(page: Page, dialog: Locator, key: string, label: string): Promise<void> {
  await dialog.locator(`button[data-transition="${key}"]`).focus();
  await page.keyboard.press('Enter');
  const confirm = page.locator('.form-dialog').filter({ has: page.getByRole('button', { name: label, exact: true }) });
  await confirm.getByRole('button', { name: label, exact: true }).click();
  await expect(confirm).toBeHidden();
}

async function takeStep(page: Page, dialog: Locator, key: string): Promise<void> {
  await dialog.locator(`button[data-transition="${key}"]`).focus();
  await page.keyboard.press('Enter');
}

async function addContact(page: Page, customer: Locator, first: string, last: string, title: string, email: string): Promise<void> {
  await customer.getByRole('button', { name: 'Add contact', exact: true }).focus();
  await page.keyboard.press('Enter');
  const fresh = page.getByRole('dialog', { name: 'New contact', exact: true });
  await fresh.locator('[name="contact-first-name"]').fill(first);
  await fresh.locator('[name="contact-last-name"]').fill(last);
  await fresh.locator('[name="contact-title"]').fill(title);
  await fresh.locator('[name="contact-email"]').fill(email);
  await takeStep(page, fresh, 'contact-save-draft');
  const saved = page.getByRole('dialog', { name: `${first} ${last}`, exact: true });
  await expect(saved.locator('.state-pill__button')).toHaveAttribute('data-state', 'Create');
  await saved.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(page.getByRole('dialog')).toHaveCount(1);
  await expect(customer.locator('tr.list-row').filter({ hasText: `${first} ${last}` })).toHaveCount(1);
}

test(`a coordinator creates a customer with two contacts and deletes it with them`, { tag: '@staffing' }, async ({ page }) => {
  test.setTimeout(150_000);
  await page.setViewportSize({ width, height: 800 });
  const state = readFoundationState();
  const name = `Brightpath Learning ${Date.now().toString(36)}${Math.floor(Math.random() * 1000)}`;
  await loginAs(page, persona(personaKeys.coordinator, state));
  await gotoReady(page, '/staffing/customers');

  await page.getByRole('button', { name: 'New customer', exact: true }).click();
  const fresh = page.getByRole('dialog', { name: 'New customer', exact: true });
  await fresh.locator('[name="customer-name"]').fill(name);
  await fresh.locator('[name="customer-description"]').fill('Upper secondary school');
  await takeStep(page, fresh, 'customer-save-draft');
  const customer = page.getByRole('dialog', { name, exact: true });
  await expect(customer.locator('.state-pill__button')).toHaveAttribute('data-state', 'Create');
  await capture(page, '01-customer-draft', width);

  await expect(customer.locator('.state-entity-dialog-footer button[data-dialog-action="edit"]')).toBeVisible();
  await expectStepCardInside(page, customer, 'customer-activate');
  await editDialog(customer);
  await expectStepCardInside(page, customer, 'customer-save-draft');
  await capture(page, '01b-save-card', width);
  await customer.locator('.state-entity-dialog-footer button[data-dialog-action="cancel"]').click();
  await expect(customer.locator('.state-entity-dialog-footer button[data-dialog-action="edit"]')).toBeVisible();
  await page.setViewportSize({ width: 390, height: 844 });
  await expectStepCardInside(page, customer, 'customer-activate');
  await editDialog(customer);
  await expectStepCardInside(page, customer, 'customer-save-draft');
  await capture(page, '01b-save-card', 390);
  await customer.locator('.state-entity-dialog-footer button[data-dialog-action="cancel"]').click();
  await expect(customer.locator('.state-entity-dialog-footer button[data-dialog-action="edit"]')).toBeVisible();
  await page.setViewportSize({ width, height: 800 });

  await customer.getByRole('tab', { name: 'Contacts' }).click();
  await expect(customer.getByText('No contact yet')).toBeVisible();
  await addContact(page, customer, 'Ada', 'Moreno', 'Principal', 'ada.moreno@brightpath.example');
  await addContact(page, customer, 'Ben', 'Okafor', 'Job planner', 'ben.okafor@brightpath.example');
  await capture(page, '02-contacts-new', width);

  await takeStep(page, customer, 'customer-activate');
  await expect(customer.locator('.state-pill__button')).toHaveAttribute('data-state', 'Active');
  for (const contact of ['Ada Moreno', 'Ben Okafor']) {
    await customer.locator('tr.list-row').filter({ hasText: contact }).locator('td').first().click();
    const shown = page.getByRole('dialog', { name: contact, exact: true });
    await expect(shown.locator('[data-customer]')).toContainText(name);
    await takeStep(page, shown, 'contact-activate');
    await expect(shown.locator('.state-pill__button')).toHaveAttribute('data-state', 'Active');
    if (contact === 'Ada Moreno') await capture(page, '03-contact-active', width);
    await shown.getByRole('button', { name: 'Close', exact: true }).click();
    await expect(page.getByRole('dialog')).toHaveCount(1);
  }
  await expect(customer.locator('tr.list-row').filter({ hasText: /\bActive\b/ })).toHaveCount(2);
  await capture(page, '04-customer-active', width);
  await customer.getByRole('button', { name: 'Close', exact: true }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(page.locator('tr.list-row').filter({ hasText: name })).toContainText('2 (2 active)');
  await capture(page, '05-customers', width);

  await page.locator('tr.list-row').filter({ hasText: name }).click();
  await confirmStep(page, customer, 'customer-deactivate', 'Deactivate');
  await expect(customer.locator('.state-pill__button')).toHaveAttribute('data-state', 'Inactive');
  await expect(customer.locator('[name="customer-name"]')).toHaveAttribute('readonly', '');
  await customer.getByRole('tab', { name: 'Contacts' }).click();
  await expect(customer.locator('tr.list-row').filter({ hasText: /\bInactive\b/ })).toHaveCount(2);
  await expect(customer.getByRole('button', { name: 'Add contact', exact: true })).toHaveCount(0);
  await capture(page, '06-customer-inactive', width);
  await confirmStep(page, customer, 'customer-delete', 'Delete');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(page.locator('tr.list-row').filter({ hasText: name })).toHaveCount(0);
  await capture(page, '07-customer-deleted', width);
});
