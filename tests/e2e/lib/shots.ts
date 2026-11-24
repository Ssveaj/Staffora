import { join } from 'node:path';
import type { Locator, Page } from '@playwright/test';
import { repositoryRoot } from './foundation-state';

export const screenshotRoot = join(repositoryRoot, 'output', 'playwright');

export const takesShots = process.env.STAFFORA_E2E_SHOTS !== '0';

export async function shot(page: Page, area: string, name: string, mask: Locator[] = []): Promise<string> {
  const path = join(screenshotRoot, area, `${name}.png`);
  if (takesShots) await page.screenshot({ path, fullPage: true, mask });
  return path;
}
