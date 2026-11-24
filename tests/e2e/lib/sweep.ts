import type { Locator, Page } from '@playwright/test';
import { seriousViolations } from './accessibility';
import { expect } from './page-health';
import { waitForReady, waitForSettled } from './session';
import { shot } from './shots';

export type SweepPart =
  | 'sweep: visitor and portal'
  | 'sweep: platform owner pages'
  | 'sweep: platform owner dialogs'
  | 'sweep-tenant: pages'
  | 'sweep-tenant: dialogs'
  | 'sweep-tenant: organization and staff'
  | 'sweep-staffing: staff and catalogues'
  | 'sweep-staffing: schedules and absence'
  | 'sweep-staffing: customers'
  | 'sweep-staffing: jobs';

interface AllowedViolation {
  part: SweepPart;
  rule: string;
  where: RegExp;
  knownIssue: string;
  reason: string;
}

export const allowedViolations: AllowedViolation[] = [];

export const skippedRoutes: Record<string, string> = {
  '/superadmin/tenants/{TenantId}': 'needs a tenant\'s id, which the sweep reads from the tenant list: every tab of the tenant console is swept at 1280 and 390 px in "the tenant console" below',
  '/superadmin/tenants/{TenantId}/{Tab}': 'needs a tenant\'s id, which the sweep reads from the tenant list: every tab of the tenant console is swept at 1280 and 390 px in "the tenant console" below',
};

export const unsweptDialogs: Record<string, string> = {
  MessageDialog: 'no caller yet; swept when the first caller arrives',
  DeadLetterDialog: 'its one caller is a row of a tenant console\'s Dead letters tab, and every message of the journeys\' data is delivered, so no tenant has a dead letter to open; ShortDialogAccessibilityTests checks its structure',
  ErrorDialog: 'its one caller is a refused add or remove of a role holding in the organization tree\'s role view, which the service refuses only after a change made elsewhere meanwhile, so the sweep cannot provoke it honestly; ShortDialogAccessibilityTests checks its structure',
  PromptDialog: 'its one caller is the comment prompt of a state-entity dialog step; the first step with one, an absence\'s Reject, slots a requested absence of a staff member, which the administrator never makes (their registration is approved at once): absence.spec.ts opens the prompt and checks it with axe at 1280 and 390 px',
  PersonScheduleDialog: 'the schedule of a staff member for a coordinator, opened from the name of a staff member on the coordination workspace (mockup C5d), whose rows depend on who is in service: coordination-timeline.spec.ts opens it at 1280 px and coordination-showcase.spec.ts at 1440, 1280 and 390 px, each checking it with axe, StaffPagesTests checks its structure over a real database (named by the person, every button named), and the view it hosts is swept on "My schedule" (staff-pages.spec.ts)',
};

export const anonymousPages = ['/Account/Lockout', '/Account/AccessDenied', '/no-such-page', '/not-found', '/status/429', '/status/500', '/Error', '/Account/Login'];

export const ownerPages = [
  '/',
  '/superadmin/overview',
  '/superadmin/tenants',
  '/superadmin/settings',
  '/superadmin/errorlog',
  '/status/404',
  '/Account/AccessDenied',
];

export const tenantPages = [
  '/me/overview',
  '/me/assignments',
  '/me/schedule',
  '/me/work',
  '/me/profile',
  '/portal',
  '/tenant/management',
  '/tenant/management/users',
  '/tenant/management/roles',
  '/tenant/management/organization-units',
  '/tenant/management/settings',
  '/tenant/management/settings/custom-fields',
  '/tenant/management/settings/services',
  '/tenant/management/settings/closed-days',
  '/staffing/staff',
  '/staffing/qualifications',
  '/staffing/work-patterns',
  '/staffing/absence',
  '/staffing/customers',
  '/staffing/jobs',
  '/staffing/overview',
  '/coordination',
  '/no-such-page',
  '/Account/AccessDenied',
];

export const staffPages = ['/me/work', '/me/assignments', '/me/schedule', '/me/overview', '/me/profile'];

export const portalPages = [
  '/portal',
  '/portal/orders',
  '/portal/orders/new',
  '/portal/orders/00000000-0000-0000-0000-000000000001',
  '/portal/users',
];

