import type { Page } from '@playwright/test';
import { persona, personaKeys, readFoundationState } from './lib/foundation-state';
import { expectTenantConsoleTree } from './lib/organization-tree';
import { expect, test } from './lib/page-health';
import { consoleTabPath, openTenantConsole, openTenantDialog, type ConsoleTab } from './lib/platform-console';
import { loginAs, waitForReady, waitForSettled } from './lib/session';
import { shot } from './lib/shots';
import { anonymousPages, ownerPages, portalPages, sweeper, sweepViewports } from './lib/sweep';

test.use({ viewport: { width: 1280, height: 800 } });

test('every page of a visitor and of a customer administrator and the order dialog are free of serious accessibility violations', { tag: '@a11y' }, async ({ page }) => {
  test.setTimeout(240_000);
  const state = readFoundationState();
  const { visit, dialog, fromPage, finish } = sweeper(page, 'sweep: visitor and portal');

  await test.step('anonymous pages', async () => {
    for (const path of anonymousPages) await visit('anonymous', path);
  });

  await test.step('a customer administrator', async () => {
    await loginAs(page, persona(personaKeys.customerAdmin, state));
    for (const path of portalPages) await visit('customer admin', path);
    await dialog('customer admin dialog: new request', 'OrderDialog', fromPage('/portal/orders', (p) => p.getByRole('button', { name: 'New order', exact: true }).click()), { shotName: 'new-request' });
  });

  finish();
});

test('every page of the platform owner, the impersonation dialog and the tenant console are free of serious accessibility violations', { tag: '@a11y' }, async ({ page }) => {
  test.setTimeout(240_000);
  const state = readFoundationState();
  const { check, visit, dialog, fromPage, finish } = sweeper(page, 'sweep: platform owner pages');

  await loginAs(page, persona(personaKeys.platformOwner, state));
  for (const path of ownerPages) await visit('owner', path);

  for (const width of [1280, 390]) {
    await page.setViewportSize({ width, height: width === 390 ? 844 : 800 });
    await dialog(`owner dialog: impersonate (${width})`, 'ImpersonateUserDialog', fromPage('/superadmin/overview', async (p) => {
      await p.getByRole('button', { name: 'Impersonate User' }).click();
      await expect(p.getByRole('dialog').locator('[data-impersonate]').first()).toBeVisible();
    }));
  }
  await page.setViewportSize({ width: 1280, height: 800 });

  await test.step('the tenant console', async () => {
    const tenant = state.tenantAdministrator.tenant;
    await openTenantConsole(page, tenant);
    await expectTenantConsoleTree(page, tenant);
    const tabs: ConsoleTab[] = ['Overview', 'Users', 'Roles', 'Custom fields'];
    for (const { width, height } of sweepViewports) {
      await page.setViewportSize({ width, height });
      for (const [index, tab] of tabs.entries()) {
        await openTenantConsole(page, tenant, tab);
        await check(`owner tenant console: ${tab} (${width})`);
        await shot(page, 'platform-console', `${String(index + 5).padStart(2, '0')}-tenant-${consoleTabPath(tab)}-${width}`);
      }
    }
    await page.setViewportSize({ width: 1280, height: 800 });
  });

  finish();
});

