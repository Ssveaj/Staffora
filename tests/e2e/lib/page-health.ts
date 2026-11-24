import { test as base, expect, type Response } from '@playwright/test';
import { errorBanner } from './session';
import { currentSlot } from './slot';

const allowedFailures: { pattern: RegExp; reason: string }[] = [
  {
    pattern: /^404 \/no-such-page$/,
    reason: 'navigation.spec.ts opens an unknown address on purpose, to see the status page inside the layout',
  },
];

function isAllowed(status: number, path: string): boolean {
  return allowedFailures.some((allowed) => allowed.pattern.test(`${status} ${path}`));
}

type PageHealthOptions = { expectedConsoleErrors: RegExp | null };

export const test = base.extend<PageHealthOptions>({
  baseURL: [async ({}, use) => use(currentSlot().baseURL), { option: true }],
  expectedConsoleErrors: [null, { option: true }],
  page: async ({ page, baseURL, expectedConsoleErrors }, use) => {
    const problems: string[] = [];
    const origin = new URL(baseURL!).origin;

    page.on('pageerror', (error) => problems.push(`page error: ${error.message}`));
    page.on('console', (message) => {
      if (message.type() !== 'error') return;
      const status = /status of (\d{3})/.exec(message.text())?.[1];
      const location = message.location().url ? new URL(message.location().url) : undefined;
      if (status && location?.origin === origin && isAllowed(Number(status), location.pathname)) return;
      if (expectedConsoleErrors?.test(message.text())) return;
      problems.push(`console error: ${message.text()}`);
    });
    page.on('response', (response: Response) => {
      const url = new URL(response.url());
      if (url.origin !== origin || response.status() < 400) return;
      if (!isAllowed(response.status(), url.pathname)) {
        problems.push(`HTTP ${response.status()} ${response.request().method()} ${url.pathname}`);
      }
    });

    await use(page);

    if (!page.isClosed() && (await page.locator(errorBanner).isVisible())) {
      problems.push('Blazor\'s unhandled-error banner is showing');
    }
    expect(problems, 'the page reported errors while the test ran').toEqual([]);
  },
});

export { expect };
