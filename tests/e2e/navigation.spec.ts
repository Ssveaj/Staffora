import type { Page } from '@playwright/test';
import { expectNoSeriousViolations } from './lib/accessibility';
import { persona, personaKeys, readFoundationState } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { accountTrigger, accountTriggerName, endImpersonation, gotoReady, impersonate, loginAs, openAccountPanel, waitForSettled } from './lib/session';
import { shot } from './lib/shots';
import { pageScroll, timelineReady } from './lib/workspace';

const widths = [1500, 1280, 390] as const;
const heightOf = (width: number) => (width === 390 ? 844 : 900);

const navigation = (page: Page) => page.getByRole('navigation', { name: 'Main navigation' });
const breadcrumb = (page: Page) => page.getByRole('navigation', { name: 'Breadcrumb' });
const sidebar = (page: Page) => page.locator('#app-sidebar');
const openMenu = (page: Page) => page.getByRole('button', { name: 'Open menu', exact: true });
const closeMenu = (page: Page) => page.getByRole('button', { name: 'Close menu', exact: true });
const collapseMenu = (page: Page) => page.getByRole('button', { name: 'Collapse menu', exact: true });
const expandMenu = (page: Page) => page.getByRole('button', { name: 'Expand menu', exact: true });
const accountDetails = (page: Page) => page.locator('details.account-menu__profile');

async function links(page: Page): Promise<string[]> {
  return page.locator('[data-app-nav] a.app-nav__link').evaluateAll((anchors) => anchors.map((anchor) => anchor.getAttribute('href') ?? ''));
}

async function steps(page: Page): Promise<string[]> {
  return breadcrumb(page).locator('li').evaluateAll((items) =>
    items.map((item) => (item.querySelector('h1') ?? item).textContent?.trim() ?? '').filter((text) => text.length > 0));
}

async function shootWidths(page: Page, name: string, prepare?: (width: number) => Promise<void>): Promise<void> {
  for (const width of widths) {
    await page.setViewportSize({ width, height: heightOf(width) });
    await waitForSettled(page);
    if (prepare) await prepare(width);
    await shot(page, 'navigation', `${name}-${width}`);
    if (prepare) await page.keyboard.press('Escape');
  }
  await page.setViewportSize({ width: 1280, height: 900 });
}

const withAccountPanel = (page: Page) => async () => {
  await openAccountPanel(page);
};

const sidebarWidth = async (page: Page) => (await sidebar(page).boundingBox())?.width ?? 0;

test.use({ viewport: { width: 1280, height: 900 } });

