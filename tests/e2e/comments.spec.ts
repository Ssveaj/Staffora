import { expectNoSeriousViolations } from './lib/accessibility';
import { arrive, dialogHostUrl, dialogs, expectDialogs, readDialogHostState, signIn, type DialogHostState } from './lib/dialog-host';
import { expect, test } from './lib/page-health';
import { waitForSettled } from './lib/session';
import { join } from 'node:path';
import { screenshotRoot, shot } from './lib/shots';
import type { Locator, Page } from '@playwright/test';

let host: DialogHostState;

test.use({ baseURL: dialogHostUrl });

test.beforeEach(() => {
  host = readDialogHostState();
});

async function openCommentedParent(page: Page): Promise<Locator> {
  await signIn(page, host.commentReaderId);
  await arrive(page, `/me/overview?dlg=testparent-${host.commentedParentId}`);
  await expectDialogs(page, 1);
  const dialog = page.locator(dialogs);
  await expect(dialog.locator('.state-entity-dialog-header__title')).toHaveText(host.commentedParentName);
  return dialog;
}

async function openParentAs(page: Page, userId: string, parentId: string, name: string): Promise<Locator> {
  await signIn(page, userId);
  await arrive(page, `/me/overview?dlg=testparent-${parentId}`);
  await expectDialogs(page, 1);
  const dialog = page.locator(dialogs);
  await expect(dialog.locator('.state-entity-dialog-header__title')).toHaveText(name);
  return dialog;
}

async function countOf(tab: Locator): Promise<number> {
  return Number(await tab.locator('.dialog-side-tabs__count').getAttribute('data-count'));
}

