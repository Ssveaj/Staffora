import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import type { Page } from '@playwright/test';
import { repositoryRoot } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { countRadzenDialogTimers, nextFrames, pastRadzenDialogTimers } from './lib/frames';
import { shot } from './lib/shots';

const fixtureFolder = join(repositoryRoot, 'tests', 'e2e', 'fixtures', 'state-entity-dialog');
const fixtureAddress = '/e2e-fixtures/state-entity-dialog';

function fixture(name: string): string {
  return readFileSync(join(fixtureFolder, name), 'utf8');
}

function pageOf(body: string): string {
  return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <base href="/" />
  <title>State-entity dialog</title>
  <link rel="stylesheet" href="lib/bootstrap/dist/css/bootstrap.min.css" />
  <link rel="stylesheet" href="_content/Radzen.Blazor/css/material-base.css" />
  <link rel="stylesheet" href="_content/Radzen.Blazor/css/material.css" />
  <link rel="stylesheet" href="app.tokens.css" />
  <link rel="stylesheet" href="app.typography.css" />
  <link rel="stylesheet" href="app.css" />
  <link rel="stylesheet" href="Staffora.Blazor.styles.css" />
</head>
<body>
${body}
<script src="_content/Radzen.Blazor/Radzen.Blazor.js"></script>
</body>
</html>`;
}

async function openDialog(page: Page, options: { cardOpen: boolean; above?: string; height?: string }): Promise<void> {
  const body = fixture('dialog.html') + (options.above ? fixture(options.above) : '');
  await page.route(`**${fixtureAddress}`, (route) => route.fulfill({ status: 200, contentType: 'text/html; charset=utf-8', body: pageOf(body) }));
  await countRadzenDialogTimers(page);
  await page.goto(fixtureAddress);
  await page.addStyleTag({ content: '*, *::before, *::after { animation: none !important; transition: none !important; }' });
  await page.evaluate(() => document.fonts.ready);

  await page.addScriptTag({ type: 'module', content: "import * as module from '/scripts/state-entity-dialog.js'; window.dialogModule = module;" });
  await page.waitForFunction(() => 'dialogModule' in window);
  await page.evaluate(({ cardOpen, height, dialogs }) => {
    const w = window as unknown as Record<string, unknown>;
    const module = w.dialogModule as { holdEscape: (root: HTMLElement, pill: HTMLElement, component: object) => void };
    const root = document.querySelector<HTMLElement>('.state-pill')!;
    const pill = root.querySelector<HTMLElement>('.state-pill__button')!;
    const card = root.querySelector<HTMLElement>('.state-pill__card')!;
    const calls: string[] = [];
    let closes = 0;
    const render = (open: boolean) => {
      root.dataset.open = open ? 'true' : 'false';
      card.hidden = !open;
      pill.setAttribute('aria-expanded', open ? 'true' : 'false');
    };

    if (height) document.querySelector<HTMLElement>('.state-entity-dialog-shell')!.style.height = height;
    render(cardOpen);
    w.calls = calls;
    w.closes = () => closes;
    w.renderCard = render;
    module.holdEscape(root, pill, {
      invokeMethodAsync: (name: string) => {
        calls.push(name);
        render(false);
        return Promise.resolve();
      },
    });

    const radzen = (window as unknown as { Radzen: { openDialog: (options: object, service: object, dialog: unknown) => void } }).Radzen;
    for (let index = 0; index < dialogs; index++) {
      radzen.openDialog({ closeDialogOnEsc: true }, { invokeMethodAsync: () => { closes++; return Promise.resolve(false); } }, null);
    }
  }, { cardOpen: options.cardOpen, height: options.height, dialogs: options.above ? 2 : 1 });
  await pastRadzenDialogTimers(page);
}

async function heard(page: Page): Promise<{ calls: string[]; radzenCloses: number }> {
  return page.evaluate(() => {
    const w = window as unknown as { calls: string[]; closes: () => number };
    return { calls: [...w.calls], radzenCloses: w.closes() };
  });
}

async function reopenCard(page: Page): Promise<void> {
  await page.evaluate(() => {
    const w = window as unknown as { calls: string[]; renderCard: (open: boolean) => void };
    w.calls.length = 0;
    w.renderCard(true);
  });
}

async function lengthenBody(page: Page): Promise<void> {
  await page.evaluate(() => {
    const body = document.querySelector('.state-entity-dialog__body')!;
    for (let index = 0; index < 40; index++) {
      const field = document.createElement('p');
      field.textContent = `Field ${index + 1}, a line of a long form that wraps on a phone`;
      body.appendChild(field);
    }
  });
}

async function scrollCardToItsEnd(page: Page): Promise<void> {
  await page.evaluate(() => {
    const card = document.querySelector<HTMLElement>('.state-pill__card')!;
    card.scrollTop = card.scrollHeight;
  });
}

async function frameScrolled(page: Page): Promise<boolean> {
  return page.evaluate(() => ['.state-entity-dialog-shell', '.state-entity-dialog-shell .rz-dialog-content', '.state-entity-dialog-header']
    .some((selector) => { const element = document.querySelector(selector)!; return element.scrollTop !== 0 || element.scrollLeft !== 0; }));
}

async function showsAtItsMiddle(page: Page, selector: string): Promise<boolean> {
  return page.evaluate((selector) => {
    const element = document.querySelector(selector)!;
    const box = element.getBoundingClientRect();
    const hit = document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2);
    return !!hit && (hit === element || element.contains(hit));
  }, selector);
}

test.describe('the state pill\'s card holds Esc only for itself', { tag: '@dialogs' }, () => {
  test('Esc with the focus in the card closes the card alone, and with the card closed Esc reaches Radzen', async ({ page }) => {
    await openDialog(page, { cardOpen: true });

    await page.locator('[data-open-history]').focus();
    await page.keyboard.press('Escape');
    expect(await heard(page)).toEqual({ calls: ['CloseCardByEscape'], radzenCloses: 0 });
    await expect(page.locator('.state-pill__button')).toBeFocused();
    await expect(page.locator('.state-pill__card')).toBeHidden();

    await page.keyboard.press('Escape');
    expect(await heard(page)).toEqual({ calls: ['CloseCardByEscape'], radzenCloses: 1 });
  });

  test('Esc with the focus in the dialog itself while it is on top closes the card first', async ({ page }) => {
    await openDialog(page, { cardOpen: true });
    await page.evaluate(() => (document.querySelector('#test-parent-notes') as HTMLElement).focus({ preventScroll: true }));
    await reopenCard(page);
    await page.keyboard.press('Escape');
    expect(await heard(page)).toEqual({ calls: ['CloseCardByEscape'], radzenCloses: 0 });
    await page.keyboard.press('Escape');
    expect((await heard(page)).radzenCloses).toBe(1);
  });

  test('Esc with the focus in a dialog on top reaches Radzen and leaves the open card alone', async ({ page }) => {
    await openDialog(page, { cardOpen: true, above: 'confirm.html' });
    await page.getByRole('button', { name: 'Cancel' }).focus();
    await reopenCard(page);

    await page.keyboard.press('Escape');

    expect(await heard(page)).toEqual({ calls: [], radzenCloses: 1 });
    await expect(page.locator('.state-pill')).toHaveAttribute('data-open', 'true');
    await expect(page.getByRole('button', { name: 'Cancel' })).toBeFocused();
  });
});

test.describe('the state pill\'s card closes when the user moves on', { tag: '@dialogs' }, () => {
  test('a press or the focus outside the card closes it, and one inside does not', async ({ page }) => {
    await openDialog(page, { cardOpen: true });

    await page.locator('.state-pill__description').click();
    expect((await heard(page)).calls).toEqual([]);

    await page.locator('.state-entity-dialog-header__title').click();
    expect((await heard(page)).calls).toEqual(['CloseCardFromOutside']);
    await expect(page.locator('.state-pill__card')).toBeHidden();

    await page.evaluate(() => (window as unknown as { renderCard: (open: boolean) => void }).renderCard(true));
    await page.locator('[data-open-history]').focus();
    expect((await heard(page)).calls).toEqual(['CloseCardFromOutside']);
    await page.locator('.state-entity-dialog-header__close').focus();
    expect((await heard(page)).calls).toEqual(['CloseCardFromOutside', 'CloseCardFromOutside']);
  });

  test('the focus goes back to the pill when a dialog opened from the card closes, unless the user put it elsewhere', async ({ page }) => {
    await openDialog(page, { cardOpen: false, above: 'history.html' });
    const returnFocus = () => page.evaluate(() => {
      const module = (window as unknown as { dialogModule: { returnFocus: (pill: HTMLElement) => void } }).dialogModule;
      module.returnFocus(document.querySelector<HTMLElement>('.state-pill__button')!);
    });

    await page.locator('.form-dialog-header__close').focus();
    await returnFocus();
    await expect(page.locator('.state-pill__button')).toBeFocused();

    await page.locator('.form-dialog-header__close').focus();
    await page.evaluate(() => document.querySelectorAll('.rz-dialog-wrapper')[1].remove());
    expect(await page.evaluate(() => document.activeElement === document.body)).toBe(true);
    await returnFocus();
    await expect(page.locator('.state-pill__button')).toBeFocused();

    await page.locator('#test-parent-notes').focus();
    await returnFocus();
    await expect(page.locator('#test-parent-notes')).toBeFocused();
  });
});

for (const viewport of [{ width: 1280, height: 900 }, { width: 390, height: 844 }]) {
  test.describe(`the dialog's frame at ${viewport.width} px`, { tag: '@dialogs' }, () => {
    test.use({ viewport });

    for (const height of [undefined, '320px']) {
      test(`keeps its footer in view and scrolls its body${height ? ` with a Height of ${height}` : ''}, the open card unclipped`, async ({ page }) => {
        await openDialog(page, { cardOpen: false, height });
        await lengthenBody(page);

        for (const cardOpen of [false, true]) {
          await page.evaluate((open) => (window as unknown as { renderCard: (open: boolean) => void }).renderCard(open), cardOpen);
          await nextFrames(page);
          const frame = await page.evaluate(() => {
            const box = (selector: string) => document.querySelector(selector)!.getBoundingClientRect();
            const body = document.querySelector('.state-entity-dialog__body')!;
            return {
              viewport: window.innerHeight,
              shell: box('.state-entity-dialog-shell').toJSON() as DOMRect,
              footer: box('.state-entity-dialog-footer').toJSON() as DOMRect,
              card: box('.state-pill__card').toJSON() as DOMRect,
              bodyScrolls: body.scrollHeight > body.clientHeight,
            };
          });

          expect(frame.footer.top).toBeGreaterThanOrEqual(0);
          expect(frame.footer.bottom).toBeLessThanOrEqual(frame.viewport);
          expect(frame.footer.bottom).toBeLessThanOrEqual(frame.shell.bottom + 1);
          expect(frame.bodyScrolls).toBe(true);
          await expect(page.locator('.state-entity-dialog-footer button[data-transition="test-parent-save"]')).toBeInViewport();
          if (height) expect(Math.round(frame.shell.height)).toBe(Number.parseInt(height, 10));
          if (cardOpen) {
            expect(frame.card.bottom).toBeLessThanOrEqual(frame.viewport);
            await scrollCardToItsEnd(page);
            expect(await showsAtItsMiddle(page, '[data-open-history]')).toBe(true);
            expect(await frameScrolled(page)).toBe(false);
          }
        }

        if (height) {
          await shot(page, 'state-entity', `07-height-dialog-card-${viewport.width}`);
        }
      });
    }

    test('screenshots of the header, the card and the full history', async ({ page }) => {
      await openDialog(page, { cardOpen: false });
      await shot(page, 'state-entity', `01-header-${viewport.width}`);
      await page.evaluate(() => (window as unknown as { renderCard: (open: boolean) => void }).renderCard(true));
      await nextFrames(page);
      await shot(page, 'state-entity', `02-hover-card-${viewport.width}`);

      await openDialog(page, { cardOpen: false, above: 'history.html' });
      await shot(page, 'state-entity', `03-history-dialog-${viewport.width}`);
    });
  });
}

