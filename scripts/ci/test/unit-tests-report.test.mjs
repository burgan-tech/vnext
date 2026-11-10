import { test } from 'node:test';
import assert from 'node:assert/strict';
import { writeFileSync, mkdtempSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { parseTrx, signature, groupBySignature, decide } from '../unit-tests-report.mjs';

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

test('decide: enforce fails on any failure, report never fails', () => {
  assert.equal(decide('enforce', { failed: 1 }), 'fail');
  assert.equal(decide('enforce', { failed: 0 }), 'pass');
  assert.equal(decide('report', { failed: 3 }), 'pass');
});

test('CLI: report mode exits 0 and writes the failures file', () => {
  const dir = tmp();
  const trx = join(dir, 'Domain.trx');
  writeFileSync(trx, TRX);
  const out = join(dir, 'failing.json');
  const r = cli('--trx', trx, '--mode', 'report', '--failures-out', out);
  assert.equal(r.status, 0, r.stderr);
  assert.match(r.stdout, /\| Domain \| 1 \| 3 \| 1 \| 5 \|/);
  assert.equal(JSON.parse(readFileSync(out, 'utf8')).failing.length, 3);
});

test('CLI: enforce is the default and exits 1 on a failure', () => {
  const dir = tmp();
  const trx = join(dir, 'a.trx');
  writeFileSync(trx, TRX);
  assert.equal(cli('--trx', trx).status, 1);
});

test('CLI: enforce exits 0 when every test passed', () => {
  const dir = tmp();
  const trx = join(dir, 'a.trx');
  writeFileSync(trx, '<TestRun><Results><UnitTestResult testName="A.Ok" outcome="Passed" /></Results></TestRun>');
  assert.equal(cli('--trx', trx).status, 0);
});

test('CLI: an unknown mode is a usage error', () => {
  const dir = tmp();
  const trx = join(dir, 'a.trx');
  writeFileSync(trx, TRX);
  assert.equal(cli('--trx', trx, '--mode', 'baseline').status, 2);
});
