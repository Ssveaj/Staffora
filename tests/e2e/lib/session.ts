import { expect, type Cookie, type Locator, type Page } from '@playwright/test';
import { expectNoSeriousViolations } from './accessibility';
import type { FoundationPersona, FoundationTenantUser } from './foundation-state';

const readyLandmark = '[data-session="ready"]';

export const errorBanner = '#blazor-error-ui';

export async function waitForReady(page: Page): Promise<void> {
  await page.locator(readyLandmark).waitFor({ state: 'attached' });
  await expect(page.locator(errorBanner), 'Blazor\'s unhandled-error banner is showing').toBeHidden();
}

const busyIndicators = '[role="progressbar"]:visible, .rz-progressbar-circular:visible, .ui-spinner:visible, .session-loader:visible, .boot-loader:visible';

const quietMs = 300;
const settleTimeoutMs = 10_000;

export async function waitForSettled(page: Page): Promise<void> {
  await expect(page.locator(busyIndicators), 'the page is still loading').toHaveCount(0, { timeout: settleTimeoutMs });
  await page.evaluate(
    ({ quiet, limit }) =>
      new Promise<void>((resolve, reject) => {
        const giveUp = setTimeout(() => {
          observer.disconnect();
          reject(new Error(`the page kept changing for ${limit} ms`));
        }, limit);
        let timer = setTimeout(done, quiet);
        const observer = new MutationObserver(() => {
          clearTimeout(timer);
          timer = setTimeout(done, quiet);
        });
        function done() {
          clearTimeout(giveUp);
          observer.disconnect();
          void document.fonts.ready.then(() => resolve());
        }
        observer.observe(document.documentElement, { subtree: true, childList: true, attributes: true, characterData: true });
      }),
    { quiet: quietMs, limit: settleTimeoutMs },
  );
}

export async function expectSettledPageAccessible(page: Page): Promise<void> {
  await waitForSettled(page);
  await expectNoSeriousViolations(page);
}

export async function gotoReady(page: Page, path: string): Promise<void> {
  await page.goto(path, { waitUntil: 'domcontentloaded' });
  await waitForReady(page);
  await expectSettledPageAccessible(page);
}

const authCookie = /^\.AspNetCore\.Identity\.Application/;

const signedIn = new Map<string, Cookie[]>();

export async function loginAs(page: Page, persona: FoundationPersona, options: { throughForm?: boolean } = {}): Promise<void> {
  const onLoginPage = new URL(page.url(), 'http://localhost').pathname === '/Account/Login';
  const kept = signedIn.get(persona.email);
  if (kept && !onLoginPage && !options.throughForm) {
    await page.context().addCookies(kept);
    await page.goto(persona.key === 'platformOwner' ? '/superadmin/overview' : '/', { waitUntil: 'domcontentloaded' });
    await waitForReady(page);
    if (new URL(page.url()).pathname !== '/Account/Login') return;
    signedIn.delete(persona.email);
  }

  if (new URL(page.url(), 'http://localhost').pathname !== '/Account/Login') {
    await gotoReady(page, '/Account/Login');
  }
  await page.getByLabel('Email').fill(persona.email);
  await enterSecret(page.getByLabel('Password'), persona.password);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page).not.toHaveURL(/\/Account\/Login/);
  await waitForReady(page);

  const cookies = (await page.context().cookies()).filter((cookie) => authCookie.test(cookie.name));
  if (cookies.length > 0) signedIn.set(persona.email, cookies);
}

export const accountTrigger = (page: Page): Locator => page.locator('details.account-menu__profile > summary');

export function accountTriggerName(name: string | RegExp, context?: string): RegExp {
  const escape = (text: string) => text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const who = typeof name === 'string' ? escape(name) : name.source;
  return new RegExp(`^Account menu:\\s*${who}${context ? `\\s*,\\s*${escape(context)}` : ''}$`);
}

export async function openAccountPanel(page: Page): Promise<Locator> {
  const details = page.locator('details.account-menu__profile');
  if (!(await details.evaluate((element) => (element as HTMLDetailsElement).open))) {
    await accountTrigger(page).click();
  }
  await expect(details).toHaveJSProperty('open', true);
  return page.locator('.account-menu__panel');
}

export async function endImpersonation(page: Page): Promise<void> {
  const panel = await openAccountPanel(page);
  await panel.getByRole('button', { name: 'End impersonation', exact: true }).click();
}

export async function impersonate(page: Page, target: FoundationTenantUser): Promise<void> {
  if (new URL(page.url(), 'http://localhost').pathname !== '/superadmin/overview') {
    await gotoReady(page, '/superadmin/overview');
  }
  await page.getByRole('button', { name: 'Impersonate User' }).click();
  const dialog = page.getByRole('dialog');
  await dialog.locator(`[data-impersonate="${target.email}"]`).click();
  await expect(page).not.toHaveURL(/\/superadmin\//);
  await waitForReady(page);
}

let secretHandoffs = 0;

export async function enterSecret(input: Locator, value: string): Promise<void> {
  await expect(input).toBeEditable();
  const handoff = `__stafforaSecretHandoff${++secretHandoffs}`;
  await input.page().exposeFunction(handoff, () => value);
  await input.evaluate(async (element, name) => {
    const field = element as HTMLInputElement;
    const secret = await (window as unknown as Record<string, () => Promise<string>>)[name]();
    field.focus();
    field.value = secret;
    field.dispatchEvent(new Event('input', { bubbles: true }));
    field.dispatchEvent(new Event('change', { bubbles: true }));
  }, handoff);
}

export async function editDialog(dialog: Locator): Promise<void> {
  const edit = dialog.locator('.state-entity-dialog-footer button[data-dialog-action="edit"]');
  await expect(edit).toBeVisible();
  await expect(edit).not.toHaveAttribute('aria-disabled', 'true');
  await edit.click();
  await expect(dialog.locator('.state-entity-dialog-footer button[data-dialog-action="cancel"]')).toBeVisible();
}