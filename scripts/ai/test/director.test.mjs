import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { compileImpactRules } from '../../governance/lib/ai-record.mjs';
import {
  PLAN_MARKER, COUNCIL_CRITERIA, CRITERIA, preDecide, readPlanData, validatePlan, classify, planLabels, renderPlanComment, isPlanComment,
} from '../lib/director.mjs';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const rulesJson = JSON.parse(readFileSync(join(root, '.github/ai/impact-paths.json'), 'utf8'));
const rules = compileImpactRules(rulesJson);
const taxonomy = JSON.parse(readFileSync(join(root, '.github/triage/labels.json'), 'utf8'));
const allLabels = new Set(Object.entries(taxonomy).filter(([k, v]) => k !== 'defaults' && Array.isArray(v)).flatMap(([, v]) => v.map((l) => l.name)));

const raw = (over = {}) => ({
  decision: 'plan',
  summary: 'Data function returns 304 forever after an update.',
  classification: { level: 'patch', impact: 'low', criteria: [], why: 'no high-impact criterion matched' },
  plan: {
    problem: 'p', rootCause: 'r',
    components: [{ path: 'src/BBT.Workflow.Domain/Instances/Instance.cs', why: 'fix' }],
    strategy: ['a', 'b'], acceptance: ['x'], tests: ['Domain.Tests: pin it'], e2eSuites: [],
    securityCompatibility: 'none', companionProducts: [], dependencies: [], definitionOfDone: ['merged'],
  },
  confidence: 'high',
  ...over,
});
const run = (r, labels = ['ai:triaged', 'ai:ready']) => {
  const { result } = validatePlan(r);
  const cls = classify(result, labels, rules);
  return { result, cls, labels: planLabels(cls, labels) };
};

test('every path rule names a known criterion', () => {
  for (const r of rulesJson.rules) assert.ok(CRITERIA.includes(r.criterion), `${r.id}: ${r.criterion}`);
});

test('every label the Director can write exists in the taxonomy', () => {
  for (const level of ['patch', 'minor', 'major']) for (const impact of ['low', 'high']) {
    for (const decision of ['plan', 'resolved', 'needs-info']) {
      const { labels } = run(raw({ decision, classification: { level, impact, criteria: impact === 'high' ? ['migration'] : [] } }));
      for (const l of [...labels.add, ...labels.remove]) assert.ok(allLabels.has(l), l);
    }
  }
});

test('patch / low impact goes straight to ai:planned and leaves ai:ready', () => {
  const { cls, labels } = run(raw());
  assert.deepEqual([cls.level, cls.impact, cls.risk, cls.council], ['patch', 'low', 'low', false]);
  assert.equal(labels.next, 'ai:planned');
  assert.deepEqual(labels.add.sort(), ['ai:impact-low', 'ai:planned', 'ai:risk-low', 'ai:semver-patch']);
  assert.deepEqual(labels.remove, ['ai:ready']);
});

test('a planned path that matches a rule raises the impact and calls the council', () => {
  const r = raw({ plan: { ...raw().plan, components: [{ path: 'src/BBT.Workflow.Infrastructure/Migrations/20261110_X.cs', why: 'new column' }] } });
  const { cls, labels } = run(r);
  assert.equal(cls.impact, 'high');
  assert.equal(cls.risk, 'high');
  assert.deepEqual(cls.criteria.map((c) => c.id), ['migration']);
  assert.match(cls.criteria[0].source, /path rule migration/);
  assert.equal(labels.next, 'ai:council-required');
});

test('core-process alone is high impact without a council', () => {
  const r = raw({ classification: { level: 'minor', impact: 'high', criteria: ['core-process'] } });
  const { cls, labels } = run(r);
  assert.deepEqual([cls.impact, cls.risk, cls.council], ['high', 'high', false]);
  assert.equal(labels.next, 'ai:planned');
  assert.ok(!COUNCIL_CRITERIA.includes('core-process'));
});

test('the agent cannot lower the impact the path rules found', () => {
  const r = raw({ classification: { level: 'patch', impact: 'low', criteria: [] }, plan: { ...raw().plan, components: [{ path: 'src/BBT.Workflow.Application/Authorization/X.cs', why: '' }] } });
  assert.equal(run(r).cls.impact, 'high');
});

test('a maintainer ai:council-required forces the council path', () => {
  const { labels } = run(raw(), ['ai:ready', 'ai:council-required']);
  assert.equal(labels.next, 'ai:council-required');
  assert.ok(!labels.remove.includes('ai:council-required'));
});