for (const viewport of [{ width: 844, height: 390 }, { width: 390, height: 500 }]) {
  test.describe(`the state pill's card on a short screen of ${viewport.width} by ${viewport.height}`, { tag: '@dialogs' }, () => {
    test.use({ viewport });

    test('stays within the screen and scrolls, so its links are reachable', async ({ page }) => {
      await openDialog(page, { cardOpen: true });

      const card = await page.evaluate(() => {
        const element = document.querySelector<HTMLElement>('.state-pill__card')!;
        const box = element.getBoundingClientRect();
        return { bottom: box.bottom, viewport: window.innerHeight, maxHeight: element.style.maxHeight, scrolls: element.scrollHeight > element.clientHeight };
      });
      expect(card.bottom).toBeLessThanOrEqual(card.viewport);
      expect(card.maxHeight).not.toBe('');

      await scrollCardToItsEnd(page);
      await expect(page.locator('[data-open-history]')).toBeInViewport();
      expect(await showsAtItsMiddle(page, '[data-open-history]')).toBe(true);
      expect(await frameScrolled(page)).toBe(false);
      await page.evaluate(() => document.querySelector('.state-pill__card')!.scrollTo(0, 0));
      await shot(page, 'state-entity', `06-card-${viewport.width}x${viewport.height}`);
    });
  });
}
