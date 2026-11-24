import { expect, test } from '@playwright/test';
import { databaseOf, persona, personaKeys, readFoundationState } from './lib/foundation-state';
import { readSlots } from './lib/slot';
import { slotConnection } from '../../scripts/e2e-slots.mjs';

const maximumAgeMs = 60 * 60 * 1000;

test('every slot\'s foundation state holds the verified personas from this run: the platform owner, the coordinator, the two staff members and the customer administrator', async () => {
  const slots = readSlots();
  expect(slots.slots.map((each) => each.slot), 'the server started a slot for every worker (STAFFORA_E2E_WORKERS and --workers agree)').toEqual(
    Array.from({ length: Math.max(slots.slots.length, test.info().config.workers) }, (_, index) => index),
  );
  const base = process.env.STAFFORA_E2E_CONNECTION ?? '';
  expect(databaseOf(base), 'STAFFORA_E2E_CONNECTION names no database').toBeTruthy();

  for (const slot of slots.slots) {
    await test.step(`slot ${slot.slot} (${slot.baseURL})`, async () => {
      const state = readFoundationState(slot.slot);

      const database = databaseOf(slotConnection(base, slot.slot));
      expect(state.database.toLowerCase()).toBe(database!.toLowerCase());
      expect(slot.database.toLowerCase()).toBe(database!.toLowerCase());
      expect(state.database.toLowerCase()).toContain('e2e');

      const preparedAt = Date.parse(state.preparedAt);
      expect(preparedAt).not.toBeNaN();
      const age = Date.now() - preparedAt;
      expect(age, `the state was prepared at ${state.preparedAt}`).toBeGreaterThan(-60_000);
      expect(age, `the state was prepared at ${state.preparedAt}, more than an hour ago`).toBeLessThan(maximumAgeMs);

      const owner = persona(personaKeys.platformOwner, state);
      expect(owner.actor).toBe('Platform owner');
      expect(owner.email).toContain('@');
      expect(owner.password.length).toBeGreaterThanOrEqual(16);

      expect(state.personas.map((each) => each.key)).toEqual([personaKeys.platformOwner, personaKeys.coordinator, personaKeys.staff, personaKeys.secondStaff, personaKeys.customerAdmin, personaKeys.orderer]);
      for (const key of [personaKeys.coordinator, personaKeys.staff, personaKeys.secondStaff]) {
        const tenantPersona = persona(key, state);
        expect(tenantPersona.email).toContain('@company-a.example.com');
        expect(tenantPersona.password.length).toBeGreaterThanOrEqual(16);
      }

      for (const key of [personaKeys.customerAdmin, personaKeys.orderer]) {
        const portalPersona = persona(key, state);
        expect(portalPersona.email).toContain('@brightpath.example.com');
        expect(portalPersona.password.length).toBeGreaterThanOrEqual(16);
      }

      expect(state.staffing.customer).toBeTruthy();
      expect(state.staffing.qualification).toBeTruthy();
    });
  }
});