export const sweepViewports = [{ width: 1500, height: 900 }, { width: 1280, height: 800 }, { width: 390, height: 844 }];

const workingWidth = 1280;

export async function layoutProblems(page: Page): Promise<string[]> {
  return page.evaluate(() => {
    const problems: string[] = [];
    const where = (element: Element) => {
      const classes = typeof element.className === 'string' && element.className.trim() ? `.${element.className.trim().split(/\s+/).slice(0, 2).join('.')}` : '';
      return `${element.tagName.toLowerCase()}${classes}`;
    };
    const name = (element: Element) => {
      const label = (element.getAttribute('aria-label') ?? element.textContent ?? '').trim().replace(/\s+/g, ' ');
      return `${where(element)}${label ? ` "${label.slice(0, 40)}"` : ''}`;
    };
    for (const element of [document.documentElement, ...Array.from(document.querySelectorAll<HTMLElement>('.app-shell .app-shell__body'))]) {
      if (element.scrollWidth > element.clientWidth) problems.push(`scrolls sideways: ${where(element)} is ${element.scrollWidth}px wide in ${element.clientWidth}px`);
    }

    const width = window.innerWidth;
    const dialogs = Array.from(document.querySelectorAll<HTMLElement>('.rz-dialog')).filter((dialog) => dialog.getBoundingClientRect().width > 0);
    for (const dialog of dialogs) {
      const box = dialog.getBoundingClientRect();
      if (box.left < -1 || box.right > width + 1) problems.push(`a dialog reaches past the window: ${Math.round(box.left)} to ${Math.round(box.right)} px in ${width} px`);
    }

    const reachable = (from: number, to: number, start: number, end: number) => Math.min(to, end) - Math.max(from, start) >= Math.min(8, to - from);
    const scope: ParentNode = dialogs.length > 0 ? dialogs[dialogs.length - 1] : document;
    const controls = scope.querySelectorAll<HTMLElement>('a[href], button, input:not([type="hidden"]), select, textarea, summary, [tabindex]:not([tabindex="-1"])');
    for (const control of Array.from(controls)) {
      const box = control.getBoundingClientRect();
      const style = getComputedStyle(control);
      if (box.width <= 1 || box.height <= 1 || style.visibility === 'hidden') continue;
      if ((control as HTMLButtonElement).disabled || control.closest('[inert], [aria-hidden="true"]') || control.classList.contains('app-skip-link')) continue;
      let scrollsX = false;
      let scrollsY = false;
      let clippedBy: Element | null = null;
      let position = style.position;
      let node: HTMLElement | null = control;
      while (node && node !== document.documentElement && position !== 'fixed' && !clippedBy) {
        let next: HTMLElement | null = node.parentElement;
        if (position === 'absolute') {
          while (next && next !== document.documentElement && getComputedStyle(next).position === 'static' && getComputedStyle(next).transform === 'none') next = next.parentElement;
        }
        if (!next || next === document.documentElement || next === document.body) break;
        const outer = getComputedStyle(next);
        const clip = next.getBoundingClientRect();
        if (!scrollsX) {
          if (outer.overflowX === 'auto' || outer.overflowX === 'scroll') scrollsX = true;
          else if ((outer.overflowX === 'hidden' || outer.overflowX === 'clip') && !reachable(box.left, box.right, clip.left, clip.right)) clippedBy = next;
        }
        if (!scrollsY && !clippedBy) {
          if (outer.overflowY === 'auto' || outer.overflowY === 'scroll') scrollsY = true;
          else if ((outer.overflowY === 'hidden' || outer.overflowY === 'clip') && !reachable(box.top, box.bottom, clip.top, clip.bottom)) clippedBy = next;
        }
        if (scrollsX && scrollsY) break;
        position = outer.position;
        node = next;
      }
      if (clippedBy) problems.push(`out of reach: ${name(control)} lies outside ${where(clippedBy)}, which clips it`);
      else if (!scrollsX && !reachable(box.left, box.right, 0, width)) problems.push(`out of reach: ${name(control)} lies outside the window (${Math.round(box.left)} to ${Math.round(box.right)} px in ${width} px)`);
    }

    return problems;
  });
}

