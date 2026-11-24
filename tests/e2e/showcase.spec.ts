import type { Locator, Page } from '@playwright/test';
import { persona, personaKeys, readFoundationState } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { accountTrigger, accountTriggerName, gotoReady, impersonate, loginAs, openAccountPanel } from './lib/session';
import { shot } from './lib/shots';

const viewports = [
  { width: 1280, height: 800 },
  { width: 390, height: 844 },
];

async function expectNoHorizontalOverflow(page: Page): Promise<void> {
  const overflowing = await page.evaluate(() =>
    [document.documentElement, ...Array.from(document.querySelectorAll<HTMLElement>('.app-shell .app-shell__body'))]
      .filter((element) => element.scrollWidth > element.clientWidth)
      .map((element) => `${element.tagName.toLowerCase()}.${element.className}: ${element.scrollWidth}px wide in ${element.clientWidth}px`),
  );
  expect(overflowing, 'the page scrolls sideways').toEqual([]);
}

async function unrollLayout(page: Page): Promise<void> {
  await page.addStyleTag({
    content: '.app-shell { height: auto !important; min-height: 100dvh; overflow: visible !important; } .app-shell .app-shell__body { overflow: visible !important; }',
  });
}

async function steadyOverview(page: Page): Promise<Locator[]> {
  const growing = page.locator('.tenant-rows, .act-card__recent, .legend, .timeline, .waiting');
  await growing.evaluateAll((elements) => {
    for (const element of elements as HTMLElement[]) {
      element.style.cssText += ';display:block;box-sizing:border-box;height:240px;overflow:hidden;';
    }
  });
  await page.addStyleTag({
    content: [
      '.act-card__recent-label { display: none !important; }',
      '.home-head__eyebrow, .home-head__title { display: inline-block !important; box-sizing: border-box !important; width: 22rem !important; max-width: 100% !important; overflow: hidden !important; white-space: nowrap !important; }',
      '.hero-figures { box-sizing: border-box !important; height: 140px !important; overflow: hidden !important; }',
      '.act-card > form, .act-card__none, .timeline, .today-card__none { display: block !important; box-sizing: border-box !important; height: 240px !important; margin: 16px 0 0 !important; padding: 0 !important; overflow: hidden !important; }',
    ].join('\n'),
  });

  return [
    page.locator('.home-head__eyebrow'),
    page.locator('.home-head__title'),
    page.locator('.hero-figures'),
    page.locator('.ring'),
    page.locator('.state-counts'),
    page.locator('.today-card__none, .today-card__foot, .act-card__none, .act-card > form'),
    growing,
  ];
}

async function capture(page: Page, name: string, mask: Locator[] = []): Promise<void> {
  await expectNoHorizontalOverflow(page);
  await unrollLayout(page);
  await shot(page, 'showcase', name);
  await expect.soft(page).toHaveScreenshot(`${name}.png`, { fullPage: true, mask });
}

for (const viewport of viewports) {
  test.describe(`at ${viewport.width} px`, { tag: '@visual' }, () => {
    test.use({ viewport });

    test('the key pages match their reviewed baselines', async ({ page }) => {
      const state = readFoundationState();

      await gotoReady(page, '/Account/Login');
      await capture(page, `login-${viewport.width}`);

      await loginAs(page, persona(personaKeys.platformOwner, state));
      await gotoReady(page, '/superadmin/overview');
      await expect(page.getByRole('heading', { name: 'Platform overview', level: 1 })).toBeVisible();
      await capture(page, `platform-console-${viewport.width}`, await steadyOverview(page));

      await impersonate(page, state.impersonationTarget);
      await gotoReady(page, '/me/overview');
      await expect(accountTrigger(page)).toBeInViewport();
      await expect(accountTrigger(page)).toHaveAttribute('data-impersonating', 'true');
      await expect(accountTrigger(page)).toHaveAccessibleName(accountTriggerName(state.impersonationTarget.email, 'Impersonating'));
      const panel = await openAccountPanel(page);
      await expect(panel.getByRole('button', { name: 'End impersonation', exact: true })).toBeInViewport();
      await page.keyboard.press('Escape');
      await expect(page.locator('.page-header__meta')).toContainText(state.impersonationTarget.email);
      await capture(page, `tenant-overview-${viewport.width}`);
    });
  });
}