test('the sidebar follows the session, folds to its rail, and system pages keep it', { tag: '@shell' }, async ({ page }) => {
  const state = readFoundationState();
  await loginAs(page, persona(personaKeys.platformOwner, state));

  await gotoReady(page, '/superadmin/overview');
  await expect(page.locator('.app-shell--platform')).toHaveCount(1);
  expect((await links(page)).every((href) => href.startsWith('/superadmin/'))).toBe(true);
  await expect(navigation(page).getByRole('list', { name: 'Platform', exact: true })).toBeVisible();
  await expect(navigation(page).getByRole('link', { name: 'Platform health', exact: true })).toBeVisible();
  expect(await steps(page)).toEqual(['Platform', 'Overview']);
  await expect(breadcrumb(page).locator('[aria-current="page"]')).toHaveText('Overview');
  const overview = navigation(page).getByRole('link', { name: 'Overview', exact: true });
  await expect(overview).toHaveAttribute('aria-current', 'page');
  await expect(overview).toHaveClass(/\bactive\b/);
  await expect(page.getByRole('banner'), 'the top row is the one banner of the page').toHaveCount(1);
  await expect(page.getByRole('banner').getByRole('navigation', { name: 'Breadcrumb' })).toBeVisible();
  expect(Math.round(await sidebarWidth(page)), 'the sidebar unfolded').toBe(248);
  await shootWidths(page, 'platform-console');
  await page.setViewportSize({ width: 1440, height: 900 });
  await waitForSettled(page);
  await shot(page, 'shell', 'platform-overview-expanded-1440');

  await test.step('the sidebar folds to its rail, keeps every link named, and stays folded across pages and a reload', async () => {
    await collapseMenu(page).click();
    await expect(page.locator('html')).toHaveAttribute('data-sidebar', 'collapsed');
    await expect(expandMenu(page), 'the focus moves to the button that took the pressed one\'s place').toBeFocused();
    await expect(collapseMenu(page)).toBeHidden();
    await expect.poll(() => sidebarWidth(page)).toBeLessThanOrEqual(64);
    await expect(sidebar(page).locator('.app-nav__label').first()).not.toBeInViewport();
    await expect(sidebar(page).locator('.app-nav__heading').first()).toBeHidden();
    await expect(navigation(page).getByRole('link', { name: 'Tenants', exact: true })).toHaveAttribute('title', 'Tenants');
    await expect(navigation(page).getByRole('link', { name: 'Tenants', exact: true })).toBeInViewport();
    await expectNoSeriousViolations(page);
    await waitForSettled(page);
    await shot(page, 'shell', 'platform-overview-collapsed-1440');

    await navigation(page).getByRole('link', { name: 'Tenants', exact: true }).click();
    await expect(page).toHaveURL(/\/superadmin\/tenants/);
    await expect.poll(() => steps(page)).toEqual(['Platform', 'Tenants']);
    await expect(page.locator('html')).toHaveAttribute('data-sidebar', 'collapsed');
    const served = await page.request.get('/superadmin/overview');
    expect(await served.text(), 'the server marks the document folded').toMatch(/<html[^>]*data-sidebar="collapsed"/);
    await page.reload();
    await expect(page.locator('html')).toHaveAttribute('data-sidebar', 'collapsed');
    await gotoReady(page, '/superadmin/tenants');
    expect(await sidebarWidth(page)).toBeLessThanOrEqual(64);

    await expandMenu(page).click();
    await expect(page.locator('html')).not.toHaveAttribute('data-sidebar', 'collapsed');
    await expect(collapseMenu(page)).toBeFocused();
    await expect.poll(() => sidebarWidth(page)).toBe(248);
    await expect(sidebar(page).locator('.app-nav__heading').first()).toBeVisible();
    expect(await (await page.request.get('/superadmin/overview')).text()).not.toMatch(/<html[^>]*data-sidebar=/);
    await page.setViewportSize({ width: 1280, height: 900 });
  });

  await test.step('the skip link is the first stop and moves the focus to the page', async () => {
    await gotoReady(page, '/superadmin/overview');
    await page.locator('body').evaluate((body) => {
      body.tabIndex = -1;
      body.focus();
      body.removeAttribute('tabindex');
    });
    await page.keyboard.press('Tab');
    const skip = page.getByRole('link', { name: 'Skip to content' });
    await expect(skip).toBeFocused();
    await expect(skip).toBeInViewport();
    await page.keyboard.press('Enter');
    await expect(page.locator('main#main-content')).toBeFocused();
  });

  await test.step('the account panel closes on Escape and on a click outside it', async () => {
    const trigger = accountTrigger(page);
    await expect(trigger).toHaveAccessibleName(accountTriggerName(/.+/, 'Platform owner'));
    await trigger.click();
    await expect(accountDetails(page)).toHaveJSProperty('open', true);
    await expect(page.getByRole('button', { name: 'Sign out', exact: true })).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(accountDetails(page)).toHaveJSProperty('open', false);
    await expect(trigger).toBeFocused();

    await trigger.click();
    await expect(accountDetails(page)).toHaveJSProperty('open', true);
    await page.getByRole('heading', { level: 1 }).click();
    await expect(accountDetails(page)).toHaveJSProperty('open', false);
    await shootWidths(page, 'platform-account-menu-open', withAccountPanel(page));
  });

  await test.step('on a phone the sidebar is a drawer over an inert page, closed by Escape, its button and the scrim', async () => {
    await page.setViewportSize({ width: 390, height: 844 });
    await gotoReady(page, '/superadmin/overview');
    await expect(openMenu(page)).toBeVisible();
    await expect(openMenu(page)).toHaveAttribute('aria-expanded', 'false');
    await expect(navigation(page).getByRole('link', { name: 'Tenants', exact: true }), 'the links wait in the closed drawer').toBeHidden();
    await expect(collapseMenu(page), 'a phone has no rail').toBeHidden();

    await openMenu(page).click();
    await expect(openMenu(page)).toHaveAttribute('aria-expanded', 'true');
    await expect(navigation(page).getByRole('link', { name: 'Tenants', exact: true })).toBeInViewport();
    await expect(navigation(page).getByRole('link', { name: 'Platform health', exact: true })).toBeInViewport();
    await expect(navigation(page).getByRole('link', { name: 'Overview', exact: true }), 'the focus moves into the drawer').toBeFocused();
    await expect(page.locator('[data-app-content]')).toHaveJSProperty('inert', true);
    await waitForSettled(page);
    await expectNoSeriousViolations(page);
    await shot(page, 'navigation', 'platform-phone-menu-open-390');
    await shot(page, 'shell', 'phone-drawer-open-390');

    await page.keyboard.press('Escape');
    await expect(openMenu(page)).toHaveAttribute('aria-expanded', 'false');
    await expect(openMenu(page)).toBeFocused();
    await expect(page.locator('[data-app-content]')).toHaveJSProperty('inert', false);
    await expect(navigation(page).getByRole('link', { name: 'Tenants', exact: true })).toBeHidden();

    await openMenu(page).click();
    await closeMenu(page).click();
    await expect(openMenu(page)).toHaveAttribute('aria-expanded', 'false');
    await expect(openMenu(page)).toBeFocused();

    await openMenu(page).click();
    await page.mouse.click(370, 760);
    await expect(openMenu(page)).toHaveAttribute('aria-expanded', 'false');
    await expect(page).toHaveURL(/\/superadmin\/overview$/);
    await page.setViewportSize({ width: 1280, height: 900 });
  });

  await gotoReady(page, '/no-such-page');
  await expect(page.getByRole('heading', { name: 'Not found', level: 1 })).toBeVisible();
  expect(await links(page)).toContain('/superadmin/tenants');
  await shot(page, 'navigation', 'not-found-platform-1280');

  await impersonate(page, state.impersonationTarget);
  await gotoReady(page, '/me/overview');
  expect(await links(page)).toEqual(['/me/overview']);
  expect(await steps(page)).toEqual(['My work', 'My overview']);
  await expect(accountTrigger(page)).toHaveAttribute('data-impersonating', 'true');
  await expect(accountTrigger(page)).toHaveAccessibleName(accountTriggerName(state.impersonationTarget.email, 'Impersonating'));
  await expect(page.locator('.app-shell--platform')).toHaveCount(0);
  await shootWidths(page, 'impersonating');
  await shootWidths(page, 'impersonating-account-menu-open', async () => {
    const panel = await openAccountPanel(page);
    await expect(panel.locator('.account-menu__running-as')).toContainText(state.impersonationTarget.email);
    await expect(panel.getByRole('button', { name: 'End impersonation', exact: true })).toBeVisible();
    await expect(panel.getByRole('link', { name: 'My profile' }), 'no profile link in the account panel while impersonating').toHaveCount(0);
  });

  await page.setViewportSize({ width: 390, height: 844 });
  await openMenu(page).click();
  await navigation(page).getByRole('link', { name: 'My overview', exact: true }).click();
  await expect(page).toHaveURL(/\/me\/overview$/);
  await expect(openMenu(page)).toHaveAttribute('aria-expanded', 'false');
  await expect(page.locator('[data-app-content]')).toHaveJSProperty('inert', false);
  await waitForSettled(page);
  await expectNoSeriousViolations(page);
  await shot(page, 'navigation', 'overview-390');
  await page.setViewportSize({ width: 1280, height: 900 });

  await gotoReady(page, '/no-such-page');
  await expect(page.getByRole('heading', { name: 'Not found', level: 1 })).toBeVisible();
  expect(await links(page)).toEqual(['/me/overview']);
  await shot(page, 'navigation', 'not-found-tenant-1280');

  await endImpersonation(page);
  await expect(page).toHaveURL(/\/superadmin\/overview/);
  await expect(page.locator('.app-shell--platform')).toHaveCount(1);
  await expect(accountTrigger(page)).not.toHaveAttribute('data-impersonating', 'true');
});

