import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { inflateRawSync } from 'node:zlib';
import type { FullResult, Reporter } from '@playwright/test/reporter';
import { repositoryRoot, type FoundationState } from './foundation-state';

interface Options {
  reportFolder: string;
  outputFolder: string;
}

export default class ReportSecretGuard implements Reporter {
  private readonly reportFolder: string;
  private readonly outputFolder: string;

  constructor(options: Options) {
    this.reportFolder = join(repositoryRoot, options.reportFolder);
    this.outputFolder = join(repositoryRoot, options.outputFolder);
  }

  printsToStdio(): boolean {
    return false;
  }

  async onEnd(result: FullResult): Promise<{ status?: FullResult['status'] } | undefined> {
    let findings: string[];
    try {
      const passwords = personaPasswords();
      findings = [...searchReport(this.reportFolder, passwords), ...searchFolder(this.outputFolder, passwords)];
    } catch (error) {
      console.error(`report-secret-guard: cannot search the evidence: ${(error as Error).message}`);
      return { status: 'failed' };
    }

    const searched = [this.reportFolder, this.outputFolder].map((folder) => relative(repositoryRoot, folder)).join(' and ');
    if (findings.length === 0) {
      console.log(`report-secret-guard: no persona password in ${searched}.`);
      return undefined;
    }

    const where = findings.map((finding) => `  ${finding}`).join('\n');
    const run = result.status === 'passed' ? 'a passing run' : 'this failed run';
    console.error(`report-secret-guard: a persona password is in the evidence of ${run}; do not share it, delete it:\n${where}`);
    return { status: 'failed' };
  }

  async onExit(): Promise<void> {
    let findings: string[];
    try {
      findings = searchFolder(this.outputFolder, personaPasswords());
    } catch (error) {
      console.error(`report-secret-guard: cannot search ${relative(repositoryRoot, this.outputFolder)} as the run ends: ${(error as Error).message}`);
      process.exitCode = 1;
      return;
    }

    if (findings.length > 0) {
      console.error(`report-secret-guard: a persona password is in the run's output folder; do not share it, delete it:\n${findings.map((finding) => `  ${finding}`).join('\n')}`);
      process.exitCode = 1;
    }
  }
}

function personaPasswords(): string[] {
  const folder = join(repositoryRoot, '.e2e');
  const files = existsSync(folder) ? readdirSync(folder).filter((name) => /^foundation-state(\.w\d+)?\.json$/.test(name)) : [];
  if (files.length === 0) {
    throw new Error(`no foundation state in ${folder}, so there are no passwords to look for.`);
  }

  return files.flatMap((name) => {
    const state = JSON.parse(readFileSync(join(folder, name), 'utf-8')) as FoundationState;
    const passwords = (state.personas ?? []).map((persona) => persona.password).filter((password) => !!password);
    if (passwords.length === 0) throw new Error(`the foundation state ${name} holds no passwords.`);
    return passwords;
  });
}

export function searchReport(folder: string, passwords: string[]): string[] {
  if (!existsSync(folder)) throw new Error(`no report at ${folder}.`);
  const { findings, embeddedReports } = search(folder, passwords);

  if (embeddedReports === 0) throw new Error(`no embedded report data found in ${folder}/index.html; the report's format changed.`);
  return findings;
}

export function searchFolder(folder: string, passwords: string[]): string[] {
  return existsSync(folder) ? search(folder, passwords).findings : [];
}

function search(folder: string, passwords: string[]): { findings: string[]; embeddedReports: number } {
  const needles = passwords.flatMap((password) => [password, JSON.stringify(password).slice(1, -1), encodeURIComponent(password)]);
  const findings: string[] = [];
  let embeddedReports = 0;

  const searchData = (where: string, data: Buffer): void => {
    if (isZip(data)) {
      for (const entry of zipEntries(data, where)) searchData(`${where} > ${entry.name}`, entry.data);
      return;
    }

    const text = data.toString('utf-8');
    if (needles.some((needle) => text.includes(needle))) findings.push(where);

    for (const match of text.matchAll(/data:application\/zip;base64,([A-Za-z0-9+/=]+)/g)) {
      embeddedReports++;
      searchData(`${where} (embedded report data)`, Buffer.from(match[1], 'base64'));
    }
  };

  for (const file of filesUnder(folder)) searchData(relative(repositoryRoot, file), readFileSync(file));
  return { findings, embeddedReports };
}

function filesUnder(folder: string): string[] {
  return readdirSync(folder, { withFileTypes: true }).flatMap((entry) =>
    entry.isDirectory() ? filesUnder(join(folder, entry.name)) : [join(folder, entry.name)],
  );
}

function isZip(data: Buffer): boolean {
  return data.length >= 4 && data.readUInt32LE(0) === 0x04034b50;
}

function zipEntries(zip: Buffer, where: string): { name: string; data: Buffer }[] {
  const fail = (problem: string): never => {
    throw new Error(`${where} is not a zip archive the guard can read: ${problem}.`);
  };

  let end = -1;
  for (let at = zip.length - 22; at >= Math.max(0, zip.length - 22 - 0xffff); at--) {
    if (zip.readUInt32LE(at) === 0x06054b50) {
      end = at;
      break;
    }
  }
  if (end < 0) fail('no end of central directory');

  const entries: { name: string; data: Buffer }[] = [];
  const count = zip.readUInt16LE(end + 10);
  let at = zip.readUInt32LE(end + 16);
  for (let index = 0; index < count; index++) {
    if (zip.readUInt32LE(at) !== 0x02014b50) fail(`no central directory entry at ${at}`);
    const method = zip.readUInt16LE(at + 10);
    const size = zip.readUInt32LE(at + 20);
    const nameLength = zip.readUInt16LE(at + 28);
    const extraLength = zip.readUInt16LE(at + 30);
    const commentLength = zip.readUInt16LE(at + 32);
    const local = zip.readUInt32LE(at + 42);
    const name = zip.toString('utf-8', at + 46, at + 46 + nameLength);
    if (size === 0xffffffff || local === 0xffffffff) fail(`${name} is a zip64 entry`);

    const start = local + 30 + zip.readUInt16LE(local + 26) + zip.readUInt16LE(local + 28);
    const raw = zip.subarray(start, start + size);
    if (method === 0) entries.push({ name, data: raw });
    else if (method === 8) entries.push({ name, data: inflateRawSync(raw) });
    else fail(`${name} uses compression method ${method}`);

    at += 46 + nameLength + extraLength + commentLength;
  }

  return entries;
}
