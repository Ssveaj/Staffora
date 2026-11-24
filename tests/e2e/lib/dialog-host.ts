import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { expect, type Page } from '@playwright/test';
import { repositoryRoot } from './foundation-state';
import { waitForReady } from './session';
import { dialogHostURLOf } from '../../../scripts/e2e-slots.mjs';

export interface DialogHostState {
  baseUrl: string;
  userId: string;
  parentId: string;
  parentName: string;
  childId: string;
  childName: string;
  foreignParentId: string;
  foreignChildId: string;
  platformOwnerId: string;
  platformParentId: string;
  platformParentName: string;
  commentReaderId: string;
  commentedParentId: string;
  commentedParentName: string;
  longThreadParentId: string;
  longThreadParentName: string;
  longThreadComments: number;
}

export const dialogHostUrl: string = dialogHostURLOf();

export function readDialogHostState(): DialogHostState {
  return JSON.parse(readFileSync(join(repositoryRoot, '.e2e', 'dialog-host-state.json'), 'utf8')) as DialogHostState;
}

export const dialogs = '.state-entity-dialog';
export const titles = '.state-entity-dialog-header__title';

export async function signIn(page: Page, userId: string): Promise<void> {
  const response = await page.goto(`/test/sign-in/${userId}`);
  expect(response?.status()).toBe(200);
}

export async function arrive(page: Page, path: string): Promise<void> {
  await page.goto(path);
  await waitForReady(page);
}

export function chainOf(page: Page): string | null {
  return new URL(page.url()).searchParams.get('dlg');
}

export async function expectDialogs(page: Page, count: number): Promise<void> {
  await expect(page.locator(dialogs)).toHaveCount(count);
  if (count > 0) {
    await expect(page.locator(`${dialogs}[data-load-state="loaded"]`)).toHaveCount(count);
  }
}

export function focused(page: Page): Promise<string> {
  return page.evaluate(() => document.activeElement?.textContent?.trim() ?? '');
}

export async function navigateInApp(page: Page, path: string): Promise<void> {
  await page.evaluate((target) => (window as unknown as { Blazor: { navigateTo(url: string): void } }).Blazor.navigateTo(target), path);
}

export async function countLeavePrompts(page: Page): Promise<void> {
  await page.addInitScript(() => {
    const counted = window as unknown as { leavePrompts: number };
    counted.leavePrompts = 0;
    const add = window.addEventListener.bind(window);
    const remove = window.removeEventListener.bind(window);
    window.addEventListener = ((type: string, listener: EventListenerOrEventListenerObject, options?: boolean | AddEventListenerOptions) => {
      if (type === 'beforeunload') counted.leavePrompts++;
      add(type, listener, options);
    }) as typeof window.addEventListener;
    window.removeEventListener = ((type: string, listener: EventListenerOrEventListenerObject, options?: boolean | EventListenerOptions) => {
      if (type === 'beforeunload') counted.leavePrompts--;
      remove(type, listener, options);
    }) as typeof window.removeEventListener;
  });
}

export function leavePrompts(page: Page): Promise<number> {
  return page.evaluate(() => (window as unknown as { leavePrompts?: number }).leavePrompts ?? -1);
}