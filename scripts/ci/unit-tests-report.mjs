#!/usr/bin/env node
// Summarises unit-test TRX files for CI and decides whether the run blocks.
//   node scripts/ci/unit-tests-report.mjs --trx <file>... [--mode enforce|report]
//                                        [--summary <md out>] [--failures-out <json out>]
// Modes:
//   enforce  (default) fails on any failing test — the suite is kept green, there is no known-red list
//   report   never fails; an emergency switch only (repository variable UNIT_TESTS_MODE=report)
// Both print totals and the failures grouped by error signature.
import { readFileSync, writeFileSync, realpathSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const MODES = new Set(['enforce', 'report']);
const byText = (a, b) => a.localeCompare(b);
const OUTCOME_KIND = { Passed: 'passed', Failed: 'failed' };

function attr(tagAttrs, name) {
  const m = new RegExp(String.raw`(?:^|\s)${name}\s*=\s*(?:"([^"]*)"|'([^']*)')`).exec(tagAttrs);
  return m ? (m[1] ?? m[2]) : null;
}

function decode(s) {
  return s
    .replaceAll('&lt;', '<').replaceAll('&gt;', '>').replaceAll('&quot;', '"')
    .replaceAll('&apos;', "'").replace(/&#xD;|&#xA;|&#13;|&#10;/g, '\n').replaceAll('&amp;', '&');
}

/** Totals and the failed results (name + first message line) of one TRX document. */
export function parseTrx(xml) {
  const totals = { passed: 0, failed: 0, skipped: 0, total: 0 };
  const failures = [];
  for (const m of xml.matchAll(/<UnitTestResult\b([^>]*?)(?:\/>|>([\s\S]*?)<\/UnitTestResult>)/g)) {
    const kind = OUTCOME_KIND[attr(m[1], 'outcome')] ?? 'skipped';
    totals[kind]++;
    totals.total++;
    if (kind !== 'failed') continue;
    const msg = /<Message>([\s\S]*?)<\/Message>/.exec(m[2] ?? '');
    failures.push({ name: decode(attr(m[1], 'testName') ?? '(unknown)'), message: msg ? decode(msg[1]).trim() : '' });
  }
  return { totals, failures };
}

/** Error signature: first message line with numbers, GUIDs and quoted values blanked. */
export function signature(message) {
  const first = (message.split('\n').find((l) => l.trim()) ?? '(no message)').trim();
  return first
    .replace(/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/gi, '<guid>')
    .replace(/"[^"]*"|'[^']*'/g, '<v>')
    .replace(/\d+/g, 'N')
    .slice(0, 200);
}

export function groupBySignature(failures) {
  const groups = new Map();
  for (const f of failures) {
    const key = signature(f.message);
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(f.name);
  }
  return [...groups.entries()]
    .map(([sig, names]) => ({ signature: sig, count: names.length, tests: names.sort(byText) }))
    .sort((a, b) => b.count - a.count || a.signature.localeCompare(b.signature));
}

export function decide(mode, totals) {
  return mode === 'enforce' && totals.failed > 0 ? 'fail' : 'pass';
}

const esc = (s) => s.replaceAll('|', String.raw`\|`).replaceAll('`', "'");
const code = (s) => '`' + esc(s) + '`';

export function renderSummary({ mode, perFile, totals, groups, verdict }) {
  const lines = [
    '## Unit tests',
    '',
    `Mode: ${code(mode)} · Result: **${verdict === 'fail' ? 'blocking failures' : 'not blocking'}**`,
    '',
    '| Project | Passed | Failed | Skipped | Total |',
    '|---|---:|---:|---:|---:|',
  ];
  for (const f of perFile) lines.push(`| ${esc(f.name)} | ${f.totals.passed} | ${f.totals.failed} | ${f.totals.skipped} | ${f.totals.total} |`);
  lines.push(`| **All** | **${totals.passed}** | **${totals.failed}** | **${totals.skipped}** | **${totals.total}** |`);
  if (groups.length) {
    lines.push('', '### Failures by error signature', '', '| # | Signature | Example tests |', '|---:|---|---|');
    for (const g of groups.slice(0, 40)) {
      const examples = g.tests.slice(0, 3).map(code).join('<br>');
      lines.push(`| ${g.count} | ${code(g.signature)} | ${examples} |`);
    }
  }
  return lines.join('\n') + '\n';
}

function parseArgs(argv) {
  const out = { trx: [], mode: 'enforce' };
  for (let i = 0; i < argv.length; i += 2) {
    const key = argv[i]?.replace(/^--/, '');
    const value = argv[i + 1];
    if (!argv[i]?.startsWith('--') || value === undefined) throw new Error(`Bad argument near ${argv[i]}`);
    if (key === 'trx') out.trx.push(value);
    else if (['mode', 'summary', 'failures-out'].includes(key)) out[key] = value;
    else throw new Error(`Unknown option: --${key}`);
  }
  if (!MODES.has(out.mode)) throw new Error(`--mode must be one of ${[...MODES].join(', ')}`);
  return out;
}

function main() {
  const args = parseArgs(process.argv.slice(2));
  if (args.trx.length === 0) throw new Error('--trx is required');

  const perFile = args.trx.map((f) => ({ name: f.split('/').pop().replace(/\.trx$/, ''), ...parseTrx(readFileSync(f, 'utf8')) }));
  const totals = { passed: 0, failed: 0, skipped: 0, total: 0 };
  for (const f of perFile) for (const k of Object.keys(totals)) totals[k] += f.totals[k];
  const failures = perFile.flatMap((f) => f.failures);
  if (totals.total === 0) throw new Error('No test results found in the TRX input.');

  const verdict = decide(args.mode, totals);

  const md = renderSummary({ mode: args.mode, perFile, totals, groups: groupBySignature(failures), verdict });
  if (args.summary) writeFileSync(args.summary, md, { flag: 'a' });
  else process.stdout.write(md);
  if (args['failures-out']) {
    writeFileSync(args['failures-out'], JSON.stringify({ failing: [...new Set(failures.map((f) => f.name))].sort(byText) }, null, 2) + '\n');
  }
  console.log(`unit-tests-report: ${totals.failed} failed of ${totals.total}; mode=${args.mode}; verdict=${verdict}`);
  if (verdict === 'fail') process.exit(1);
}

if (process.argv[1] && realpathSync(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    main();
  } catch (err) {
    console.error(`unit-tests-report: ${err.message}`);
    process.exit(2);
  }
}
