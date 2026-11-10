import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  COUNCIL_MARKER, VERDICTS, isCouncilComment, readCouncilData, councilFingerprint, preDecide, validateCouncil, decideCouncil,
  councilLabels, councilLogRow, renderCouncilComment,
} from '../lib/council.mjs';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const taxonomy = JSON.parse(readFileSync(join(root, '.github/triage/labels.json'), 'utf8'));
const allLabels = new Set(Object.entries(taxonomy).filter(([k, v]) => k !== 'defaults' && Array.isArray(v)).flatMap(([, v]) => v.map((l) => l.name)));

const plan = (level = 'minor') => `<!-- ai-director-plan:v1 -->\n<!-- ai-director-data:{"level":"${level}","impact":"high","risk":"high","council":true,"decision":"plan"} -->\nplan`;
const core = [
  { role: 'Solution Architect', why: 'design', position: 'p', vote: 'approve' },
  { role: 'Pragmatic Engineer', why: 'cost', position: 'p', vote: 'approve' },
  { role: 'Test & Evidence', why: 'proof', position: 'p', vote: 'approve' },
];
const raw = (over = {}) => ({
  verdict: 'APPROVED', summary: 's', problem: 'p', council: core, approaches: [{ name: 'A', summary: 'a' }, { name: 'B', summary: 'b' }],
  recommended: 'A', conditions: [{ id: 'C-1', text: 'add a deprecation entry', owner: 'developer' }], humanDecisions: [], confidence: 'high', ...over,
});
const judge = (r, labels = ['ai:council-required', 'ai:planned'], level = 'minor') =>
  decideCouncil(validateCouncil(r).result, { labels, directorPlan: plan(level) });

test('a sound approval with developer-owned conditions lets development start', () => {
  const d = judge(raw());
  assert.deepEqual([d.verdict, d.develop, d.reasons], ['APPROVED', true, []]);
});

test('the script only makes the verdict stricter', () => {
  assert.equal(judge(raw({ conditions: [{ text: 'owner signs off', owner: 'human' }] })).verdict, 'APPROVE_WITH_CONDITIONS');
  assert.equal(judge(raw({ humanDecisions: ['pick a TTL'] })).verdict, 'APPROVE_WITH_CONDITIONS');
  assert.equal(judge(raw({ conditions: [{ text: 'x', owner: 'whoever' }] })).verdict, 'APPROVE_WITH_CONDITIONS');
  assert.equal(judge(raw({ confidence: 'low' })).verdict, 'CLARIFICATION_REQUIRED');
  assert.equal(judge(raw({ confidence: 'sure' })).verdict, 'CLARIFICATION_REQUIRED');
  assert.match(judge(raw({ council: core.slice(0, 2) })).reasons[0], /core role\(s\) missing: Test & Evidence/);
  assert.equal(judge(raw({ verdict: 'LGTM' })).verdict, 'CLARIFICATION_REQUIRED');
  assert.equal(judge(raw({ verdict: 'REJECTED' })).verdict, 'REJECTED');
});

test('a major (breaking) change is never approved, from the label or the Director plan', () => {
  assert.equal(judge(raw(), ['ai:council-required', 'ai:semver-major']).verdict, 'BLOCKED');
  const d = judge(raw(), ['ai:council-required'], 'major');
  assert.equal(d.verdict, 'BLOCKED');
  assert.equal(d.develop, false);
});

test('no result is INCONCLUSIVE and changes no label', () => {
  const d = decideCouncil(null, {});
  assert.equal(d.verdict, 'INCONCLUSIVE');
  assert.deepEqual(councilLabels(d.verdict, ['ai:council-required']), { add: [], remove: [], next: null });
});

test('labels: APPROVED -> council-approved + planned; anything else -> blocked; council-required stays', () => {
  const ok = councilLabels('APPROVED', ['ai:council-required', 'ai:planned', 'ai:impact-high']);
  assert.deepEqual([ok.add, ok.remove, ok.next], [['ai:council-approved'], [], 'ai:planned']);
  const no = councilLabels('EXPERIMENT_REQUIRED', ['ai:council-required', 'ai:council-approved', 'ai:planned']);
  assert.deepEqual([no.add, no.remove.sort(), no.next], [['ai:blocked'], ['ai:council-approved', 'ai:planned'], 'ai:blocked']);
  for (const v of VERDICTS) for (const l of councilLabels(v, []).add) assert.ok(allLabels.has(l), l);
});

