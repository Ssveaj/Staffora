import { expectNoSeriousViolations } from './lib/accessibility';
import { arrive, chainOf, countLeavePrompts, dialogHostUrl, dialogs, expectDialogs, focused, leavePrompts, navigateInApp, readDialogHostState, signIn as signInAs, titles, type DialogHostState } from './lib/dialog-host';
import { expect, test } from './lib/page-health';
import { waitForReady, waitForSettled } from './lib/session';
import { shot } from './lib/shots';
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

function signIn(page: Page): Promise<void> {
  return signInAs(page, host.userId);
}

test.describe('dialog restore through the URL', { tag: ['@dialogs', '@dialog-host'], lock: 'dialog-host' }, () => {
  test('F5 on an open nested dialog reopens the same chain, and closing returns the focus to each opener', async ({ page }) => {
    await signIn(page);

    await arrive(page, `/me/overview?dlg=${parent}`);
    await expectDialogs(page, 1);
    await expect(page.locator(titles)).toHaveText([host.parentName]);
    await expect.poll(() => chainOf(page)).toBe(parent);

    await page.getByRole('tab', { name: 'Children' }).click();
    await page.getByRole('button', { name: host.childName, exact: true }).click();
    await expectDialogs(page, 2);
    await expect.poll(() => chainOf(page)).toBe(`${parent}.${child}`);

    await page.reload();
    await waitForReady(page);
    await expectDialogs(page, 2);
    await expect(page.locator(titles)).toHaveText([host.parentName, 'Child']);
    await expect(page.locator(dialogs).last().locator('[name="test-child-name"]')).toHaveValue(host.childName);
    await expect.poll(() => page.evaluate(() => document.activeElement?.classList.contains('state-entity-dialog-header__title') && document.activeElement.textContent?.trim())).toBe('Child');
    expect(chainOf(page)).toBe(`${parent}.${child}`);

    await waitForSettled(page);
    await expectNoSeriousViolations(page);
    await shot(page, 'state-entity', 'restored-chain-1280');
    await page.setViewportSize({ width: 390, height: 844 });
    await waitForSettled(page);
    await expectNoSeriousViolations(page);
    await shot(page, 'state-entity', 'restored-chain-390');
    await page.setViewportSize({ width: 1280, height: 800 });

    await page.locator(dialogs).last().getByRole('button', { name: 'Close' }).click();
    await expectDialogs(page, 1);
    await expect.poll(() => focused(page)).toBe(host.parentName);
    await expect.poll(() => chainOf(page)).toBe(parent);

    await page.getByRole('tab', { name: 'Children' }).click();
    await page.getByRole('button', { name: host.childName, exact: true }).click();
    await expectDialogs(page, 2);
    await page.locator(dialogs).last().getByRole('button', { name: 'Close' }).click();
    await expectDialogs(page, 1);
    await expect(page.getByRole('button', { name: host.childName, exact: true })).toBeFocused();
  });

  test('Back within the app to an entry that kept its chain reopens nothing', async ({ page }) => {
    await signIn(page);
    await arrive(page, `/me/overview?dlg=${parent}.${child}`);
    await expectDialogs(page, 2);

    await page.evaluate(() => ((window as unknown as { restoreMarker: number }).restoreMarker = 1));

    await navigateInApp(page, '/me/profile');
    await expect(page).toHaveURL(/\/me\/profile$/);
    await expectDialogs(page, 0);

    await page.goBack();
    await expect(page).toHaveURL(/\/me\/overview/);
    await expect.poll(() => chainOf(page)).toBeNull();
    await waitForSettled(page);
    await expectDialogs(page, 0);

    await page.goForward();
    await expect(page).toHaveURL(/\/me\/profile$/);
    await page.goBack();
    await expect(page).toHaveURL(/\/me\/overview$/);
    await waitForSettled(page);
    await expectDialogs(page, 0);
    expect(await page.evaluate(() => (window as unknown as { restoreMarker?: number }).restoreMarker)).toBe(1);
  });

  test('Back within the app across layouts, which makes the restore host anew, reopens nothing either', async ({ page }) => {
    await signInAs(page, host.platformOwnerId);
    const own = `testparent-${host.platformParentId}`;
    await arrive(page, `/superadmin/overview?dlg=${own}`);
    await expectDialogs(page, 1);
    await expect(page.locator(titles)).toHaveText([host.platformParentName]);
    await expect.poll(() => chainOf(page)).toBe(own);
    await page.evaluate(() => ((window as unknown as { restoreMarker: number }).restoreMarker = 1));

    await navigateInApp(page, '/not-found');
    await expect(page).toHaveURL(/\/not-found$/);
    await expectDialogs(page, 0);
    await waitForSettled(page);

    await page.goBack();
    await expect(page).toHaveURL(/\/superadmin\/overview/);
    await expect(page.locator('h1')).toBeVisible();
    await expect.poll(() => chainOf(page)).toBeNull();
    await waitForSettled(page);

    await page.waitForTimeout(1000);
    await expectDialogs(page, 0);
    expect(await page.evaluate(() => (window as unknown as { restoreMarker?: number }).restoreMarker)).toBe(1);

    await arrive(page, `/superadmin/overview?dlg=${own}`);
    await expectDialogs(page, 1);
  });

  test('Back from a restored chain with edits asks once, through the browser, and keeps the edits when the user stays', async ({ page }) => {
    await countLeavePrompts(page);
    await signIn(page);
    await arrive(page, '/me/profile');
    await arrive(page, `/me/overview?dlg=${parent}.${child}`);
    await expectDialogs(page, 2);
    const unarmed = await leavePrompts(page);

    await page.locator(dialogs).last().locator('[name="test-child-name"]').click();
    await page.locator(dialogs).last().locator('[name="test-child-name"]').fill('Week 1 timetable, revised');
    await page.locator(dialogs).last().getByRole('button', { name: 'Close' }).focus();
    await expect(page.locator('.state-entity-dialog-header__unsaved')).toHaveCount(1);

    await expect.poll(() => leavePrompts(page)).toBeGreaterThan(unarmed);

    const asked: string[] = [];
    page.on('dialog', (dialog) => {
      asked.push(dialog.type());
      void (asked.length === 1 ? dialog.dismiss() : dialog.accept());
    });
    await page.evaluate(() => history.back());
    await expect.poll(() => asked).toEqual(['beforeunload']);
    await expect(page).toHaveURL(new RegExp(`dlg=${parent}\\.${child}`));
    await expectDialogs(page, 2);
    await expect(page.locator(dialogs).last().locator('[name="test-child-name"]')).toHaveValue('Week 1 timetable, revised');

    await page.evaluate(() => history.back());
    await expect(page).toHaveURL(/\/me\/profile$/);
    expect(asked).toEqual(['beforeunload', 'beforeunload']);
    await waitForReady(page);
    await expectDialogs(page, 0);
  });

  test('A link naming another tenant\'s row opens nothing, as a missing row would', async ({ page }) => {
    await signIn(page);

    await arrive(page, `/me/overview?dlg=testparent-${host.foreignParentId}`);
    await waitForSettled(page);
    await expectDialogs(page, 0);
    await expect(page.locator('.state-entity-dialog__could-not-open')).toHaveCount(0);
    expect(chainOf(page)).toBeNull();

    await arrive(page, `/me/overview?dlg=${parent}.testchild-${host.foreignChildId}`);
    await expectDialogs(page, 1);
    await waitForSettled(page);
    await expectDialogs(page, 1);
    await expect(page.locator(titles)).toHaveText([host.parentName]);
    expect(chainOf(page)).toBe(parent);
  });
});
