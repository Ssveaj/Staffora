import type { Browser, Locator, Page } from '@playwright/test';
import { persona, personaKeys, personaName, readFoundationState } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { expectSettledPageAccessible, gotoReady, loginAs, waitForSettled } from './lib/session';
import { shot } from './lib/shots';
import { cancelJob, openJob as findJob, runSuffix, staffInPlace, startedJob } from './lib/jobs';

const wideToo = new Set(['04-conversation-unanswered', '05-conversation-replied', '07-staff-new-reply']);

async function capture(page: Page, name: string, width: number): Promise<void> {
  await page.mouse.move(0, 0);
  await expectSettledPageAccessible(page);
  await shot(page, 'conversations', `${name}-${width}`, [page.locator('.state-entity-dialog-footer__notice')]);
  if (width === 1280 && wideToo.has(name)) {
    await page.setViewportSize({ width: 1500, height: 900 });
    await waitForSettled(page);
    await shot(page, 'conversations', `${name}-1500`, [page.locator('.state-entity-dialog-footer__notice')]);
    await page.setViewportSize({ width: 1280, height: 800 });
    await waitForSettled(page);
  }
}

const sideOf = (dialog: Locator) => dialog.getByRole('region', { name: 'Comments' });

async function openNotice(page: Page, text: string): Promise<Locator> {
  await page.locator('.notice-bell__trigger').click();
  const panel = page.locator('[data-notice-panel]');
  await expect(panel).toBeVisible();
  await panel.getByRole('button', { name: 'All', exact: true }).click();
  await waitForSettled(page);
  await panel.locator('.notice-row').filter({ hasText: text }).first().click();
  const dialog = page.getByRole('dialog').last();
  await expect(dialog.locator('[data-job]')).toBeVisible();
  return dialog;
}

async function expectNotice(page: Page, text: string): Promise<void> {
  await page.locator('.notice-bell__trigger').click();
  const panel = page.locator('[data-notice-panel]');
  await panel.getByRole('button', { name: 'All', exact: true }).click();
  await waitForSettled(page);
  await expect(panel.locator('.notice-row').filter({ hasText: text }).first()).toBeVisible();
  await page.keyboard.press('Escape');
}

async function assignStaff(page: Page, jobDialog: Locator, staffMember: string): Promise<void> {
  await jobDialog.getByRole('tab', { name: 'Slots' }).click();
  await jobDialog.locator('tr.list-row').filter({ hasText: 'Lead' }).locator('td').first().click();
  const need = page.getByRole('dialog', { name: 'Lead', exact: true });
  await need.getByRole('tab', { name: 'Staffing' }).click();
  await staffInPlace(need, staffMember, 'assign');
  await need.getByRole('button', { name: 'Close', exact: true }).first().click();
  await expect(page.getByRole('dialog')).toHaveCount(1);
}

async function openJob(page: Page, jobName: string): Promise<Locator> {
  const job = await findJob(page, jobName);
  expect(job, `the job ${jobName} is listed`).not.toBeNull();
  return job!;
}

async function post(page: Page, scope: Locator, text: string): Promise<void> {
  const box = scope.getByRole('textbox', { name: 'Write a comment' });
  await box.fill(text);
  await box.press('Enter');
  await expect(scope.locator('.comment-bubble__text').filter({ hasText: text })).toHaveCount(1);
  await expect(box).toHaveValue('');
}

async function staffBrowser(browser: Browser, width: number): Promise<{ page: Page; close: () => Promise<void> }> {
  const context = await browser.newContext({ viewport: { width, height: 800 } });
  const page = await context.newPage();
  await loginAs(page, persona(personaKeys.staff, readFoundationState()));
  return { page, close: () => context.close() };
}

