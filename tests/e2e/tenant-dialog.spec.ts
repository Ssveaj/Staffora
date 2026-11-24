import type { Locator, Page } from '@playwright/test';
import { persona, personaKeys, readFoundationState } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { openTenantConsole, tenantList } from './lib/platform-console';
import { editDialog, enterSecret, expectSettledPageAccessible, gotoReady, loginAs, waitForReady } from './lib/session';
import { countRadzenDialogTimers, pastRadzenDialogTimers } from './lib/frames';
import { shot } from './lib/shots';

const headingOf = (dialog: Locator) => dialog.locator('.state-entity-dialog-header__title');

async function waitForHeadingFocus(page: Page, title: string): Promise<void> {
  await page.waitForFunction(
    (expected) => {
      const active = document.activeElement;
      return !!active && active.classList.contains('state-entity-dialog-header__title') && active.textContent?.trim() === expected;
    },
    title,
    { polling: 'raf' },
  );
}

async function expectNamedByHeading(page: Page, title: string): Promise<Locator> {
  const dialog = page.getByRole('dialog', { name: title, exact: true });
  await expect(dialog, `a dialog is named "${title}"`).toBeVisible();
  const heading = headingOf(dialog);
  await expect(heading).toHaveText(title);
  const id = await heading.getAttribute('id');
  expect(id, 'the heading has an id').toBeTruthy();
  await expect(dialog, 'the dialog is labelled by its heading').toHaveAttribute('aria-labelledby', id!);
  await expect(dialog, 'the dialog carries no fixed label beside it').not.toHaveAttribute('aria-label', /.*/);
  return dialog;
}

async function expectFocusInside(dialog: Locator, why: string): Promise<void> {
  await expect.poll(() => dialog.evaluate((element) => document.activeElement !== document.body && element.contains(document.activeElement)), { message: why }).toBe(true);
}

const stepButton = (dialog: Locator, key: string) => dialog.locator(`.state-entity-dialog-footer button[data-transition="${key}"]`);

async function step(page: Page, dialog: Locator, key: string, label: string, state: string, confirm = false): Promise<void> {
  await stepButton(dialog, key).click();
  if (confirm) {
    const question = page.locator('.form-dialog').filter({ has: page.locator('.dialog-notice__message') });
    await expect(question).toBeVisible();
    await question.locator('.dialog-notice__buttons button').filter({ hasText: label }).click();
    await expect(question).toBeHidden();
  }

  await expect(dialog.locator('.state-pill__button')).toHaveAttribute('data-state', state);
  await expect(dialog.locator('.state-entity-dialog-footer__bar')).toHaveAttribute('aria-busy', 'false');
  await expect(dialog.locator('.state-entity-dialog-footer__status')).toContainText(`${label}: done.`);
}

async function pastRadzensFirstFocus(page: Page): Promise<void> {
  await pastRadzenDialogTimers(page);
}

