#!/usr/bin/env node
// CLI: node scripts/governance/meta-lint.mjs [--milestone vX.Y.Z] [--json]
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { lintMeta } from './lib/meta-lint.mjs';

const args = process.argv.slice(2);
const json = args.includes('--json');
const mi = args.indexOf('--milestone');
const milestone = mi >= 0 ? args[mi + 1] : undefined;
if (mi >= 0 && !/^v\d+\.\d+\.\d+$/.test(milestone ?? '')) {
  console.error('--milestone must look like vX.Y.Z');
  process.exit(2);
}

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const findings = lintMeta({ metaDir: resolve(repoRoot, 'vnext-meta'), repoRoot, milestone });

if (json) {
  console.log(JSON.stringify(findings, null, 2));
} else {
  for (const f of findings) console.log(`${f.severity.toUpperCase()} ${f.id} ${f.file}: ${f.message}`);
}
process.exit(findings.some((f) => f.severity === 'error') ? 1 : 0);
