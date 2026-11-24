import type { Locator, Page } from '@playwright/test';
import { persona, personaKeys, personaName, readFoundationState } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { expectSettledPageAccessible, gotoReady, loginAs, waitForReady } from './lib/session';
import { shot } from './lib/shots';

const width = 1280;

const absencePage = '/staffing/absence';

async function capture(page: Page, name: string, width: number): Promise<void> {
  await expectSettledPageAccessible(page);
  await shot(page, 'absence', `${name}-${width}`, [page.locator('.state-entity-dialog-footer__notice')]);
}

async function chooseKind(page: Page, dialog: Locator, kind: 'Planned leave' | 'Sick leave'): Promise<void> {
  await dialog.locator('.rz-dropdown').filter({ has: page.locator('[name="absence-kind"]') }).click();
  await page.getByRole('option', { name: kind, exact: true }).filter({ visible: true }).click();
  await expect(dialog.locator('.rz-dropdown').filter({ has: page.locator('[name="absence-kind"]') })).toContainText(kind);
  await expect(page.getByRole('option', { name: kind, exact: true }).filter({ visible: true })).toHaveCount(0);
}

async function takeStep(page: Page, dialog: Locator, key: string): Promise<void> {
  const button = dialog.locator(`button[data-transition="${key}"]`);
  await button.focus();
  await expect(button).toBeFocused();
  await page.keyboard.press('Enter');
}

const pill = (dialog: Locator) => dialog.locator('.state-pill__button');

async function clearAbsences(page: Page): Promise<void> {
  const steps = [['requested', 'absence-cancel'], ['approved', 'absence-cancel-approved'], ['question', 'absence-cancel-question'], ['staffquestion', 'absence-cancel-staff-question']] as const;
  for (const [state, key] of steps) {
    await gotoReady(page, `${absencePage}?status=${state}`);
    for (;;) {
      const rows = page.locator('tr.list-row');
      if ((await rows.count()) === 0) break;
      const row = rows.first();
      const id = await row.getAttribute('data-row');
      await row.click();
      const dialog = page.getByRole('dialog').last();
      await expect(dialog.locator(`button[data-transition="${key}"]`)).toBeVisible();
      await takeStep(page, dialog, key);
      await page.locator('.form-dialog').getByRole('button', { name: 'Cancel absence', exact: true }).click();
      await expect(pill(dialog)).toHaveAttribute('data-state', 'Canceled');
      await dialog.getByRole('button', { name: 'Close', exact: true }).first().click();
      await expect(page.getByRole('dialog')).toHaveCount(0);
      await expect(page.locator(`tr.list-row[data-row="${id}"]`)).toHaveCount(0);
    }
  }
}

async function report(page: Page, kind: 'Planned leave' | 'Sick leave', width: number, start?: string, shotName?: string): Promise<Locator> {
  await gotoReady(page, absencePage);
  await page.getByRole('button', { name: 'Report absence', exact: true }).click();
  const fresh = page.getByRole('dialog').last();
  await expect(fresh.getByRole('heading')).toContainText(' on ');
  await expect(fresh.locator('[data-absence-note]')).toContainText('You report your own absence');
  await expect(fresh.locator('button[data-transition]')).toHaveCount(1);
  await chooseKind(page, fresh, kind);
  if (start) await fresh.locator('[name="absence-start-time"]').fill(start);
  if (shotName) await capture(page, shotName, width);
  await takeStep(page, fresh, 'absence-report');
  const stored = page.getByRole('dialog').last();
  await expect(stored.locator('button[data-transition="absence-report"]')).toHaveCount(0);
  await expect(pill(stored)).toHaveAttribute('data-state', 'Requested');
  return stored;
}

async function openBell(page: Page): Promise<Locator> {
  await page.locator('.notice-bell__trigger').click();
  const panel = page.locator('[data-notice-panel]');
  await expect(panel).toBeVisible();
  await panel.getByRole('button', { name: 'All', exact: true }).click();
  return panel;
}

