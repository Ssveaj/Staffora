import type { Browser, Locator, Page } from '@playwright/test';
import { persona, personaKeys, personaName, readFoundationState } from './lib/foundation-state';
import { expect, test } from './lib/page-health';
import { enterSecret, expectSettledPageAccessible, gotoReady, impersonate, loginAs, waitForReady, waitForSettled } from './lib/session';
import { shot } from './lib/shots';
import { chooseEveryWeek } from './lib/jobs';

const width = 1280;

async function capture(page: Page, name: string): Promise<void> {
  await page.mouse.move(0, 0);
  await expectSettledPageAccessible(page);
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow, 'the page is no wider than the window').toBeLessThanOrEqual(0);
  const style = await page.addStyleTag({
    content: '.app-shell { height: auto !important; min-height: 100dvh; overflow: visible !important; } .app-shell .app-shell__body { overflow: visible !important; }',
  });
  try {
    await shot(page, 'portal', name, [page.locator('.state-entity-dialog-footer__notice')]);
  } finally {
    await style.evaluate((element) => element.remove());
  }
}

async function takeStep(page: Page, dialog: Locator, key: string): Promise<void> {
  const button = dialog.locator(`button[data-transition="${key}"]`);
  await button.focus();
  await expect(button).toBeFocused();
  await page.keyboard.press('Enter');
}

async function confirmStep(page: Page, dialog: Locator, key: string, label: string): Promise<void> {
  await takeStep(page, dialog, key);
  const confirm = page.locator('.form-dialog').filter({ has: page.getByRole('button', { name: label, exact: true }) });
  await confirm.getByRole('button', { name: label, exact: true }).click();
  await expect(confirm).toHaveCount(0);
}

async function writeAndSend(page: Page, orderName: string): Promise<void> {
  await page.goto('/portal/orders/new');
  await waitForReady(page);
  const fresh = page.getByRole('dialog', { name: 'New order', exact: true });
  await chooseEveryWeek(fresh);
  await fresh.locator('[name="order-name"]').fill(orderName);
  await takeStep(page, fresh, 'order-save-draft');
  const dialog = page.getByRole('dialog', { name: orderName, exact: true });
  await expect(dialog.locator('.state-pill__button')).toHaveAttribute('data-state', 'Registration');
  await confirmStep(page, dialog, 'order-send', 'Send');
  await expect(dialog.locator('.state-pill__button')).toHaveAttribute('data-state', 'Sent');
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(page.locator('[data-order-details="ready"]')).toBeVisible();
}

async function coordinatorPage(browser: Browser): Promise<Page> {
  const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  const page = await context.newPage();
  await loginAs(page, persona(personaKeys.coordinator, readFoundationState()));
  return page;
}

async function openJob(page: Page, orderName: string): Promise<Locator> {
  await gotoReady(page, `/staffing/jobs?q=${encodeURIComponent(orderName)}`);
  await page.locator('tr.list-row').filter({ hasText: orderName }).click();
  const dialog = page.getByRole('dialog', { name: orderName, exact: true });
  await expect(dialog.locator('.state-pill__button')).toBeVisible();
  return dialog;
}

async function expectPortalHome(page: Page, supplier: string): Promise<void> {
  await expect(page).toHaveURL(/\/portal$/);
  await expect(page.getByRole('heading', { name: 'Your portal', level: 1 })).toBeVisible();
  await expect(page.locator('[data-portal-home="ready"]')).toBeVisible();
  await expect(page.locator('[data-portal-supplier]')).toContainText(supplier);
}

async function expectNotPartOfThePortal(page: Page): Promise<void> {
  await expect(page.getByRole('heading', { name: 'Not part of your portal', level: 1 })).toBeVisible();
  await expect(page.getByText('This page is not part of your portal.')).toBeVisible();
  await expect(page.locator('.list-view, [data-list-view], table')).toHaveCount(0);
  await expect(page.getByRole('link', { name: 'Go to your portal\'s home', exact: true })).toHaveAttribute('href', '/portal');
}

