import type { Page } from '@playwright/test';
import { buttonContrastViolations } from './lib/accessibility';
import { persona, personaKeys, personaName, readFoundationState, type FoundationTenantUser } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { expectSettledPageAccessible, impersonate, loginAs } from './lib/session';
import { shot } from './lib/shots';

async function capture(page: Page, name: string, width: number): Promise<void> {
  await page.mouse.move(0, 0);
  await expectSettledPageAccessible(page);
  expect(await buttonContrastViolations(page)).toEqual([]);
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow, 'the page is no wider than the window').toBeLessThanOrEqual(0);
  const style = await page.addStyleTag({
    content: '.app-shell { height: auto !important; min-height: 100dvh; overflow: visible !important; } .app-shell .app-shell__body { overflow: visible !important; }',
  });
  try {
    await shot(page, 'home', `${name}-${width}`);
  } finally {
    await style.evaluate((element) => element.remove());
  }
}

async function captureWideToo(page: Page, name: string, width: number): Promise<void> {
  await capture(page, name, width);
  if (width !== 1280) return;
  await page.setViewportSize({ width: 1500, height: 1000 });
  await capture(page, name, 1500);
  await page.setViewportSize({ width, height: 800 });
}

async function expectHome(page: Page, path: RegExp, title: string | RegExp): Promise<void> {
  await expect(page).toHaveURL(path);
  await expect(page.getByRole('heading', { name: title, level: 1 })).toBeVisible();
}

async function expectBands(page: Page): Promise<void> {
  await expect(page.locator('.home-head__date')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Refresh', exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'To do', level: 2 })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Situation', level: 2 })).toBeVisible();
}

async function expectHero(page: Page, figures: number): Promise<void> {
  await expect(page.locator('.home-hero .home-head__eyebrow')).toBeVisible();
  expect(await page.locator('.home-hero [data-figure]').count()).toBeGreaterThanOrEqual(figures);
  await expect(page.locator('.home-hero [data-home-ring]')).toBeVisible();
  await expect(page.getByRole('heading', { name: 'To do', level: 2 })).toHaveCount(0);
}

async function impersonating(page: Page, target: FoundationTenantUser): Promise<void> {
  await loginAs(page, persona(personaKeys.platformOwner, readFoundationState()));
  await impersonate(page, target);
}

for (const width of [1280, 390]) {
  test.describe(`home per role (${width} px)`, { tag: '@homes' }, () => {
    test.use({ viewport: { width, height: width === 390 ? 844 : 800 } });

    test('the platform owner lands on the platform console', async ({ page }) => {
      await loginAs(page, persona(personaKeys.platformOwner, readFoundationState()), { throughForm: true });
      await expectHome(page, /\/superadmin\/overview$/, 'Platform overview');
      await expect(page.getByRole('heading', { name: 'Act as a user', level: 2 })).toBeVisible();
      await expect(page.getByRole('button', { name: 'Impersonate User' })).toHaveCount(1);
      await capture(page, '01-platform-owner', width);
    });

    test('the coordinator and an impersonated coordinator land on the staffing overview', async ({ page }) => {
      const state = readFoundationState();
      const coordinator = persona(personaKeys.coordinator, state);
      await loginAs(page, coordinator, { throughForm: true });
      await expectHome(page, /\/staffing\/overview$/, 'Staffing overview');
      await expect(page.locator('[data-coordinator-home]')).toHaveAttribute('data-coordinator-home', 'ready');
      for (const section of ['short-of-staff', 'waiting', 'today']) {
        await expect(page.locator(`[data-coordinator-section="${section}"]`)).toBeVisible();
      }
      await expectHero(page, 4);
      await captureWideToo(page, '02-coordinator', width);

      await page.context().clearCookies();
      await loginAs(page, persona(personaKeys.platformOwner, state));
      await impersonate(page, { tenant: state.tenantAdministrator.tenant, name: personaName(coordinator), email: coordinator.email });
      await expectHome(page, /\/staffing\/overview$/, 'Staffing overview');
      await expect(page.locator('[data-coordinator-home]')).toHaveAttribute('data-coordinator-home', 'ready');
    });

    for (const key of [personaKeys.staff, personaKeys.secondStaff]) {
      test(`the staff persona "${key}" lands on My work`, { tag: width === 1280 && key === personaKeys.staff ? '@smoke' : [] }, async ({ page }) => {
        await loginAs(page, persona(key, readFoundationState()), { throughForm: true });
        await expectHome(page, /\/me\/work$/, 'My work');
        await expect(page.locator('[data-my-work]')).toHaveAttribute('data-my-work', 'ready');
        for (const section of ['requests', 'next', 'week', 'absence']) {
          await expect(page.locator(`[data-my-work-section="${section}"]`)).toBeVisible();
        }
        await expect(page.locator('[data-absence-status]')).toBeVisible();
        await expect(page.locator('.week-strip > li')).toHaveCount(7);
        await expectHero(page, 4);
        await captureWideToo(page, `03-${key}`, width);
      });
    }

    test('a platform owner impersonating the administrator lands on the administration overview', async ({ page }) => {
      const state = readFoundationState();
      await impersonating(page, state.tenantAdministrator);
      await expectHome(page, /\/tenant\/management$/, 'Organization');
      await expect(page.locator('[data-admin-home]')).toHaveAttribute('data-admin-home', 'ready');
      for (const section of ['short-of-staff', 'people', 'customers']) {
        await expect(page.locator(`[data-admin-section="${section}"]`)).toBeVisible();
      }
      await expect(page.locator('[data-admin-section="setup"]')).toHaveCount(0);
      await expect(page.locator('[data-setup-checklist]')).toHaveCount(0);
      const noPattern = page.locator('[data-admin-section="people"] [data-people="no-pattern"]');
      await expect(noPattern).toHaveAttribute('href', '/staffing/staff?pattern=none');
      await expect(noPattern).toHaveText(/without a work pattern|has a work pattern/);
      await expectHero(page, 4);
      await captureWideToo(page, '04-administrator', width);
    });

    test('the customer administrator lands on the portal\'s home', async ({ page }) => {
      await loginAs(page, persona(personaKeys.customerAdmin, readFoundationState()), { throughForm: true });
      await expectHome(page, /\/portal$/, 'Your portal');
      await expect(page.locator('[data-portal-home]')).toHaveAttribute('data-portal-home', 'ready');
      await expectBands(page);
      await capture(page, '06-customer-admin', width);
    });

    test('a platform owner impersonating a user without a home of a role lands on their own overview', async ({ page }) => {
      await impersonating(page, readFoundationState().impersonationTarget);
      await expectHome(page, /\/me\/overview$/, 'My overview');
      await capture(page, '05-no-role', width);
    });
  });
}