test('the tenant, history, custom field and error dialogs of the platform owner are free of serious accessibility violations', { tag: '@a11y' }, async ({ page }) => {
  test.setTimeout(240_000);
  const state = readFoundationState();
  const { dialog, fromPage, finish } = sweeper(page, 'sweep: platform owner dialogs');
  await loginAs(page, persona(personaKeys.platformOwner, state));

  const setupTenant = `Sweep Setup ${Date.now()}`;
  const statePill = (p: Page) => p.getByRole('dialog').locator('.state-pill__button');
  for (const width of [1280, 390]) {
    await page.setViewportSize({ width, height: width === 390 ? 844 : 800 });
    await dialog(`owner dialog: new tenant (${width})`, 'TenantOnboardingDialog', fromPage('/superadmin/tenants', async (p) => {
      await p.getByRole('button', { name: 'New Tenant' }).click();
      await expect(p.getByRole('dialog', { name: 'New tenant' }).getByLabel('Tenant name')).toBeVisible();
    }));
    await dialog(`owner dialog: tenant in Setup (${width})`, 'TenantDialog', async () => {
      if (width === 1280) {
        await page.goto('/superadmin/tenants');
        await waitForReady(page);
        await waitForSettled(page);
        const p = page;
        await p.getByRole('button', { name: 'New Tenant' }).click();
        const form = p.getByRole('dialog', { name: 'New tenant' });
        await form.getByLabel('Tenant name').fill(setupTenant);
        await form.getByLabel('First name').fill('Sweep');
        await form.getByLabel('Last name').fill('Owner');
        await form.getByLabel('Email').fill(`sweep.owner.${Date.now()}@sweep.example.com`);
        await form.getByLabel('Email').press('Tab');
        await form.getByRole('button', { name: 'Create tenant' }).click();
        await form.locator('div[role=checkbox][aria-label="I have copied the password"]').click();
        await form.getByRole('button', { name: 'Done' }).click();
      } else {
        await openTenantDialog(page, setupTenant);
      }
      await expect(statePill(page)).toHaveAttribute('data-state', 'Setup');
    });
    await dialog(`owner dialog: tenant in Active (${width})`, 'TenantDialog', async () => {
      await openTenantDialog(page, state.tenantAdministrator.tenant);
      await expect(statePill(page)).toHaveAttribute('data-state', 'Active');
    });
  }
  await page.setViewportSize({ width: 1280, height: 800 });

  await openTenantDialog(page, state.tenantAdministrator.tenant);
  const tenantDialog = page.getByRole('dialog', { name: state.tenantAdministrator.tenant });
  await expect(tenantDialog.locator('.state-pill__button')).toHaveAttribute('data-state', 'Active');
  const fromStatePill = (link: string) => async () => {
    const pill = tenantDialog.locator('.state-pill__button');
    await page.mouse.move(0, 0);
    await pill.focus();
    if ((await pill.getAttribute('aria-expanded')) !== 'true') await page.keyboard.press('Enter');
    await expect(tenantDialog.locator('.state-pill__card')).toBeVisible();
    await tenantDialog.getByRole('button', { name: link, exact: true }).click();
  };
  await dialog('owner dialog: tenant history', 'StateHistoryDialog', fromStatePill('Full history'));
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await dialog('owner dialog: user', 'UserDialog', async () => {
    await openTenantConsole(page, state.tenantAdministrator.tenant, 'Users');
    await page.locator('tr.list-row').filter({ hasText: state.tenantAdministrator.name }).click();
  });
  await dialog('owner dialog: role', 'TenantRoleDialog', async () => {
    await openTenantConsole(page, state.tenantAdministrator.tenant, 'Roles');
    await page.locator('tr.list-row').first().click();
  });
  await dialog('owner dialog: new custom field', 'ChooseCustomFieldTargetDialog', fromPage('/superadmin/settings', (p) => p.getByRole('button', { name: 'New Field' }).click()));
  await dialog('owner dialog: new custom field of users', 'UserFieldDialog', fromPage('/superadmin/settings', async (p) => {
    await p.getByRole('button', { name: 'New Field' }).click();
    const chooser = p.locator('[data-form-dialog="ChooseCustomFieldTargetDialog"]');
    await chooser.getByRole('button', { name: 'Continue' }).click();
    await expect(chooser).toHaveCount(0);
    await expect(p.locator('.state-entity-dialog')).toBeVisible();
  }));
  await dialog('owner dialog: global custom field', 'UserFieldDialog', fromPage('/superadmin/settings', (p) => p.locator('tr.list-row').filter({ hasText: 'Employee number' }).click()));
  const searchedRow = (name: string) => async (p: Page) => {
    await p.locator('input[name="custom-fields-search"]').fill(name);
    await p.locator('tr.list-row').filter({ hasText: name }).first().click();
  };
  await dialog('owner dialog: unit custom field', 'UnitFieldDialog', fromPage('/superadmin/settings', searchedRow('Floor area')));
  await dialog('owner dialog: job custom field', 'JobFieldDialog', fromPage('/superadmin/settings', searchedRow('Budget code')));
  await dialog('owner dialog: an error of Platform health', 'ErrorEventDialog', fromPage('/superadmin/errorlog', (p) => p.locator('tr.list-row').first().click()));

  finish();
});
