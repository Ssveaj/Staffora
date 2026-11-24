import type { Page } from '@playwright/test';
import { expect, test } from './lib/page-health';
import { enterSecret, expectSettledPageAccessible, gotoReady, waitForReady } from './lib/session';
import { shot } from './lib/shots';

async function expectNoHorizontalOverflow(page: Page): Promise<void> {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow, 'the page scrolls sideways').toBeLessThanOrEqual(0);
}

test('at 1440 px the brand panel stands beside the card, the form is intact and a refused attempt is announced', { tag: ['@shell', '@a11y'] }, async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await gotoReady(page, '/Account/Login');

  await expect(page.locator('h1')).toHaveCount(1);
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Welcome back');
  await expect(page.locator('.sign-in-card')).toContainText('Sign in to Staffora');

  const panel = page.locator('.sign-in__panel');
  const panelBox = (await panel.boundingBox())!;
  const cardBox = (await page.locator('.sign-in-card').boundingBox())!;
  expect(panelBox.x).toBe(0);
  expect(panelBox.height).toBe(900);
  expect(Math.abs(panelBox.width - 1440 * 0.55)).toBeLessThan(4);
  expect(cardBox.x).toBeGreaterThan(panelBox.x + panelBox.width);
  expect(cardBox.width).toBeLessThanOrEqual(420);

  await expect(panel.locator('.sign-in__brand')).toContainText('Staffora');
  await expect(panel.locator('.sign-in__headline')).toHaveText('Every job, the right people.');
  await expect(panel.locator('.sign-in__lede')).toBeVisible();
  await expect(panel.getByRole('listitem')).toContainText(['Plan jobs and their staff', 'Staff answer in one tap', 'Customers order through their portal']);

  const art = panel.locator('img.sign-in__art');
  await expect(art).toHaveAttribute('alt', '');
  expect(new URL((await art.evaluate((image) => (image as HTMLImageElement).currentSrc))).origin).toBe(new URL(page.url()).origin);
  await expect
    .poll(() => art.evaluate((image) => (image as HTMLImageElement).complete && (image as HTMLImageElement).naturalWidth > 0), { message: 'the art has loaded' })
    .toBe(true);

  const form = page.locator('form.sign-in-card__form');
  await expect(form).toHaveAttribute('method', 'post');
  await expect(form.locator('input[name="_handler"]')).toHaveValue('login');
  await expect(form.locator('input[name="__RequestVerificationToken"]')).toHaveCount(1);
  await expect(page.getByLabel('Email')).toHaveAttribute('name', 'Input.Email');
  await expect(page.getByLabel('Email')).toHaveAttribute('autocomplete', 'username');
  await expect(page.getByLabel('Password')).toHaveAttribute('name', 'Input.Password');
  await expect(page.getByLabel('Password')).toHaveAttribute('type', 'password');
  await expect(page.getByLabel('Remember me')).toHaveAttribute('name', 'Input.RememberMe');
  await expect(page.getByRole('button', { name: 'Sign in', exact: true })).toHaveAttribute('type', 'submit');
  await expect(form.getByRole('button')).toHaveCount(1);
  await expect(form.locator('passkey-submit')).toHaveCount(0);

  await expectNoHorizontalOverflow(page);
  await shot(page, 'sign-in', '01-sign-in-1440');

  await page.getByLabel('Email').fill('nobody@sign-in.example.com');
  await enterSecret(page.getByLabel('Password'), 'Not-the-password-1');
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await waitForReady(page);
  const alert = page.locator('.sign-in-card').getByRole('alert');
  await expect(alert).toHaveText('Wrong email or password. Please try again.');
  await expect(alert).toHaveClass(/sign-in-card__error/);
  await expect(page).toHaveURL(/\/Account\/Login/);
  await expectSettledPageAccessible(page);
  await shot(page, 'sign-in', '02-sign-in-refused-1440');
});

test('at 390 px the panel is a short band over the card and nothing scrolls sideways', { tag: ['@shell', '@a11y'] }, async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await gotoReady(page, '/Account/Login');

  const panel = page.locator('.sign-in__panel');
  const panelBox = (await panel.boundingBox())!;
  const cardBox = (await page.locator('.sign-in-card').boundingBox())!;

  expect(panelBox.x).toBe(0);
  expect(panelBox.y).toBe(0);
  expect(panelBox.width).toBe(390);
  expect(cardBox.y).toBeGreaterThan(150);
  expect(cardBox.y).toBeLessThan(panelBox.y + panelBox.height);
  expect(cardBox.x).toBeGreaterThanOrEqual(16);
  expect(cardBox.x + cardBox.width).toBeLessThanOrEqual(390 - 16);

  await expect(panel.locator('.sign-in__brand')).toBeVisible();
  await expect(panel.locator('.sign-in__headline')).toBeVisible();
  await expect(panel.locator('.sign-in__lede')).toBeHidden();
  await expect(panel.locator('.sign-in__proofs')).toBeHidden();
  await expect(page.getByRole('button', { name: 'Sign in', exact: true })).toBeVisible();

  await expectNoHorizontalOverflow(page);
  await shot(page, 'sign-in', '03-sign-in-390');
});

test('under reduced motion the art, its glow and the card stand still', { tag: ['@shell', '@a11y'] }, async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.setViewportSize({ width: 1440, height: 900 });
  await gotoReady(page, '/Account/Login');

  for (const selector of ['.sign-in__art', '.sign-in__glow', '.sign-in-card']) {
    expect(await page.locator(selector).evaluate((element) => getComputedStyle(element).animationName), selector).toBe('none');
  }
});