test('a staff member asks, the job shows it and tells the coordinator, and the reply clears it and tells the staff member', { tag: '@staffing' }, async ({ page, browser }) => {
  test.setTimeout(300_000);
  const width = 1280;
  await page.setViewportSize({ width, height: 800 });
  const state = readFoundationState();
  const staffMember = personaName(persona(personaKeys.staff, state));
  const suffix = runSuffix();
  const jobName = `Geometry ${suffix}`;
  const note = `Budget is tight this period ${suffix}`;
  const question = `Is the room on the ground floor? ${suffix}`;
  const reply = `Yes, room B12 by the entrance ${suffix}`;

  await loginAs(page, persona(personaKeys.coordinator, state));
  let jobDialog = await test.step('set-up: a started job of the run', () => startedJob(page, jobName, { fields: { Room: 'B12', 'Budget code': 'BX-4200' }, keepOpen: true }));
  const jobLink = new URL(page.url()).searchParams.get('dlg');
  expect(jobLink).toMatch(/^job-/);
  await assignStaff(page, jobDialog, staffMember);

  const jobSide = sideOf(jobDialog);
  await expect(jobSide.getByRole('tab', { name: /^Staff/ })).toHaveAccessibleName('Staff, 1 conversation');
  await expect(jobSide.getByRole('tab', { name: /^Internal/ })).toHaveAttribute('aria-selected', 'true');
  await expect(jobSide.getByText('Notes for the coordinators only')).toBeVisible();
  await post(page, jobSide, note);
  await capture(page, '01-job-internal-note', width);
  await jobDialog.getByRole('button', { name: 'Close', exact: true }).first().click();

  const staffSide = await staffBrowser(browser, width);
  try {
    const staffPage = staffSide.page;
    await gotoReady(staffPage, '/me/overview');
    const request = await openNotice(staffPage, `You are assigned to ${jobName}`);
    const requestSide = sideOf(request);
    await expect(requestSide.getByRole('tab', { name: /^Questions to the coordinator/ })).toBeVisible();
    await post(staffPage, requestSide, question);
    await expect(request).not.toContainText(note);
    await capture(staffPage, '02-staff-asks', width);
    await request.getByRole('button', { name: 'Close', exact: true }).first().click();

    await gotoReady(page, '/staffing/jobs');
    await expectNotice(page, `${staffMember} wrote about ${jobName}`);
    jobDialog = await openJob(page, jobName);
    const side = sideOf(jobDialog);
    const staffMembersTab = side.getByRole('tab', { name: /^Staff/ });
    await expect(staffMembersTab).toHaveAttribute('data-attention', 'true');
    await expect(staffMembersTab).toHaveAccessibleName(/1 awaits your reply/);
    await staffMembersTab.click();
    const row = side.locator('.staff-talk__row').filter({ hasText: staffMember });
    await expect(row).toHaveAttribute('data-attention', 'true');
    await expect(row).toContainText(question);
    await capture(page, '03-job-staff-attention', width);

    await row.click();
    const open = side.locator('.staff-talk__open');
    await expect(open.getByRole('button', { name: 'All staff members' })).toBeFocused();
    await expect(open.locator('[data-marker]')).toHaveText('Unanswered');
    await expect(open.locator('[data-attention-text]')).toHaveText('Awaits your reply');
    await capture(page, '04-conversation-unanswered', width);
    await post(page, open, reply);
    await expect(open.locator('[data-attention-text]')).toHaveCount(0);
    await expect(staffMembersTab).toHaveAttribute('data-attention', 'false');
    await capture(page, '05-conversation-replied', width);
    await open.getByRole('button', { name: 'All staff members' }).click();
    await expect(side.locator('.staff-talk__row').filter({ hasText: staffMember })).toHaveAttribute('data-attention', 'false');
    await expect(side.locator('.staff-talk__row').filter({ hasText: staffMember })).toBeFocused();
    await jobDialog.getByRole('button', { name: 'Close', exact: true }).first().click();

    await gotoReady(staffPage, '/me/assignments');
    await expect(staffPage.locator('.list-row').filter({ hasText: jobName }).locator('[data-badge="new-reply"]')).toHaveText(/New reply/);
    await capture(staffPage, '06-my-assignments-new-reply', width);
    const replied = await openNotice(staffPage, `wrote about ${jobName}`);
    const repliedSide = sideOf(replied);
    await expect(repliedSide.locator('[data-attention-text]')).toHaveText('New reply');
    await expect(repliedSide.locator('[data-marker]')).toHaveText('New');
    await expect(repliedSide.locator('.comment-bubble--own').filter({ hasText: question })).toHaveCount(1);
    await expect(repliedSide.locator('.comment-bubble--other').filter({ hasText: reply })).toHaveCount(1);
    await expect(replied).not.toContainText(note);
    await capture(staffPage, '07-staff-new-reply', width);
    await staffPage.setViewportSize({ width: 390, height: 844 });
    await capture(staffPage, '07-staff-new-reply', 390);
    await staffPage.setViewportSize({ width, height: 800 });
    await replied.getByRole('button', { name: 'Close', exact: true }).first().click();
    await gotoReady(staffPage, '/me/assignments');
    await expect(staffPage.locator('.list-row').filter({ hasText: jobName })).toBeVisible();
    await expect(staffPage.locator('.list-row').filter({ hasText: jobName }).locator('[data-badge="new-reply"]')).toHaveCount(0);

    await gotoReady(staffPage, `/me/overview?dlg=${jobLink}`);
    await waitForSettled(staffPage);
    await expect(staffPage.getByRole('dialog')).toHaveCount(0);
    await expect(staffPage.locator('body')).not.toContainText(note);

    await cancelJob(page, jobName);
  } finally {
    await staffSide.close();
  }
});
