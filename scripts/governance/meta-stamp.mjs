#!/usr/bin/env node
// CLI: node scripts/governance/meta-stamp.mjs --version X.Y.Z [--date YYYY-MM-DD] [--schema-version X.Y.Z]
//        [--meta-dir <path>] [--notes-out <file>] [--dry-run]
import { cpSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { resolve, join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { stampMeta } from './lib/meta-stamp.mjs';

const args = process.argv.slice(2);
const flag = (name) => args.includes(name);
const opt = (name) => {
  const i = args.indexOf(name);
  return i >= 0 ? args[i + 1] : undefined;
};

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const version = opt('--version');
const date = opt('--date') ?? new Date().toISOString().slice(0, 10);
const schemaVersion = opt('--schema-version');
const metaDir = resolve(opt('--meta-dir') ?? join(repoRoot, 'vnext-meta'));
const notesOut = opt('--notes-out');
const dryRun = flag('--dry-run');

let work = metaDir;
let tmp;
try {
  if (dryRun) {
    tmp = mkdtempSync(join(tmpdir(), 'meta-stamp-dry-'));
    cpSync(metaDir, tmp, { recursive: true });
    work = tmp;
  }
  const result = stampMeta({ metaDir: work, version, date, schemaVersion });
  // Only a run that stamped has notes; an already-stamped re-run writes no file (the upload step ignores a missing one).
  if (notesOut && result.status === 'stamped') {
    writeFileSync(resolve(notesOut), JSON.stringify({ version, schemaVersion: result.schemaVersion, date, notes: result.notes }, null, 2) + '\n');
  }
  const prefix = dryRun ? '[dry-run] ' : '';
  if (result.status === 'already-stamped') {
    console.log(`${prefix}${version} is already in the manifest; nothing changed (schemaVersion ${result.schemaVersion}).`);
  } else {
    const row = JSON.parse(readFileSync(join(work, 'version-manifest.json'), 'utf8')).versions[version];
    console.log(`${prefix}manifest row ${version}: ${JSON.stringify(row)}`);
    console.log(`${prefix}runtimeVersion: ${version}`);
    console.log(`${prefix}schemaVersion: ${result.schemaVersion}`);
    console.log(`${prefix}consumed fragments (${result.consumed.length}): ${result.consumed.join(', ') || 'none'}`);
    console.log(`${prefix}leftover fragments (${result.leftovers.length}): ${result.leftovers.length ? `${result.leftovers.join(', ')} (already released; deleted, not merged)` : 'none'}`);
  }
} catch (e) {
  console.error(`meta-stamp: ${e.message}`);
  process.exitCode = 1;
} finally {
  if (tmp) rmSync(tmp, { recursive: true, force: true });
}
