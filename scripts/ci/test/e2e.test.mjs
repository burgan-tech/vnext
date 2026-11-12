import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseSuites, selectRun, buildFilter, redsLine, failedTests, applyEvidence } from '../lib/e2e.mjs';
import { parseEvidence } from '../../governance/lib/pr-body.mjs';

test('parseSuites: human line and AI Done/skipped row, junk dropped', () => {
  assert.deepEqual(parseSuites('x\nE2E suites: Smoke, ChainBusy  ChainBusy\n'), ['Smoke', 'ChainBusy']);
  assert.deepEqual(parseSuites('| E2E (vnext-example) | not-run | suites: FanOut, TimeoutLab — run them locally |'), ['FanOut', 'TimeoutLab']);
  assert.deepEqual(parseSuites('E2E suites: Smoke; rm -rf /, ../x, `Bad`'), []);
  assert.deepEqual(parseSuites('E2E suites: Smoke,ErrorBoundaryLab'), ['Smoke', 'ErrorBoundaryLab']);
  assert.deepEqual(parseSuites(null), []);
});

test('selectRun: who runs, with which suites', () => {
  const pr = (over) => selectRun({ eventName: 'pull_request', action: 'synchronize', headRef: 'feature/x', labels: [], body: '', ...over });
  assert.equal(pr({}).run, false);
  assert.deepEqual(pr({ headRef: 'ai/issue-7', body: '| E2E (vnext-example) | not-run | suites: Smoke — x |' }), { run: true, suites: ['Smoke'], why: 'AI PR' });
  assert.deepEqual(pr({ headRef: 'ai/issue-7' }).suites, ['Smoke'], 'AI PR without suites runs the smoke test');
  assert.deepEqual(pr({ labels: ['e2e'] }).suites, [], 'a human e2e label without suites runs the full set');
  assert.deepEqual(pr({ labels: ['e2e'], body: 'E2E suites: ChainBusy' }).suites, ['ChainBusy']);
  assert.deepEqual(pr({ labels: ['e2e:full', 'e2e'], body: 'E2E suites: ChainBusy' }).suites, []);
  assert.equal(pr({ action: 'labeled', label: 'ai:pr-ready', headRef: 'ai/issue-7' }).run, false, 'pipeline labels never restart it');
  assert.equal(pr({ action: 'labeled', label: 'e2e', labels: ['e2e'] }).run, true);
  assert.equal(pr({ headRef: 'ai/issue-7x' }).run, false);
  assert.deepEqual(selectRun({ eventName: 'schedule' }).suites, []);
  assert.deepEqual(selectRun({ eventName: 'workflow_dispatch', inputSuites: 'Smoke, FanOut' }).suites, ['Smoke', 'FanOut']);
});

test('buildFilter: known suites narrow the run, an unknown one widens it to the full set', () => {
  assert.deepEqual(buildFilter([], ['Smoke']), { filter: '', note: '' });
  assert.equal(buildFilter(['Smoke', 'ChainBusy'], ['Smoke', 'ChainBusy']).filter,
    'FullyQualifiedName~Core.IntegrationTests.Tests.Smoke|FullyQualifiedName~Core.IntegrationTests.Tests.ChainBusy');
  assert.deepEqual(buildFilter(['Smoke', 'Nope'], ['Smoke']), { filter: '', note: 'unknown suite(s) Nope — full set run instead' });
});

test('failedTests + redsLine', () => {
  const trx = '<UnitTestResult testId="1" testName="Core.IntegrationTests.Tests.FanOut.FanOutTests.A" outcome="Failed" />'
    + '<UnitTestResult testId="2" testName="Core.IntegrationTests.Tests.Smoke.B" outcome="Passed"/>';
  const f = failedTests(trx);
  assert.deepEqual(f, ['FanOut.FanOutTests.A']);
  assert.match(redsLine(f, 'https://run'), /^1 failed in CI \(FanOut\.FanOutTests\.A\); not triaged — .*https:\/\/run$/);
  assert.match(redsLine(Array.from({ length: 12 }, (_, i) => `T${i}`), 'u'), /and 2 more/);
});

test('applyEvidence: replaces the section and the Done/skipped row, leaves the rest', () => {
  const body = '## Summary\nx\n\n## E2E evidence\n- Status: not-run\n- Reason: the e2e job is not built yet, run locally\n\n## Product impact\n| a | b |\n\n### Done / skipped\n| E2E (vnext-example) | not-run | suites: Smoke — later |\n';
  const ev = '- Status: run\n- Runtime: `abcdef1`\n- Base URL: `http://localhost:4201`\n- Result: 1/1 passed, 0 failed, 0 skipped\n\n| Suite | Passed | Failed | Skipped |\n|---|---|---|---|\n| SmokeTests | 1 | 0 | 0 |';
  const out = applyEvidence(body, ev, '1/1 passed — CI run https://x');
  assert.match(out, /## E2E evidence\n- Status: run\n[\s\S]*\| SmokeTests \| 1 \| 0 \| 0 \|\n\n## Product impact/);
  assert.match(out, /\| E2E \(vnext-example\) \| run \| 1\/1 passed — CI run https:\/\/x \|/);
  assert.doesNotMatch(out, /not built yet/);
  const section = out.split('## E2E evidence')[1].split('## Product impact')[0];
  assert.equal(parseEvidence(section).totals.passed, 1);
  assert.equal(applyEvidence('## Summary\nno section', ev, 'x'), null);
});