test('a staffing company enables a customer\'s portal and its first customer administrator signs in to the portal\'s home', { tag: '@portal' }, async ({ page, browser }) => {
  test.setTimeout(180_000);
  await page.setViewportSize({ width: 1280, height: 800 });
  const state = readFoundationState();
  const run = `${Date.now().toString(36)}${Math.floor(Math.random() * 1000)}`;
  const customer = `Seaview College ${run}`;
  const administrator = { first: 'Noor', last: `Sand ${run}`, email: `noor.sand.${run}@seaview.example.com` };
  await loginAs(page, persona(personaKeys.platformOwner, state));
  await impersonate(page, state.tenantAdministrator);

  await gotoReady(page, '/staffing/customers');
  await page.getByRole('button', { name: 'New customer', exact: true }).click();
  const fresh = page.getByRole('dialog', { name: 'New customer', exact: true });
  await fresh.locator('[name="customer-name"]').fill(customer);
  await fresh.locator('button[data-transition="customer-save-draft"]').click();
  const dialog = page.getByRole('dialog', { name: customer, exact: true });
  await expect(dialog.locator('.state-pill__button')).toHaveAttribute('data-state', 'Create');
  await dialog.locator('button[data-transition="customer-activate"]').click();
  await expect(dialog.locator('.state-pill__button')).toHaveAttribute('data-state', 'Active');
  await expect(dialog.locator('[data-customer-portal]')).toHaveAttribute('data-customer-portal', 'none');

  await dialog.locator('button[data-transition="customer-enable-portal"]').click();
  const form = page.getByRole('dialog', { name: `Enable the portal of ${customer}`, exact: true });
  await expect(form.locator('[data-form-dialog]')).toHaveAttribute('data-form-dialog', 'EnableCustomerPortalDialog');
  await form.getByLabel('First name').fill(administrator.first);
  await form.getByLabel('Last name').fill(administrator.last);
  await form.getByLabel('Email').fill(administrator.email);
  await form.getByLabel('Email').press('Tab');
  await capture(page, '01-enable-portal-form-1280');
  await form.getByRole('button', { name: 'Enable portal', exact: true }).click();
  await expect(form).toBeHidden();

  const notice = dialog.locator('.state-entity-dialog-footer__notice');
  await expect(notice).toContainText('The portal is enabled.');
  const password = /initial password (\S{16}) - give it/.exec((await notice.textContent()) ?? '')?.[1] ?? '';
  expect(password, 'the footer shows the administrator\'s initial password').toMatch(/^\S{16}$/);
  expect(page.url()).not.toContain(password);
  await expect(dialog.locator('[data-customer-portal]')).toHaveAttribute('data-customer-portal', 'enabled');
  await expect(dialog.locator('[data-customer-portal]')).toContainText('Customer portal: enabled.');
  await expect(dialog.locator('button[data-transition="customer-enable-portal"]')).toHaveCount(0);
  await capture(page, '02-portal-enabled-1280');

  const portalContext = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  try {
    const portalPage = await portalContext.newPage();
    await gotoReady(portalPage, '/Account/Login');
    await portalPage.getByLabel('Email').fill(administrator.email);
    await enterSecret(portalPage.getByLabel('Password'), password);
    await portalPage.getByRole('button', { name: 'Sign in', exact: true }).click();
    await expect(portalPage).not.toHaveURL(/\/Account\/Login/);
    await waitForReady(portalPage);
    await expectPortalHome(portalPage, state.tenantAdministrator.tenant);
    await capture(portalPage, '03-new-administrator-home-1280');
  } finally {
    await portalContext.close();
  }
});

