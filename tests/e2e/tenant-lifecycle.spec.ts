import type { Browser, Locator, Page } from '@playwright/test';
import { persona, personaKeys, readFoundationState, type FoundationPersona } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { openTenantDialog } from './lib/platform-console';
import { accountTrigger, accountTriggerName, endImpersonation, enterSecret, expectSettledPageAccessible, gotoReady, impersonate, loginAs, waitForReady } from './lib/session';
import { shot } from './lib/shots';

const usersPage = '/tenant/management/users';

const notActive = "Your organization's account is not active, so nobody in it can sign in.";

async function expectState(dialog: Locator, state: string): Promise<void> {
  await expect(dialog.locator('.state-pill__button')).toHaveAttribute('data-state', state);
  await expect(dialog.locator('.state-entity-dialog-footer__bar')).toHaveAttribute('aria-busy', 'false');
}

async function capture(page: Page, name: string): Promise<void> {
  await expectSettledPageAccessible(page);
  await shot(page, 'tenant', `lifecycle-${name}`, [page.locator('.state-entity-dialog-footer__notice')]);
}

async function captureBothWidths(page: Page, name: string): Promise<void> {
  for (const width of [390, 1280]) {
    await page.setViewportSize({ width, height: width === 390 ? 844 : 800 });
    await capture(page, `${name}-${width}`);
  }
}

async function submitSignIn(page: Page, email: string, password: string): Promise<void> {
  await page.getByLabel('Email').fill(email);
  await enterSecret(page.getByLabel('Password'), password);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
}

async function moveTenant(page: Page, name: string, key: string, label: string, state: string, confirm: boolean): Promise<void> {
  await openTenantDialog(page, name);
  const dialog = page.getByRole('dialog', { name, exact: true });
  await expect(dialog.locator('.state-entity-dialog-footer__bar')).toHaveAttribute('aria-busy', 'false');
  await dialog.locator(`.state-entity-dialog-footer button[data-transition="${key}"]`).click();
  if (confirm) {
    const question = page.locator('.form-dialog').filter({ has: page.locator('.dialog-notice__message') });
    await expect(question).toBeVisible();
    await question.locator('.dialog-notice__buttons button').filter({ hasText: label }).click();
    await expect(question).toBeHidden();
  }

  await expectState(dialog, state);
  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
}

async function tenantState(page: Page, name: string): Promise<string | null> {
  await openTenantDialog(page, name);
  const dialog = page.getByRole('dialog', { name, exact: true });
  await expect(dialog.locator('.state-entity-dialog-footer__bar')).toHaveAttribute('aria-busy', 'false');
  const state = await dialog.locator('.state-pill__button').getAttribute('data-state');
  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
  return state;
}

async function ensureActive(page: Page, name: string, failure: unknown): Promise<void> {
  try {
    const state = await tenantState(page, name);
    if (state === 'Active') return;
    if (state === 'PendingDeletion') await moveTenant(page, name, 'tenant-cancel-deletion', 'Cancel deletion', 'Suspended', false);
    if (state === 'Suspended' || state === 'PendingDeletion') {
      await moveTenant(page, name, 'tenant-reactivate', 'Reactivate', 'Active', false);
      return;
    }
    throw new Error(`it is ${state ?? 'in no state the dialog shows'}, which the console cannot make Active`);
  } catch (error) {
    const first = failure === undefined ? '' : ` The journey had failed first: ${failure instanceof Error ? failure.message : String(failure)}`;
    throw new Error(`${name} was left not Active by the lifecycle journey and could not be reactivated: ${error instanceof Error ? error.message : String(error)}. Reactivate it from the platform console before the next run.${first}`);
  }
}

async function pageOfItsOwn(browser: Browser, width: number): Promise<Page> {
  const context = await browser.newContext({ viewport: { width, height: 800 } });
  return context.newPage();
}

