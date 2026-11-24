import type { Locator, Page } from '@playwright/test';
import { persona, personaKeys, readFoundationState } from './lib/foundation-state';
import { journeyStep, loadJourney } from './lib/journey-contract';
import { expect, test } from './lib/page-health';
import { editDialog, expectSettledPageAccessible, gotoReady, impersonate, loginAs, waitForReady, waitForSettled } from './lib/session';
import { shot } from './lib/shots';

const journey = loadJourney('platform-owner-sets-up-custom-fields');

const companyName = 'Company A';

const row = (page: Page, name: string): Locator => page.locator('tr.list-row').filter({ has: page.locator('td[data-label="Name"]', { hasText: name }) });

const dropDown = (scope: Locator, label: string): Locator => scope.locator('.rz-dropdown').filter({ has: scope.page().locator(`input[aria-label="${label}"]`) });

async function choose(scope: Locator, label: string, option: string): Promise<void> {
  const field = dropDown(scope, label);
  await field.click();
  await scope.page().getByRole('option', { name: option, exact: true }).click();
  await expect(field.locator('.rz-dropdown-label')).toHaveText(option);
}

async function expectNoSidewaysScroll(page: Page, name: string, width: number): Promise<void> {
  const overflowing = await page.evaluate(() =>
    [document.documentElement, ...Array.from(document.querySelectorAll<HTMLElement>('.app-shell .app-shell__body'))]
      .filter((element) => element.scrollWidth > element.clientWidth)
      .map((element) => `${element.tagName.toLowerCase()}.${element.className}: ${element.scrollWidth}px wide in ${element.clientWidth}px`),
  );
  expect(overflowing, `${name} scrolls sideways at ${width} px`).toEqual([]);
}

async function shootBothWidths(page: Page, name: string, options: { dialog?: boolean } = {}): Promise<void> {
  for (const viewport of [{ width: 1280, height: 800 }, { width: 390, height: 844 }]) {
    await page.setViewportSize(viewport);
    await waitForSettled(page);
    await expectNoSidewaysScroll(page, name, viewport.width);
    if (options.dialog) {
      const box = await page.getByRole('dialog').boundingBox();
      expect(box && box.x >= 0 && box.x + box.width <= viewport.width, `the dialog of ${name} fits the ${viewport.width} px window`).toBeTruthy();
      await shot(page, 'custom-fields', `${name}-${viewport.width}`);
    } else {
      const style = await page.addStyleTag({
        content: '.app-shell { height: auto !important; min-height: 100dvh; overflow: visible !important; } .app-shell .app-shell__body { overflow: visible !important; }',
      });
      try {
        await shot(page, 'custom-fields', `${name}-${viewport.width}`);
      } finally {
        await style.evaluate((element) => element.remove());
      }
    }
  }
  await page.setViewportSize({ width: 1280, height: 800 });
  await waitForSettled(page);
}

async function closeDialog(page: Page): Promise<void> {
  const dialog = page.getByRole('dialog');
  await page.keyboard.press('Escape');
  const closed = await dialog.waitFor({ state: 'hidden', timeout: 1500 }).then(() => true, () => false);
  if (!closed) await dialog.getByRole('button', { name: /^(Close|Cancel)$/ }).last().click();
  await expect(dialog).toBeHidden();
}

test.use({ viewport: { width: 1280, height: 800 } });

