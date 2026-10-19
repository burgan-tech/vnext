import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync, mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { summarizeTrx } from '../e2e-evidence.mjs';
import { parseEvidence } from '../lib/pr-body.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const SCRIPT = join(here, '..', 'e2e-evidence.mjs');
const FIXTURE = join(here, 'fixtures', 'sample.trx');
const SHA = 'abcdef1234567';

const cli = (...args) => spawnSync(process.execPath, [SCRIPT, ...args], { encoding: 'utf8' });

test('summarizeTrx counts outcomes and groups by class name', () => {
  const r = summarizeTrx(readFileSync(FIXTURE, 'utf8'));
  assert.deepEqual(r.totals, { passed: 3, failed: 1, skipped: 1, total: 5 });
  assert.equal(r.suites.length, 2);
  assert.deepEqual(r.suites, [
    { name: 'AlphaTests', passed: 2, failed: 1, skipped: 0 },
    { name: 'BetaTests', passed: 1, failed: 0, skipped: 1 },
  ]);
});

test('CLI output parses as a run evidence block with reds', () => {
  const r = cli('--trx', FIXTURE, '--runtime-sha', SHA, '--base-url', 'http://localhost:4201', '--reds', 'A_Three: pre-existing');
  assert.equal(r.status, 0, r.stderr);
  const e = parseEvidence(r.stdout);
  assert.equal(e.status, 'run');
  assert.equal(e.runtimeSha, SHA);
  assert.equal(e.baseUrl, 'http://localhost:4201');
  assert.ok(e.suites.length >= 1);
  assert.ok(e.reds);
  assert.deepEqual(e.totals, { passed: 3, failed: 1, skipped: 1, total: 5 });
});

test('CLI requires --reds when something failed', () => {
  const r = cli('--trx', FIXTURE, '--runtime-sha', SHA, '--base-url', 'http://x');
  assert.equal(r.status, 2);
  assert.match(r.stderr, /Reds required/);
});

test('CLI rejects a short runtime sha and a missing base url', () => {
  assert.equal(cli('--trx', FIXTURE, '--runtime-sha', 'abc12', '--base-url', 'http://x', '--reds', 'r').status, 2);
  assert.equal(cli('--trx', FIXTURE, '--runtime-sha', SHA).status, 2);
});

test('CLI rejects option values starting with a dash', () => {
  assert.equal(cli('--trx', '-x', '--runtime-sha', SHA, '--base-url', 'http://x').status, 2);
});

test('multiple trx files are summed and same-name suites merge', () => {
  const dir = mkdtempSync(join(tmpdir(), 'trx-'));
  const second = join(dir, 'b.trx');
  writeFileSync(second, readFileSync(FIXTURE, 'utf8'));
  const r = cli('--trx', FIXTURE, '--trx', second, '--runtime-sha', SHA.toUpperCase(), '--base-url', 'http://x', '--reds', 'known');
  assert.equal(r.status, 0, r.stderr);
  const e = parseEvidence(r.stdout);
  assert.deepEqual(e.totals, { passed: 6, failed: 2, skipped: 2, total: 10 });
  assert.equal(e.suites.length, 2);
  assert.equal(e.suites[0].passed, 4);
});

test('all-green run needs no reds', () => {
  const dir = mkdtempSync(join(tmpdir(), 'trx-'));
  const f = join(dir, 'g.trx');
  writeFileSync(f, readFileSync(FIXTURE, 'utf8').replace('outcome="Failed"', 'outcome="Passed"'));
  const r = cli('--trx', f, '--runtime-sha', SHA, '--base-url', 'http://x');
  assert.equal(r.status, 0, r.stderr);
  assert.equal(parseEvidence(r.stdout).reds, undefined);
});
