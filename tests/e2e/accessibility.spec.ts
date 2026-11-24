import { expect, test } from '@playwright/test';
import { seriousViolations } from './lib/accessibility';
import { waitForSettled } from './lib/session';
import { focusReturn, focusStaysInside, layoutProblems, openerOf, rememberPresses } from './lib/sweep';

const page = (body: string) => `<!DOCTYPE html><html lang="en"><head><title>Check</title></head><body><main><h1>Check</h1>${body}</main></body></html>`;

test('the check fails a page with an unnamed icon button or an image without a text alternative', { tag: '@a11y' }, async ({ page: browser }) => {
  await browser.setContent(page('<button type="button"><span aria-hidden="true">x</span></button><img src="data:image/gif;base64,R0lGODlhAQABAAAAACw=">'));

  const violations = await seriousViolations(browser);

  expect(violations.map((violation) => violation.split(' ')[0]).sort()).toEqual(['button-name', 'image-alt']);
});

test('the check passes a page whose controls are named', { tag: '@a11y' }, async ({ page: browser }) => {
  await browser.setContent(page('<button type="button" aria-label="Close"><span aria-hidden="true">x</span></button><a href="/">Start page</a>'));

  expect(await seriousViolations(browser)).toEqual([]);
});

const radzenButtons = '<style>.rz-button{position:relative;overflow:hidden;border:0;padding:6px 12px;font-size:12px}.rz-button:before{content:"";position:absolute;inset:0;opacity:0;background:radial-gradient(circle, currentColor 1%, transparent 1%) center/15000%;pointer-events:none}</style>';

test('the check fails a faint button, whose contrast axe leaves unjudged under the theme, and passes a clear one', { tag: '@a11y' }, async ({ page: browser }) => {
  await browser.setContent(page(`${radzenButtons}<button type="button" class="rz-button" style="color:#eeeeee;background:transparent">Add time</button><button type="button" class="rz-button rz-button-icon-only" aria-label="Remove the time" style="color:#eeeeee;background:transparent"><span aria-hidden="true">x</span></button><button type="button" class="rz-button" disabled style="color:#eeeeee;background:transparent">Disabled</button>`));

  const violations = await seriousViolations(browser);

  expect(violations.map((violation) => violation.split(' ')[0])).toEqual(['button-contrast', 'button-contrast']);
  expect(violations[0]).toContain('Add time');
  expect(violations[1]).toContain('Remove the time');

  await browser.setContent(page(`${radzenButtons}<button type="button" class="rz-button" style="color:color-mix(in srgb, #eeeeee 50%, white);background:transparent">Mixed faint</button><button type="button" class="rz-button" style="color:#333333;background:color-mix(in srgb, black 90%, white)">Mixed dark</button><button type="button" class="rz-button" style="color:oklch(0.97 0 0);background:transparent">Other space</button>`));
  const mixed = await seriousViolations(browser);
  expect(mixed.map((violation) => violation.split(' ')[0])).toEqual(['button-contrast', 'button-contrast', 'button-contrast']);
  expect(mixed[0]).toContain('Mixed faint');
  expect(mixed[1]).toContain('Mixed dark');
  expect(mixed[2]).toContain('Other space');

  await browser.setContent(page(`${radzenButtons}<button type="button" class="rz-button" style="color:#4340d2;background:transparent;border:1px solid #4340d2">Add time</button><button type="button" class="rz-button rz-button-icon-only" aria-label="Remove the time" style="color:#212121;background:transparent"><span aria-hidden="true">x</span></button>`));
  expect(await seriousViolations(browser)).toEqual([]);
});

test('the check waits for the page to settle, so a loading indicator gone by then does not decide it', { tag: '@a11y' }, async ({ page: browser }) => {
  await browser.setContent(page('<div id="slot"><div role="progressbar" class="rz-progressbar-circular" style="width: 40px; height: 40px"></div></div><script>setTimeout(() => { document.getElementById("slot").innerHTML = "<p>Loaded</p>"; }, 2000);</script>'));

  expect((await seriousViolations(browser)).map((violation) => violation.split(' ')[0])).toContain('aria-progressbar-name');
  await waitForSettled(browser);
  expect(await seriousViolations(browser)).toEqual([]);
});