test(`a staff member reports absence, a coordinator decides and registers it, and each is told`, { tag: '@staffing' }, async ({ page, browser }) => {
  test.setTimeout(420_000);
  const viewport = { width, height: 800 };
  await page.setViewportSize(viewport);
  const state = readFoundationState();
  const staffPersona = persona(personaKeys.staff, state);
  const staffMember = personaName(staffPersona);
  const reason = `Exam week ${Date.now().toString(36)}`;

  const coordinatorContext = await browser.newContext({ viewport });
  try {
    const coordinator = await coordinatorContext.newPage();
    await loginAs(coordinator, persona(personaKeys.coordinator, state));
    await loginAs(page, staffPersona);
    await clearAbsences(page);

    await gotoReady(page, absencePage);
    await expect(page.getByRole('button', { name: 'Register absence', exact: true })).toHaveCount(0);
    await capture(page, '01-staff-absence-page', width);
    const sick = await report(page, 'Sick leave', width, '13:00', '02-report-sick-leave');
    await expect(sick.getByRole('heading')).toContainText('Sick leave on ');
    await expect(sick.getByRole('heading')).toContainText('from 13:00');
    await expect(sick.locator('.state-entity-dialog-side')).toHaveCount(0);
    await capture(page, '03-sick-leave-reported', width);
    await sick.getByRole('button', { name: 'Close', exact: true }).first().click();
    const planned = await report(page, 'Planned leave', width);
    await planned.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(page.locator('tr.list-row').filter({ hasText: 'Sick leave' }).filter({ hasText: 'Blocks staffing' }).first()).toBeVisible();
    await expect(page.locator('tr.list-row').filter({ hasText: 'Planned leave' }).filter({ hasText: 'Does not block' }).first()).toBeVisible();

    await gotoReady(coordinator, absencePage);
    const coordinatorBell = await openBell(coordinator);
    await expect(coordinatorBell.locator('.notice-row').filter({ hasText: `Leave requested by ${staffMember}` }).first()).toBeVisible();
    await expect(coordinatorBell.locator('.notice-row').filter({ hasText: `Sick leave reported for ${staffMember}` }).first()).toBeVisible();
    await coordinator.keyboard.press('Escape');
    await gotoReady(coordinator, `${absencePage}?status=requested&kind=plannedleave`);
    await capture(coordinator, '04-coordinator-requested-leave', width);

    await coordinator.locator('tr.list-row').filter({ hasText: staffMember }).first().click();
    const leave = coordinator.getByRole('dialog').last();
    await expect(pill(leave)).toHaveAttribute('data-state', 'Requested');
    await takeStep(coordinator, leave, 'absence-approve');
    await expect(pill(leave)).toHaveAttribute('data-state', 'Approved');
    await capture(coordinator, '05-leave-approved', width);
    await leave.getByRole('button', { name: 'Close', exact: true }).first().click();
    await expect(coordinator.getByRole('dialog')).toHaveCount(0);

    await gotoReady(coordinator, `${absencePage}?status=requested&kind=sickleave`);
    await coordinator.locator('tr.list-row').filter({ hasText: staffMember }).first().click();
    const reported = coordinator.getByRole('dialog').last();
    await expect(pill(reported)).toHaveAttribute('data-state', 'Requested');
    await expect(reported.locator('button[data-transition="absence-handle-sick"]')).toHaveText(/Handled/);
    await expect(reported.locator('button[data-transition="absence-approve"]')).toHaveCount(0);
    await takeStep(coordinator, reported, 'absence-reject');
    const prompt = coordinator.locator('.form-dialog').filter({ hasText: 'reads what you write on the absence' });
    await prompt.getByLabel('Why is it not approved?').fill(reason);
    await capture(coordinator, '06-reject-prompt', width);
    await coordinator.setViewportSize({ width: 390, height: 844 });
    await capture(coordinator, '06-reject-prompt', 390);
    await coordinator.setViewportSize({ width, height: 800 });
    await prompt.getByRole('button', { name: 'Reject', exact: true }).click();
    await expect(pill(reported)).toHaveAttribute('data-state', 'Rejected');
    await expect(reported.locator('.state-entity-dialog-side')).toContainText(reason);
    await expect(reported.locator('[data-thread-closed]')).toHaveCount(0);
    await capture(coordinator, '07-rejected-reason-in-the-thread', width);
    await reported.getByRole('button', { name: 'Close', exact: true }).first().click();

    await gotoReady(coordinator, absencePage);
    await coordinator.getByRole('button', { name: 'Register absence', exact: true }).click();
    const chooser = coordinator.locator('[data-form-dialog="ChooseAbsenceStaffDialog"]');
    await chooser.locator('.rz-dropdown').filter({ has: coordinator.locator('[name="absence-staff"]') }).click();
    await coordinator.getByRole('option', { name: staffMember, exact: true }).click();
    await capture(coordinator, '08-register-choose-staff', width);
    await chooser.getByRole('button', { name: 'Continue', exact: true }).click();
    const registration = coordinator.getByRole('dialog').last();
    await expect(registration.locator('[data-absence-note]')).toContainText('it is approved at once');
    await expect(registration.locator('button[data-transition]')).toHaveCount(1);
    await capture(coordinator, '09-register-absence', width);
    await takeStep(coordinator, registration, 'absence-register');
    const registered = coordinator.getByRole('dialog').last();
    await expect(pill(registered)).toHaveAttribute('data-state', 'Approved');
    await registered.getByRole('button', { name: 'Close', exact: true }).first().click();

    await gotoReady(coordinator, '/staffing/staff');
    await coordinator.locator('tr.list-row').filter({ hasText: staffMember }).first().click();
    const user = coordinator.getByRole('dialog', { name: staffMember, exact: true });
    await user.getByRole('tab', { name: 'Absence' }).click();
    await expect(user.locator('tr.list-row').filter({ hasText: 'Rejected' }).first()).toBeVisible();
    await expect(user.locator('tr.list-row').filter({ hasText: /\bApproved\b/ })).toHaveCount(2);
    await capture(coordinator, '10-staff-absence-view', width);
    await user.getByRole('button', { name: 'Close', exact: true }).first().click();

    await gotoReady(page, absencePage);
    const bell = await openBell(page);
    await expect(bell.locator('.notice-row').filter({ hasText: 'Your planned leave is approved' }).first()).toBeVisible();
    await expect(bell.locator('.notice-row').filter({ hasText: 'Absence registered for you' }).first()).toBeVisible();
    await capture(page, '11-staff-bell', width);
    await bell.locator('.notice-row').filter({ hasText: 'Your sick leave was rejected' }).first().click();
    const rejected = page.getByRole('dialog').last();
    await expect(pill(rejected)).toHaveAttribute('data-state', 'Rejected');
    await expect(rejected.locator('[data-absence-note]')).toHaveCount(0);
    await expect(rejected.locator('.state-entity-dialog-side')).toContainText(reason);
    await expect(rejected.locator('.state-entity-dialog-side textarea')).toHaveCount(0);
    await capture(page, '12-rejected-for-the-staff', width);
    await rejected.getByRole('button', { name: 'Close', exact: true }).first().click();

    await clearAbsences(page);
  } finally {
    await coordinatorContext.close();
  }
});

