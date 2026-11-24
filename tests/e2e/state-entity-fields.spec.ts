import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import type { Page } from '@playwright/test';
import { expectNoSeriousViolations } from './lib/accessibility';
import { repositoryRoot } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { shot } from './lib/shots';

const fixtureFolder = join(repositoryRoot, 'tests', 'e2e', 'fixtures', 'state-entity-dialog');
const fixtureAddress = '/e2e-fixtures/state-entity-fields';

function pageOf(body: string): string {
  return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <base href="/" />
  <title>State-aware fields</title>
  <link rel="stylesheet" href="lib/bootstrap/dist/css/bootstrap.min.css" />
  <link rel="stylesheet" href="_content/Radzen.Blazor/css/material-base.css" />
  <link rel="stylesheet" href="_content/Radzen.Blazor/css/material.css" />
  <link rel="stylesheet" href="app.tokens.css" />
  <link rel="stylesheet" href="app.typography.css" />
  <link rel="stylesheet" href="app.css" />
  <link rel="stylesheet" href="Staffora.Blazor.styles.css" />
</head>
<body>
${body}
</body>
</html>`;
}

async function openFixture(page: Page, name: string): Promise<void> {
  const body = readFileSync(join(fixtureFolder, name), 'utf8');
  await page.route(`**${fixtureAddress}`, (route) => route.fulfill({ status: 200, contentType: 'text/html; charset=utf-8', body: pageOf(body) }));
  await page.goto(fixtureAddress);
  await page.addStyleTag({ content: '*, *::before, *::after { animation: none !important; transition: none !important; }' });
  await page.evaluate(() => document.fonts.ready);
  const labels = await page.evaluate(() =>
    [...document.querySelectorAll('label[for]')].map((label) => ({
      for: label.getAttribute('for') ?? '',
      matches: document.querySelectorAll(`[id="${CSS.escape(label.getAttribute('for') ?? '')}"]`).length,
    })),
  );
  expect(labels.length).toBeGreaterThan(0);
  for (const label of labels) {
    expect(label.matches, `label for="${label.for}"`).toBe(1);
  }
}

for (const viewport of [{ width: 1280, height: 900 }, { width: 390, height: 844 }]) {
  test.describe(`the state-aware fields at ${viewport.width} pixels`, { tag: '@dialogs' }, () => {
    test.use({ viewport });

    test('a Draft parent: editable and required fields, a greyed forward step with its reason and no hint for a required field', async ({ page }) => {
      await openFixture(page, 'fields-draft.html');
      await expectNoSeriousViolations(page);

      const name = page.locator('.state-field[data-field=Name]');
      await expect(name.locator('.state-field__required-mark')).toBeVisible();
      await expect(page.getByRole('textbox', { name: 'Name' })).toHaveAttribute('aria-required', 'true');
      await expect(page.locator('fieldset.state-area-fieldset[data-area=Planning]')).toBeVisible();

      const grade = page.getByRole('textbox', { name: 'Grade' });
      await expect(grade).toBeEditable();
      await expect(grade).not.toHaveAttribute('aria-required', 'true');
      await expect(grade).toHaveAccessibleDescription('Needed before Active');
      await expect(page.locator('.attribute-value-field__needed-before')).toHaveText('Needed before Active');
      await expect(page.locator('.attribute-value-field__required-mark')).toHaveCount(0);

      await expect(page.getByRole('textbox', { name: 'Seats' })).toBeEditable();
      await expect(page.getByRole('textbox', { name: 'Budget' })).toBeEditable();
      await expect(page.getByRole('textbox', { name: 'Opens on' })).toBeEditable();
      await expect(page.locator('div[role=checkbox][aria-label=Projector]')).toBeVisible();
      await expect(page.getByRole('combobox', { name: 'Room' })).toBeVisible();
      await grade.scrollIntoViewIfNeeded();
      await shot(page, 'state-entity', `16-fields-needed-before-${viewport.width}`);

      await shot(page, 'state-entity', `08-fields-editable-required-${viewport.width}`);

      const step = page.locator('button[data-transition="test-parent-activate"]');
      await expect(step).toHaveAttribute('aria-disabled', 'true');
      await step.focus();
      const card = page.locator(`#${await step.getAttribute('aria-describedby')}`);
      await expect(card).toBeVisible();
      await expect(card).toContainText('Grade is required');
      await shot(page, 'state-entity', `10-fields-greyed-step-reason-${viewport.width}`);
    });

    test('an Active parent: read-only fields stay focusable and take no typing, the notes stay editable', async ({ page }) => {
      await openFixture(page, 'fields-active.html');
      await expectNoSeriousViolations(page);

      const name = page.getByRole('textbox', { name: 'Name' });
      await expect(name).not.toBeEditable();
      await expect(name).toBeEnabled();
      await name.focus();
      await expect(name).toBeFocused();
      await page.keyboard.type('changed');
      await expect(name).toHaveValue('Autumn period job');
      await expect(page.getByRole('textbox', { name: 'Grade' })).not.toBeEditable();
      await expect(page.getByRole('textbox', { name: 'Notes' })).toBeEditable();
      await expect(page.locator('fieldset.state-area-fieldset[data-area=Planning] legend')).toContainText('(read-only)');

      await expect(page.getByRole('textbox', { name: 'Seats' })).not.toBeEditable();
      await expect(page.getByRole('textbox', { name: 'Budget' })).not.toBeEditable();
      await expect(page.getByRole('textbox', { name: 'Opens on' })).not.toBeEditable();
      await expect(page.getByRole('textbox', { name: 'Projector' })).toHaveValue('Yes');
      await expect(page.getByRole('textbox', { name: 'Room' })).not.toBeEditable();
      await expect(page.getByRole('combobox', { name: 'Room' })).toHaveCount(0);
      await expect(page.locator('.attribute-value-field__needed-before')).toHaveCount(0);
      await shot(page, 'state-entity', `11-fields-read-only-${viewport.width}`);
    });

    test('a Confirmed parent: the required custom field is required, after the step that needed it', async ({ page }) => {
      await openFixture(page, 'fields-confirmed.html');
      await expectNoSeriousViolations(page);

      const grade = page.getByRole('textbox', { name: 'Grade' });
      await expect(grade).toBeEditable();
      await expect(grade).toHaveAttribute('aria-required', 'true');
      await expect(page.locator('.attribute-value-field[data-access=required] .attribute-value-field__required-mark')).toBeVisible();
      await expect(page.locator('.attribute-value-field__needed-before')).toHaveCount(0);
      await expect(page.getByRole('textbox', { name: 'Name' })).not.toBeEditable();
      await grade.scrollIntoViewIfNeeded();
      await shot(page, 'state-entity', `14-fields-required-after-the-step-${viewport.width}`);
    });

    test('a Canceled parent: a hidden field, a hidden area and the hidden custom fields take no room', async ({ page }) => {
      await openFixture(page, 'fields-canceled.html');
      await expectNoSeriousViolations(page);

      await expect(page.getByRole('textbox', { name: 'Name' })).toBeVisible();
      await expect(page.locator('[name=test-parent-notes]')).toHaveCount(0);
      await expect(page.locator('fieldset.state-area-fieldset')).toHaveCount(0);
      await expect(page.locator('.custom-fields-fieldset')).toHaveCount(0);
      await shot(page, 'state-entity', `12-fields-hidden-area-${viewport.width}`);
    });

    test('a Draft child: an editable drop-down and switch, each named by its label', async ({ page }) => {
      await openFixture(page, 'fields-child-draft.html');
      await expectNoSeriousViolations(page);

      const parent = page.getByRole('combobox', { name: 'Parent' });
      await expect(parent).not.toHaveAttribute('aria-readonly', 'true');
      await parent.focus();
      await expect(parent).toBeFocused();
      const pinned = page.getByRole('switch', { name: 'Pinned' });
      await expect(pinned).toBeEnabled();
      await pinned.focus();
      await expect(pinned).toBeFocused();
      await shot(page, 'state-entity', `15-fields-editable-drop-down-switch-${viewport.width}`);
    });

    test('an active child: a read-only drop-down and yes or no keep the focus and say what they hold', async ({ page }) => {
      await openFixture(page, 'fields-child.html');
      await expectNoSeriousViolations(page);

      const parent = page.getByRole('textbox', { name: 'Parent' });
      await expect(parent).not.toBeEditable();
      await parent.focus();
      await expect(parent).toBeFocused();
      const pinned = page.getByRole('textbox', { name: 'Pinned' });
      await expect(pinned).toHaveValue('Yes');
      await expect(pinned).not.toBeEditable();
      await shot(page, 'state-entity', `13-fields-read-only-drop-down-switch-${viewport.width}`);
    });
  });
}