test('a tenant user\'s sidebar marks the current page, and the workspace and the lists fill the content beside it', { tag: '@shell' }, async ({ page }) => {
  test.setTimeout(120_000);
  const state = readFoundationState();
  await loginAs(page, persona(personaKeys.coordinator, state));
  await gotoReady(page, '/coordination');

  const current = navigation(page).locator('a.app-nav__link.active');
  await expect(current).toHaveCount(1);
  await expect(current).toHaveAccessibleName('Coordination');
  await expect(current).toHaveAttribute('aria-current', 'page');
  expect(await steps(page)).toEqual(['Staffing', 'Coordination']);
  await expect(accountTrigger(page)).not.toHaveAttribute('data-impersonating', 'true');
  await shootWidths(page, 'tenant-home');
  await shootWidths(page, 'tenant-account-menu-open', withAccountPanel(page));
  await shootWidths(page, 'tenant-menu-open', async (width) => {
    if (width === 390) {
      await openMenu(page).click();
      await expect(navigation(page).getByRole('link', { name: 'Coordination', exact: true })).toBeInViewport();
    } else {
      await expect(navigation(page).getByRole('link', { name: 'Coordination', exact: true })).toBeVisible();
    }
    await expectNoSeriousViolations(page);
  });

  for (const width of [1500, 1440, 1280]) {
    await page.setViewportSize({ width, height: 900 });
    await gotoReady(page, '/coordination');
    await timelineReady(page);
    await waitForSettled(page);
    expect(await pageScroll(page), `no page scroll at ${width} px`).toEqual({ document: 0, body: 0 });
    const workspace = await page.locator('.workspace').boundingBox();
    const bar = await page.locator('[data-app-topbar]').boundingBox();
    expect(Math.round((workspace?.y ?? 0) - ((bar?.y ?? 0) + (bar?.height ?? 0))), 'the workspace starts under the top row').toBe(0);
    expect(Math.round((workspace?.y ?? 0) + (workspace?.height ?? 0)), 'and ends at the window\'s foot').toBe(900);
    if (width === 1440) await shot(page, 'shell', 'coordination-1440');
  }

  await page.setViewportSize({ width: 1440, height: 900 });
  await gotoReady(page, '/staffing/jobs');
  expect(await steps(page)).toEqual(['Staffing', 'Jobs']);
  await expect(breadcrumb(page).getByRole('link', { name: 'Staffing', exact: true })).toHaveAttribute('href', '/staffing/overview');
  await expect(navigation(page).getByRole('link', { name: 'Jobs', exact: true })).toHaveAttribute('aria-current', 'page');
  await expect(breadcrumb(page).getByRole('heading', { name: 'Jobs', level: 1 })).toBeVisible();
  await expect(page.getByRole('heading', { level: 1 })).toHaveCount(1);
  await expect(page.locator('#main-content .page-header__lead .page-header__actions').getByRole('button', { name: 'New job', exact: true })).toBeVisible();
  await expect(page.getByRole('banner').getByRole('button', { name: 'New job' })).toHaveCount(0);
  await waitForSettled(page);
  await shot(page, 'shell', 'jobs-1440');

  await page.context().clearCookies();
  await loginAs(page, persona(personaKeys.platformOwner, state));
  await gotoReady(page, '/superadmin/settings');
  await expect(breadcrumb(page).getByRole('heading', { name: 'Platform settings', level: 1 })).toBeVisible();
  await expect(breadcrumb(page).getByRole('button', { name: 'About this page', exact: true })).toBeVisible();
  await waitForSettled(page);
  await shot(page, 'shell', 'platform-settings-1440');

  await page.setViewportSize({ width: 390, height: 844 });
  await gotoReady(page, '/superadmin/tenants');
  const actions = page.locator('#main-content .page-header__actions');
  await expect(actions.getByRole('button').first()).toBeInViewport();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
  await waitForSettled(page);
  await shot(page, 'shell', 'tenants-actions-390');
});