export const rememberPresses = () => {
  const start = () => {
    const memory = document.documentElement as unknown as { __sweepPresses?: EventTarget[] };
    if (memory.__sweepPresses) return;
    memory.__sweepPresses = [];
    const remember = (event: Event) => {
      if (event instanceof KeyboardEvent && event.key !== 'Enter' && event.key !== ' ') return;
      memory.__sweepPresses!.push(event.target as EventTarget);
      if (memory.__sweepPresses!.length > 20) memory.__sweepPresses!.shift();
    };
    document.addEventListener('pointerdown', remember, true);
    document.addEventListener('keydown', remember, true);
  };
  if (document.documentElement) start();
  else document.addEventListener('readystatechange', start, { once: true });
};

export async function focusStaysInside(page: Page, shown: Locator): Promise<string | null> {
  const stops = await shown.evaluate((dialog) => dialog.querySelectorAll('a[href], button:not([disabled]), input:not([type="hidden"]):not([disabled]), select, textarea, summary, [tabindex]:not([tabindex="-1"])').length);
  for (let press = 0; press <= Math.min(stops, 40); press++) {
    await page.keyboard.press('Tab');
    const inside = await shown.evaluate((dialog) => dialog.contains(document.activeElement));
    if (!inside) {
      const where = await page.evaluate(() => { const active = document.activeElement; return active ? `${active.tagName.toLowerCase()} "${(active.getAttribute('aria-label') ?? active.textContent ?? '').trim().slice(0, 40)}"` : 'nothing'; });
      return `Tab ${press + 1} took the focus out of the dialog, to ${where}`;
    }
  }

  return null;
}

export async function openerOf(page: Page, shown: Locator) {
  const dialog = await shown.elementHandle();
  return page.evaluateHandle((element) => {
    const presses = (document.documentElement as unknown as { __sweepPresses?: EventTarget[] }).__sweepPresses ?? [];
    for (let index = presses.length - 1; index >= 0; index--) {
      const press = presses[index] as Element;
      if (press instanceof Element && !(element as Element).contains(press)) return press;
    }

    return null;
  }, dialog);
}

export async function focusReturn(page: Page, opener: Awaited<ReturnType<typeof openerOf>>): Promise<string | null> {
  return page.evaluate((pressed) => {
    const describe = (element: Element) => `${element.tagName.toLowerCase()} "${(element.getAttribute('aria-label') ?? element.textContent ?? '').trim().replace(/\s+/g, ' ').slice(0, 40)}"`;
    const active = document.activeElement;
    if (!active || active === document.body || active === document.documentElement) return 'the focus fell to the page\'s body';
    const box = active.getBoundingClientRect();
    if (box.width === 0 && box.height === 0) return `the focus is on ${describe(active)}, which does not show`;
    const opener = pressed as Element | null;
    if (!opener || !opener.isConnected || opener.getBoundingClientRect().width === 0) return null;
    if (active === opener || opener.contains(active) || active.contains(opener)) return null;
    const row = (element: Element) => element.closest('tr, [role="row"], [role="treeitem"], li, .list-card');
    if (row(opener) && row(opener) === row(active)) return null;
    return `the focus went to ${describe(active)}, not back to ${describe(opener)}, which opened the dialog`;
  }, opener);
}