test.describe('the tenant dialog on the platform console', { tag: ['@platform', '@dialogs'] }, () => {
  test.beforeEach(async ({ page }) => {
    await countRadzenDialogTimers(page);
    await loginAs(page, persona(personaKeys.platformOwner, readFoundationState()));
  });

  const width = 1280;
  test(`A tenant onboarded from New Tenant with its first system owner moves through every state with the focus in the dialog and back on New Tenant at close`, async ({ page, browser }) => {
    test.setTimeout(150_000);
    await page.setViewportSize({ width, height: 800 });
    await gotoReady(page, tenantList);
    const newTenant = page.getByRole('button', { name: 'New Tenant', exact: true });
    await expect(newTenant).toHaveCount(1);

    await newTenant.focus();
    await page.keyboard.press('Enter');
    const form = page.getByRole('dialog', { name: 'New tenant', exact: true });
    await expect(form.getByLabel('Tenant name')).toBeVisible();
    await expectFocusInside(form, 'the form takes the focus as it opens');
    const create = form.getByRole('button', { name: 'Create tenant', exact: true });
    await expect(create).toBeDisabled();
    const name = `Northfield Staff ${width} ${Date.now()}`;
    const ownerEmail = `owner.${width}.${Date.now()}@northfield.example.com`;
    await form.getByLabel('Tenant name').fill(name);
    await form.getByLabel('Description').fill('Places staff members on jobs in the north');
    await form.getByLabel('First name').fill('Robin');
    await form.getByLabel('Last name').fill('Hale');
    await form.getByLabel('Email').fill(ownerEmail);
    await form.getByLabel('Email').press('Tab');
    await expect(create).toBeEnabled();
    await expectSettledPageAccessible(page);
    await shot(page, 'tenant', `01-onboarding-${width}`);

    await create.click();
    const password = (await form.locator('.tenant-onboarding__password code').textContent())?.trim() ?? '';
    expect(password, 'the form shows the initial password').toMatch(/^\S{16}$/);
    await expect(form).toContainText(ownerEmail);
    expect(page.url()).not.toContain(password);
    await expectSettledPageAccessible(page);
    await shot(page, 'tenant', `02-onboarding-password-${width}`, [form.locator('.tenant-onboarding__password')]);

    await page.keyboard.press('Escape');
    const question = page.getByRole('dialog', { name: 'Close without the password?', exact: true });
    await expect(question).toBeVisible();
    await expectSettledPageAccessible(page);
    await shot(page, 'tenant', `02b-onboarding-close-question-${width}`, [form.locator('.tenant-onboarding__password')]);
    await question.getByRole('button', { name: 'Keep it open', exact: true }).click();
    await expect(question).toHaveCount(0);
    await expect(form.locator('.tenant-onboarding__password code')).toHaveText(password);

    await form.locator('div[role=checkbox][aria-label="I have copied the password"]').click();
    await expect(form.locator('div[role=checkbox][aria-label="I have copied the password"]')).toHaveAttribute('aria-checked', 'true');
    await form.getByRole('button', { name: 'Done', exact: true }).click();
    await waitForHeadingFocus(page, name);
    let dialog = await expectNamedByHeading(page, name);
    await expect(dialog.locator('.state-pill__button')).toHaveAttribute('data-state', 'Setup');
    await expect(page.locator('.tenant-onboarding__password')).toHaveCount(0);
    await expect(page).toHaveURL(/[?&]dlg=tenant-[0-9a-f-]{36}/);
    await expectSettledPageAccessible(page);
    await shot(page, 'tenant', `03-setup-${width}`);

    const ownerContext = await browser.newContext({ viewport: { width, height: 800 } });
    try {
      const owner = await ownerContext.newPage();
      await gotoReady(owner, '/Account/Login');
      await owner.getByLabel('Email').fill(ownerEmail);
      await enterSecret(owner.getByLabel('Password'), password);
      await owner.getByRole('button', { name: 'Sign in', exact: true }).click();
      await expect(owner).not.toHaveURL(/\/Account\/Login/);
      await waitForReady(owner);
      await expect(owner.locator('[data-session="ready"]')).toHaveCount(1);
    } finally {
      await ownerContext.close();
    }

    await step(page, dialog, 'tenant-activate', 'Activate', 'Active');
    await expectFocusInside(dialog, 'the focus stays inside the dialog after Activate');
    await expectSettledPageAccessible(page);
    await shot(page, 'tenant', `04-active-${width}`);

    await stepButton(dialog, 'tenant-suspend').click();
    const confirmation = page.locator('.form-dialog').filter({ has: page.locator('.dialog-notice__message') });
    await expect(confirmation).toBeVisible();
    await expectFocusInside(confirmation, 'the question takes the focus as it opens');
    await confirmation.getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(confirmation).toBeHidden();
    await pastRadzensFirstFocus(page);
    await expect(stepButton(dialog, 'tenant-suspend'), 'Cancel gives the focus back to the step\'s button').toBeFocused();
    await expect(dialog.locator('.state-pill__button')).toHaveAttribute('data-state', 'Active');

    await step(page, dialog, 'tenant-suspend', 'Suspend', 'Suspended', true);
    await expectFocusInside(dialog, 'the focus stays inside the dialog after Suspend');
    await pastRadzensFirstFocus(page);
    await expectFocusInside(dialog, 'the focus is still inside the dialog once Radzen\'s first focus would have run');
    await expect(dialog.getByLabel('Name')).toHaveAttribute('readonly', '');
    await expectSettledPageAccessible(page);
    await shot(page, 'tenant', `05-suspended-${width}`);

    await step(page, dialog, 'tenant-schedule-deletion-suspended', 'Schedule deletion', 'PendingDeletion', true);
    await expectFocusInside(dialog, 'the focus stays inside the dialog after Schedule deletion');
    await expectSettledPageAccessible(page);
    await shot(page, 'tenant', `06-pending-deletion-${width}`);

    await step(page, dialog, 'tenant-archive', 'Archive', 'Archived', true);
    await expectFocusInside(dialog, 'the focus stays inside the dialog after Archive');
    await expect(dialog.locator('.state-entity-dialog-footer button[data-transition]')).toHaveCount(0);
    await expectSettledPageAccessible(page);
    await shot(page, 'tenant', `07-archived-${width}`);

    const pill = dialog.locator('.state-pill__button');
    const card = dialog.locator('.state-pill__card');
    await page.mouse.move(0, 0);
    await pill.focus();
    await page.keyboard.press('Enter');
    await expect(card).toBeVisible();
    await expect(card.locator('[data-history-step]')).toHaveCount(3);
    await expect(card.locator('[data-history-step]').first()).toContainText('Archive');
    await expect(card.locator('[data-history-step]').first()).toContainText('(platform owner)');
    await expect(card).not.toContainText('Someone outside this organization');
    await expectSettledPageAccessible(page);
    await shot(page, 'tenant', `08-history-card-${width}`);
    await dialog.getByRole('button', { name: 'Full history', exact: true }).click();
    await expect(page.locator('[data-state-history] li').first()).toBeVisible();

    await expect(page.locator('.state-history__summary')).toContainText("times in Europe/Stockholm, the zone of this record's organization");
    await expect(page.locator('[data-history-zone]')).toHaveText('Europe/Stockholm');
    await expectSettledPageAccessible(page);
    await shot(page, 'tenant', `09-history-${width}`);
    await shot(page, 'jobs', `09-console-history-zone-${width}`);
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(1);
    await expect(pill).toBeFocused();

    await headingOf(dialog).focus();
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await expect(newTenant, 'the focus is back on the opener').toBeFocused();
    await page.getByRole('searchbox', { name: 'Search' }).fill(name);
    await page.keyboard.press('Enter');
    await expect(page.locator('tr.list-row').filter({ has: page.getByRole('link', { name, exact: true }) })).toContainText('Archived');
    await expect(page).not.toHaveURL(/[?&]dlg=/);
  });

  test('The tenant\'s dialog opened from its console gives the focus back to "Tenant details" when it closes, by Esc and by its close button', async ({ page }) => {
    const tenant = readFoundationState().tenantAdministrator.tenant;
    await openTenantConsole(page, tenant);
    const details = page.getByRole('button', { name: 'Tenant details', exact: true });

    await details.focus();
    await page.keyboard.press('Enter');
    await waitForHeadingFocus(page, tenant);
    await expectNamedByHeading(page, tenant);
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await expect(details, 'Esc gives the focus back to "Tenant details"').toBeFocused();

    await page.locator('body').click({ position: { x: 1, y: 1 } });
    await details.click();
    await waitForHeadingFocus(page, tenant);
    const dialog = await expectNamedByHeading(page, tenant);
    await dialog.getByRole('button', { name: 'Close', exact: true }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await expect(details, 'the close button gives the focus back to "Tenant details"').toBeFocused();
  });

  for (const width of [1280, 390]) {
    test(`Closing the tenant's dialog with edits asks before it discards them, keeps the focus inside and gives it back to the opener at close (${width} px)`, async ({ page }) => {
      await page.setViewportSize({ width, height: width === 390 ? 844 : 800 });
      const tenant = readFoundationState().tenantAdministrator.tenant;
      await openTenantConsole(page, tenant);
      const details = page.getByRole('button', { name: 'Tenant details', exact: true });
      await details.click();
      await waitForHeadingFocus(page, tenant);
      const dialog = await expectNamedByHeading(page, tenant);

      await editDialog(dialog);
      await dialog.getByLabel('Description').fill('Not kept by closing');
      await headingOf(dialog).focus();
      await page.keyboard.press('Escape');
      const question = page.locator('.form-dialog').filter({ hasText: 'Discard changes?' });
      await expect(question.locator('.dialog-notice__message')).toHaveText('You have changes that are not saved. Close the dialog and lose them?');
      await expectSettledPageAccessible(page);
      await shot(page, 'tenant', `11-leave-question-${width}`);

      await question.getByRole('button', { name: 'Keep editing', exact: true }).click();
      await expect(question).toBeHidden();
      await pastRadzensFirstFocus(page);
      await expectFocusInside(dialog, 'Keep editing leaves the focus inside the dialog');
      await expect(dialog.getByLabel('Description')).toHaveValue('Not kept by closing');

      await headingOf(dialog).focus();
      await page.keyboard.press('Escape');
      await question.getByRole('button', { name: 'Discard changes', exact: true }).click();
      await expect(page.getByRole('dialog')).toHaveCount(0);
      await expect(details, 'the focus is back on "Tenant details"').toBeFocused();
      await details.click();
      await waitForHeadingFocus(page, tenant);
      await expect(dialog.getByLabel('Description')).not.toHaveValue('Not kept by closing');
      await shot(page, 'tenant', `12-reopened-unchanged-${width}`);
      await page.keyboard.press('Escape');
      await expect(page.getByRole('dialog')).toHaveCount(0);
    });
  }
});