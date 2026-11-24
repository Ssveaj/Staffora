import AxeBuilder from '@axe-core/playwright';
import { expect, type Page } from '@playwright/test';

const failingImpacts = new Set(['serious', 'critical']);

export const disabledRules: { id: string; reason: string }[] = [];

export async function seriousViolations(page: Page): Promise<string[]> {
  const builder = new AxeBuilder({ page });
  if (disabledRules.length > 0) {
    builder.disableRules(disabledRules.map((rule) => rule.id));
  }

  const results = await builder.analyze();
  return [
    ...results.violations
      .filter((violation) => failingImpacts.has(violation.impact ?? ''))
      .map((violation) => `${violation.id} (${violation.impact}): ${violation.help}, at ${violation.nodes.map(describe).join(' | ')}`),
    ...(await buttonContrastViolations(page)),
  ];
}

function describe(node: { target: unknown[]; html: string }): string {
  return `${node.target.join(' ')} ${node.html.slice(0, 160)}`;
}

export async function buttonContrastViolations(page: Page): Promise<string[]> {
  return page.evaluate(() => {
    type Rgba = [number, number, number, number];
    const canvas = document.createElement('canvas');
    canvas.width = 1;
    canvas.height = 1;
    const pen = canvas.getContext('2d', { willReadFrequently: true });
    const parse = (value: string): Rgba | null => {
      const numbers = (list: string) => list.split(/[\s,/]+/).filter((part) => part.length > 0).map((part) => (part.endsWith('%') ? Number(part.slice(0, -1)) / 100 : Number(part)));
      const rgb = value.match(/^rgba?\(([^)]+)\)$/);
      if (rgb) {
        const parts = numbers(rgb[1]);
        return [parts[0], parts[1], parts[2], parts.length > 3 ? parts[3] : 1];
      }

      const srgb = value.match(/^color\(srgb\s+([^)]+)\)$/);
      if (srgb) {
        const parts = numbers(srgb[1]);
        return [parts[0] * 255, parts[1] * 255, parts[2] * 255, parts.length > 3 ? parts[3] : 1];
      }

      if (!pen || value === '' || value === 'transparent') return value === 'transparent' ? [0, 0, 0, 0] : null;
      pen.clearRect(0, 0, 1, 1);
      pen.fillStyle = 'rgba(0, 0, 0, 0)';
      pen.fillStyle = value;
      pen.fillRect(0, 0, 1, 1);
      const [r, g, b, a] = Array.from(pen.getImageData(0, 0, 1, 1).data);
      return [r, g, b, a / 255];
    };
    const over = (top: Rgba, bottom: Rgba): Rgba => {
      const alpha = top[3] + bottom[3] * (1 - top[3]);
      if (alpha === 0) return [0, 0, 0, 0];
      const channel = (i: number) => (top[i] * top[3] + bottom[i] * bottom[3] * (1 - top[3])) / alpha;
      return [channel(0), channel(1), channel(2), alpha];
    };
    const luminance = (color: Rgba) => {
      const [r, g, b] = [color[0], color[1], color[2]].map((value) => {
        const s = value / 255;
        return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
      });
      return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    };
    const ratio = (a: Rgba, b: Rgba) => {
      const [lighter, darker] = [luminance(a), luminance(b)].sort((x, y) => y - x);
      return (lighter + 0.05) / (darker + 0.05);
    };
    const background = (element: Element): Rgba => {
      const layers: Rgba[] = [];
      for (let node: Element | null = element; node; node = node.parentElement) {
        const color = parse(getComputedStyle(node).backgroundColor);
        if (color && color[3] > 0) {
          layers.push(color);
          if (color[3] >= 1) break;
        }
      }

      let shown: Rgba = [255, 255, 255, 1];
      for (const layer of layers.reverse()) shown = over(layer, shown);
      return shown;
    };
    const fullyOpaque = (element: Element) => {
      for (let node: Element | null = element; node; node = node.parentElement) {
        if (Number(getComputedStyle(node).opacity) < 1) return false;
      }

      return true;
    };

    const problems: string[] = [];
    for (const button of Array.from(document.querySelectorAll<HTMLElement>('.rz-button'))) {
      const box = button.getBoundingClientRect();
      const style = getComputedStyle(button);
      if (box.width === 0 || box.height === 0 || style.visibility === 'hidden') continue;
      if ((button as HTMLButtonElement).disabled || button.classList.contains('rz-state-disabled') || button.getAttribute('aria-disabled') === 'true') continue;
      if (!fullyOpaque(button)) continue;
      const text = parse(style.color);
      if (!text) continue;
      const behind = background(button);
      const needed = button.classList.contains('rz-button-icon-only') ? 3 : 4.5;
      const contrast = ratio(over(text, behind), behind);
      if (contrast < needed) {
        problems.push(`button-contrast (serious): A button stands out from what is behind it (${contrast.toFixed(2)}:1 where ${needed}:1 is needed), at ${button.outerHTML.slice(0, 160)}`);
      }
    }

    return problems;
  });
}

export async function expectNoSeriousViolations(page: Page): Promise<void> {
  expect(await seriousViolations(page), `axe found serious or critical violations on ${new URL(page.url()).pathname}`).toEqual([]);
}