export function sweeper(page: Page, part: SweepPart) {
  const found: string[] = [];
  const used = new Set<AllowedViolation>();
  const familyWidths = new Map<string, Set<number>>();
  const focusOrderChecked = new Set<string>();
  const pagesChecked = new Set<string>();
  const pageKey = (actor: string, path: string, at: number) => `${actor.replace(/ \(\d+\)$/, '')}|${path}|${at}`;
  const width = () => page.viewportSize()?.width ?? workingWidth;
  let remembering = false;

  async function check(label: string): Promise<void> {
    await waitForSettled(page);
    for (const violation of await seriousViolations(page)) {
      const rule = violation.split(' ')[0];
      const allowed = allowedViolations.find((entry) => entry.part === part && entry.rule === rule && entry.where.test(label));
      if (allowed) used.add(allowed);
      else found.push(`${label}: ${violation}`);
    }
    for (const problem of await layoutProblems(page)) found.push(`${label} at ${width()} px: ${problem}`);
  }

  async function atWidths(label: string, widths: number[], after?: (viewport: { width: number; height: number }) => Promise<void>): Promise<void> {
    if (widths.length === 0) return;
    const back = page.viewportSize() ?? sweepViewports[1];
    for (const viewport of sweepViewports.filter((candidate) => widths.includes(candidate.width))) {
      await page.setViewportSize(viewport);
      await check(`${label} (${viewport.width})`);
      if (after) await after(viewport);
    }
    await page.setViewportSize(back);
    await waitForSettled(page);
  }

  async function visit(actor: string, path: string): Promise<void> {
    await page.goto(path);
    await waitForReady(page);
    await check(`${actor} ${path}`);
    pagesChecked.add(pageKey(actor, path, width()));
    if (width() !== workingWidth) return;
    const others = sweepViewports.map((viewport) => viewport.width).filter((other) => other !== workingWidth && !pagesChecked.has(pageKey(actor, path, other)));
    await atWidths(`${actor} ${path}`, others, async (viewport) => { pagesChecked.add(pageKey(actor, path, viewport.width)); });
  }

  async function dialog(label: string, component: string, open: () => Promise<void>, options: { close?: (shown: Locator) => Promise<void>; shotName?: string; wide?: boolean } = {}): Promise<void> {
    if (!remembering) {
      await page.addInitScript(rememberPresses);
      remembering = true;
    }
    await page.evaluate(rememberPresses);
    const below = await page.getByRole('dialog').count();
    await open();
    await expect(page.getByRole('dialog'), `the dialog "${label}" opens`).toHaveCount(below + 1);
    const shown = page.getByRole('dialog').nth(below);
    await expect(shown, `the dialog "${label}" opens`).toBeVisible();
    const formDialog = shown.locator('[data-form-dialog]');
    if (await formDialog.count() > 0) await expect(formDialog.first(), `the dialog "${label}" is ${component}`).toHaveAttribute('data-form-dialog', component);
    const opener = await openerOf(page, shown);
    await check(label);
    if (options.shotName) await shot(page, 'dialogs', options.shotName);
    const widths = familyWidths.get(component) ?? new Set<number>();
    familyWidths.set(component, widths);
    widths.add(width());
    if (width() === workingWidth) {
      const wideShot = options.shotName && options.wide;
      const others = sweepViewports.map((viewport) => viewport.width).filter((other) => other !== workingWidth && (!widths.has(other) || (other === 1500 && wideShot)));
      await atWidths(label, others, async (viewport) => {
        widths.add(viewport.width);
        if (viewport.width === 1500 && wideShot) await shot(page, 'dialogs', options.shotName!.replace(/-1280$/, '-1500'));
      });
    }
    if (!focusOrderChecked.has(component)) {
      focusOrderChecked.add(component);
      const escaped = await focusStaysInside(page, shown);
      if (escaped) found.push(`${label}: ${escaped}`);
    }
    if (options.close) {
      await options.close(shown);
    } else {
      await page.keyboard.press('Escape');
      const closed = await shown.waitFor({ state: 'hidden', timeout: 1500 }).then(() => true, () => false);
      if (!closed) await shown.getByRole('button', { name: 'Close', exact: true }).first().click();
    }
    await expect(page.getByRole('dialog'), `the dialog "${label}" closes`).toHaveCount(below);
    await waitForSettled(page);
    const lost = await focusReturn(page, opener);
    if (lost) found.push(`${label}: ${lost}`);
    await opener.dispose();
  }

  const fromPage = (path: string, click: (page: Page) => Promise<void>) => async () => {
    await page.goto(path);
    await waitForReady(page);
    await waitForSettled(page);
    await click(page);
  };

  function finish(): void {
    expect(found, 'serious or critical violations the allow-list does not name, and layout and focus problems').toEqual([]);
    const stale = allowedViolations.filter((entry) => entry.part === part && !used.has(entry)).map((entry) => `${entry.knownIssue} ${entry.rule} ${entry.where}`);
    expect(stale, 'allow-list entries that matched nothing: remove them with the known issue they name').toEqual([]);
  }

  return { check, visit, dialog, fromPage, finish };
}
