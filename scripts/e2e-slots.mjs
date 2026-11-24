import { join } from 'node:path';

export const defaultWorkers = 4;

export const maximumWorkers = 8;

export function workerCount(env = process.env) {
  const raw = env.STAFFORA_E2E_WORKERS;
  if (raw === undefined || raw === '') return defaultWorkers;
  const count = Number(raw);
  if (!Number.isInteger(count) || count < 1 || count > maximumWorkers) {
    throw new Error(`STAFFORA_E2E_WORKERS must be a whole number from 1 to ${maximumWorkers}; it is "${raw}".`);
  }
  return count;
}

export function baseURLOf(env = process.env) {
  return new URL(env.STAFFORA_E2E_BASE_URL ?? 'http://localhost:5280').origin;
}

export function slotURL(baseURL, slot) {
  const url = new URL(baseURL);
  url.port = String(Number(url.port || 80) + 2 * slot);
  return url.origin;
}

export function dialogHostURLOf(env = process.env) {
  if (env.STAFFORA_E2E_DIALOG_HOST_URL) return new URL(env.STAFFORA_E2E_DIALOG_HOST_URL).origin;
  const url = new URL(baseURLOf(env));
  url.port = String(Number(url.port || 80) + 1);
  return url.origin;
}

const pair = /\s*([^=;]+?)\s*=\s*("(?:[^"]|"")*"|'(?:[^']|'')*'|[^;]*)\s*(?:;|$)/g;

export function databaseOf(connection) {
  let database;
  for (const match of connection.matchAll(pair)) {
    const key = match[1].toLowerCase().replace(/\s+/g, ' ');
    if (key !== 'database' && key !== 'initial catalog') continue;
    const value = match[2].trim();
    database = /^".*"$/s.test(value) ? value.slice(1, -1).replace(/""/g, '"') : /^'.*'$/s.test(value) ? value.slice(1, -1).replace(/''/g, "'") : value;
  }
  return database;
}

export function slotConnection(connection, slot) {
  if (slot === 0) return connection;
  const database = databaseOf(connection);
  if (!database) throw new Error('STAFFORA_E2E_CONNECTION names no database.');
  const kept = [...connection.matchAll(pair)]
    .filter((match) => match[1].trim() !== '')
    .filter((match) => !['database', 'initial catalog'].includes(match[1].toLowerCase().replace(/\s+/g, ' ')))
    .map((match) => `${match[1].trim()}=${match[2].trim()}`);
  return [...kept, `Database=${database}.w${slot}`].join(';');
}

export function slotStateFile(root, slot) {
  return join(root, '.e2e', slot === 0 ? 'foundation-state.json' : `foundation-state.w${slot}.json`);
}

export function slotsFile(root) {
  return join(root, '.e2e', 'slots.json');
}
