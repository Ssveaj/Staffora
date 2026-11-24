import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { currentSlot } from './slot';

export interface FoundationPersona {
  key: string;
  actor: string;
  email: string;
  password: string;
  name?: string;
}

export function personaName(found: FoundationPersona): string {
  if (!found.name) throw new Error(`The persona "${found.key}" has no name in the foundation state.`);
  return found.name;
}

export interface FoundationTenantUser {
  tenant: string;
  name: string;
  email: string;
}

export interface FoundationStaffing {
  customer: string;
  qualification: string;
  service: string;
}

export interface FoundationErrorLogEntry {
  key: string;
  level: string;
  message: string;
  daysAgo: number;
}

export interface FoundationState {
  preparedAt: string;
  database: string;
  personas: FoundationPersona[];
  impersonationTarget: FoundationTenantUser;
  tenantAdministrator: FoundationTenantUser;
  tenantViewer: FoundationTenantUser;
  errorLog: { search: string; entries: FoundationErrorLogEntry[] };
  staffing: FoundationStaffing;
}

export const personaKeys = {
  platformOwner: 'platformOwner',
  coordinator: 'coordinator',
  staff: 'staff',
  secondStaff: 'secondStaff',
  customerAdmin: 'customerAdmin',
  orderer: 'orderer',
} as const;

export const repositoryRoot = resolve(__dirname, '..', '..', '..');

export function foundationStatePath(slot?: number): string {
  return currentSlot(slot).stateFile;
}

export function readFoundationState(slot?: number): FoundationState {
  const statePath = currentSlot(slot).stateFile;
  let raw: string;
  try {
    raw = readFileSync(statePath, 'utf-8');
  } catch {
    throw new Error(`No foundation state at ${statePath}: Staffora.E2ESetup did not run, or it failed.`);
  }

  const fail = (problem: string): never => {
    throw new Error(`The foundation state at ${statePath} ${problem}`);
  };

  let state: FoundationState;
  try {
    state = JSON.parse(raw) as FoundationState;
  } catch {
    return fail('is not valid JSON.');
  }

  if (!state || typeof state.database !== 'string' || !state.database) fail('names no database.');
  if (!Array.isArray(state.personas) || state.personas.length === 0) fail('holds no personas.');
  for (const persona of state.personas) {
    for (const field of ['key', 'actor', 'email', 'password'] as const) {
      if (typeof persona[field] !== 'string' || !persona[field]) fail(`has a persona without "${field}".`);
    }
  }
  for (const key of ['impersonationTarget', 'tenantAdministrator', 'tenantViewer'] as const) {
    for (const field of ['tenant', 'name', 'email'] as const) {
      if (typeof state[key]?.[field] !== 'string' || !state[key][field]) {
        fail(`has no ${key} "${field}".`);
      }
    }
  }
  for (const field of ['customer', 'qualification', 'service'] as const) {
    if (typeof state.staffing?.[field] !== 'string' || !state.staffing[field]) fail(`has no staffing "${field}".`);
  }
  if (typeof state.errorLog?.search !== 'string' || !state.errorLog.search) fail('has no error-log search word.');
  if (!Array.isArray(state.errorLog?.entries) || state.errorLog.entries.length === 0) fail('holds no error-log rows.');
  for (const entry of state.errorLog.entries) {
    for (const field of ['key', 'level', 'message'] as const) {
      if (typeof entry[field] !== 'string' || !entry[field]) fail(`has an error-log row without "${field}".`);
    }
    if (typeof entry.daysAgo !== 'number') fail('has an error-log row without "daysAgo".');
  }

  return state;
}

export function databaseOf(connectionString: string): string | undefined {
  let database: string | undefined;
  for (const match of connectionString.matchAll(/\s*([^=;]+?)\s*=\s*("(?:[^"]|"")*"|'(?:[^']|'')*'|[^;]*)\s*(?:;|$)/g)) {
    const key = match[1].toLowerCase().replace(/\s+/g, ' ');
    if (key !== 'database' && key !== 'initial catalog') continue;

    const value = match[2].trim();
    database = /^".*"$/s.test(value)
      ? value.slice(1, -1).replace(/""/g, '"')
      : /^'.*'$/s.test(value)
        ? value.slice(1, -1).replace(/''/g, "'")
        : value;
  }

  return database;
}

export function errorLogEntry(key: string, state: FoundationState = readFoundationState()): FoundationErrorLogEntry {
  const found = state.errorLog.entries.find((candidate) => candidate.key === key);
  if (!found) {
    throw new Error(`The foundation state has no error-log row "${key}"; it has ${state.errorLog.entries.map((entry) => entry.key).join(', ')}.`);
  }

  return found;
}

export function persona(key: string, state: FoundationState = readFoundationState()): FoundationPersona {
  const found = state.personas.find((candidate) => candidate.key === key);
  if (!found) {
    throw new Error(`The foundation state has no persona "${key}"; it has ${state.personas.map((p) => p.key).join(', ')}.`);
  }

  return found;
}
