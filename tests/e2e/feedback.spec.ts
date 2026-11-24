import type { Page } from '@playwright/test';
import { controlCircuit } from './lib/circuit';
import { persona, personaKeys, readFoundationState } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { expectNoSeriousViolations } from './lib/accessibility';
import { gotoReady, loginAs } from './lib/session';
import { shot } from './lib/shots';

test.use({
  expectedConsoleErrors: /WebSocket|negotiat|Failed to fetch|Failed to start the connection|Connection disconnected|ERR_CONNECTION_REFUSED|ERR_FAILED/i,
});

async function pageIsAt(page: Page, x: number, y: number): Promise<boolean> {
  return page.evaluate(([px, py]) => {
    const element = document.elementFromPoint(px, py);
    return element !== null && element.closest('#components-reconnect-modal') === null;
  }, [x, y]);
}

for (const width of [1500, 390]) {
  test(`a dropped circuit shows the reconnect banner in a corner, never over the page (${width})`, { tag: '@shell' }, async ({ page }) => {
    test.setTimeout(120_000);
    const viewport = { width, height: width === 390 ? 844 : 900 };
    await page.setViewportSize(viewport);
    const circuit = await controlCircuit(page);

    await loginAs(page, persona(personaKeys.platformOwner, readFoundationState()));
    await gotoReady(page, '/superadmin/overview');
    const banner = page.locator('#components-reconnect-modal');
    await expect(banner).toHaveAttribute('role', 'status');
    await expect(banner.locator('.reconnect-banner__text:visible')).toHaveCount(0);

    await circuit.drop();
    await expect(banner).toHaveClass(/components-reconnect-show/);
    await expect(banner.locator('.reconnect-banner__text:visible')).toHaveText(/Connection lost\./);
    await expect(banner.locator('.reconnect-banner__spinner')).toBeVisible();
    const box = await banner.boundingBox();
    expect(box, 'the banner has a box').not.toBeNull();
    expect(box!.x, 'the banner starts at the left gutter').toBeLessThanOrEqual(24);
    expect(box!.y + box!.height, 'the banner ends at the bottom gutter').toBeGreaterThanOrEqual(viewport.height - 24);
    expect(box!.height, 'the banner is one line high').toBeLessThanOrEqual(64);
    expect(box!.width, 'the banner is a card, not a band across the page').toBeLessThanOrEqual(Math.min(26 * 16, width - 32) + 1);

    expect(await banner.evaluate((element) => element.matches(':modal'))).toBe(false);
    await expect(page.locator('[inert]')).toHaveCount(0);
    expect(await pageIsAt(page, viewport.width / 2, viewport.height / 2), 'the middle of the window is the page').toBe(true);
    const heading = await page.getByRole('heading', { level: 1 }).first().boundingBox();
    expect(await pageIsAt(page, heading!.x + 4, heading!.y + heading!.height / 2), 'the heading is in reach').toBe(true);
    await expectNoSeriousViolations(page);
    await shot(page, 'feedback', `reconnect-banner-${width}`);

    circuit.restore();
    await expect(banner).toHaveClass(/components-reconnect-hide/, { timeout: 45_000 });
    await expect(banner.locator('.reconnect-banner__text:visible')).toHaveCount(0);
    await page.getByRole('button', { name: 'Impersonate User' }).first().click();
    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible();
    await dialog.getByRole('button', { name: 'Close', exact: true }).click();
    await expect(dialog).toBeHidden();
  });
}