test('the platform owner sets up a company\'s custom fields, which its administrator fills in', { tag: ['@admin', '@platform'] }, async ({ page }) => {
  test.setTimeout(180_000);
  const state = readFoundationState();
  const sam = state.impersonationTarget;
  const alex = state.tenantAdministrator;
  const field = 'Shirt size';

  await journeyStep(journey, 'create-choice-field', async () => {
    await loginAs(page, persona(personaKeys.platformOwner, state));
    await gotoReady(page, '/superadmin/settings');

    await page.getByRole('button', { name: 'New Field' }).click();
    const chooser = page.locator('[data-form-dialog="ChooseCustomFieldTargetDialog"]');
    await expect(chooser).toBeVisible();
    await expect(dropDown(chooser, 'Applies to').locator('.rz-dropdown-label')).toHaveText('Users');
    await chooser.getByRole('button', { name: 'Continue' }).click();
    await expect(chooser).toHaveCount(0);

    const editor = page.getByRole('dialog', { name: 'New custom field of users' });
    await expect(editor).toBeVisible();
    const create = editor.locator('.state-entity-dialog-footer button[data-transition="field-create"]');
    await expect(create).toHaveCount(0);
    await choose(editor, 'Tenant', companyName);
    await editor.locator('[name="custom-field-name"]').fill(field);
    await expect(create).toHaveCount(1);
    await choose(editor, 'Type', 'Choice');

    await expect(editor).toContainText('Add the options to choose from.');
    await expect(create).toHaveCount(0);
    for (const [index, option] of ['Small', 'Medium', 'Large'].entries()) {
      await editor.getByRole('button', { name: 'Add option' }).click();
      await editor.locator(`[name="custom-field-option-${index + 1}"]`).fill(option);
    }
    await expect(editor.locator('textarea')).toHaveCount(0);
    await expectSettledPageAccessible(page);
    await shootBothWidths(page, 'editor-choice', { dialog: true });

    await create.click();
    const saved = page.getByRole('dialog', { name: field });
    await expect(saved.locator('.state-pill__button')).toHaveAttribute('data-state', 'Draft');
    await expect(saved.locator('[name="custom-field-tenant"]')).toHaveValue(companyName);
    await expect(saved.locator('[name="custom-field-tenant"]')).not.toBeEditable();
    await expect(saved.locator('.state-choice-options__text')).toHaveText(['Small', 'Medium', 'Large']);
    await expect(saved.locator('button[data-dialog-action="edit"]')).toBeVisible();
    await closeDialog(page);

    const companyGroup = page.locator('tbody.list-section').filter({ has: page.locator('.list-heading__label', { hasText: companyName }) });
    const created = companyGroup.locator('tr.list-row').filter({ has: page.locator('td[data-label="Name"]', { hasText: field }) });
    await expect(created).toHaveCount(1);
    await expect(created.locator('td[data-label="Type"]')).toContainText('Choice');
    await expect(created).toContainText('Draft');
  });

  await journeyStep(journey, 'activate-choice-field', async () => {
    const created = row(page, field);
    await created.click();
    const dialog = page.getByRole('dialog', { name: field });
    await expect(dialog.locator('.state-pill__button')).toHaveAttribute('data-state', 'Draft');
    await dialog.locator('button[data-transition="field-activate"]').click();
    await expect(dialog.locator('.state-pill__button')).toHaveAttribute('data-state', 'Active');
    await expect(dialog.locator('button[data-transition="field-deactivate"]')).toBeVisible();
    await expect(dialog.getByRole('button', { name: 'Delete' })).toHaveCount(0);
    await expectSettledPageAccessible(page);
    await closeDialog(page);
    await expect(created).toContainText('Active');
  });

  await journeyStep(journey, 'impersonate-administrator', async () => {
    await impersonate(page, alex);
  });

  await journeyStep(journey, 'see-custom-fields', async () => {
    await gotoReady(page, '/tenant/management/settings/custom-fields');
    for (const name of [field, 'Team', 'Badge number', 'Has keys', 'Opened', 'Rooms', 'Floor area', 'Employee number']) {
      await expect(row(page, name), `the panel lists ${name}`).toHaveCount(1);
    }
    await expect(row(page, 'Employee number').locator('td[data-label="Scope"]')).toContainText('Global');
    await expect(row(page, 'Team').locator('td[data-label="Scope"]')).toContainText('Own');
    await expect(page.getByRole('button', { name: /^New field$/i })).toHaveCount(0);
    await expect(page.locator('table.list-table thead th', { hasText: 'Applies to' })).toHaveCount(0);
    await shootBothWidths(page, 'panel');

    for (const name of [field, 'Employee number']) {
      await row(page, name).click();
      const view = page.getByRole('dialog', { name });
      await expect(view).toBeVisible();
      await expect(view.locator('[name="custom-field-name"]')).not.toBeEditable();
      await expect(view.locator('.state-entity-dialog-footer button[data-transition]')).toHaveCount(0);
      await expect(view).toContainText('The platform sets up custom fields');
      await expectSettledPageAccessible(page);
      await closeDialog(page);
    }
  });

  await journeyStep(journey, 'set-choice-on-user', async () => {
    await gotoReady(page, '/tenant/management/users');
    await row(page, sam.name).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.locator('[name="user-first-name"]')).toHaveValue(sam.name.split(' ')[0]);
    for (const label of [field, 'Team', 'Badge number', 'Has keys', 'Employee number']) {
      await expect(dialog.locator('label', { hasText: label }).first(), `the user dialog shows ${label}`).toBeVisible();
    }
    const customFields = dialog.locator('.custom-fields-fieldset');
    await expect(customFields.locator('label', { hasText: field })).toBeVisible();
    await expect(customFields.locator('label', { hasText: 'Employee number' })).toBeVisible();
    await expect(dialog.getByRole('group', { name: /^(Custom|Global) fields$/ })).toHaveCount(0);
    await expect(customFields.locator('[name="user-phone"]')).toBeVisible();
    await editDialog(dialog);
    await choose(dialog, field, 'Medium');
    await expectSettledPageAccessible(page);
    await shootBothWidths(page, 'user-dialog', { dialog: true });
    await dialog.locator('button[data-transition="user-save"]').click();
    await expect(dialog.locator('.state-entity-dialog-footer__status')).toHaveText('Save: done.');
    await closeDialog(page);
  });

  await journeyStep(journey, 'reload-keeps-choice', async () => {
    await page.reload();
    await waitForReady(page);
    await expectSettledPageAccessible(page);
    await row(page, sam.name).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.locator('[name="user-first-name"]')).toHaveValue(sam.name.split(' ')[0]);
    await expect(dialog.getByRole('textbox', { name: field }), 'the choice saved before F5 shows after it').toHaveValue('Medium');
    await closeDialog(page);
  });

  await journeyStep(journey, 'see-unit-fields', async () => {
    await gotoReady(page, '/tenant/management/organization-units?view=list');
    await row(page, 'Head office').click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.locator('[name="unit-name"]')).toHaveValue('Head office');
    for (const label of ['Opened', 'Rooms', 'Floor area']) {
      await expect(dialog.locator('label', { hasText: label }).first(), `the unit dialog shows ${label}`).toBeVisible();
    }
    await expect(dialog.locator('.custom-fields-fieldset').locator('label', { hasText: 'Floor area' })).toBeVisible();
    await expectSettledPageAccessible(page);
    await shootBothWidths(page, 'unit-dialog', { dialog: true });
    await closeDialog(page);
  });
});