test('pre-decision: blocked and approved issues are left alone, no plan is reported, unchanged is skipped', () => {
  const input = { labels: ['ai:council-required'], directorPlan: plan(), fp: 'f1', previousCouncil: null };
  assert.equal(preDecide({ ...input, labels: ['ai:council-required', 'ai:blocked'] }), 'blocked');
  assert.equal(preDecide({ ...input, labels: ['ai:council-required', 'ai:council-approved'] }), 'approved');
  assert.equal(preDecide({ ...input, labels: ['ai:council-required', 'ai:council-approved'] }, { force: true }), 'council');
  assert.equal(preDecide({ ...input, directorPlan: null }), 'no-plan');
  const prev = `${COUNCIL_MARKER}\n<!-- ai-council-data:{"verdict":"BLOCKED","fp":"f1"} -->`;
  assert.equal(preDecide({ ...input, previousCouncil: prev }), 'unchanged');
  assert.equal(preDecide({ ...input, previousCouncil: prev }, { force: true }), 'council');
  assert.equal(preDecide({ ...input, previousCouncil: prev.replace('f1', 'f0') }), 'council');
  // A failed run stores no fingerprint, so it is retried.
  assert.equal(preDecide({ ...input, previousCouncil: `${COUNCIL_MARKER}\n<!-- ai-council-data:{"verdict":"INCONCLUSIVE","fp":null} -->` }), 'council');
});

test('the fingerprint ignores state labels and ordering, not the plan or human input', () => {
  const base = { title: 't', body: 'b', directorPlan: 'p', comments: [{ body: 'c' }], labels: ['ai:impact-high', 'ai:semver-minor', 'ai:planned'] };
  assert.equal(councilFingerprint(base), councilFingerprint({ ...base, labels: ['ai:semver-minor', 'ai:blocked', 'ai:impact-high'] }));
  assert.notEqual(councilFingerprint(base), councilFingerprint({ ...base, directorPlan: 'p2' }));
  assert.notEqual(councilFingerprint(base), councilFingerprint({ ...base, comments: [{ body: 'c' }, { body: 'new' }] }));
  assert.notEqual(councilFingerprint(base), councilFingerprint({ ...base, labels: ['ai:impact-high', 'ai:semver-major'] }));
});

test('agent text is sanitized and bounded', () => {
  const { result } = validateCouncil(raw({
    summary: 'ping @octocat <!-- ai-council:v1 --> ghp_abcdefghijklmnopqrstuvwxyz0123',
    council: [...core, { role: '', position: 'nameless' }, { role: 'Devil\'s Advocate', vote: 'maybe' }],
    objections: [{ by: 'x', claim: '' }, { by: 'x', against: 'A', claim: 'c', resolution: 'won' }],
  }));
  assert.ok(!result.summary.includes('@octocat') && !result.summary.includes('<!--') && result.summary.includes('[redacted]'));
  assert.equal(result.council.length, 4);
  assert.equal(result.council[3].vote, 'clarify');
  assert.deepEqual(result.objections.map((o) => o.resolution), ['open']);
});

test('the log row fits the sessions table and the comment round-trips its data', () => {
  const { result } = validateCouncil(raw({ recommended: 'A | with a pipe' }));
  const decision = decideCouncil(result, { labels: [], directorPlan: plan() });
  const row = councilLogRow(result, decision, { date: '2026-11-10', issue: 42, title: 'Add a cache', url: 'https://github.com/o/r/issues/42#issuecomment-1', risk: 'high' });
  assert.equal(row.split('|').length - 2, 7);
  assert.match(row, /^\| 2026-11-10 \| \[issue-42-council\]\(https:\/\/github.com\/o\/r\/issues\/42#issuecomment-1\) \| Add a cache \| High \| `APPROVED` \(CI council\) \| A \/ with a pipe \|/);
  const labels = councilLabels(decision.verdict, ['ai:council-required']);
  const body = renderCouncilComment({ issue: 42, result, decision, labels, mode: 'dry-run', fp: 'f9', logRow: row });
  assert.ok(body.startsWith(COUNCIL_MARKER));
  assert.match(body, /dry run — labels not applied/);
  assert.match(body, /would add `ai:council-approved` `ai:planned`/);
  assert.deepEqual(readCouncilData(body), { verdict: 'APPROVED', fp: 'f9' });
  assert.ok(isCouncilComment({ user: { login: 'github-actions[bot]' }, body }));
  assert.ok(!isCouncilComment({ user: { login: 'someone' }, body }));
});

test('the no-plan comment explains what to do and changes nothing', () => {
  const decision = decideCouncil(null, {});
  const body = renderCouncilComment({ issue: 7, result: null, decision, labels: councilLabels(decision.verdict, []), mode: 'apply', fp: 'f1', note: 'Add `ai:ready`.' });
  assert.match(body, /Add `ai:ready`/);
  assert.deepEqual(readCouncilData(body), { verdict: 'INCONCLUSIVE', fp: 'f1' });
});
