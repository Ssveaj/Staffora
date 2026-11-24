import type { Browser, Locator, Page } from '@playwright/test';
import { persona, personaKeys, personaName, readFoundationState } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { expectSettledPageAccessible, gotoReady, loginAs, waitForSettled } from './lib/session';
import { shot } from './lib/shots';
import { cancelJob, candidateRow, runSuffix, staffInPlace, startedJob } from './lib/jobs';

const wideToo = new Set(['13-question-request-for-the-staff']);

async function capture(page: Page, name: string, width: number): Promise<void> {
  await page.mouse.move(0, 0);
  await expectSettledPageAccessible(page);
  await shot(page, 'assignments', `${name}-${width}`, [page.locator('.state-entity-dialog-footer__notice')]);
  if (width === 1280 && wideToo.has(name)) {
    await page.setViewportSize({ width: 1500, height: 900 });
    await waitForSettled(page);
    await expectSettledPageAccessible(page);
    await shot(page, 'assignments', `${name}-1500`, [page.locator('.state-entity-dialog-footer__notice')]);
    await page.setViewportSize({ width: 1280, height: 800 });
    await waitForSettled(page);
  }
}

async function takeStep(page: Page, dialog: Locator, key: string): Promise<void> {
  await dialog.locator(`button[data-transition="${key}"]`).focus();
  await page.keyboard.press('Enter');
}

const pill = (dialog: Locator) => dialog.locator('.state-pill__button');

async function openBell(page: Page): Promise<Locator> {
  await page.locator('.notice-bell__trigger').click();
  const panel = page.locator('[data-notice-panel]');
  await expect(panel).toBeVisible();
  await panel.getByRole('button', { name: 'All', exact: true }).click();

  await waitForSettled(page);
  return panel;
}

async function openNotice(page: Page, text: string, also?: string): Promise<Locator> {
  const bell = await openBell(page);
  const rows = bell.locator('.notice-row').filter({ hasText: text });
  await (also ? rows.filter({ hasText: also }) : rows).first().click();
  const dialog = page.getByRole('dialog').last();
  await expect(dialog.locator('[data-job]')).toBeVisible();
  return dialog;
}

async function openSlotStaffMembers(page: Page, jobDialog: Locator, name: string): Promise<Locator> {
  await jobDialog.getByRole('tab', { name: 'Slots' }).click();
  await jobDialog.locator('tr.list-row').filter({ hasText: name }).locator('td').first().click();
  const need = page.getByRole('dialog', { name, exact: true });
  await need.getByRole('tab', { name: /^Staff(?!ing)/ }).click();
  return need;
}

async function requestStaff(page: Page, need: Locator, staffMember: string, width: number, shotName?: string): Promise<void> {
  await need.getByRole('tab', { name: 'Staffing' }).click();
  await expect(candidateRow(need, staffMember).locator('button[data-staff-action="request"]')).toBeEnabled();
  if (shotName) await capture(page, shotName, width);
  await staffInPlace(need, staffMember, 'request');
  await need.getByRole('tab', { name: /^Staff(?!ing)/ }).click();
  await expect(need.locator('tr.list-row').filter({ hasText: staffMember }).filter({ hasText: 'Requested' })).toHaveCount(1);
}

async function staffBrowser(browser: Browser, width: number): Promise<{ page: Page; close: () => Promise<void> }> {
  const context = await browser.newContext({ viewport: { width, height: 800 } });
  const page = await context.newPage();
  await loginAs(page, persona(personaKeys.staff, readFoundationState()));
  return { page, close: () => context.close() };
}

