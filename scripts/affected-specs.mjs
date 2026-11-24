import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { areaTags, lastFullRunFile, runPlaywright } from './e2e-run.mjs';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const specDir = join(root, 'tests', 'e2e');

const functional = ['@shell', '@dialogs', '@staffing', '@coordination', '@portal', '@platform', '@homes', '@admin', '@lists'];
const FULL = 'full';

export const pathMap = [
  [/^(playwright\.e2e\.config\.ts|package(-lock)?\.json|scripts\/e2e-[^/]+\.mjs|tests\/e2e\/(foundation\.setup|global-teardown)\.ts|tests\/e2e\/lib\/(session|page-health|foundation-state|slot|accessibility|report-secret-guard)\.ts|Staffora\.E2ESetup\/)/, FULL],
  [/^Staffora\.(Core|Data)\//, functional],
  [/^Staffora\.Services\/(Workflow|Caching)\//, functional],
  [/^Staffora\.Blazor\/(Program\.cs|_Imports\.razor|App\.razor|Routes\.razor|appsettings)/, functional],

  [/^tests\/e2e\/fixtures\//, ['@dialogs']],
  [/^tests\/e2e\/baselines\//, ['@visual']],
  [/^Staffora\.Test\/E2E\/DialogHost\//, ['@dialogs']],

  [/^Staffora\.Blazor\/(Layout|Components\/Common\/(Layout|Ui))\//, ['@shell', '@homes', '@visual']],
  [/^Staffora\.Blazor\/wwwroot\/(css\/|app[^/]*\.css$)/, ['@shell', '@homes', '@visual']],
  [/^Staffora\.Blazor\/Components\/Account\//, ['@shell', '@homes', '@visual']],
  [/^Staffora\.Blazor\/Pages\/(Home|NotFound|StatusPage|Error)\.razor/, ['@shell', '@homes']],
  [/^Staffora\.Services\/(Messaging|Home)\//, ['@shell', '@homes']],
  [/^Staffora\.Services\/Authorization\//, ['@shell', '@platform', '@admin', '@homes']],
  [/^Staffora\.Services\/Search\/|^Staffora\.Blazor\/Components\/Common\/Dialogs\/GlobalSearchDialog\.|^Staffora\.Blazor\/wwwroot\/scripts\/global-search\.js$/, ['@shell', '@a11y']],
  [/^Staffora\.Blazor\/wwwroot\/scripts\/dialog-focus\.js$/, ['@dialogs', '@shell', '@a11y']],

  [/^Staffora\.Blazor\/Components\/Common\/(Dialogs\/StateEntity|WorkflowFields)\//, ['@dialogs', '@admin', '@platform']],
  [/^Staffora\.Blazor\/Components\/Common\/Dialogs\/(ConfirmDialog|ErrorDialog|MessageDialog|PromptDialog|FormDialog\w*|DialogNotice|DialogScope|DialogTone)\./, ['@dialogs', '@admin']],
  [/^Staffora\.Blazor\/wwwroot\/scripts\/state-entity-dialog/, ['@dialogs']],
  [/^Staffora\.Blazor\/Components\/Common\/Comments\/|^Staffora\.Services\/Comments\//, ['@dialogs', '@staffing']],
  [/^Staffora\.Blazor\/(Components\/Common\/List|Helper\/UrlState)\/|^Staffora\.Blazor\/wwwroot\/scripts\/url-state/, ['@lists', '@admin']],

  [/^Staffora\.Blazor\/Pages\/Staffing\/(?!Coordination)/, ['@staffing']],
  [/^Staffora\.Blazor\/Pages\/Me\//, ['@staffing', '@homes']],
  [/^Staffora\.Blazor\/Components\/Common\/Schedule\//, ['@staffing']],
  [/^Staffora\.Blazor\/Components\/Common\/Dialogs\/(Job|Slot|Staff|Absence|ChooseAbsence|Customer|Qualification|AddQualification|RequestRound|WorkPattern|PatternAssignment|PersonSchedule|Service)\w*\./, ['@staffing']],
  [/^Staffora\.Services\/(Jobs|Scheduling|Resources|Customers|Catalogue)\//, ['@staffing', '@coordination']],

  [/^Staffora\.Blazor\/(Components\/Coordination\/|Pages\/Staffing\/Coordination)/, ['@coordination']],
  [/^Staffora\.Services\/Coordination\/|^Staffora\.Services\/Demo\/CoordinationShowcase/, ['@coordination']],
  [/^Staffora\.Blazor\/wwwroot\/scripts\/tab-bar\.js$/, ['@coordination']],
  [/^Staffora\.Blazor\/wwwroot\/scripts\/coordination-timeline\.js$/, ['@coordination']],

  [/^Staffora\.Blazor\/Pages\/Portal\//, ['@portal']],
  [/^Staffora\.Blazor\/Components\/Common\/Dialogs\/(OrderDialog|EnableCustomerPortalDialog)\./, ['@portal']],
  [/^Staffora\.Services\/(Portal|Integration)\//, ['@portal']],

  [/^Staffora\.Blazor\/(Pages\/SuperAdmin|Components\/TenantComponents)\//, ['@platform']],
  [/^Staffora\.Blazor\/Components\/Common\/Dialogs\/(Tenant(Dialog|OnboardingDialog)|ImpersonateUserDialog)\w*\./, ['@platform']],
  [/^Staffora\.Services\/(Platform|Tenancy|Diagnostics)\//, ['@platform']],

  [/^Staffora\.Blazor\/(Pages\/TenantAdmin|Components\/(OrganizationComponents|Settings|EntityAttributes))\//, ['@admin']],
  [/^Staffora\.Blazor\/Components\/Common\/Dialogs\/(User|TenantRole|RoleHolding|AddRoleHolding|AddUserToRole|OrganizationUnit)\w*\./, ['@admin']],
  [/^Staffora\.Blazor\/Components\/Common\/Dialogs\/ClosedDay\w*\./, ['@admin', '@staffing']],
  [/^Staffora\.Services\/(CustomFields|Settings|Services)\//, ['@admin']],

  [/^(docs\/|Staffora\.Test\/|\.github\/|\.claude\/|[^/]+\.md$)/, []],
];

const ignoredFolders = new Set(['bin', 'obj', 'node_modules', '.e2e', 'output', '.vs', 'TestResults', '.git']);

function changedSince(since, folder = root) {
  const found = [];
  for (const entry of readdirSync(folder, { withFileTypes: true })) {
    if (ignoredFolders.has(entry.name)) continue;
    const path = join(folder, entry.name);
    if (entry.isDirectory()) found.push(...changedSince(since, path));
    else if (statSync(path).mtimeMs > since) found.push(relative(root, path).replace(/\\/g, '/'));
  }
  return found;
}

function specsImporting(lib) {
  const imports = (file) => [...readFileSync(file, 'utf8').matchAll(/from\s+'(\.{1,2}\/[^']+)'/g)].map((match) => resolve(dirname(file), match[1]).replace(/\.ts$/, '') + '.ts');
  const target = resolve(root, lib);
  const reaches = (file, seen = new Set()) => {
    if (seen.has(file)) return false;
    seen.add(file);
    return imports(file).some((next) => next === target || (existsSync(next) && reaches(next, seen)));
  };
  return readdirSync(specDir).filter((name) => name.endsWith('.spec.ts')).filter((name) => reaches(join(specDir, name)));
}

function specsNaming(id) {
  return readdirSync(specDir).filter((name) => name.endsWith('.spec.ts') && readFileSync(join(specDir, name), 'utf8').includes(`'${id}'`));
}

export function select(paths) {
  const tags = new Set(['@smoke']);
  const specs = new Set();
  const unmapped = [];
  let full = false;
  for (const raw of paths) {
    const path = relative(root, resolve(root, raw)).replace(/\\/g, '/');
    const spec = /^tests\/e2e\/([^/]+\.spec\.ts)$/.exec(path);
    const lib = /^tests\/e2e\/lib\/[^/]+\.ts$/.test(path);
    const journey = /^journeys\/([^/]+)\.journey\.json$/.exec(path);
    if (spec) specs.add(spec[1]);
    else if (lib) specsImporting(path).forEach((name) => specs.add(name));
    else if (journey) specsNaming(journey[1]).forEach((name) => specs.add(name));

    const matches = pathMap.filter(([pattern]) => pattern.test(path));
    if (matches.some(([, chosen]) => chosen === FULL)) full = true;
    for (const [, chosen] of matches) if (chosen !== FULL) chosen.forEach((tag) => tags.add(tag));
    if (!spec && !lib && !journey && matches.length === 0) unmapped.push(path);
  }

  const all = readdirSync(specDir).filter((name) => name.endsWith('.spec.ts'));
  if (specs.size === all.length) full = true;
  return { full, tags: [...tags], specs: [...specs], unmapped };
}

export function grepOf(selection) {
  const escape = (text) => text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  return [...selection.tags, ...selection.specs.map((name) => `${escape(name)}`)].join('|');
}

function main() {
  const args = process.argv.slice(2);
  const run = args.includes('--run');
  const passThrough = args.indexOf('--');
  const playwrightArgs = passThrough >= 0 ? args.slice(passThrough + 1) : [];
  const paths = (passThrough >= 0 ? args.slice(0, passThrough) : args).filter((arg) => arg !== '--run');

  let changed = paths;
  if (changed.length === 0) {
    if (!existsSync(lastFullRunFile)) {
      console.error('affected-specs: no paths given and no passing full run recorded (.e2e/last-full-run.json); run `npm run test:e2e` once, or name the changed paths.');
      return 2;
    }
    const since = Date.parse(JSON.parse(readFileSync(lastFullRunFile, 'utf8')).startedAt);
    changed = changedSince(since);
    console.log(`affected-specs: ${changed.length} file(s) changed since the full run of ${new Date(since).toISOString()}.`);
  }

  const selection = select(changed);
  for (const path of selection.unmapped) console.log(`affected-specs: no area for ${path}; only @smoke covers it (add a line to the table in scripts/affected-specs.mjs).`);
  if (selection.full) {
    console.log('affected-specs: the change reaches the test platform or every spec: run the full suite (npm run test:e2e).');
    return run ? runPlaywright({ grep: undefined, args: playwrightArgs, full: true }) : 0;
  }

  const grep = grepOf(selection);
  console.log(`affected-specs: areas ${selection.tags.join(' ')}${selection.specs.length ? `; specs ${selection.specs.join(' ')}` : ''}`);
  console.log(`--grep "${grep}"`);
  return run ? runPlaywright({ grep, args: playwrightArgs, full: false }) : 0;
}

if (process.argv[1] && resolve(fileURLToPath(import.meta.url)) === resolve(process.argv[1])) {
  process.exit(main());
}

export { areaTags };
