import { test } from 'node:test';
import assert from 'node:assert/strict';
import { writeFileSync, mkdtempSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { parseTrx, signature, groupBySignature, compareWithBaseline, decide } from '../unit-tests-report.mjs';

const SCRIPT = join(dirname(fileURLToPath(import.meta.url)), '..', 'unit-tests-report.mjs');

const TRX = `<?xml version="1.0" encoding="utf-8"?>
<TestRun><Results>
  <UnitTestResult testId="1" testName="A.Tests.One" outcome="Passed" />
  <UnitTestResult testId="2" testName="A.Tests.Two" outcome="Failed"><Output><ErrorInfo>
    <Message>System.InvalidOperationException : Instance 42 not found&#xD;&#xA;second line</Message>
  </ErrorInfo></Output></UnitTestResult>
  <UnitTestResult testId="3" testName="A.Tests.Three" outcome="Failed"><Output><ErrorInfo>
    <Message>System.InvalidOperationException : Instance 7 not found</Message>
  </ErrorInfo></Output></UnitTestResult>
  <UnitTestResult testId="4" testName="A.Tests.Four" outcome="NotExecuted" />
  <UnitTestResult testId="5" testName="A.Tests.Five&lt;T&gt;" outcome="Failed"><Output><ErrorInfo>
    <Message>Assert.Equal() Failure: "a" vs "b"</Message>
  </ErrorInfo></Output></UnitTestResult>
</Results></TestRun>`;

const tmp = () => mkdtempSync(join(tmpdir(), 'utr-'));
const cli = (...args) => spawnSync(process.execPath, [SCRIPT, ...args], { encoding: 'utf8' });

test('parseTrx counts outcomes and keeps failure names and messages', () => {
  const r = parseTrx(TRX);
  assert.deepEqual(r.totals, { passed: 1, failed: 3, skipped: 1, total: 5 });
  assert.deepEqual(r.failures.map((f) => f.name), ['A.Tests.Two', 'A.Tests.Three', 'A.Tests.Five<T>']);
  assert.match(r.failures[0].message, /^System.InvalidOperationException : Instance 42 not found\n/);
});

test('signature blanks numbers and quoted values so same-cause failures group', () => {
  assert.equal(signature('Instance 42 not found\nmore'), 'Instance N not found');
  assert.equal(signature('Expected "x" got \'y\''), 'Expected <v> got <v>');
  const groups = groupBySignature(parseTrx(TRX).failures);
  assert.equal(groups[0].count, 2);
  assert.deepEqual(groups[0].tests, ['A.Tests.Three', 'A.Tests.Two']);
});

test('baseline comparison separates regressions, known reds and fixed tests', () => {
  const cmp = compareWithBaseline(parseTrx(TRX).failures, ['A.Tests.Two', 'A.Tests.Gone']);
  assert.deepEqual(cmp.regressions.map((f) => f.name), ['A.Tests.Three', 'A.Tests.Five<T>']);
  assert.deepEqual(cmp.knownReds.map((f) => f.name), ['A.Tests.Two']);
  assert.deepEqual(cmp.fixed, ['A.Tests.Gone']);
});

test('decide: report never fails, baseline fails on regressions, enforce on any failure', () => {
  const totals = { failed: 1 };
  assert.equal(decide('report', totals, { regressions: [1] }), 'pass');
  assert.equal(decide('baseline', totals, { regressions: [] }), 'pass');
  assert.equal(decide('baseline', totals, { regressions: [1] }), 'fail');
  assert.equal(decide('enforce', totals, { regressions: [] }), 'fail');
  assert.equal(decide('enforce', { failed: 0 }, { regressions: [] }), 'pass');
});

test('CLI: report mode exits 0 and writes the failures file', () => {
  const dir = tmp();
  const trx = join(dir, 'Domain.trx');
  writeFileSync(trx, TRX);
  const out = join(dir, 'failing.json');
  const r = cli('--trx', trx, '--failures-out', out);
  assert.equal(r.status, 0, r.stderr);
  assert.match(r.stdout, /\| Domain \| 1 \| 3 \| 1 \| 5 \|/);
  assert.equal(JSON.parse(readFileSync(out, 'utf8')).failing.length, 3);
});

test('CLI: baseline mode exits 1 on a regression and 0 when all reds are known', () => {
  const dir = tmp();
  const trx = join(dir, 'a.trx');
  writeFileSync(trx, TRX);
  const partial = join(dir, 'partial.json');
  writeFileSync(partial, JSON.stringify({ failing: ['A.Tests.Two'] }));
  assert.equal(cli('--trx', trx, '--mode', 'baseline', '--baseline', partial).status, 1);
  const full = join(dir, 'full.json');
  writeFileSync(full, JSON.stringify({ failing: ['A.Tests.Two', 'A.Tests.Three', 'A.Tests.Five<T>'] }));
  assert.equal(cli('--trx', trx, '--mode', 'baseline', '--baseline', full).status, 0);
});

test('CLI: baseline mode without a baseline file is a usage error', () => {
  const dir = tmp();
  const trx = join(dir, 'a.trx');
  writeFileSync(trx, TRX);
  assert.equal(cli('--trx', trx, '--mode', 'baseline').status, 2);
});