test('a staff member writes a question on a request, the coordinator answers, the request is back with the staff member, who accepts', { tag: '@staffing' }, async ({ page, browser }) => {
  test.setTimeout(300_000);
  const width = 1280;
  await page.setViewportSize({ width, height: 800 });
  const state = readFoundationState();
  const staffMember = personaName(persona(personaKeys.staff, state));
  const suffix = runSuffix();
  const customerName = state.staffing.customer;
  const jobName = `Geometry ${suffix}`;
  const question = `Is there parking at the place? ${suffix}`;
  const answer = `Yes, behind the building. ${suffix}`;

  await loginAs(page, persona(personaKeys.coordinator, state));
  const jobDialog = await test.step('set-up: a started job of the run', () => startedJob(page, jobName, { keepOpen: true }));
  const need = await openSlotStaffMembers(page, jobDialog, 'Lead');
  await requestStaff(page, need, staffMember, width);
  for (const open of [need, jobDialog]) await open.getByRole('button', { name: 'Close', exact: true }).first().click();
  await expect(page.getByRole('dialog')).toHaveCount(0);

  const staffSide = await staffBrowser(browser, width);
  try {
    const staffPage = staffSide.page;
    await gotoReady(staffPage, '/me/overview');
    const request = await openNotice(staffPage, `You are asked to work ${jobName}`);
    await expect(pill(request)).toHaveAttribute('data-state', 'Requested');
    await expect(request.locator('[data-status]')).toHaveText('Your answer is needed');
    await expect(request.locator('[data-job]')).toHaveText(jobName);
    await expect(request.locator('[data-customer]')).toHaveText(customerName);
    await expect(request.locator('[data-slot]')).toHaveText('Lead');
    await expect(request.locator('[data-qualifications]')).toHaveText('None asked for');
    await expect(request.locator('[data-staff]')).toHaveCount(0);
    await expect(request.locator('button[data-transition]')).toHaveCount(3);
    for (const key of ['assignment-decline', 'assignment-ask-coordinator', 'assignment-accept']) {
      await expect(request.locator(`button[data-transition="${key}"]`)).toBeVisible();
    }

    await capture(staffPage, '13-question-request-for-the-staff', width);

    await takeStep(staffPage, request, 'assignment-ask-coordinator');
    const write = staffPage.locator('.form-dialog').filter({ hasText: 'Write a question to the coordinator' });
    await expect(write).toContainText('The coordinator sees your question on the request.');
    await write.getByRole('button', { name: 'Write a question', exact: true }).click();
    await expect(write.locator('.prompt-dialog__required')).toHaveText('Write the question first, or the coordinator only sees that something happened.');
    await write.getByLabel('What do you want to ask about?').fill(question);
    await capture(staffPage, '14-write-a-question-prompt', width);
    await write.getByRole('button', { name: 'Write a question', exact: true }).click();
    await expect(pill(request)).toHaveAttribute('data-state', 'StaffQuestion');
    await expect(request.locator('[data-status]')).toHaveText('Your question waits for the coordinator');
    await expect(request.locator('.state-entity-dialog-side')).toContainText(question);
    await expect(request.locator('.state-entity-dialog-side textarea')).toHaveCount(0);
    await expect(request.locator('[data-thread-closed]')).toHaveCount(0);
    for (const key of ['assignment-decline-question', 'assignment-ask-coordinator-again', 'assignment-accept-question']) {
      await expect(request.locator(`button[data-transition="${key}"]`)).toBeVisible();
    }

    await capture(staffPage, '15-question-to-the-coordinator-for-the-staff', width);
    await request.getByRole('button', { name: 'Close', exact: true }).first().click();

    await gotoReady(page, '/staffing/jobs');
    const asked = await openNotice(page, 'New question about a request', question);
    await expect(pill(asked)).toHaveAttribute('data-state', 'StaffQuestion');
    await expect(asked.locator('.state-entity-dialog-side')).toContainText(question);
    await expect(asked.locator('button[data-transition]')).toHaveCount(3);
    await expect(asked.locator('button[data-transition="assignment-question-handled"]')).toHaveText(/Mark as handled/);
    await capture(page, '16-question-for-the-coordinator', width);
    await takeStep(page, asked, 'assignment-answer-staff');
    const reply = page.locator('.form-dialog').filter({ hasText: 'The request goes back to the staff member with your answer.' });
    await reply.getByLabel('Your answer').fill(answer);
    await reply.getByRole('button', { name: 'Answer the staff member', exact: true }).click();
    await expect(pill(asked)).toHaveAttribute('data-state', 'Requested');
    await expect(asked.locator('.state-entity-dialog-side')).toContainText(answer);
    await expect(asked.locator('button[data-transition]')).toHaveCount(1);
    await expect(asked.locator('button[data-transition="assignment-cancel-request"]')).toBeVisible();
    await asked.getByRole('button', { name: 'Close', exact: true }).first().click();

    await gotoReady(staffPage, '/me/overview');
    const answered = await openNotice(staffPage, 'New answer about your request', answer);
    await expect(pill(answered)).toHaveAttribute('data-state', 'Requested');
    await expect(answered.locator('.state-entity-dialog-side')).toContainText(answer);
    await takeStep(staffPage, answered, 'assignment-accept');
    await expect(pill(answered)).toHaveAttribute('data-state', 'Accepted');
    await capture(staffPage, '17-accepted-after-question', width);
    await answered.getByRole('button', { name: 'Close', exact: true }).first().click();

    await cancelJob(page, jobName);
  } finally {
    await staffSide.close();
  }
});
