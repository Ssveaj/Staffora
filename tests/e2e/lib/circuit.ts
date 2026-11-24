import type { Page, WebSocketRoute } from '@playwright/test';

export interface CircuitControl {
  drop(): Promise<void>;
  restore(): void;
}

export async function controlCircuit(page: Page): Promise<CircuitControl> {
  let down = false;
  const open: { browser: WebSocketRoute; server: WebSocketRoute }[] = [];

  await page.route(/\/_blazor\/negotiate/, (route) => (down ? route.abort('connectionrefused') : route.continue()));
  await page.routeWebSocket(/\/_blazor(\?|$)/, (browser) => {
    if (down) {
      void browser.close({ code: 4000, reason: 'The journey keeps the circuit down.' });
      return;
    }

    open.push({ browser, server: browser.connectToServer() });
  });

  return {
    async drop() {
      down = true;
      for (const { browser, server } of open.splice(0)) {
        await server.close({ code: 4000, reason: 'The journey dropped the circuit.' }).catch(() => undefined);
        await browser.close({ code: 4000, reason: 'The journey dropped the circuit.' }).catch(() => undefined);
      }
    },
    restore() {
      down = false;
    },
  };
}
