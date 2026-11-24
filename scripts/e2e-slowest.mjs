import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const count = Number(process.argv[2] ?? 15);
const file = process.argv[3] ?? join(root, '.e2e', 'playwright-report', 'results.json');

const report = JSON.parse(readFileSync(file, 'utf8'));
const tests = [];
const walk = (suite, titles) => {
  for (const spec of suite.specs ?? []) {
    for (const test of spec.tests ?? []) {
      const results = test.results ?? [];
      const duration = results.reduce((sum, result) => sum + (result.duration ?? 0), 0);
      tests.push({ title: [...titles, spec.title].filter(Boolean).join(' › '), file: `${spec.file}:${spec.line}`, project: test.projectName, duration, status: results.at(-1)?.status ?? 'skipped' });
    }
  }
  for (const child of suite.suites ?? []) walk(child, child.title && !child.title.endsWith('.ts') ? [...titles, child.title] : titles);
};
for (const suite of report.suites ?? []) walk(suite, []);

const seconds = (ms) => `${(ms / 1000).toFixed(1)} s`.padStart(8);
tests.sort((a, b) => b.duration - a.duration);
for (const test of tests.slice(0, count)) console.log(`${seconds(test.duration)}  ${test.status.padEnd(8)} ${test.file}  ${test.title}`);

const total = tests.reduce((sum, test) => sum + test.duration, 0);
const wall = report.stats?.duration ?? 0;
console.log(`\n${tests.length} tests; wall ${seconds(wall).trim()}, the tests' own time ${seconds(total).trim()} (${wall > 0 ? (total / wall).toFixed(1) : '?'}x side by side); workers ${report.config?.metadata?.actualWorkers ?? report.config?.workers ?? '?'}.`);
