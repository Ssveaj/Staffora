import { expect, type Page } from '@playwright/test';

export async function nextFrames(page: Page): Promise<void> {
  await page.evaluate(() => new Promise<void>((resolve) => requestAnimationFrame(() => requestAnimationFrame(() => resolve()))));
}

export async function countRadzenDialogTimers(page: Page): Promise<void> {
  await page.addInitScript(() => {
    const counted = window as unknown as { radzenDialogTimers: number; Radzen?: { openDialog: (...args: unknown[]) => unknown } };
    if ('radzenDialogTimers' in counted) return;
    counted.radzenDialogTimers = 0;
    let radzen: typeof counted.Radzen;
    Object.defineProperty(window, 'Radzen', {
      configurable: true,
      get: () => radzen,
      set: (value: typeof counted.Radzen) => {
        radzen = value;
        if (!value || typeof value.openDialog !== 'function') return;
        const open = value.openDialog;
        value.openDialog = function (this: unknown, ...args: unknown[]) {
          counted.radzenDialogTimers++;
          const result = open.apply(this, args);
          setTimeout(() => counted.radzenDialogTimers--, 500);
          return result;
        };
      },
    });
  });
}

export async function pastRadzenDialogTimers(page: Page): Promise<void> {
  await expect.poll(() => page.evaluate(() => (window as unknown as { radzenDialogTimers?: number }).radzenDialogTimers ?? -1), { message: 'Radzen\'s dialog timers have run' }).toBe(0);
}
