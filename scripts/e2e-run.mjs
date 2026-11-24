import { spawnSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
export const lastFullRunFile = join(root, '.e2e', 'last-full-run.json');

export const areaTags = ['@shell', '@dialogs', '@staffing', '@coordination', '@portal', '@platform', '@homes', '@admin', '@lists', '@a11y', '@visual'];

export function parseTags(text) {
  const tags = text.split(/[,\s|]+/).filter((part) => part !== '').map((part) => (part.startsWith('@') ? part : `@${part}`));
  const unknown = tags.filter((tag) => !areaTags.includes(tag) && tag !== '@smoke');
  if (tags.length === 0 || unknown.length > 0) {
    throw new Error(`Unknown area ${unknown.join(', ') || '(none given)'}; the areas are ${areaTags.join(', ')} (and @smoke).`);
  }
  return tags;
}

export function runPlaywright({ grep, args, full }) {
  const extra = [...args];
  const env = { ...process.env };

  const shots = extra.indexOf('--shots');
  if (shots >= 0) extra.splice(shots, 1);
  env.STAFFORA_E2E_SHOTS = full || shots >= 0 ? '1' : '0';

  const workersAt = extra.findIndex((arg) => arg === '--workers' || arg === '-j' || arg.startsWith('--workers='));
  if (workersAt >= 0) {
    env.STAFFORA_E2E_WORKERS = extra[workersAt].includes('=') ? extra[workersAt].split('=')[1] : extra[workersAt + 1];
  }

  const filtered = Boolean(grep) || extra.some((arg) => /\.ts(:\d+)?$|^--grep|^-g$|^--last-failed$|^--only-changed/.test(arg));
  if (filtered && !/@dialog-host|@dialogs|comments|dialog-(restore|escape)/.test(`${grep ?? ''} ${extra.join(' ')}`)) {
    env.STAFFORA_E2E_DIALOG_HOST = env.STAFFORA_E2E_DIALOG_HOST ?? 'off';
  }

  env.STAFFORA_E2E_RETRIES = env.STAFFORA_E2E_RETRIES ?? (full ? '1' : '0');
  env.STAFFORA_E2E_MAX_FAILURES = env.STAFFORA_E2E_MAX_FAILURES ?? (full ? '0' : '3');

  const startedAt = new Date().toISOString();
  const command = ['test', '--config', 'playwright.e2e.config.ts', ...(grep ? ['--grep', grep] : []), ...extra];
  console.log(`e2e-run: playwright ${command.join(' ')} (workers ${env.STAFFORA_E2E_WORKERS ?? 'default'}, dialog host ${env.STAFFORA_E2E_DIALOG_HOST ?? 'on'}, screenshots ${env.STAFFORA_E2E_SHOTS === '1' ? 'on' : 'off'}, retries ${env.STAFFORA_E2E_RETRIES}, max failures ${env.STAFFORA_E2E_MAX_FAILURES})`);
  const result = spawnSync(process.execPath, [join(root, 'node_modules', '@playwright', 'test', 'cli.js'), ...command], { cwd: root, stdio: 'inherit', env });
  const code = result.status ?? 1;

  if (code === 0 && full && !filtered) {
    mkdirSync(dirname(lastFullRunFile), { recursive: true });
    writeFileSync(lastFullRunFile, JSON.stringify({ startedAt, finishedAt: new Date().toISOString() }, null, 2));
  }
  return code;
}

function main() {
  const [mode, ...rest] = process.argv.slice(2);
  switch (mode) {
    case 'full':
      return runPlaywright({ grep: undefined, args: rest, full: true });
    case 'smoke':
      return runPlaywright({ grep: '@smoke', args: rest, full: false });
    case 'area': {
      const [tags, ...args] = rest;
      return runPlaywright({ grep: parseTags(tags ?? '').join('|'), args, full: false });
    }
    case 'grep': {
      const [grep, ...args] = rest;
      return runPlaywright({ grep, args, full: false });
    }
    default:
      console.error('Usage: node scripts/e2e-run.mjs full|smoke|area <tag>[,<tag>]|grep <regex> [playwright args]');
      return 2;
  }
}

if (process.argv[1] && resolve(fileURLToPath(import.meta.url)) === resolve(process.argv[1])) {
  try {
    process.exit(main());
  } catch (error) {
    console.error(`e2e-run: ${error.message}`);
    process.exit(2);
  }
}