test('the layout check fails a sideways scroll, a dialog wider than the window and a control out of reach, and passes a clean page', { tag: '@a11y' }, async ({ page: browser }) => {
  await browser.setViewportSize({ width: 390, height: 844 });
  const problems = async (body: string) => {
    await browser.setContent(page(body));
    return layoutProblems(browser);
  };

  expect(await problems('<div style="width:600px">Too wide</div>')).toEqual([expect.stringMatching(/^scrolls sideways: html is \d+px wide in 390px$/)]);
  expect(await problems('<div style="overflow:hidden;width:100px;height:40px"><button type="button" style="margin-left:200px">Cut off</button></div>')).toEqual([expect.stringMatching(/^out of reach: button "Cut off" lies outside div, which clips it$/)]);
  expect(await problems('<div style="position:relative;overflow:hidden;width:200px;height:60px"><button type="button" style="position:absolute;left:250px">Beside</button></div>')).toEqual([expect.stringMatching(/^out of reach: button "Beside" lies outside div/)]);
  expect(await problems('<div class="rz-dialog" role="dialog" aria-label="Wide" style="position:fixed;left:0;top:0;width:500px"><button type="button">Inside</button></div>')).toEqual([expect.stringMatching(/^a dialog reaches past the window: 0 to 500 px in 390 px$/)]);

  expect(await problems('<div style="overflow-x:auto"><table style="width:900px"><tr><td><button type="button" style="margin-left:800px">Far right</button></td></tr></table></div><button type="button" style="position:absolute;width:1px;height:1px;overflow:hidden;clip:rect(0 0 0 0)">Hidden until focused</button><div style="overflow:hidden;width:100px"><button type="button" style="width:160px">Partly shown</button></div>')).toEqual([]);
});

test('the focus checks fail a dialog Tab leaves and a focus lost to the page, and pass a trapped dialog and a focus back on its opener', { tag: '@a11y' }, async ({ page: browser }) => {
  const dialogPage = (trapped: boolean) => page(`<button type="button" id="opener" onclick="document.getElementById('box').hidden = false; document.getElementById('one').focus()">Open</button>
    <div id="box" role="dialog" aria-label="Box" hidden><button type="button" id="one">One</button><button type="button">Two</button></div><button type="button">Behind</button>
    <script>${trapped ? `document.getElementById('box').addEventListener('keydown', (event) => { if (event.key !== 'Tab') return; event.preventDefault(); const buttons = [...document.querySelectorAll('#box button')]; const next = (buttons.indexOf(document.activeElement) + 1) % buttons.length; buttons[next].focus(); });` : ''}</script>`);

  await browser.setContent(dialogPage(false));
  await browser.evaluate(rememberPresses);
  await browser.getByRole('button', { name: 'Open' }).click();
  const box = browser.getByRole('dialog', { name: 'Box' });
  expect(await focusStaysInside(browser, box)).toMatch(/^Tab \d+ took the focus out of the dialog, to button "Behind"$/);
  await browser.setContent(dialogPage(true));
  await browser.evaluate(rememberPresses);
  await browser.getByRole('button', { name: 'Open' }).click();
  expect(await focusStaysInside(browser, box)).toBeNull();

  const opener = await openerOf(browser, box);
  await browser.evaluate(() => { document.getElementById('box')!.hidden = true; (document.activeElement as HTMLElement | null)?.blur(); });
  expect(await focusReturn(browser, opener)).toBe('the focus fell to the page\'s body');
  await browser.evaluate(() => { const after = document.createElement('button'); after.textContent = 'Elsewhere'; document.body.append(after); after.focus(); });
  expect(await focusReturn(browser, opener)).toBe('the focus went to button "Elsewhere", not back to button "Open", which opened the dialog');
  await browser.getByRole('button', { name: 'Open' }).focus();
  expect(await focusReturn(browser, opener)).toBeNull();
});