test.describe('comment threads in the side column', { tag: ['@dialogs', '@dialog-host'], lock: 'dialog-host' }, () => {
  test('the thread shows beside the body, under it at 390 pixels, and is accessible at both widths', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    const dialog = await openCommentedParent(page);
    const side = dialog.getByRole('region', { name: 'Comments' });
    const body = dialog.locator('.state-entity-dialog__body');

    const internal = side.getByRole('tab', { name: /^Internal/ });
    const staffTab = side.getByRole('tab', { name: /^Staff/ });
    await expect(internal).toHaveAttribute('aria-selected', 'true');
    expect(await countOf(internal)).toBeGreaterThanOrEqual(3);
    await expect(staffTab).toHaveAttribute('data-attention', 'true');
    await expect(staffTab).toHaveAccessibleName(/awaits your reply/);

    const panel = side.getByRole('tabpanel');
    const comments = panel.getByRole('list', { name: 'Internal comments' }).getByRole('listitem');
    await expect(comments.nth(0)).toContainText('Who takes the morning group in week 1?');
    await expect(comments.nth(1)).toContainText('I can, from Monday.');
    await expect(comments.nth(2)).not.toContainText('Annotate');
    await expect(comments.nth(2)).toContainText('Moved the start to week 2 after the room change.');
    await expect(panel.getByRole('textbox', { name: 'Write a comment' })).toBeVisible();

    const [bodyBox, sideBox] = [await body.boundingBox(), await side.boundingBox()];
    expect(sideBox!.x).toBeGreaterThanOrEqual(bodyBox!.x + bodyBox!.width - 1);

    await waitForSettled(page);
    await expectNoSeriousViolations(page);
    await shot(page, 'comments', 'side-column-1280');
    await page.setViewportSize({ width: 1500, height: 900 });
    await waitForSettled(page);
    await shot(page, 'comments', 'side-column-1500');

    await page.setViewportSize({ width: 390, height: 844 });
    await waitForSettled(page);
    const [narrowBody, narrowSide] = [await body.boundingBox(), await side.boundingBox()];
    expect(narrowSide!.y).toBeGreaterThanOrEqual(narrowBody!.y + narrowBody!.height - 1);
    expect(narrowSide!.width).toBeGreaterThan(narrowBody!.width - 2);
    await expectNoSeriousViolations(page);
    await shot(page, 'comments', 'dialog-390');
    await side.scrollIntoViewIfNeeded();
    await side.screenshot({ path: join(screenshotRoot, 'comments', 'side-column-390.png') });
  });

  test('a post is saved at once and the arrow keys move between the threads', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    const dialog = await openCommentedParent(page);
    const side = dialog.getByRole('region', { name: 'Comments' });
    const internal = side.getByRole('tab', { name: /^Internal/ });
    const before = await countOf(internal);

    const text = `I'll cover the afternoon group (${Date.now()}).`;
    const box = side.getByRole('textbox', { name: 'Write a comment' });
    await box.fill(text);
    await side.getByRole('button', { name: 'Post' }).click();
    const comments = side.getByRole('list', { name: 'Internal comments' }).getByRole('listitem');
    await expect(comments.last()).toContainText(text);
    await expect(comments.last()).toContainText('(you)');
    await expect.poll(() => countOf(internal)).toBe(before + 1);
    await expect(box).toHaveValue('');
    await expect(box).toBeFocused();
    await expect(side.getByRole('status').filter({ hasText: 'Comment posted.' })).toHaveCount(1);

    await internal.focus();
    await page.keyboard.press('ArrowRight');
    const staffTab = side.getByRole('tab', { name: /^Staff/ });
    await expect(staffTab).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(staffTab).toHaveAttribute('aria-selected', 'true');
    await expect(side.getByRole('list', { name: 'Staff comments' }).getByRole('listitem')).toContainText(['Can you take the Monday group?']);
    await expect(side.getByRole('textbox', { name: 'Write a comment' })).toHaveCount(0);
  });

  test('the dialog is 1180 pixels wide only for a reader who gets the side column', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });

    await openCommentedParent(page);
    await expect(page.locator('.rz-dialog').getByRole('region', { name: 'Comments' })).toBeVisible();
    await expect.poll(async () => Math.round((await page.locator('.rz-dialog').boundingBox())!.width)).toBe(1180);

    const dialog = await openParentAs(page, host.userId, host.parentId, host.parentName);
    await expect(dialog.getByRole('region', { name: 'Comments' })).toHaveCount(0);
    await expect.poll(async () => Math.round((await page.locator('.rz-dialog').boundingBox())!.width)).toBe(720);
  });

  test('a long thread shows its newest 200 and keeps the composer in reach', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 900 });
    const dialog = await openParentAs(page, host.commentReaderId, host.longThreadParentId, host.longThreadParentName);
    const side = dialog.getByRole('region', { name: 'Comments' });
    const list = side.getByRole('list', { name: 'Internal comments' });
    const box = side.getByRole('textbox', { name: 'Write a comment' });

    const total = await countOf(side.getByRole('tab', { name: /^Internal/ }));
    expect(total).toBeGreaterThanOrEqual(host.longThreadComments);
    await expect(side.getByText(`Showing the newest 200 of ${total} comments.`)).toBeVisible();
    await expect(list.getByRole('listitem')).toHaveCount(200);

    await expect(list.getByRole('listitem').filter({ hasText: `Long thread comment ${host.longThreadComments}.` })).toHaveCount(1);
    await expect(list.getByRole('listitem').last()).toBeInViewport();
    await expect(list.getByRole('listitem').first()).not.toBeInViewport();
    await expect(box).toBeInViewport();
    expect(await side.evaluate((element) => element.scrollTop)).toBe(0);
    await waitForSettled(page);
    await expectNoSeriousViolations(page);
    await shot(page, 'comments', 'long-thread-1280');
    await page.setViewportSize({ width: 1500, height: 900 });
    await waitForSettled(page);
    await shot(page, 'comments', 'long-thread-1500');
    await page.setViewportSize({ width: 1280, height: 900 });
    await waitForSettled(page);

    const text = `One more on the long thread (${Date.now()}).`;
    await box.fill(text);
    await side.getByRole('button', { name: 'Post' }).click();
    await expect(list.getByRole('listitem').last()).toContainText(text);
    await expect(list.getByRole('listitem').last()).toBeInViewport();

    await page.setViewportSize({ width: 390, height: 844 });
    await waitForSettled(page);
    const scroller = side.locator('.comment-bubbles__scroller');
    const [scrollerBox, composerBox] = [await scroller.boundingBox(), await box.boundingBox()];
    expect(scrollerBox!.height).toBeLessThanOrEqual(844 / 2 + 1);
    expect(composerBox!.y - (scrollerBox!.y + scrollerBox!.height)).toBeLessThan(120);
    await box.scrollIntoViewIfNeeded();
    await expect(list.getByRole('listitem').last()).toBeInViewport();
    await expectNoSeriousViolations(page);
    await shot(page, 'comments', 'long-thread-390');
  });
});
