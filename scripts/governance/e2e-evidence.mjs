#!/usr/bin/env node
// Generates the `## E2E evidence` block for a PR body from one or more TRX files.
//   node scripts/governance/e2e-evidence.mjs --trx <file>... --runtime-sha <sha> --base-url <url> [--reds "<text>"]
// TRX comes from the integration tests in vnext-example:
//   dotnet test --logger "trx;LogFileName=e2e.trx"
// Output (stdout) is the block format that parseEvidence in lib/pr-body.mjs reads.
import { readFileSync, realpathSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { renderEvidence } from './lib/pr-body.mjs';

const USAGE = '--trx <file>... --runtime-sha <sha (7+ hex)> --base-url <url> [--reds "<text>"]';

function attr(tagAttrs, name) {
  const m = new RegExp(`(?:^|\\s)${name}\\s*=\\s*(?:"([^"]*)"|'([^']*)')`).exec(tagAttrs);
  return m ? (m[1] ?? m[2]) : null;
}

/** Counts TRX results per test class. Outcomes other than Passed/Failed count as skipped. */
export function summarizeTrx(xml) {
  const classById = new Map();
  for (const m of xml.matchAll(/<UnitTest\b([^>]*)>([\s\S]*?)<\/UnitTest>/g)) {
    const id = attr(m[1], 'id');
    const method = /<TestMethod\b([^>]*?)\/?>/.exec(m[2]);
    const className = method ? attr(method[1], 'className') : null;
    if (id && className) classById.set(id, className.slice(className.lastIndexOf('.') + 1));
  }

  const totals = { passed: 0, failed: 0, skipped: 0, total: 0 };
  const suites = new Map();
  for (const m of xml.matchAll(/<UnitTestResult\b([^>]*?)\/?>/g)) {
    const outcome = attr(m[1], 'outcome');
    const kind = outcome === 'Passed' ? 'passed' : outcome === 'Failed' ? 'failed' : 'skipped';
    const name = classById.get(attr(m[1], 'testId')) ?? '(unknown)';
    if (!suites.has(name)) suites.set(name, { name, passed: 0, failed: 0, skipped: 0 });
    suites.get(name)[kind]++;
    totals[kind]++;
    totals.total++;
  }
  return { totals, suites: [...suites.values()].sort((a, b) => a.name.localeCompare(b.name)) };
}

function merge(summaries) {
  const totals = { passed: 0, failed: 0, skipped: 0, total: 0 };
  const suites = new Map();
  for (const s of summaries) {
    for (const k of Object.keys(totals)) totals[k] += s.totals[k];
    for (const suite of s.suites) {
      const cur = suites.get(suite.name) ?? { name: suite.name, passed: 0, failed: 0, skipped: 0 };
      cur.passed += suite.passed;
      cur.failed += suite.failed;
      cur.skipped += suite.skipped;
      suites.set(suite.name, cur);
    }
  }
  return { totals, suites: [...suites.values()].sort((a, b) => a.name.localeCompare(b.name)) };
}

const NO_DASH = new Set(['trx', 'runtime-sha', 'base-url']);

function parseArgs(argv) {
  const out = { trx: [] };
  for (let i = 0; i < argv.length; i++) {
    if (!argv[i].startsWith('--')) throw new Error(`Unexpected argument: ${argv[i]}`);
    const key = argv[i].slice(2);
    const value = argv[i + 1];
    if (value === undefined || value.startsWith('--')) throw new Error(`--${key} needs a value`);
    if (NO_DASH.has(key) && value.startsWith('-')) throw new Error(`--${key} value must not start with "-" (usage: ${USAGE})`);
    if (key === 'trx') out.trx.push(value);
    else if (key === 'runtime-sha' || key === 'base-url' || key === 'reds') out[key] = value;
    else throw new Error(`Unknown option: --${key} (usage: ${USAGE})`);
    i++;
  }
  return out;
}

function main() {
  const args = parseArgs(process.argv.slice(2));
  if (args.trx.length === 0) throw new Error(`--trx is required (usage: ${USAGE})`);
  if (!args['runtime-sha'] || !/^[0-9a-f]{7,}$/i.test(args['runtime-sha'])) {
    throw new Error('--runtime-sha must be a git SHA of at least 7 hex characters');
  }
  if (!args['base-url']) throw new Error('--base-url is required');

  const { totals, suites } = merge(args.trx.map((f) => summarizeTrx(readFileSync(f, 'utf8'))));
  if (totals.total === 0) throw new Error('No test results found in the TRX input.');
  if (totals.failed > 0 && !args.reds) {
    throw new Error(`Reds required: ${totals.failed} test(s) failed; pass --reds "<which failed and why>".`);
  }

  console.log(renderEvidence({
    status: 'run',
    runtimeSha: args['runtime-sha'],
    baseUrl: args['base-url'],
    totals,
    suites,
    reds: totals.failed > 0 ? args.reds : undefined,
  }));
}

if (process.argv[1] && realpathSync(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    main();
  } catch (err) {
    console.error(`e2e-evidence: ${err.message}`);
    process.exit(2);
  }
}
