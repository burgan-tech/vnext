#!/usr/bin/env node
// CLI: node scripts/governance/meta-sync.mjs --source <meta dir> --target <meta dir> [--dry-run]
// Merges a patch line's vnext-meta (--source) into master's (--target); see lib/meta-sync.mjs.
import { cpSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { syncMeta } from './lib/meta-sync.mjs';

const args = process.argv.slice(2);
const flag = (name) => args.includes(name);
const opt = (name) => {
  const i = args.indexOf(name);
  return i >= 0 ? args[i + 1] : undefined;
};

const source = opt('--source');
const target = opt('--target');
const dryRun = flag('--dry-run');

let tmp;
try {
  if (!source || !target) throw new Error('usage: --source <meta dir> --target <meta dir> [--dry-run]');
  let work = resolve(target);
  if (dryRun) {
    tmp = mkdtempSync(join(tmpdir(), 'meta-sync-dry-'));
    cpSync(work, tmp, { recursive: true });
    work = tmp;
  }
  const result = syncMeta({ sourceDir: resolve(source), targetDir: work });
  const prefix = dryRun ? '[dry-run] ' : '';
  console.log(`${prefix}status: ${result.status}`);
  console.log(`${prefix}changed files (${result.changed.length}): ${result.changed.join(', ') || 'none'}`);
  console.log(`${prefix}added versions (${result.addedVersions.length}): ${result.addedVersions.join(', ') || 'none'}`);
} catch (e) {
  console.error(`meta-sync: ${e.message}`);
  process.exitCode = 1;
} finally {
  if (tmp) rmSync(tmp, { recursive: true, force: true });
}
