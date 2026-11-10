import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import {
  MARKER,
  extractResult,
  sanitizeText,
  validateResult,
  renderComment,
  planActions,
  planDefaults,
  needsTriage,
  isTriageComment,
} from '../lib/triage.mjs';

const taxonomy = JSON.parse(readFileSync(fileURLToPath(new URL('../../../.github/triage/labels.json', import.meta.url)), 'utf8'));

const raw = {
  kind: 'bug',
  areas: ['area:subflow', 'area:nope', 'area:subflow', 'area:state-function'],
  priority: 'priority:P2',
  summary: 'Data function returns 304 forever.',
  reproduction: ['start', 'poll'],
  affectedFiles: [
    { path: 'src/A.cs', line: 12, why: 'builds the ETag' },
    { path: 'src/missing.cs', line: 1 },
    { path: '../etc/passwd' },
  ],
  confidence: 'medium',
};

test('extracts the result from the agent envelope, fenced text or bare JSON', () => {
  const answer = 'Here it is:\n```json\n{"kind":"bug","areas":["area:subflow"]}\n```';
  assert.equal(extractResult(JSON.stringify({ type: 'result', subtype: 'success', is_error: false, result: answer })).kind, 'bug');
  assert.equal(extractResult(answer).kind, 'bug');
  assert.equal(extractResult('{"kind":"question"}').kind, 'question');
  assert.throws(() => extractResult(JSON.stringify({ type: 'result', is_error: true, result: '' })), /error/);
  assert.throws(() => extractResult('I could not triage this.'), /no JSON/);
});

test('drops labels and files outside the taxonomy and the tree', () => {
  const { result, dropped } = validateResult(raw, taxonomy, { fileExists: (p) => p === 'src/A.cs' });
  assert.equal(result.kind, 'bug');
  assert.equal(result.type, 'Bug');
  assert.deepEqual(result.areas, ['area:subflow', 'area:state-function']);
  assert.deepEqual(result.affectedFiles.map((f) => f.path), ['src/A.cs']);
  assert.ok(dropped.includes('area:area:nope'));
  assert.ok(dropped.includes('file:src/missing.cs'));
  assert.ok(dropped.includes('file:../etc/passwd'));
});

test('unknown kind and confidence fall back safely', () => {
  const { result, dropped } = validateResult({ kind: 'wontfix', confidence: 'sure' }, taxonomy);
  assert.equal(result.kind, null);
  assert.equal(result.type, null);
  assert.equal(result.confidence, 'low');
  assert.ok(dropped.includes('kind:wontfix'));
});

test('sanitizes secrets, mentions and the marker', () => {
  const text = sanitizeText('ping @alice and @org/team, key sk-ant-api03-abcdefghijk ghs_abcdefghijklmnopqrstuvwxyz <!-- vnext-issue-triage --> mysecretvalue', 2000, ['mysecretvalue']);
  assert.ok(!text.includes('@alice'));
  assert.ok(!text.includes('@org'));
  assert.ok(!text.includes('sk-ant-'));
  assert.ok(!text.includes('ghs_'));
  assert.ok(!text.includes('<!--'));
  assert.ok(!text.includes('mysecretvalue'));
  assert.equal(sanitizeText('mail a@b.com and `@x`'), 'mail a@b.com and `@x`');
});

test('truncates long text', () => {
  assert.equal(sanitizeText('x'.repeat(50), 10).length, 10);
});

test('comment carries the marker, dry-run banner and file links', () => {
  const { result } = validateResult(raw, taxonomy, { fileExists: () => true });
  const body = renderComment(result, { mode: 'dry-run', repo: 'o/r', sha: 'abc' });
  assert.ok(body.startsWith(MARKER));
  assert.match(body, /dry run/);
  assert.match(body, /\(https:\/\/github\.com\/o\/r\/blob\/abc\/src\/A\.cs#L12\)/);
  assert.doesNotMatch(renderComment(result, { mode: 'apply' }), /dry run/);
});

test('dry-run writes no labels or type', () => {
  const { result } = validateResult(raw, taxonomy);
  assert.deepEqual(planActions(result, { labels: [] }, 'dry-run', taxonomy), { addLabels: [], setType: null });
});

test('apply never overrides a human kind, priority or type', () => {
  const { result } = validateResult(raw, taxonomy);
  const plan = planActions(result, { labels: ['enhancement', 'priority:P1', 'area:subflow'], type: 'Feature' }, 'apply', taxonomy);
  assert.deepEqual(plan.addLabels, ['area:state-function', 'ai:triaged']);
  assert.equal(plan.setType, null);
});

test('apply on a blank issue adds kind, areas, priority, triaged and type', () => {
  const { result } = validateResult({ ...raw, needsInfo: true, questions: ['version?'] }, taxonomy);
  const plan = planActions(result, { labels: [] }, 'apply', taxonomy);
  assert.deepEqual(plan.addLabels, ['bug', 'area:subflow', 'area:state-function', 'priority:P2', 'needs-info', 'ai:triaged']);
  assert.equal(plan.setType, 'Bug');
});

test('P0 is shown but never applied', () => {
  const { result } = validateResult({ ...raw, priority: 'priority:P0' }, taxonomy);
  const plan = planActions(result, { labels: [] }, 'apply', taxonomy);
  assert.ok(!plan.addLabels.includes('priority:P0'));
  assert.match(renderComment(result, { mode: 'apply' }), /maintainer must confirm/);
});

test('sweep skips PRs, triaged issues and bot-commented issues', () => {
  assert.equal(needsTriage({ labels: [] }, false), true);
  assert.equal(needsTriage({ labels: ['ai:triaged'] }, false), false);
  assert.equal(needsTriage({ labels: [], pull_request: {} }, false), false);
  assert.equal(needsTriage({ labels: [] }, true), false);
});

test('only the bot comment with the marker counts as the triage comment', () => {
  assert.equal(isTriageComment({ user: { login: 'github-actions[bot]' }, body: `${MARKER}\nx` }), true);
  assert.equal(isTriageComment({ user: { login: 'mallory' }, body: MARKER }), false);
  assert.equal(isTriageComment({ user: { login: 'github-actions[bot]' }, body: 'other' }), false);
});

test('every taxonomy kind maps to an org issue type', () => {
  for (const k of taxonomy.kinds) assert.ok(['Bug', 'Feature', 'Task'].includes(k.type), k.name);
});

test('defaults fill only what is empty: milestone, Priority from the label, Effort from the agent', () => {
  const { result } = validateResult({ ...raw, effort: 'high' }, taxonomy);
  const plan = planDefaults(result, { labels: ['priority:P1'], milestone: null, fields: {} }, 'apply', taxonomy.defaults);
  assert.equal(plan.milestone, 'backlog');
  assert.deepEqual(plan.fields, [{ name: 'Priority', value: 'High' }, { name: 'Effort', value: 'High' }]);

  const set = planDefaults(result, { labels: [], milestone: 'v0.0.103', fields: { Priority: 'Urgent', Effort: 'Low' } }, 'apply', taxonomy.defaults);
  assert.deepEqual(set, { milestone: null, fields: [] });
});

test('defaults never write Urgent, unknown effort, or anything in dry-run', () => {
  const { result } = validateResult({ ...raw, priority: 'priority:P0', effort: 'huge' }, taxonomy);
  assert.equal(result.effort, null);
  assert.deepEqual(planDefaults(result, { labels: [] }, 'apply', taxonomy.defaults).fields, []);
  assert.deepEqual(planDefaults(result, { labels: [] }, 'dry-run', taxonomy.defaults), { milestone: null, fields: [] });
});
