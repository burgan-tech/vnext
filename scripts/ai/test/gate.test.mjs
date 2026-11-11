import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  GATE_MARKER, evaluateEvidence, fingerprint, validateReview, decideVerdict, nextRepairs, planGateLabels, renderGateComment,
  readGateData, isGateComment, isReviewedBase,
} from '../lib/gate.mjs';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const config = JSON.parse(readFileSync(join(root, '.github/ai/gate.json'), 'utf8'));
const taxonomy = JSON.parse(readFileSync(join(root, '.github/triage/labels.json'), 'utf8'));
const allLabels = new Set(Object.entries(taxonomy).filter(([k, v]) => k !== 'defaults' && Array.isArray(v)).flatMap(([, v]) => v.map((l) => l.name)));

const ok = (name, conclusion = 'success', started_at = '2026-11-10T10:00:00Z') => ({ name, status: 'completed', conclusion, started_at });
const green = () => [ok('Unit Tests'), ok('Build and analyze'), ok('pr-contract', 'skipped'), ok('SonarCloud Code Analysis'), ok('GitGuardian Security Checks')];
const sonarOk = { status: 'OK', conditions: [], issues: [] };
const evidence = (over = {}) => ({ checks: green(), statuses: [{ context: 'security/snyk (burgan-tech)', state: 'success' }], sonar: sonarOk, contract: [], isAiPr: true, ...over });
const cleanReview = { summary: 's', findings: [], acceptance: [{ criterion: 'c', met: true, evidence: 'e' }], confidence: 'high' };

test('green evidence and a clean review pass', () => {
  const ev = evaluateEvidence(evidence(), config);
  assert.equal(ev.complete, true);
  assert.deepEqual(ev.blocking, []);
  assert.equal(decideVerdict(ev, cleanReview).verdict, 'PASS');
});

test('a running or missing required check is INCONCLUSIVE, and the agent cannot change that', () => {
  const running = evaluateEvidence(evidence({ checks: [...green().filter((c) => c.name !== 'Unit Tests'), { name: 'Unit Tests', status: 'in_progress' }] }), config);
  assert.equal(running.complete, false);
  assert.equal(decideVerdict(running, cleanReview).verdict, 'INCONCLUSIVE');
  const missing = evaluateEvidence(evidence({ checks: green().filter((c) => c.name !== 'Build and analyze') }), config);
  assert.match(missing.pending.join(), /Build and analyze: not reported for this SHA/);
  // While other CI for the SHA is still queued, a missing check is not yet a reason to push again.
  const queued = evaluateEvidence(evidence({ checks: [...green().filter((c) => !['Build and analyze', 'Unit Tests'].includes(c.name)), { name: 'Unit Tests', status: 'queued' }] }), config);
  assert.match(queued.pending.join(), /Build and analyze: not reported yet — other CI for this SHA is still running/);
  assert.doesNotMatch(queued.pending.join(), /push a commit/);
});

test('failed unit tests block whatever the review says', () => {
  const ev = evaluateEvidence(evidence({ checks: [...green().filter((c) => c.name !== 'Unit Tests'), ok('Unit Tests', 'failure')] }), config);
  const v = decideVerdict(ev, cleanReview);
  assert.equal(v.verdict, 'CHANGES_REQUIRED');
  assert.match(v.reasons[0], /Unit Tests failure — the unit-test suite must be green/);
});

test('the latest re-run of a check wins', () => {
  const checks = [...green().filter((c) => c.name !== 'Unit Tests'), ok('Unit Tests', 'failure', '2026-11-10T09:00:00Z'), ok('Unit Tests', 'success', '2026-11-10T11:00:00Z')];
  assert.deepEqual(evaluateEvidence(evidence({ checks }), config).blocking, []);
});

test('a Sonar gate failing only on new-code coverage is a COVERAGE_WARNING, not a block', () => {
  const sonar = { status: 'ERROR', conditions: [{ metricKey: 'new_coverage', status: 'ERROR', actualValue: '0.0', errorThreshold: '80' }], issues: [] };
  const checks = [...green().filter((c) => c.name !== 'SonarCloud Code Analysis'), ok('SonarCloud Code Analysis', 'failure')];
  const ev = evaluateEvidence(evidence({ checks, sonar }), config);
  assert.deepEqual(ev.blocking, []);
  assert.match(ev.warnings.join(), /COVERAGE_WARNING/);
  assert.equal(decideVerdict(ev, cleanReview).verdict, 'PASS');
});