test('a suspended tenant refuses its person at sign-in and ends a live impersonation, and reactivation restores both', { tag: '@platform' }, async ({ page, browser }) => {
  test.setTimeout(240_000);
  await page.setViewportSize({ width: 1280, height: 800 });
  const state = readFoundationState();
  const owner: FoundationPersona = persona(personaKeys.platformOwner, state);
  const tenant = state.tenantAdministrator.tenant;
  const target = state.impersonationTarget;

  await loginAs(page, owner);
  await impersonate(page, state.tenantAdministrator);
  await gotoReady(page, usersPage);
  const suffix = `${Date.now().toString(36)}${Math.floor(Math.random() * 1000)}`;
  const email = `lifecycle.${suffix}@company-a.example`;
  await page.getByRole('button', { name: 'New User', exact: true }).click();
  const created = page.getByRole('dialog', { name: 'New user', exact: true });
  await created.locator('[name="user-first-name"]').fill('Kim');
  await created.locator('[name="user-last-name"]').fill(`Lindgren ${suffix}`);
  await created.locator('[name="user-email"]').fill(email);
  await created.locator('[name="user-username"]').fill(email);
  await created.locator('button[data-transition="user-create"]').click();
  const dialog = page.getByRole('dialog', { name: `Kim Lindgren ${suffix}`, exact: true });
  await expectState(dialog, 'Active');
  const notice = (await dialog.locator('.state-entity-dialog-footer__notice').textContent()) ?? '';
  const password = /Their initial password is (\S+) —/.exec(notice)?.[1];
  expect(password, 'the dialog shows the initial password').toBeTruthy();
  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();

  await endImpersonation(page);
  await expect(page).toHaveURL(/\/superadmin\/overview/);
  await waitForReady(page);

  const person = await pageOfItsOwn(browser, 1280);
  await gotoReady(person, '/Account/Login');
  await submitSignIn(person, email, password!);
  await expect(person).not.toHaveURL(/\/Account\/Login/);
  await waitForReady(person);

  await impersonate(page, target);
  const console = await pageOfItsOwn(browser, 1280);
  await loginAs(console, owner);

  let failure: unknown;
  try {
    await moveTenant(console, tenant, 'tenant-suspend', 'Suspend', 'Suspended', true);

    await person.goto('/me/overview');
    await expect(person).toHaveURL(/\/Account\/Login/);
    await waitForReady(person);

    await submitSignIn(person, email, password!);
    await expect(person.getByRole('alert')).toContainText(notActive);
    await expect(person).toHaveURL(/\/Account\/Login/);
    await captureBothWidths(person, '01-sign-in-refused');

    await page.goto('/');
    await waitForReady(page);
    await expect(accountTrigger(page)).toHaveAttribute('data-impersonating', 'true');
    await expect(accountTrigger(page)).toContainText('Impersonation ended');
    await captureBothWidths(page, '02-impersonation-ended');

    await endImpersonation(page);
    await expect(page).toHaveURL(/\/superadmin\/overview/);
    await waitForReady(page);
    await page.getByRole('button', { name: 'Impersonate User' }).click();
    const choose = page.getByRole('dialog');
    await expect(choose.locator('[data-impersonate]').first()).toBeVisible();
    await expect(choose.locator(`[data-impersonate="${target.email}"]`)).toHaveCount(0);
    await captureBothWidths(page, '03-not-offered');
    await page.keyboard.press('Escape');
    await expect(choose).toBeHidden();
  } catch (error) {
    failure = error;
    throw error;
  } finally {
    await ensureActive(console, tenant, failure);
  }

  await gotoReady(person, '/Account/Login');
  await submitSignIn(person, email, password!);
  await expect(person).not.toHaveURL(/\/Account\/Login/);
  await waitForReady(person);
  await capture(person, '04-signed-in-again');
  await impersonate(page, target);
  await expect(accountTrigger(page)).toHaveAttribute('data-impersonating', 'true');
  await expect(accountTrigger(page)).toHaveAccessibleName(accountTriggerName(target.email, 'Impersonating'));

  await person.context().close();
  await console.context().close();
});
