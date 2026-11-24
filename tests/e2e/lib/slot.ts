import { readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';

export interface Slot {
  slot: number;
  baseURL: string;
  database: string;
  stateFile: string;
}

export interface Slots {
  startedAt: string;
  dialogHost: string | null;
  slots: Slot[];
}

const slotsPath = join(resolve(__dirname, '..', '..', '..'), '.e2e', 'slots.json');

export function readSlots(): Slots {
  let slots: Slots;
  try {
    slots = JSON.parse(readFileSync(slotsPath, 'utf-8')) as Slots;
  } catch {
    throw new Error(`No slots at ${slotsPath}: scripts/e2e-server.mjs did not start, or did not finish starting.`);
  }
  if (!Array.isArray(slots.slots) || slots.slots.length === 0) throw new Error(`The slots at ${slotsPath} list no slot.`);
  return slots;
}

export function slotIndex(): number {
  return Number(process.env.TEST_PARALLEL_INDEX ?? 0);
}

export function currentSlot(index: number = slotIndex()): Slot {
  const slots = readSlots();
  const found = slots.slots.find((each) => each.slot === index);
  if (!found) {
    throw new Error(`Worker slot ${index} has no app: the server started ${slots.slots.length} slot(s). Run with as many workers as STAFFORA_E2E_WORKERS says (or set both).`);
  }
  return found;
}
