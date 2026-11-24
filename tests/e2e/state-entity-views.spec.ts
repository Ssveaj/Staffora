import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import type { Page } from '@playwright/test';
import { expectNoSeriousViolations } from './lib/accessibility';
import { repositoryRoot } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { nextFrames } from './lib/frames';
import { shot } from './lib/shots';

test.use({ launchOptions: { args: ['--disable-smooth-scrolling'] } });

const fixtureFolder = join(repositoryRoot, 'tests', 'e2e', 'fixtures', 'state-entity-dialog');
const fixtureAddress = '/e2e-fixtures/state-entity-views';

function pageOf(body: string): string {
  return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <base href="/" />
  <title>State-entity views</title>
  <link rel="stylesheet" href="lib/bootstrap/dist/css/bootstrap.min.css" />
  <link rel="stylesheet" href="_content/Radzen.Blazor/css/material-base.css" />
  <link rel="stylesheet" href="_content/Radzen.Blazor/css/material.css" />
  <link rel="stylesheet" href="app.tokens.css" />
  <link rel="stylesheet" href="app.typography.css" />
  <link rel="stylesheet" href="app.css" />
  <link rel="stylesheet" href="Staffora.Blazor.styles.css" />
</head>
<body>
<button type="button" id="opener">Open the parent</button>
${body}
</body>
</html>`;
}

async function openFixture(page: Page, name: string): Promise<void> {
  const body = readFileSync(join(fixtureFolder, name), 'utf8');
  await page.route(`**${fixtureAddress}`, (route) => route.fulfill({ status: 200, contentType: 'text/html; charset=utf-8', body: pageOf(body) }));
  await page.goto(fixtureAddress);
  await page.addStyleTag({ content: '*, *::before, *::after { animation: none !important; transition: none !important; }' });
  await page.evaluate(() => document.fonts.ready);
  await page.addScriptTag({ type: 'module', content: "import * as module from '/scripts/state-entity-dialog.js'; window.dialogModule = module;" });
  await page.waitForFunction(() => 'dialogModule' in window);
  await page.evaluate(() => {
    const module = (window as unknown as { dialogModule: { attachTabs: (tablist: HTMLElement) => void } }).dialogModule;
    for (const tablist of document.querySelectorAll<HTMLElement>('[role="tablist"]')) module.attachTabs(tablist);
  });
}

async function focusedTab(page: Page): Promise<string | null> {
  return page.evaluate(() => (document.activeElement?.getAttribute('role') === 'tab' ? document.activeElement.getAttribute('data-view') : null));
}

for (const viewport of [{ width: 1280, height: 900 }, { width: 390, height: 844 }]) {
  test.describe(`the state-entity dialog's views at ${viewport.width} pixels`, { tag: '@dialogs' }, () => {
    test.use({ viewport });

    test('the view tabs: a tablist whose selected tab alone takes the tab stop, moved between by the arrow keys, Home and End', async ({ page }) => {
      await openFixture(page, 'views-info.html');
      await expectNoSeriousViolations(page);

      const tablist = page.getByRole('tablist', { name: 'Views' });
      await expect(tablist).toBeVisible();
      const info = tablist.getByRole('tab', { name: 'Info' });
      const children = tablist.getByRole('tab', { name: 'Children' });
      await expect(info).toHaveAttribute('aria-selected', 'true');
      await expect(children).toHaveAttribute('aria-selected', 'false');
      await expect(page.getByRole('tabpanel', { name: 'Info' })).toBeVisible();
      await expect(page.getByRole('textbox', { name: 'Name' })).toBeVisible();
      await shot(page, 'state-entity', `17-views-tabs-${viewport.width}`);

      await info.focus();
      await page.keyboard.press('Shift+Tab');
      await page.keyboard.press('Tab');
      expect(await focusedTab(page)).toBe('info');
      await page.keyboard.press('Tab');
      expect(await focusedTab(page)).toBeNull();
      await info.focus();

      const body = page.locator('.state-entity-dialog__body');
      await page.addStyleTag({ content: 'html { scroll-behavior: auto !important; } body::after { content: ""; display: block; width: 4000px; height: 4000px; } .state-entity-dialog-tabs::after { content: ""; flex: 0 0 2000px; }' });
      await page.evaluate(() => {
        window.scrollTo({ left: 1000, top: 1000, behavior: 'instant' });
        document.querySelector<HTMLElement>('[role="tab"][data-view="info"]')!.focus({ preventScroll: true });
      });
      const scrolled = async () => page.evaluate(() => ({ x: window.scrollX, y: window.scrollY, body: document.querySelector('.state-entity-dialog__body')!.scrollTop, tabs: document.querySelector('[role="tablist"]')!.scrollLeft }));
      const before = await scrolled();
      expect(before.y).toBeGreaterThan(0);
      expect(before.x).toBeGreaterThan(0);
      expect(await page.evaluate(() => { const tabs = document.querySelector('[role="tablist"]')!; return tabs.scrollWidth > tabs.clientWidth; })).toBe(true);
      for (const [key, focused] of [['ArrowRight', 'children'], ['ArrowRight', 'info'], ['ArrowLeft', 'children'], ['Home', 'info'], ['End', 'children']] as const) {
        await page.keyboard.press(key);
        expect(await focusedTab(page)).toBe(focused);
        await nextFrames(page);
        expect(await scrolled(), `${key} scrolled`).toEqual(before);
      }
      await page.evaluate(() => window.scrollTo({ left: 0, top: 0, behavior: 'instant' }));
      await expect(body).toBeVisible();

      await expect(children).toHaveAttribute('aria-selected', 'false');
      await page.evaluate(() => {
        const w = window as unknown as { clicked: string[] };
        w.clicked = [];
        for (const tab of document.querySelectorAll<HTMLElement>('[role="tab"]')) tab.addEventListener('click', () => w.clicked.push(tab.dataset.view ?? ''));
      });
      await page.keyboard.press('Enter');
      expect(await page.evaluate(() => (window as unknown as { clicked: string[] }).clicked)).toEqual(['children']);
      await children.focus();
      await shot(page, 'state-entity', `17-views-tabs-focus-${viewport.width}`);
    });

    test('the Children view: the child list in its tabpanel, each row with its state', async ({ page }) => {
      await openFixture(page, 'views-children.html');
      await expectNoSeriousViolations(page);

      await expect(page.getByRole('tab', { name: 'Children' })).toHaveAttribute('aria-selected', 'true');
      const panel = page.getByRole('tabpanel', { name: 'Children' });
      await expect(panel).toBeVisible();
      await expect(page.locator('[role=tabpanel][data-view=info]')).toBeHidden();
      const rows = panel.getByRole('table', { name: 'Children' }).locator('tr[data-row]');
      await expect(rows).toHaveCount(2);
      await expect(rows.nth(0)).toContainText('Afternoon group');
      await expect(rows.nth(0)).toContainText('Draft');
      await expect(rows.nth(1)).toContainText('Morning group');
      await expect(rows.nth(1)).toContainText('Active');
      await expect(panel.getByRole('button', { name: 'Morning group' })).toBeVisible();
      await shot(page, 'state-entity', `18-views-children-${viewport.width}`);
    });

    test("a child's own dialog stacked on its parent's", async ({ page }) => {
      await openFixture(page, 'views-stacked.html');
      await expectNoSeriousViolations(page);

      const dialogs = page.locator('.rz-dialog-wrapper');
      await expect(dialogs).toHaveCount(2);
      await expect(dialogs.nth(0).locator('.state-entity-dialog-header__title')).toHaveText('Spring period job');
      await expect(dialogs.nth(1).locator('.state-entity-dialog-header__title')).toHaveText('Child');
      await expect(dialogs.nth(1).getByRole('textbox', { name: 'Name' })).toHaveValue('Morning group');
      await expect(dialogs.nth(1).getByRole('textbox', { name: 'Parent' })).toBeVisible();
      await shot(page, 'state-entity', `19-views-stacked-child-${viewport.width}`);
    });

    test("a session's navigator: Previous, Next and where the record shown sits", async ({ page }) => {
      await openFixture(page, 'views-session.html');
      await expectNoSeriousViolations(page);

      const navigator = page.getByRole('navigation', { name: 'Records in this dialog' });
      await expect(navigator).toBeVisible();
      await expect(navigator.getByRole('status')).toHaveText('1 of 2');
      await expect(navigator.getByRole('button', { name: 'Previous' })).toHaveAttribute('aria-disabled', 'true');
      await expect(navigator.getByRole('button', { name: 'Next' })).not.toHaveAttribute('aria-disabled', /.*/);
      await expect(page.locator('.state-entity-dialog-header__title')).toHaveText('Spring period job');

      const overflow = await page.evaluate(() => {
        const dialog = document.querySelector('.rz-dialog')!.getBoundingClientRect();
        const bar = document.querySelector('.state-entity-dialog-session')!.getBoundingClientRect();
        return bar.left < dialog.left - 0.5 || bar.right > dialog.right + 0.5;
      });
      expect(overflow).toBe(false);
      await shot(page, 'state-entity', `21-views-session-${viewport.width}`);
    });

    test("the leave rule's question when a hop finds edits no save can take", async ({ page }) => {
      await openFixture(page, 'views-leave-ask.html');
      await expectNoSeriousViolations(page);

      const question = page.locator('.rz-dialog-wrapper').last();
      await expect(question.locator('.form-dialog-header__title')).toHaveText('Discard changes?');
      await expect(question).toContainText('Name is required');
      await expect(question.getByRole('button', { name: 'Keep editing' })).toBeVisible();
      await expect(question.getByRole('button', { name: 'Discard changes' })).toBeVisible();
      await shot(page, 'state-entity', `20-views-leave-ask-${viewport.width}`);
    });
  });
}

