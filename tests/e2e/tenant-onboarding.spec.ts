import type { Page } from '@playwright/test';
import { persona, personaKeys, readFoundationState } from './lib/foundation-state';
import { journeyStep, loadJourney } from './lib/journey-contract';
import { expect, test } from './lib/page-health';
import { openTenantConsole, openTenantDialog, tenantList } from './lib/platform-console';
import { enterSecret, expectSettledPageAccessible, gotoReady, loginAs, waitForReady } from './lib/session';
import { shot } from './lib/shots';

const journey = loadJourney('tenant-onboarding');

async function capture(page: Page, name: string): Promise<void> {
  await expectSettledPageAccessible(page);
  await shot(page, journey.id, name, [page.locator('.tenant-onboarding__password'), page.locator('.state-entity-dialog-footer__notice')]);
}

test('a company is onboarded: its tenant and first system owner, who signs in and appoints a coordinator, and the tenant is put in use', { tag: '@platform' }, async ({ page, browser }) => {
  test.setTimeout(180_000);
  await page.setViewportSize({ width: 1280, height: 800 });
  const run = `${Date.now().toString(36)}${Math.floor(Math.random() * 1000)}`;
  const company = `Brightwater Staffing ${run}`;
  const owner = { first: 'Maja', last: `Berg ${run}`, email: `maja.berg.${run}@brightwater.example.com` };
  const coordinator = { first: 'Leo', last: `Strand ${run}`, email: `leo.strand.${run}@brightwater.example.com` };
  await loginAs(page, persona(personaKeys.platformOwner, readFoundationState()));

  const form = page.getByRole('dialog', { name: 'New tenant', exact: true });
  await journeyStep(journey, 'create-tenant', async () => {
    await gotoReady(page, tenantList);
    await page.getByRole('button', { name: 'New Tenant', exact: true }).click();
    await form.getByLabel('Tenant name').fill(company);
    await form.getByLabel('Description').fill('Places staff members on jobs along the coast.');
    await capture(page, '01-create-tenant');
  });

  let password = '';
  await journeyStep(journey, 'give-first-owner', async () => {
    await form.getByLabel('First name').fill(owner.first);
    await form.getByLabel('Last name').fill(owner.last);
    await form.getByLabel('Email').fill(owner.email);
    await form.getByLabel('Email').press('Tab');
    await form.getByRole('button', { name: 'Create tenant', exact: true }).click();

    password = (await form.locator('.tenant-onboarding__password code').textContent())?.trim() ?? '';
    expect(password, 'the form shows the owner\'s initial password').toMatch(/^\S{16}$/);
    await expect(form).toContainText(owner.email);
    expect(page.url()).not.toContain(password);
    await capture(page, '02-give-first-owner');

    await form.locator('div[role=checkbox][aria-label="I have copied the password"]').click();
    await form.getByRole('button', { name: 'Done', exact: true }).click();
    const dialog = page.getByRole('dialog', { name: company, exact: true });
    await expect(dialog.locator('.state-pill__button')).toHaveAttribute('data-state', 'Setup');
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);
  });

  const ownerContext = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  try {
    const ownerPage = await ownerContext.newPage();
    await journeyStep(journey, 'owner-signs-in', async () => {
      await gotoReady(ownerPage, '/Account/Login');
      await ownerPage.getByLabel('Email').fill(owner.email);
      await enterSecret(ownerPage.getByLabel('Password'), password);
      await ownerPage.getByRole('button', { name: 'Sign in', exact: true }).click();
      await expect(ownerPage).not.toHaveURL(/\/Account\/Login/);
      await waitForReady(ownerPage);

      await gotoReady(ownerPage, '/tenant/management/users');
      await expect(ownerPage.getByRole('heading', { name: 'Users', level: 1 })).toBeVisible();
      await expect(ownerPage.locator('tr.list-row').filter({ hasText: owner.email })).toHaveCount(1);
      await capture(ownerPage, '03-owner-signs-in');
    });

    await journeyStep(journey, 'appoint-coordinator', async () => {
      await ownerPage.getByRole('button', { name: 'New User', exact: true }).click();
      const created = ownerPage.getByRole('dialog', { name: 'New user', exact: true });
      await created.locator('[name="user-first-name"]').fill(coordinator.first);
      await created.locator('[name="user-last-name"]').fill(coordinator.last);
      await created.locator('[name="user-email"]').fill(coordinator.email);
      await created.locator('[name="user-username"]').fill(coordinator.email);
      await created.locator('button[data-transition="user-create"]').click();
      const dialog = ownerPage.getByRole('dialog', { name: `${coordinator.first} ${coordinator.last}`, exact: true });
      await expect(dialog.locator('.state-pill__button')).toHaveAttribute('data-state', 'Active');

      await dialog.getByRole('tab', { name: 'Roles' }).click();
      await dialog.getByRole('button', { name: 'Add role', exact: true }).click();
      const add = ownerPage.locator('.form-dialog').filter({ has: ownerPage.getByRole('button', { name: 'Add role', exact: true }) });
      await expect(add).toBeVisible();
      await add.locator('.rz-dropdown').first().click();
      await ownerPage.getByRole('option', { name: 'Coordinator', exact: true }).click();
      await add.getByRole('button', { name: 'Add role', exact: true }).click();
      await expect(add).toBeHidden();
      const holding = dialog.locator('tr.list-row').filter({ hasText: 'Coordinator' });
      await expect(holding).toHaveCount(1);
      await expect(holding).toContainText(company);
      await capture(ownerPage, '04-appoint-coordinator');
    });
  } finally {
    await ownerContext.close();
  }

  await journeyStep(journey, 'activate-tenant', async () => {
    await openTenantConsole(page, company, 'Users');
    for (const [email, role] of [[owner.email, 'System owner'], [coordinator.email, 'Coordinator']] as const) {
      const row = page.locator('tr.list-row').filter({ hasText: email });
      await expect(row).toHaveCount(1);
      await row.click();
      const user = page.getByRole('dialog');
      await expect(user).toBeVisible();
      await user.getByRole('tab', { name: 'Roles' }).click();
      await expect(user.locator('tr.list-row').filter({ hasText: role }).filter({ hasText: company })).toHaveCount(1);
      await page.keyboard.press('Escape');
      await expect(user).toBeHidden();
    }
    await expect(page.getByRole('button', { name: 'New User' })).toHaveCount(0);

    await openTenantDialog(page, company);
    const dialog = page.getByRole('dialog', { name: company, exact: true });
    await dialog.locator('.state-entity-dialog-footer button[data-transition="tenant-activate"]').click();
    await expect(dialog.locator('.state-pill__button')).toHaveAttribute('data-state', 'Active');
    await expect(dialog.locator('.state-entity-dialog-footer__bar')).toHaveAttribute('aria-busy', 'false');
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await expect(page.locator('.console-hero [data-console-state]')).toHaveText('Active');
    await capture(page, '05-activate-tenant');
  });
});