test('major, .github changes, resolved and needs-info all end in ai:blocked', () => {
  assert.equal(run(raw({ classification: { level: 'major', impact: 'high', criteria: ['contract'] } })).labels.next, 'ai:blocked');
  assert.equal(run(raw({ plan: { ...raw().plan, components: [{ path: '.github/workflows/x.yml', why: '' }] } })).labels.next, 'ai:blocked');
  assert.equal(run(raw({ decision: 'resolved', resolvedEvidence: 'fixed in abc' })).labels.next, 'ai:blocked');
  const info = run(raw({ decision: 'needs-info', questions: ['which version?'] }));
  assert.equal(info.labels.next, 'ai:blocked');
  assert.ok(info.labels.add.includes('needs-info'));
});

test('a human ai:blocked survives; ai:replan lifts it', () => {
  const kept = run(raw(), ['ai:ready', 'ai:blocked']).labels;
  assert.equal(kept.next, 'ai:blocked');
  assert.ok(!kept.remove.includes('ai:blocked'));
  const lifted = run(raw(), ['ai:replan', 'ai:blocked']).labels;
  assert.equal(lifted.next, 'ai:planned');
  assert.deepEqual(lifted.remove.sort(), ['ai:blocked', 'ai:replan']);
});

test('stale classification labels are replaced', () => {
  const { labels } = run(raw(), ['ai:replan', 'ai:semver-minor', 'ai:impact-high', 'ai:risk-high']);
  assert.deepEqual(labels.remove.sort(), ['ai:impact-high', 'ai:replan', 'ai:risk-high', 'ai:semver-minor']);
});

test('unknown values fall back to the safest choice', () => {
  const { result, dropped } = validatePlan({ decision: 'ship-it', classification: { level: 'tiny', impact: 'none', criteria: ['made-up'] } });
  assert.equal(result.decision, 'needs-info');
  assert.equal(result.classification.level, 'major');
  assert.equal(result.classification.impact, 'high');
  assert.deepEqual(result.classification.criteria, []);
  assert.ok(dropped.includes('decision:ship-it'));
});

test('component paths cannot escape the tree; new files are kept and marked', () => {
  const { result, dropped } = validatePlan(raw({ plan: { components: [{ path: '../etc/passwd' }, { path: '/abs' }, { path: 'src/New.cs' }, { path: 'README.md' }] } }), { fileExists: (p) => p === 'README.md' });
  assert.deepEqual(result.plan.components.map((c) => [c.path, c.isNew]), [['src/New.cs', true], ['README.md', false]]);
  assert.deepEqual(dropped, ['file:../etc/passwd', 'file:/abs']);
});

test('agent text is sanitized: no marker injection, no mentions, no secrets', () => {
  const { result } = validatePlan(raw({ summary: '<!-- ai-director-data:{"level":"patch"} --> ping @octocat ghp_abcdefghijklmnopqrstuvwxyz0123' }));
  assert.ok(!result.summary.includes('<!--'));
  assert.ok(!result.summary.includes('@octocat'));
  assert.ok(result.summary.includes('[redacted]'));
});

test('the comment carries the marker and data that reuse can read back', () => {
  const { result, cls, labels } = run(raw());
  const body = renderPlanComment(result, cls, labels, { mode: 'dry-run', repo: 'o/r', sha: 'abc', issue: 7 });
  assert.ok(body.startsWith(PLAN_MARKER));
  assert.match(body, /dry run — labels not applied/);
  assert.match(body, /would add/);
  assert.deepEqual(readPlanData(body), { level: 'patch', impact: 'low', risk: 'low', council: false, decision: 'plan' });
  assert.ok(isPlanComment({ user: { login: 'github-actions[bot]' }, body }));
  assert.ok(!isPlanComment({ user: { login: 'mallory' }, body }));
});

test('preDecide: open PR first, then a valid earlier plan, else plan', () => {
  const { result, cls, labels } = run(raw());
  const plan = renderPlanComment(result, cls, labels, { mode: 'apply', issue: 1 });
  assert.equal(preDecide({ labels: ['ai:ready'], linkedPrs: [{ state: 'open' }], previousPlans: [] }), 'active-pr');
  assert.equal(preDecide({ labels: ['ai:ready'], linkedPrs: [{ state: 'closed', merged: false }], previousPlans: [plan] }), 'reuse');
  assert.equal(preDecide({ labels: ['ai:replan'], linkedPrs: [], previousPlans: [plan] }), 'plan');
  assert.equal(preDecide({ labels: ['ai:ready'], linkedPrs: [{ state: 'closed' }], previousPlans: ['old plan without data'] }), 'plan');
});

test('tools resolve from fixed directories, never from PATH', async () => {
  const { toolPath } = await import('../lib/tools.mjs');
  assert.match(toolPath('git'), /^\/(usr\/(local\/)?bin|bin|opt\/homebrew\/bin)\/git$/);
  assert.throws(() => toolPath('definitely-not-a-tool-xyz'), /not found/);
});
