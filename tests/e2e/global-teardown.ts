import { existsSync } from 'node:fs';
import { join } from 'node:path';
import { dialogHostUrl } from './lib/dialog-host';
import { repositoryRoot } from './lib/foundation-state';

const stateFile = join(repositoryRoot, '.e2e', 'dialog-host-state.json');

export default async function globalTeardown(): Promise<void> {
  try {
    const response = await fetch(`${dialogHostUrl}/test/dialog-host/stop`, { method: 'POST' });
    if (response.status !== 202) {
      throw new Error(`The dialog host answered ${response.status} to its stop request.`);
    }
  } catch (error) {
    if (!existsSync(stateFile)) return;
    throw error;
  }

  const deadline = Date.now() + 60_000;
  while (existsSync(stateFile) && Date.now() < deadline) {
    await new Promise((resolve) => setTimeout(resolve, 250));
  }

  if (existsSync(stateFile)) {
    throw new Error('The dialog host did not drop its database within a minute of its stop request; the next pool start drops it.');
  }
}