test.describe(`the customer portal`, { tag: '@portal' }, () => {
  test.use({ viewport: { width, height: 800 } });

  test('the customer administrator lands on the portal\'s home and a staffing company\'s page is not part of their portal', async ({ page }) => {
    const state = readFoundationState();
    await loginAs(page, persona(personaKeys.customerAdmin, state));
    await expectPortalHome(page, state.tenantAdministrator.tenant);

    const links = await page.locator('a.app-nav__link').evaluateAll((elements) => elements.map((element) => element.getAttribute('href')));
    expect(links).toEqual(['/portal', '/portal/orders', '/portal/orders/new']);
    await capture(page, `04-portal-home-${width}`);
    await page.setViewportSize({ width: 390, height: 844 });
    await capture(page, '04-portal-home-390');
    await page.setViewportSize({ width, height: 800 });

    for (const path of ['/staffing/jobs', '/tenant/management/users', '/coordination', '/me/overview']) {
      await gotoReady(page, path);
      await expectNotPartOfThePortal(page);
    }
    await capture(page, `05-not-part-of-the-portal-${width}`);
    await page.setViewportSize({ width: 390, height: 844 });
    await capture(page, '05-not-part-of-the-portal-390');
    await page.setViewportSize({ width, height: 800 });
    await page.getByRole('link', { name: 'Go to your portal\'s home', exact: true }).click();
    await expectPortalHome(page, state.tenantAdministrator.tenant);

    await gotoReady(page, '/me/profile');
    await expect(page.getByRole('heading', { name: 'Not part of your portal', level: 1 })).toHaveCount(0);
  });

  test('the customer administrator writes and sends an order, and the staffing company\'s coordinator is told', { tag: '@smoke' }, async ({ page, browser }) => {
    test.setTimeout(180_000);
    const state = readFoundationState();
    const admin = persona(personaKeys.customerAdmin, state);
    const supplier = state.tenantAdministrator.tenant;
    const run = `${Date.now().toString(36)}${Math.floor(Math.random() * 1000)}`;
    const orderName = `Portal algebra ${width} ${run}`;
    await loginAs(page, admin);
    await expectPortalHome(page, supplier);

    await page.goto('/portal/orders/new');
    await waitForReady(page);
    const fresh = page.getByRole('dialog', { name: 'New order', exact: true });
    await chooseEveryWeek(fresh);
    await expect(fresh).toBeVisible();
    await expect(fresh.locator('[data-order-supplier]')).toHaveText(supplier);
    await expect(fresh.locator('[data-order-orderer]')).toHaveText(personaName(admin));
    await fresh.locator('[name="order-name"]').fill(orderName);
    await fresh.locator('[name="order-description"]').fill('Two staff members who know the year 9 curriculum.');
    await fresh.locator('[name="order-description"]').press('Tab');
    await capture(page, `07-new-order-${width}`);

    await takeStep(page, fresh, 'order-save-draft');
    const dialog = page.getByRole('dialog', { name: orderName, exact: true });
    await expect(dialog.locator('.state-pill__button')).toHaveAttribute('data-state', 'Registration');
    await takeStep(page, dialog, 'order-send');
    const confirm = page.locator('.form-dialog').filter({ has: page.getByRole('button', { name: 'Send', exact: true }) });
    await expect(confirm).toContainText('It can no longer be changed once it is sent.');
    await confirm.getByRole('button', { name: 'Send', exact: true }).click();
    await expect(confirm).toHaveCount(0);
    await expect(dialog.locator('.state-pill__button')).toHaveAttribute('data-state', 'Sent');
    await expect(dialog.locator('[name="order-name"]')).toHaveAttribute('readonly');
    await capture(page, `08-order-sent-${width}`);

    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await expect(page).toHaveURL(/\/portal\/orders\/[0-9a-f-]{36}$/);
    await expect(page.locator('[data-order-details="ready"]')).toBeVisible();
    await expect(page.locator('[data-order-state]')).toHaveAttribute('data-order-state', 'Sent');
    await expect(page.locator('[data-order-name]')).toHaveText(orderName);
    expect((await page.locator('.order-details').boundingBox())!.width).toBeLessThanOrEqual(864.5);
    await expect(page.locator('.order-details__badge[data-order-state]')).toBeVisible();
    await capture(page, `09-order-page-${width}`);
    await gotoReady(page, `/portal/orders?q=${encodeURIComponent(orderName)}`);
    const row = page.locator('tr.list-row').filter({ hasText: orderName });
    await expect(row).toHaveCount(1);
    await expect(row).toContainText('Sent');
    await capture(page, `10-my-orders-${width}`);

    const coordinatorContext = await browser.newContext({ viewport: { width, height: 800 } });
    try {
      const coordinator = await coordinatorContext.newPage();
      await loginAs(coordinator, persona(personaKeys.coordinator, state));
      await coordinator.locator('[data-notice-bell] .notice-bell__trigger').click();
      const panel = coordinator.locator('[data-notice-panel]');
      await expect(panel).toBeVisible();
      await panel.getByRole('button', { name: 'All', exact: true }).click();
      await waitForSettled(coordinator);
      const notice = panel.locator('.notice-row').filter({ hasText: orderName }).first();
      await expect(notice).toContainText(`${state.staffing.customer} ordered ${orderName}`);
      await capture(coordinator, `11-coordinator-told-${width}`);
      await notice.click();
      const received = coordinator.getByRole('dialog', { name: orderName, exact: true });
      await expect(received.locator('.state-pill__button')).toHaveAttribute('data-state', 'AwaitingCoordination');
      await expect(received.locator('input[name="job-contact"]')).toHaveValue(personaName(admin));
      await expect.poll(() => received.locator('input[name="job-unit"]').inputValue()).toContain(supplier);
      await capture(coordinator, `12-coordinator-job-${width}`);
    } finally {
      await coordinatorContext.close();
    }
  });

  test('the customer administrator creates an orderer, shown the initial password once, who signs in and sees only their own orders', async ({ page, browser }) => {
    test.setTimeout(180_000);
    const state = readFoundationState();
    const supplier = state.tenantAdministrator.tenant;
    const run = `${Date.now().toString(36)}${Math.floor(Math.random() * 1000)}`;
    const orderer = { first: 'Avery', last: `Lind ${run}`, email: `avery.lind.${run}@brightpath.example.com` };
    await loginAs(page, persona(personaKeys.customerAdmin, state));
    await test.step('set-up: an order of the administrator', () => writeAndSend(page, `Kept from the orderer ${run}`));
    await gotoReady(page, '/portal');
    await expectPortalHome(page, supplier);

    await page.locator('[data-portal-users-link]').click();
    await expect(page).toHaveURL(/\/portal\/users/);
    await waitForReady(page);
    await expect(page.getByRole('heading', { name: 'Users', level: 1 })).toBeVisible();
    await expect(page.locator('tr.list-row').filter({ hasText: personaName(persona(personaKeys.customerAdmin, state)) })).toContainText('Customer administrator');
    await capture(page, `16-portal-users-${width}`);

    await page.getByRole('button', { name: 'New user', exact: true }).click();
    const fresh = page.getByRole('dialog', { name: 'New user', exact: true });
    await fresh.locator('[name="user-first-name"]').fill(orderer.first);
    await fresh.locator('[name="user-last-name"]').fill(orderer.last);
    await fresh.locator('[name="user-email"]').fill(orderer.email);
    await fresh.locator('[name="user-username"]').fill(orderer.email);
    await fresh.locator('[name="user-username"]').press('Tab');
    await expect(fresh.locator('button[data-transition="user-portal-create-administrator"]')).toBeVisible();
    await expect(fresh.locator('button[data-transition="user-create"]')).toHaveCount(0);
    await takeStep(page, fresh, 'user-portal-create-orderer');
    const created = page.getByRole('dialog', { name: `${orderer.first} ${orderer.last}`, exact: true });
    const notice = created.locator('.state-entity-dialog-footer__notice');
    await expect(notice).toContainText('Their initial password is ');
    const password = /initial password is (\S{16}) /.exec((await notice.textContent()) ?? '')?.[1] ?? '';
    expect(password, 'the footer shows the orderer\'s initial password').toMatch(/^\S{16}$/);
    expect(page.url()).not.toContain(password);
    await expect(created.locator('.state-pill__button')).toHaveAttribute('data-state', 'Active');
    await expect(created.locator('button[data-transition="user-portal-deactivate"]')).toBeVisible();
    await expect(created.locator('[name="user-first-name"]')).toHaveAttribute('readonly');
    await capture(page, `17-orderer-created-${width}`);

    await page.reload();
    await waitForReady(page);
    const restored = page.getByRole('dialog', { name: `${orderer.first} ${orderer.last}`, exact: true });
    await expect(restored.locator('.state-pill__button')).toHaveAttribute('data-state', 'Active');
    await expect(restored.locator('.state-entity-dialog-footer__notice')).toHaveCount(0);
    await expect(page.locator('body')).not.toContainText(password);

    const ordererContext = await browser.newContext({ viewport: { width, height: 800 } });
    try {
      const ordererPage = await ordererContext.newPage();
      await gotoReady(ordererPage, '/Account/Login');
      await ordererPage.getByLabel('Email').fill(orderer.email);
      await enterSecret(ordererPage.getByLabel('Password'), password);
      await ordererPage.getByRole('button', { name: 'Sign in', exact: true }).click();
      await expect(ordererPage).not.toHaveURL(/\/Account\/Login/);
      await waitForReady(ordererPage);
      await expectPortalHome(ordererPage, supplier);
      await expect(ordererPage.locator('[data-portal-users-link]')).toHaveCount(0);
      await gotoReady(ordererPage, '/portal/orders');
      await expect(ordererPage.locator('tr.list-row')).toHaveCount(0);
      await expect(ordererPage.locator('.list-view__hidden')).toBeVisible();
      await capture(ordererPage, `18-orderer-my-orders-${width}`);
      await gotoReady(ordererPage, '/portal/users');
      await expect(ordererPage.getByText('Only a customer administrator manages your organization\'s users.')).toBeVisible();
    } finally {
      await ordererContext.close();
    }
  });

  test('the orderer and the coordinator talk in the customer thread through the outbox', async ({ page, browser }) => {
    test.setTimeout(240_000);
    const state = readFoundationState();
    const run = `${Date.now().toString(36)}${Math.floor(Math.random() * 1000)}`;
    const orderName = `Portal music ${width} ${run}`;
    const question = `Can the staff member bring a piano? ${run}`;
    const answer = `Yes, a keyboard. ${run}`;
    await loginAs(page, persona(personaKeys.orderer, state));
    await writeAndSend(page, orderName);

    await page.getByRole('button', { name: 'Open order', exact: true }).click();
    const request = page.getByRole('dialog', { name: orderName, exact: true });
    await request.getByRole('tab', { name: /Messages with supplier/ }).click();
    const messages = request.locator('.state-entity-dialog-side__panel[data-thread="customer"]');
    await messages.locator('[name="comment-customer"]').fill(question);
    await messages.locator('[name="comment-customer"]').press('Enter');
    await expect(messages.locator('.comment-bubble__text').filter({ hasText: question })).toHaveCount(1);
    await capture(page, `19-orderer-message-${width}`);
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);
    const requestPage = page.url();

    const coordinator = await coordinatorPage(browser);
    try {
      let dialog: Locator | undefined;
      await expect(async () => {
        dialog = await openJob(coordinator, orderName);
        await dialog.getByRole('tab', { name: /Customer/ }).click();
        await expect(dialog.locator('.state-entity-dialog-side__panel[data-thread="customer"] .comment-bubble__text').filter({ hasText: question })).toHaveCount(1, { timeout: 2_000 });
      }).toPass({ timeout: 90_000, intervals: [1_000, 2_000, 3_000] });
      const thread = dialog!.locator('.state-entity-dialog-side__panel[data-thread="customer"]');
      await expect(thread.locator('[data-attention-text]')).toHaveText('Awaits your reply');
      await expect(thread.locator('.comment-bubble__author').first()).toContainText(personaName(persona(personaKeys.orderer, state)));
      await capture(coordinator, `20-coordinator-customer-thread-${width}`);

      await thread.locator('[name="comment-customer"]').fill(answer);
      await thread.locator('[name="comment-customer"]').press('Enter');
      await expect(thread.locator('.comment-bubble__text').filter({ hasText: answer })).toHaveCount(1);
      await coordinator.keyboard.press('Escape');

      await expect(async () => {
        await gotoReady(page, requestPage);
        await page.getByRole('button', { name: 'Open order', exact: true }).click();
        const reopened = page.getByRole('dialog', { name: orderName, exact: true });
        await reopened.getByRole('tab', { name: /Messages with supplier/ }).click();
        await expect(reopened.locator('.state-entity-dialog-side__panel[data-thread="customer"] .comment-bubble__text').filter({ hasText: answer })).toHaveCount(1, { timeout: 2_000 });
      }).toPass({ timeout: 90_000, intervals: [1_000, 2_000, 3_000] });
      const reply = page.getByRole('dialog', { name: orderName, exact: true }).locator('.state-entity-dialog-side__panel[data-thread="customer"]');
      await expect(reply.locator('[data-attention-text]')).toHaveText('Awaits your reply');
      await expect(reply.locator('.comment-bubble__author').last()).toHaveText(state.tenantAdministrator.tenant);
      await capture(page, `21-orderer-reply-${width}`);
      await page.keyboard.press('Escape');
      await page.locator('[data-notice-bell] .notice-bell__trigger').click();
      const panel = page.locator('[data-notice-panel]');
      await panel.getByRole('button', { name: 'All', exact: true }).click();
      await waitForSettled(page);
      await expect(panel.locator('.notice-row').filter({ hasText: `${state.tenantAdministrator.tenant} wrote about ${orderName}` }).first()).toBeVisible();
    } finally {
      await coordinator.context().close();
    }
  });

  test('a platform owner impersonating the customer administrator is held to the portal too', async ({ page }) => {
    const state = readFoundationState();
    const admin = persona(personaKeys.customerAdmin, state);
    await loginAs(page, persona(personaKeys.platformOwner, state));
    await impersonate(page, { tenant: state.staffing.customer, name: personaName(admin), email: admin.email });
    await expectPortalHome(page, state.tenantAdministrator.tenant);
    await gotoReady(page, '/staffing/customers');
    await expectNotPartOfThePortal(page);
    await capture(page, `06-impersonated-not-part-${width}`);
  });
});