test(`a staff member asks about their absence, the coordinator answers, handles it and approves`, { tag: '@staffing' }, async ({ page, browser }) => {
  test.setTimeout(420_000);
  const viewport = { width, height: 800 };
  await page.setViewportSize(viewport);
  const state = readFoundationState();
  const staffPersona = persona(personaKeys.staff, state);
  const staffMember = personaName(staffPersona);
  const run = Date.now().toString(36);
  const question = `Can I leave at noon the day before? ${run}`;
  const answer = `Yes, if the handover is done. ${run}`;
  const ask = `Is it one day or two? ${run}`;
  const again = `And may I take the laptop home? ${run}`;

  const coordinatorContext = await browser.newContext({ viewport });
  try {
    const coordinator = await coordinatorContext.newPage();
    await loginAs(coordinator, persona(personaKeys.coordinator, state));
    await loginAs(page, staffPersona);
    await clearAbsences(page);

    const leave = await report(page, 'Planned leave', width);
    await expect(leave.locator('.state-entity-dialog-side')).toHaveCount(0);
    await takeStep(page, leave, 'absence-ask-coordinator');
    const write = page.locator('.form-dialog').filter({ hasText: 'Write a question to the coordinator' });
    await expect(write).toContainText('The coordinator sees your question on the absence.');
    await write.getByRole('button', { name: 'Write a question', exact: true }).click();
    await expect(write.locator('.prompt-dialog__required')).toHaveText('Write the question first, or the coordinator only sees that something happened.');
    await capture(page, '13-write-a-question-prompt', width);
    await write.getByLabel('What do you want to ask about?').fill(question);
    await write.getByRole('button', { name: 'Write a question', exact: true }).click();
    await expect(pill(leave)).toHaveAttribute('data-state', 'StaffQuestion');
    await expect(leave.locator('.state-entity-dialog-side')).toContainText(question);
    await expect(leave.locator('.state-entity-dialog-side textarea')).toHaveCount(0);
    await expect(leave.locator('[data-thread-closed]')).toHaveCount(0);
    await expect(leave.locator('button[data-transition="absence-ask-coordinator-again"]')).toBeVisible();
    await capture(page, '14-question-to-the-coordinator-for-the-staff', width);
    await leave.getByRole('button', { name: 'Close', exact: true }).first().click();

    await gotoReady(coordinator, absencePage);
    const coordinatorBell = await openBell(coordinator);
    const told = coordinatorBell.locator('.notice-row').filter({ hasText: 'New question about an absence' }).filter({ hasText: question });
    await expect(told.first()).toBeVisible();
    await told.first().click();
    const asked = coordinator.getByRole('dialog').last();
    await expect(pill(asked)).toHaveAttribute('data-state', 'StaffQuestion');
    await expect(asked.locator('button[data-transition="absence-approve"]')).toHaveCount(0);
    await expect(asked.locator('button[data-transition="absence-question-handled"]')).toBeVisible();
    await capture(coordinator, '15-question-to-the-coordinator', width);
    await takeStep(coordinator, asked, 'absence-answer-staff');
    const reply = coordinator.locator('.form-dialog').filter({ hasText: 'The staff member sees your answer on the absence.' });
    await reply.getByLabel('Your answer').fill(answer);
    await capture(coordinator, '16-answer-the-staff-member-prompt', width);
    await reply.getByRole('button', { name: 'Answer the staff member', exact: true }).click();
    await expect(asked.locator('.state-entity-dialog-side')).toContainText(answer);
    await expect(pill(asked)).toHaveAttribute('data-state', 'Requested');

    const coordinatorBadge = coordinator.locator('[data-notice-bell]');
    const unreadBefore = Number(await coordinatorBadge.getAttribute('data-unread-count'));
    await gotoReady(page, `${absencePage}?status=requested`);
    await page.locator('tr.list-row').first().click();
    const waiting = page.getByRole('dialog').last();
    await expect(pill(waiting)).toHaveAttribute('data-state', 'Requested');
    await takeStep(page, waiting, 'absence-ask-coordinator');
    const another = page.locator('.form-dialog').filter({ hasText: 'Write a question to the coordinator' });
    await another.getByLabel('What do you want to ask about?').fill(again);
    await another.getByRole('button', { name: 'Write a question', exact: true }).click();
    await expect(pill(waiting)).toHaveAttribute('data-state', 'StaffQuestion');
    await expect(waiting.locator('.state-entity-dialog-side')).toContainText(again);
    await waiting.getByRole('button', { name: 'Close', exact: true }).first().click();

    await expect(coordinatorBadge).toHaveAttribute('data-unread-count', String(unreadBefore + 1));
    await coordinator.reload();
    await waitForReady(coordinator);
    await expect(pill(asked)).toHaveAttribute('data-state', 'StaffQuestion');
    const handled = asked.locator('button[data-transition="absence-question-handled"]');
    await expect(handled).toHaveText(/Mark as handled/);
    await takeStep(coordinator, asked, 'absence-question-handled');
    await expect(pill(asked)).toHaveAttribute('data-state', 'Requested');
    await expect(coordinatorBadge).toHaveAttribute('data-unread-count', String(unreadBefore));
    await capture(coordinator, '16a-question-marked-as-handled', width);
    await takeStep(coordinator, asked, 'absence-ask-staff');
    const send = coordinator.locator('.form-dialog').filter({ hasText: 'Send a question to the staff member' });
    await send.getByLabel('What needs to be made clear?').fill(ask);
    await capture(coordinator, '17-send-a-question-prompt', width);
    await send.getByRole('button', { name: 'Send a question', exact: true }).click();
    await expect(pill(asked)).toHaveAttribute('data-state', 'Question');
    await expect(asked.locator('button[data-transition="absence-approve"]')).toHaveCount(0);
    await capture(coordinator, '18-question-to-the-staff-member', width);
    await asked.getByRole('button', { name: 'Close', exact: true }).first().click();

    await gotoReady(page, absencePage);
    const bell = await openBell(page);
    await expect(bell.locator('.notice-row').filter({ hasText: 'New answer about your absence' }).filter({ hasText: answer }).first()).toBeVisible();
    await bell.locator('.notice-row').filter({ hasText: 'New question about your absence' }).filter({ hasText: ask }).first().click();
    const mine = page.getByRole('dialog').last();
    await expect(pill(mine)).toHaveAttribute('data-state', 'Question');
    await capture(page, '19-question-to-the-staff-member-for-the-staff', width);
    await takeStep(page, mine, 'absence-answer');
    const answerPrompt = page.locator('.form-dialog').filter({ hasText: 'Answer the coordinator' });
    await expect(answerPrompt).toContainText('You can send it without writing anything.');
    await capture(page, '20-answer-the-coordinator-prompt', width);
    await answerPrompt.getByRole('button', { name: 'Answer', exact: true }).click();
    await expect(pill(mine)).toHaveAttribute('data-state', 'Requested');
    await mine.getByRole('button', { name: 'Close', exact: true }).first().click();

    await gotoReady(coordinator, `${absencePage}?status=requested&kind=plannedleave`);
    await coordinator.locator('tr.list-row').filter({ hasText: staffMember }).first().click();
    const decided = coordinator.getByRole('dialog').last();
    await expect(pill(decided)).toHaveAttribute('data-state', 'Requested');
    await expect(decided.locator('.state-entity-dialog-side')).toContainText(ask);
    await takeStep(coordinator, decided, 'absence-approve');
    await expect(pill(decided)).toHaveAttribute('data-state', 'Approved');
    await decided.getByRole('button', { name: 'Close', exact: true }).first().click();

    await clearAbsences(page);
  } finally {
    await coordinatorContext.close();
  }
});
