import type { Locator, Page } from '@playwright/test';
import { persona, personaKeys, readFoundationState } from './lib/foundation-state';
import { journeyStep, loadJourney } from './lib/journey-contract';
import { expect, test } from './lib/page-health';
import { endImpersonation, expectSettledPageAccessible, gotoReady, impersonate, loginAs, waitForReady, waitForSettled } from './lib/session';
import { shot } from './lib/shots';

const journey = loadJourney('tenant-administrator-changes-time-zone');

const zonePicker = (page: Page): Locator => page.locator('.rz-dropdown').filter({ has: page.locator('input[aria-label="Time zone"]') });

const zoneLabel = (page: Page): Locator => zonePicker(page).locator('.rz-dropdown-label');

const chosenZone = async (page: Page): Promise<string> => ((await zoneLabel(page).textContent()) ?? '').trim();

const fact = (page: Page, label: string): Locator => page.locator(`.general-settings__facts dt:has-text(${JSON.stringify(label)}) + dd`);

async function expectNoSidewaysScroll(page: Page, name: string, width: number): Promise<void> {
  const overflowing = await page.evaluate(() =>
    [document.documentElement, ...Array.from(document.querySelectorAll<HTMLElement>('.app-shell .app-shell__body'))]
      .filter((element) => element.scrollWidth > element.clientWidth)
      .map((element) => `${element.tagName.toLowerCase()}.${element.className}: ${element.scrollWidth}px wide in ${element.clientWidth}px`),
  );
  expect(overflowing, `${name} scrolls sideways at ${width} px`).toEqual([]);
}

async function shootBothWidths(page: Page, name: string): Promise<void> {
  for (const viewport of [{ width: 1280, height: 800 }, { width: 390, height: 844 }]) {
    await page.setViewportSize(viewport);
    await waitForSettled(page);
    await expectNoSidewaysScroll(page, name, viewport.width);
    const style = await page.addStyleTag({
      content: '.app-shell { height: auto !important; min-height: 100dvh; overflow: visible !important; } .app-shell .app-shell__body { overflow: visible !important; }',
    });
    try {
      await shot(page, 'settings', `${name}-${viewport.width}`);
    } finally {
      await style.evaluate((element) => element.remove());
    }
  }
  await page.setViewportSize({ width: 1280, height: 800 });
  await waitForSettled(page);
}

