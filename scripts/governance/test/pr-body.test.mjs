import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  PRODUCTS,
  parseSections,
  isBlank,
  parseEvidence,
  renderEvidence,
  parseProductImpact,
  parseMetaImpact,
} from '../lib/pr-body.mjs';

const REQUIRED = ['summary', 'vnext scope', 'e2e evidence', 'product impact', 'vnext-meta impact'];

test('parses CRLF body', () => {
  const body = [
    '## Summary', 'text', '',
    '## vNext scope', 'core', '',
    '## E2E evidence', '- Status: run', '',
    '## Product impact', '| Product | Needed | Handoff |', '',
    '## vnext-meta impact', '- Status: not-needed',
  ].join('\r\n');
  const sections = parseSections(body);
  for (const key of REQUIRED) assert.ok(sections.has(key), `missing ${key}`);
  assert.equal(sections.get('summary'), 'text');
  assert.ok(!sections.get('e2e evidence').includes('\r'));
});

test('treats placeholders and comments as empty', () => {
  assert.equal(isBlank('<!-- x -->\n- Runtime: `<sha>`'), true);
  assert.equal(isBlank(undefined), true);
  assert.equal(isBlank(''), true);
  assert.equal(isBlank('<!-- only a comment -->'), true);
  assert.equal(isBlank('-\n- [ ]'), true);
  assert.equal(isBlank('- Reason: <reason>'), true);
  assert.equal(isBlank('- Runtime: `3f2a9c1`'), false);
  assert.equal(isBlank('real text <!-- hidden -->'), false);
});

test('matches headings case-insensitively', () => {
  const sections = parseSections('##  E2E Evidence \nbody\n## Summary\n<!-- c -->\nhello <!-- d -->');
  assert.equal(sections.get('e2e evidence'), 'body');
  assert.equal(sections.get('summary'), 'hello');
});

test('round-trips run evidence', () => {
  const md = [
    '- Status: run',
    '- Runtime: `3f2a9c1`',
    '- Base URL: `http://localhost:4201`',
    '- Result: 120/124 passed, 4 failed, 0 skipped',
    '',
    '| Suite | Passed | Failed | Skipped |',
    '|---|---|---|---|',
    '| SubflowLifecycleTests | 12 | 0 | 0 |',
    '',
    '- Reds: 4 × MockLab down (environment defect)',
  ].join('\n');
  const parsed = parseEvidence(md);
  assert.deepEqual(parsed, {
    status: 'run',
    runtimeSha: '3f2a9c1',
    baseUrl: 'http://localhost:4201',
    totals: { passed: 120, failed: 4, skipped: 0, total: 124 },
    suites: [{ name: 'SubflowLifecycleTests', passed: 12, failed: 0, skipped: 0 }],
    reds: '4 × MockLab down (environment defect)',
  });
  assert.deepEqual(parseEvidence(renderEvidence(parsed)), parsed);
  assert.equal(renderEvidence(parsed), md);
});

test('parses not-run with reason', () => {
  const parsed = parseEvidence('- Status: not-run\n- Reason: docs only change');
  assert.deepEqual(parsed, { status: 'not-run', reason: 'docs only change', suites: [] });
  assert.deepEqual(parseEvidence(renderEvidence(parsed)), parsed);
  assert.equal(parseEvidence('- Status: maybe').status, null);
  assert.deepEqual(parseEvidence('').suites, []);
});

test('keeps inner backticks in reason and reds', () => {
  const nr = parseEvidence('- Status: not-run\n- Reason: fix `foo`');
  assert.equal(nr.reason, 'fix `foo`');
  assert.deepEqual(parseEvidence(renderEvidence(nr)), nr);
  const run = parseEvidence('- Status: run\n- Reds: `MockLab` down');
  assert.equal(run.reds, '`MockLab` down');
  assert.deepEqual(parseEvidence(renderEvidence(run)), run);
  const run2 = parseEvidence('- Status: run\n- Reds: 2 × timeout in `Foo`');
  assert.equal(run2.reds, '2 × timeout in `Foo`');
  assert.deepEqual(parseEvidence(renderEvidence(run2)), run2);
});

test('parses product table', () => {
  const rows = PRODUCTS.map((p, i) => `| ${p} | ${i % 2 ? 'No' : 'YES'} | ${i % 2 ? '' : 'docs/integration/x/y.md'} |`);
  rows.push('| Extra-Product | maybe | |');
  const text = ['| Product | Needed | Handoff |', '|---|---|---|', ...rows].join('\n');
  const parsed = parseProductImpact(text);
  assert.equal(PRODUCTS.length, 10);
  assert.equal(parsed.length, 11);
  assert.deepEqual(parsed[0], { product: 'vnext-schema', needed: 'yes', handoff: 'docs/integration/x/y.md' });
  assert.equal(parsed[1].needed, 'no');
  assert.equal(parsed[10].product, 'extra-product');
  assert.equal(parsed[10].needed, null);
});

test('parses meta impact not-needed with reason', () => {
  assert.deepEqual(parseMetaImpact('- Status: not-needed\n- Reason: no feature change'), {
    status: 'not-needed',
    reason: 'no feature change',
  });
  assert.deepEqual(parseMetaImpact('- Status: Updated'), { status: 'updated' });
  assert.deepEqual(parseMetaImpact(''), { status: null });
});
