import { spawn, spawnSync } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { dialogHostURLOf } from './e2e-slots.mjs';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const hostURL = dialogHostURLOf();
const configuration = process.env.STAFFORA_E2E_CONFIGURATION || 'Release';
const project = join(root, 'Staffora.Test', 'Staffora.Test.csproj');

if (process.env.STAFFORA_E2E_PREBUILT !== '1') {
  console.log('e2e-dialog-host: building Staffora.Test...');
  const build = spawnSync('dotnet', ['build', project, '-c', configuration, '--nologo', '-v', 'q'], { cwd: root, stdio: 'inherit', env: process.env });
  if (build.status !== 0) {
    console.error(`e2e-dialog-host: the build failed (exit ${build.status ?? build.error?.message}); the host is not started.`);
    process.exit(1);
  }
}

console.log(`e2e-dialog-host: starting the dialog host on ${hostURL}...`);
const host = spawn('dotnet', ['run', '--project', project, '--no-build', '-c', configuration, '--', 'dialog-host'], {
  cwd: root,
  stdio: 'inherit',
  env: {
    ...process.env,
    STAFFORA_E2E_DIALOG_HOST_URL: hostURL,
    Logging__LogLevel__Default: 'Warning',
    'Logging__LogLevel__Microsoft.Hosting.Lifetime': 'Information',
  },
});

host.on('exit', (code) => {
  console.error(`e2e-dialog-host: the host exited (code ${code}).`);
  process.exit(code ?? 1);
});

for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, () => {
    host.kill(signal);
    process.exit(0);
  });
}
