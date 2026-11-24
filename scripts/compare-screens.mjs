import { mkdirSync, readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { chromium } from '@playwright/test';

export const PAD = 16;
export const GAP = 16;
export const CAPTION = 36;

function pngSize(bytes, path) {
  const signature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
  if (bytes.length < 24 || signature.some((value, index) => bytes[index] !== value)) {
    throw new Error(`${path} is not a PNG file.`);
  }
  return { width: bytes.readUInt32BE(16), height: bytes.readUInt32BE(20) };
}

function parse(argv) {
  const paths = [];
  const labels = { left: 'Staffora.Blazor', right: 'Design mockup' };
  for (let index = 0; index < argv.length; index++) {
    const argument = argv[index];
    if (argument === '--left-label' || argument === '--right-label') {
      const value = argv[++index];
      if (value === undefined) throw new Error(`${argument} needs a value.`);
      labels[argument === '--left-label' ? 'left' : 'right'] = value;
    } else {
      paths.push(argument);
    }
  }
  if (paths.length !== 3) {
    throw new Error('Usage: node scripts/compare-screens.mjs <ours.png> <mockup.png> <out.png> [--left-label <text>] [--right-label <text>]');
  }
  return { left: resolve(paths[0]), right: resolve(paths[1]), out: resolve(paths[2]), labels };
}

const escape = (text) => text.replace(/[&<>"]/g, (character) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[character]);

function column(label, image) {
  return `<figure style="width:${image.width}px">
    <figcaption><strong>${escape(label)}</strong><span>${image.width} px wide</span></figcaption>
    <img src="data:image/png;base64,${image.bytes.toString('base64')}" width="${image.width}" height="${image.height}" alt="">
  </figure>`;
}

async function main() {
  const { left, right, out, labels } = parse(process.argv.slice(2));
  const images = [left, right].map((path) => {
    const bytes = readFileSync(path);
    return { bytes, ...pngSize(bytes, path) };
  });
  const width = PAD + images[0].width + GAP + images[1].width + PAD;
  const height = PAD + CAPTION + Math.max(images[0].height, images[1].height) + PAD;

  const html = `<!doctype html><html><head><meta charset="utf-8"><style>
    * { box-sizing: border-box; margin: 0; }
    html, body { width: ${width}px; height: ${height}px; background: #e2e8f0; }
    body { display: flex; align-items: flex-start; gap: ${GAP}px; padding: ${PAD}px; font: 14px/1 system-ui, "Segoe UI", sans-serif; }
    figure { flex: none; }
    figcaption { display: flex; align-items: center; justify-content: space-between; height: ${CAPTION}px; padding: 0 12px; background: #0f172a; color: #ffffff; }
    figcaption span { color: #cbd5e1; }
    img { display: block; }
  </style></head><body>${column(labels.left, images[0])}${column(labels.right, images[1])}</body></html>`;

  const browser = await chromium.launch();
  try {
    const page = await browser.newPage({ viewport: { width, height }, deviceScaleFactor: 1 });
    await page.setContent(html, { waitUntil: 'load' });
    mkdirSync(dirname(out), { recursive: true });
    await page.screenshot({ path: out, clip: { x: 0, y: 0, width, height } });
  } finally {
    await browser.close();
  }
  console.log(`compare-screens: wrote ${out} (${width} x ${height} px).`);
}

main().catch((error) => {
  console.error(`compare-screens: ${error.message}`);
  process.exit(1);
});