test.describe('the focus as Blazor takes a dialog away', { tag: '@dialogs' }, () => {
  type Module = { nameDialog: (title: HTMLElement) => void; releaseDialog: (id: string) => void; handOffDialog: (id: string) => void };

  async function openFromTheOpener(page: Page): Promise<void> {
    await openFixture(page, 'views-info.html');
    await page.locator('#opener').focus();
    await page.evaluate(() => (window as unknown as { dialogModule: Module }).dialogModule.nameDialog(document.querySelector<HTMLElement>('.state-entity-dialog-header__title')!));
    expect(await page.evaluate(() => document.activeElement?.id)).toBe('state-entity-dialog-title-1');
  }

  async function swap(page: Page, order: 'hand-off first' | 'name first'): Promise<void> {
    await page.evaluate((order) => {
      const module = (window as unknown as { dialogModule: Module }).dialogModule;
      const first = document.querySelector<HTMLElement>('.state-entity-dialog-header__title')!;
      const second = document.createElement('h2');
      second.id = 'state-entity-dialog-title-swapped';
      second.className = 'state-entity-dialog-header__title';
      second.tabIndex = -1;
      second.textContent = 'Autumn period job';
      const firstId = first.id;
      first.replaceWith(second);
      if (order === 'hand-off first') {
        module.handOffDialog(firstId);
        module.nameDialog(second);
      } else {
        module.nameDialog(second);
        module.handOffDialog(firstId);
      }
    }, order);
  }

  async function closeAsBlazorDoes(page: Page): Promise<void> {
    await page.evaluate(() => {
      const module = (window as unknown as { dialogModule: Module }).dialogModule;
      const title = document.querySelector<HTMLElement>('.state-entity-dialog-header__title')!;
      module.releaseDialog(title.id);
      document.querySelector('.rz-dialog-wrapper')!.remove();
    });
  }

  test('a single dialog closing returns the focus to its opener', async ({ page }) => {
    await openFromTheOpener(page);
    await closeAsBlazorDoes(page);
    expect(await page.evaluate(() => document.activeElement?.id)).toBe('opener');
  });

  for (const order of ['hand-off first', 'name first'] as const) {
    test(`a swap moves the focus to the new heading, and the session's close returns it to the opener (${order})`, async ({ page }) => {
      await openFromTheOpener(page);
      await swap(page, order);
      expect(await page.evaluate(() => document.activeElement?.id)).toBe('state-entity-dialog-title-swapped');
      await expect(page.locator('.rz-dialog.state-entity-dialog-shell')).toHaveAttribute('aria-labelledby', 'state-entity-dialog-title-swapped');

      await closeAsBlazorDoes(page);
      expect(await page.evaluate(() => document.activeElement?.id)).toBe('opener');
    });
  }

  test('a heading named while another dialog is open above leaves the focus in the dialog on top', async ({ page }) => {
    await openFromTheOpener(page);
    await page.evaluate(() => {
      const question = document.createElement('div');
      question.className = 'rz-dialog-wrapper';
      question.innerHTML = '<div class="rz-dialog" role="dialog" aria-label="Discard changes?"><button type="button" id="keep-editing">Keep editing</button></div>';
      document.body.append(question);
      document.getElementById('keep-editing')!.focus();
    });
    await swap(page, 'hand-off first');
    expect(await page.evaluate(() => document.activeElement?.id)).toBe('keep-editing');
    await expect(page.locator('.rz-dialog.state-entity-dialog-shell')).toHaveAttribute('aria-labelledby', 'state-entity-dialog-title-swapped');
  });

  test('a swap from the navigator leaves the focus on the button the user pressed', async ({ page }) => {
    await openFromTheOpener(page);
    await page.evaluate(() => {
      const next = document.createElement('button');
      next.type = 'button';
      next.id = 'session-next';
      next.textContent = 'Next';
      document.querySelector('.rz-dialog-content')!.prepend(next);
      next.focus();
    });
    await swap(page, 'hand-off first');
    expect(await page.evaluate(() => document.activeElement?.id)).toBe('session-next');

    await closeAsBlazorDoes(page);
    expect(await page.evaluate(() => document.activeElement?.id)).toBe('opener');
  });
});