test('a Sonar vulnerability or a non-coverage condition blocks', () => {
  const sonar = {
    status: 'ERROR',
    conditions: [{ metricKey: 'new_coverage', status: 'ERROR' }, { metricKey: 'new_security_rating', status: 'ERROR', actualValue: '3', errorThreshold: '1' }],
    issues: [{ key: 'k1', type: 'VULNERABILITY', rule: 'githubactions:S6506', component: '.github/workflows/x.yml', line: 9, message: 'https' }],
  };
  const checks = [...green().filter((c) => c.name !== 'SonarCloud Code Analysis'), ok('SonarCloud Code Analysis', 'failure')];
  const ev = evaluateEvidence(evidence({ checks, sonar }), config);
  assert.equal(ev.blocking.length, 2);
  assert.ok(!ev.warnings.some((w) => w.includes('COVERAGE_WARNING')));
});

test('a failed Sonar check whose conditions cannot be read is INCONCLUSIVE', () => {
  const checks = [...green().filter((c) => c.name !== 'SonarCloud Code Analysis'), ok('SonarCloud Code Analysis', 'failure')];
  assert.equal(evaluateEvidence(evidence({ checks, sonar: null }), config).complete, false);
});

test('PR-AI-* contract findings block a pipeline PR and only warn on a human PR', () => {
  const contract = [{ id: 'PR-AI-RECORD', message: 'missing' }, { id: 'PR-TITLE', message: 'x' }];
  assert.deepEqual(evaluateEvidence(evidence({ contract }), config).blocking, ['PR contract PR-AI-RECORD: missing']);
  assert.deepEqual(evaluateEvidence(evidence({ contract, isAiPr: false }), config).blocking, []);
});

test('a blocking finding or an unmet criterion requires changes; a missing review is never a pass', () => {
  const ev = evaluateEvidence(evidence(), config);
  assert.equal(decideVerdict(ev, { ...cleanReview, findings: [{ severity: 'blocking', title: 'NPE', file: 'a.cs', line: 3 }] }).verdict, 'CHANGES_REQUIRED');
  assert.equal(decideVerdict(ev, { ...cleanReview, acceptance: [{ criterion: 'x', met: false }] }).verdict, 'CHANGES_REQUIRED');
  assert.equal(decideVerdict(ev, null).verdict, 'INCONCLUSIVE');
});

test('a low-impact AI PR touching a high-impact path goes to the council', () => {
  const ev = evaluateEvidence(evidence(), config);
  const v = decideVerdict(ev, cleanReview, { isAiPr: true, impactHits: [{ id: 'migration', paths: ['src/BBT.Workflow.Infrastructure/Migrations/x.cs'] }] });
  assert.equal(v.verdict, 'COUNCIL');
});

test('review findings must point at a changed file and are sanitized', () => {
  const { review, dropped } = validateReview({
    findings: [
      { severity: 'blocking', title: 'bad @octocat', file: 'src/a.cs', line: 4, detail: '<!-- x --> ghp_abcdefghijklmnopqrstuvwxyz0123', fix: 'f' },
      { severity: 'blocking', title: 'elsewhere', file: 'src/other.cs' },
      { severity: 'whatever', title: 'w' },
    ],
    acceptance: [{ criterion: 'c', met: 'maybe' }],
    confidence: 'sure',
  }, { changedFiles: ['src/a.cs'] });
  assert.deepEqual(dropped, ['finding:src/other.cs']);
  assert.equal(review.findings.length, 2);
  assert.equal(review.findings[1].severity, 'warning');
  assert.ok(!review.findings[0].title.includes('@octocat'));
  assert.ok(review.findings[0].detail.includes('[redacted]') && !review.findings[0].detail.includes('<!--'));
  assert.equal(review.acceptance[0].met, 'unknown');
  assert.equal(review.confidence, 'low');
});

