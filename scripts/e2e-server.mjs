import { spawn, spawnSync } from 'node:child_process';
import { mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { baseURLOf, databaseOf, dialogHostURLOf, slotConnection, slotStateFile, slotsFile, slotURL, workerCount } from './e2e-slots.mjs';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const connection = process.env.STAFFORA_E2E_CONNECTION;
const configuration = process.env.STAFFORA_E2E_CONFIGURATION || 'Release';
const withDialogHost = (process.env.STAFFORA_E2E_DIALOG_HOST ?? 'on').toLowerCase() !== 'off';

if (!connection) {
  console.error('e2e-server: STAFFORA_E2E_CONNECTION is not set; point it at a database whose name contains "e2e".');
  process.exit(1);
}

const baseURL = baseURLOf();
const slots = Array.from({ length: workerCount() }, (_, slot) => ({
  slot,
  baseURL: slotURL(baseURL, slot),
  connection: slotConnection(connection, slot),
  database: databaseOf(slotConnection(connection, slot)),
  stateFile: slotStateFile(root, slot),
}));

const children = new Set();
let stopping = false;

function stopAll(code) {
  stopping = true;
  for (const child of children) {
    if (process.platform === 'win32') spawnSync('taskkill', ['/pid', String(child.pid), '/T', '/F'], { stdio: 'ignore' });
    else child.kill();
  }
  process.exit(code);
}

for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => stopAll(0));

function start(label, command, args, options) {
  const child = spawn(command, args, { ...options, stdio: ['ignore', 'pipe', 'pipe'] });
  children.add(child);
  for (const [stream, target] of [[child.stdout, process.stdout], [child.stderr, process.stderr]]) {
    let partial = '';
    stream.on('data', (data) => {
      const lines = (partial + data.toString()).split(/\r?\n/);
      partial = lines.pop() ?? '';
      for (const line of lines) if (line.trim() !== '') target.write(`[${label}] ${line}\n`);
    });
  }
  child.on('exit', () => children.delete(child));
  return child;
}

function run(label, command, args, options) {
  return new Promise((resolve) => {
    const child = start(label, command, args, options);
    child.on('exit', (code) => resolve(code ?? 1));
    child.on('error', () => resolve(1));
  });
}

async function healthy(url) {
  try {
    return (await fetch(`${url}/health`)).ok;
  } catch {
    return false;
  }
}

async function waitHealthy(url, label, deadlineMs) {
  const deadline = Date.now() + deadlineMs;
  while (Date.now() < deadline) {
    if (await healthy(url)) return;
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
  throw new Error(`${label} did not answer ${url}/health healthy in time.`);
}

const started = Date.now();
const seconds = () => `${((Date.now() - started) / 1000).toFixed(0)} s`;

rmSync(slotsFile(root), { force: true });
const projects = withDialogHost
  ? [join(root, 'Staffora.Test', 'Staffora.Test.csproj')]
  : [join(root, 'Staffora.E2ESetup', 'Staffora.E2ESetup.csproj'), join(root, 'Staffora.Blazor', 'Staffora.Blazor.csproj')];
for (const project of projects) {
  console.log(`e2e-server: building ${project} (${configuration})...`);
  const build = spawnSync('dotnet', ['build', project, '-c', configuration, '--nologo', '-v', 'q'], { cwd: root, stdio: 'inherit', env: process.env });
  if (build.status !== 0) {
    console.error(`e2e-server: the build failed (exit ${build.status ?? build.error?.message}); nothing is started.`);
    process.exit(1);
  }
}
console.log(`e2e-server: built in ${seconds()}; preparing ${slots.length} slot(s): ${slots.map((each) => `${each.baseURL} over ${each.database}`).join(', ')}.`);

const dialogHostURL = dialogHostURLOf();
if (withDialogHost) {
  const host = start('dialog-host', process.execPath, [join(root, 'scripts', 'e2e-dialog-host.mjs')], {
    cwd: root,
    env: { ...process.env, STAFFORA_E2E_DIALOG_HOST_URL: dialogHostURL, STAFFORA_E2E_PREBUILT: '1', STAFFORA_E2E_CONFIGURATION: configuration },
  });
  host.on('exit', (code) => {
    if (stopping) return;
    console.error(`e2e-server: the dialog host exited (code ${code}).`);
    stopAll(code || 1);
  });
}

async function startSlot({ slot, baseURL: url, connection: slotConnectionString, stateFile }) {
  const label = `slot ${slot}`;
  const prep = await run(label, 'dotnet', ['run', '--project', join(root, 'Staffora.E2ESetup'), '--no-build', '-c', configuration], {
    cwd: root,
    env: { ...process.env, STAFFORA_E2E_CONNECTION: slotConnectionString, STAFFORA_E2E_STATE_FILE: stateFile },
  });
  if (prep !== 0) throw new Error(`the preparation of ${label} failed (exit ${prep}); no app is started.`);
  console.log(`e2e-server: ${label} prepared at ${seconds()}; starting its app on ${url}...`);

  const app = start(label, 'dotnet', ['run', '--no-launch-profile', '--no-build', '-c', configuration], {
    cwd: join(root, 'Staffora.Blazor'),
    env: {
      ...process.env,
      ASPNETCORE_ENVIRONMENT: 'Development',
      ASPNETCORE_URLS: url,
      ConnectionStrings__DefaultConnection: slotConnectionString,
      STAFFORA_E2E_SKIP_STARTUP_SEED: '1',
      JobClock__Enabled: 'false',
      IntegrationOutbox__Interval: '00:00:02',
      RateLimits__SignIn__PermitLimit: '1000',
      Logging__LogLevel__Default: 'Warning',
      'Logging__LogLevel__Microsoft.Hosting.Lifetime': 'Information',
    },
  });
  app.on('exit', (code) => {
    if (stopping) return;
    console.error(`e2e-server: the app of ${label} exited (code ${code}).`);
    stopAll(code || 1);
  });
  await waitHealthy(url, `the app of ${label}`, 240_000);
  console.log(`e2e-server: ${label} is ready at ${seconds()} (${url}/health: Healthy).`);
}

try {
  await Promise.all([
    ...slots.map(startSlot),
    ...(withDialogHost ? [waitHealthy(dialogHostURL, 'the dialog host', 480_000)] : []),
  ]);
} catch (error) {
  console.error(`e2e-server: ${error.message}`);
  stopAll(1);
}

mkdirSync(join(root, '.e2e'), { recursive: true });
writeFileSync(
  slotsFile(root),
  JSON.stringify({ startedAt: new Date().toISOString(), dialogHost: withDialogHost ? dialogHostURL : null, slots: slots.map(({ slot, baseURL: url, database, stateFile }) => ({ slot, baseURL: url, database, stateFile })) }, null, 2),
);
console.log(`e2e-server: ready, ${slots.length} slot(s)${withDialogHost ? ' and the dialog host' : ''} in ${seconds()}.`);