const platformZoneButton = (page: Page): Locator => page.getByRole('button', { name: /Use the platform's time zone \(/ });

async function openPickerFromKeyboard(page: Page): Promise<Locator> {
  await zonePicker(page).focus();
  await page.keyboard.press('Enter');
  const searchBox = page.getByRole('textbox', { name: 'Search time zones' });
  await expect(searchBox, 'the open picker puts the focus in its search box').toBeFocused();
  return searchBox;
}

test.use({ viewport: { width: 1280, height: 800 } });

test('a tenant administrator changes the company\'s time zone in the settings', { tag: '@admin' }, async ({ page }) => {
  test.setTimeout(180_000);
  const state = readFoundationState();
  const alex = state.tenantAdministrator;
  const robin = state.tenantViewer;
  let platformZone = '';
  let chosen = '';

  await journeyStep(journey, 'impersonate-administrator', async () => {
    await loginAs(page, persona(personaKeys.platformOwner, state));
    await impersonate(page, alex);
  });

  await journeyStep(journey, 'open-general-settings', async () => {
    await gotoReady(page, '/tenant/management/settings');
    await expect(page.getByRole('heading', { level: 1, name: 'Settings' })).toBeVisible();
    const panels = page.getByRole('navigation', { name: 'Settings' });
    await expect(panels.getByRole('link', { name: 'General' })).toHaveAttribute('aria-current', 'page');
    await expect(page.getByRole('heading', { level: 2, name: 'General' })).toBeVisible();

    await expect(zoneLabel(page)).toContainText('(UTC');
    platformZone = await chosenZone(page);
    expect(platformZone, 'the picker shows a zone').not.toBe('');
    await expect(fact(page, 'Platform\'s time zone')).toHaveText(platformZone);
    await expect(page.locator('.general-settings__fallback'), 'the company holds a zone of its own').toHaveCount(0);
    await expect(platformZoneButton(page), 'nothing to restore while the company has the platform\'s zone').toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeDisabled();
    await shootBothWidths(page, 'general');
  });

  await journeyStep(journey, 'change-time-zone', async () => {
    const [search, zone, other, otherZone] = platformZone.includes('New York')
      ? ['tokyo', 'Asia/Tokyo', 'lima', 'America/Lima']
      : ['new york', 'America/New York', 'tokyo', 'Asia/Tokyo'];
    const option = page.getByRole('option', { name: new RegExp(`\\) ${zone}$`) });
    const otherOption = page.getByRole('option', { name: new RegExp(`\\) ${otherZone}$`) });

    let searchBox = await openPickerFromKeyboard(page);
    await page.keyboard.type(search);
    await expect(searchBox).toHaveValue(search);
    await expect(page.getByRole('option')).toHaveCount(1);
    await expect(option).toHaveCount(1);
    await page.keyboard.press('ArrowDown');
    await expect(option, 'ArrowDown highlights the searched match').toHaveClass(/rz-state-highlight/);
    await page.keyboard.press('Enter');
    await expect(searchBox).toBeHidden();
    await expect(zoneLabel(page), 'the picker shows the zone chosen from the keyboard').toContainText(zone);
    chosen = await chosenZone(page);

    for (const viewport of [{ width: 1280, height: 800 }, { width: 390, height: 844 }]) {
      await page.setViewportSize(viewport);
      await waitForSettled(page);
      await zonePicker(page).click();
      searchBox = page.getByRole('textbox', { name: 'Search time zones' });
      await expect(searchBox).toBeVisible();
      await searchBox.clear();
      await searchBox.pressSequentially(other);
      await expect(searchBox).toHaveValue(other);
      await expect(page.getByRole('option')).toHaveCount(1);
      await expect(otherOption).toHaveCount(1);
      await expect(otherOption).toBeInViewport();
      await expectSettledPageAccessible(page);
      await shot(page, 'settings', `time-zone-picker-${viewport.width}`);
      await page.keyboard.press('Escape');
      await expect(searchBox).toBeHidden();
      await expect(zoneLabel(page), 'closing without a choice keeps the zone').toHaveText(chosen);
    }
    await page.setViewportSize({ width: 1280, height: 800 });
    await waitForSettled(page);

    const save = page.getByRole('button', { name: 'Save', exact: true });
    await expect(save).toBeEnabled();
    await save.click();
    await expect(page.getByRole('status').filter({ hasText: 'Saved:' })).toHaveText(`Saved: the company's time zone is now ${chosen}.`);
    await expect(zoneLabel(page), 'the saved zone stays shown, whatever search was left in the picker').toHaveText(chosen);
    await expect(fact(page, 'Platform\'s time zone')).toHaveText(platformZone);
    await expect(platformZoneButton(page)).toBeVisible();
    await shootBothWidths(page, 'general-own-zone');
  });

  await journeyStep(journey, 'reload-keeps-time-zone', async () => {
    await page.reload();
    await waitForReady(page);
    await expectSettledPageAccessible(page);
    await expect(zoneLabel(page), 'F5 shows the saved zone').toHaveText(chosen);
    await expect(fact(page, 'Platform\'s time zone')).toHaveText(platformZone);
    await expect(platformZoneButton(page)).toBeVisible();
    await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeDisabled();
  });

  await journeyStep(journey, 'open-custom-fields-panel', async () => {
    const panels = page.getByRole('navigation', { name: 'Settings' });
    const general = panels.getByRole('link', { name: 'General' });
    const customFields = panels.getByRole('link', { name: 'Custom fields' });
    const services = panels.getByRole('link', { name: 'Services' });
    const closedDays = panels.getByRole('link', { name: 'Closed days' });
    for (const [group, names] of [['Company', ['General']], ['Data', ['Custom fields']], ['Staffing', ['Services', 'Closed days']]] as const) {
      const links = panels.getByRole('list', { name: group }).getByRole('link');
      await expect(links, `the ${group} group holds ${names.join(' and ')}`).toHaveCount(names.length);
      for (const [index, link] of names.entries()) await expect(links.nth(index), `the ${group} group holds ${link}`).toHaveAccessibleName(link);
    }
    await general.focus();
    await page.keyboard.press('ArrowDown');
    await expect(customFields, 'ArrowDown moves to the next link').toBeFocused();
    await page.keyboard.press('ArrowDown');
    await expect(services, 'ArrowDown moves on across the groups').toBeFocused();
    await page.keyboard.press('ArrowDown');
    await expect(closedDays, 'ArrowDown moves on within a group').toBeFocused();
    await page.keyboard.press('ArrowDown');
    await expect(general, 'ArrowDown on the last link wraps round to the first').toBeFocused();
    await page.keyboard.press('ArrowUp');
    await expect(closedDays, 'ArrowUp on the first link wraps round to the last').toBeFocused();
    await page.keyboard.press('Home');
    await expect(general, 'Home moves to the first link').toBeFocused();
    await page.keyboard.press('End');
    await expect(closedDays, 'End moves to the last link').toBeFocused();
    await page.keyboard.press('ArrowUp');
    await expect(services, 'ArrowUp moves to the link before').toBeFocused();
    await page.keyboard.press('ArrowUp');
    await expect(customFields, 'ArrowUp moves on across the groups').toBeFocused();
    await page.keyboard.press('Enter');
    await expect(page).toHaveURL(/\/tenant\/management\/settings\/custom-fields$/);
    await waitForReady(page);
    await expectSettledPageAccessible(page);
    await expect(customFields).toHaveAttribute('aria-current', 'page');
    await expect(customFields, 'the focus stays on the link followed').toBeFocused();
    await expect(general).not.toHaveAttribute('aria-current', 'page');
    await expect(page.getByRole('heading', { level: 2, name: 'Custom fields' })).toBeVisible();
    for (const name of ['Team', 'Employee number']) {
      await expect(page.locator('tr.list-row').filter({ hasText: name }), `the panel lists ${name}`).toHaveCount(1);
    }
    await shootBothWidths(page, 'custom-fields');

    await page.reload();
    await waitForReady(page);
    await expect(page.getByRole('heading', { level: 2, name: 'Custom fields' })).toBeVisible();
  });

  await journeyStep(journey, 'return-to-platform-zone', async () => {
    await page.setViewportSize({ width: 390, height: 844 });
    await waitForSettled(page);
    const panels = page.getByRole('navigation', { name: 'Settings' });
    const picker = panels.getByRole('button', { name: 'Settings section: Custom fields' });
    await expect(picker).toHaveAttribute('aria-expanded', 'false');
    await expect(panels.getByRole('link', { name: 'General' }), 'the links are hidden until the button shows them').toBeHidden();
    await picker.focus();
    await page.keyboard.press('Enter');
    await expect(picker).toHaveAttribute('aria-expanded', 'true');
    await expectSettledPageAccessible(page);
    await shot(page, 'settings', 'section-picker-open-390');
    await page.keyboard.press('Tab');
    await expect(panels.getByRole('link', { name: 'General' }), 'Tab reaches the first link').toBeFocused();
    await page.keyboard.press('ArrowDown');
    await expect(panels.getByRole('link', { name: 'Custom fields' })).toBeFocused();
    await page.keyboard.press('Escape');
    await expect(picker, 'Escape gives the focus back to the button').toBeFocused();
    await expect(picker).toHaveAttribute('aria-expanded', 'false');
    await page.keyboard.press('Enter');
    await expect(picker).toHaveAttribute('aria-expanded', 'true');
    await page.keyboard.press('Tab');
    await expect(panels.getByRole('link', { name: 'General' })).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(page).toHaveURL(/\/tenant\/management\/settings$/);
    await waitForReady(page);
    const back = panels.getByRole('button', { name: 'Settings section: General' });
    await expect(back, 'the focus goes back to the button, which names the open panel').toBeFocused();
    await expect(back).toHaveAttribute('aria-expanded', 'false');
    await page.setViewportSize({ width: 1280, height: 800 });
    await waitForSettled(page);
    await expect(page.getByRole('heading', { level: 2, name: 'General' })).toBeVisible();
    await expect(zoneLabel(page)).toHaveText(chosen);
    await platformZoneButton(page).click();
    await expect(page.getByRole('status').filter({ hasText: 'Saved:' })).toHaveText(`Saved: the company's time zone is now the platform's, ${platformZone}.`);
    await expect(zoneLabel(page)).toHaveText(platformZone);
    await expect(platformZoneButton(page)).toHaveCount(0);
    await page.reload();
    await waitForReady(page);
    await expect(zoneLabel(page), 'F5 shows the platform\'s zone, now the company\'s own').toHaveText(platformZone);
    await expect(platformZoneButton(page)).toHaveCount(0);
  });

  await journeyStep(journey, 'viewer-sees-read-only', async () => {
    await endImpersonation(page);
    await expect(page).toHaveURL(/\/superadmin\//);
    await impersonate(page, robin);
    await gotoReady(page, '/tenant/management/settings');
    await expect(page.getByText('You can view the settings but not change them.')).toBeVisible();
    await expect(zoneLabel(page)).toHaveText(platformZone);
    await expect(zonePicker(page)).toHaveClass(/rz-state-disabled/);
    await expect(page.getByRole('button', { name: 'Save', exact: true }), 'a viewer is offered no Save').toHaveCount(0);
    await expect(platformZoneButton(page)).toHaveCount(0);
    await shootBothWidths(page, 'general-read-only');
  });
});