test('labels: PASS -> human approval; CHANGES_REQUIRED -> changes-required, blocked after the repair limit', () => {
  const opts = { isAiPr: true, attempts: 1, maxRepairs: 3 };
  const pass = planGateLabels('PASS', ['ai:pr-ready', 'ai:semver-patch'], opts);
  assert.deepEqual([pass.add, pass.remove], [['ai:human-approval'], ['ai:pr-ready']]);
  const fix = planGateLabels('CHANGES_REQUIRED', ['ai:pr-ready'], opts);
  assert.deepEqual([fix.add, fix.remove], [['ai:changes-required'], ['ai:pr-ready']]);
  assert.deepEqual(planGateLabels('CHANGES_REQUIRED', ['ai:fixing'], { ...opts, attempts: 4 }).add, ['ai:blocked']);
  const council = planGateLabels('COUNCIL', ['ai:pr-ready', 'ai:impact-low'], opts);
  assert.deepEqual(council.add, ['ai:impact-high', 'ai:council-required']);
  assert.deepEqual(council.remove.sort(), ['ai:impact-low', 'ai:pr-ready']);
  for (const v of ['PASS', 'CHANGES_REQUIRED', 'COUNCIL']) for (const l of [...planGateLabels(v, ['ai:pr-ready'], { ...opts, attempts: 9 }).add]) assert.ok(allLabels.has(l), l);
});

test('labels: human PRs, INCONCLUSIVE and a human ai:blocked change nothing', () => {
  const none = { add: [], remove: [], next: null };
  assert.deepEqual(planGateLabels('PASS', ['ai:pr-ready'], { isAiPr: false, attempts: 0, maxRepairs: 3 }), none);
  assert.deepEqual(planGateLabels('INCONCLUSIVE', ['ai:pr-ready'], { isAiPr: true, attempts: 0, maxRepairs: 3 }), none);
  assert.deepEqual(planGateLabels('CHANGES_REQUIRED', ['ai:blocked'], { isAiPr: true, attempts: 0, maxRepairs: 3 }), none);
});

test('repair attempts count distinct SHAs that needed changes', () => {
  let shas = nextRepairs(null, 'CHANGES_REQUIRED', 'a');
  shas = nextRepairs({ repairShas: shas }, 'CHANGES_REQUIRED', 'a');
  shas = nextRepairs({ repairShas: shas }, 'PASS', 'b');
  shas = nextRepairs({ repairShas: shas }, 'CHANGES_REQUIRED', 'c');
  assert.deepEqual(shas, ['a', 'c']);
});

test('the fingerprint changes with the evidence, not with ordering', () => {
  const base = { sha: 's', checks: green(), statuses: [], sonar: sonarOk, contract: [], labels: ['ai:pr-ready', 'bug'] };
  assert.equal(fingerprint(base), fingerprint({ ...base, checks: [...green()].reverse(), labels: ['bug', 'ai:pr-ready'] }));
  assert.notEqual(fingerprint(base), fingerprint({ ...base, sha: 't' }));
  assert.notEqual(fingerprint(base), fingerprint({ ...base, checks: [...green().slice(1), ok('Unit Tests', 'failure', '2026-11-10T12:00:00Z')] }));
});

test('the comment round-trips its data and marks a dry run', () => {
  const ev = evaluateEvidence(evidence(), config);
  const body = renderGateComment({ pr: 5, sha: 'abcdef1234567890', verdict: 'PASS', reasons: [], evaluation: ev, review: cleanReview, labels: { add: ['ai:human-approval'], remove: [] }, mode: 'dry-run', attempts: 0, maxRepairs: 3, isAiPr: true, fp: 'f1', repairShas: [], risk: 'low' });
  assert.ok(body.startsWith(GATE_MARKER));
  assert.match(body, /dry run — labels not applied/);
  assert.match(body, /would add `ai:human-approval`/);
  assert.deepEqual(readGateData(body), { sha: 'abcdef1234567890', verdict: 'PASS', fp: 'f1', repairShas: [] });
  assert.ok(isGateComment({ user: { login: 'github-actions[bot]' }, body }));
});

test('reviewed bases follow gate.json', () => {
  assert.ok(isReviewedBase('master', config.baseBranches));
  assert.ok(isReviewedBase('release-v0.0', config.baseBranches));
  assert.ok(!isReviewedBase('sprint-21', config.baseBranches));
});
