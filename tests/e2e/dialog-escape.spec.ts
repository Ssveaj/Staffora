import { arrive, dialogHostUrl, dialogs, expectDialogs, readDialogHostState, signIn, type DialogHostState } from './lib/dialog-host';
import { expect, test } from './lib/page-health';
import type { Page } from '@playwright/test';

let host: DialogHostState;
let parent: string;
let child: string;

test.use({ baseURL: dialogHostUrl });

test.beforeEach(() => {
  host = readDialogHostState();
  parent = `testparent-${host.parentId}`;
  child = `testchild-${host.childId}`;
});

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

async function openChildByHand(page: Page): Promise<void> {
  await signIn(page, host.userId);
  await arrive(page, `/me/overview?dlg=${parent}`);
  await expectDialogs(page, 1);
  await page.getByRole('tab', { name: 'Children' }).click();
  await page.getByRole('button', { name: host.childName, exact: true }).click();
  await expectDialogs(page, 2);
}

test.describe('Esc on stacked state-entity dialogs', { tag: ['@dialogs', '@dialog-host'], lock: 'dialog-host' }, () => {
  test('A child closed with Esc leaves Esc closing its parent', async ({ page }) => {
    await openChildByHand(page);

    await waitForHeadingFocus(page, 'Child');
    await page.keyboard.press('Escape');
    await expectDialogs(page, 1);
    await expect(page.locator(dialogs)).toHaveCount(1);

    await page.keyboard.press('Escape');
    await expectDialogs(page, 0);
  });

  test('The first Esc right after a child\'s heading takes the focus closes the child', async ({ page }) => {
    await openChildByHand(page);
    await waitForHeadingFocus(page, 'Child');
    await page.keyboard.press('Escape');
    await expectDialogs(page, 1);

    await page.getByRole('button', { name: host.childName, exact: true }).click();
    await waitForHeadingFocus(page, 'Child');
    await page.keyboard.press('Escape');
    await expectDialogs(page, 1);
    await expect(page.locator('.state-entity-dialog-header__title')).toHaveText([host.parentName]);
  });

  test('On a restored chain the first Esc closes the child and the next the parent', async ({ page }) => {
    await signIn(page, host.userId);
    await arrive(page, `/me/overview?dlg=${parent}.${child}`);
    await expectDialogs(page, 2);

    await waitForHeadingFocus(page, 'Child');
    await page.keyboard.press('Escape');
    await expectDialogs(page, 1);

    await page.keyboard.press('Escape');
    await expectDialogs(page, 0);
  });
});
