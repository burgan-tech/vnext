import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { checkPr } from '../../governance/lib/pr-contract.mjs';
import { PRODUCTS as TEMPLATE_PRODUCTS } from '../../governance/lib/pr-body.mjs';
import { compileImpactRules } from '../../governance/lib/ai-record.mjs';
import {
  PRODUCTS, DEV_MARKER, branchFor, decideMode, validateResult, checkDiff, pickMilestone, prLabels, developerLabels,
  extractCouncilRow, insertLogRow, findSecrets, renderPrBody, updateRepairs, renderDeveloperComment, isDeveloperComment,
} from '../lib/developer.mjs';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const taxonomy = JSON.parse(readFileSync(join(root, '.github/triage/labels.json'), 'utf8'));
const allLabels = new Set(Object.entries(taxonomy).filter(([k, v]) => k !== 'defaults' && Array.isArray(v)).flatMap(([, v]) => v.map((l) => l.name)));
const rules = compileImpactRules(JSON.parse(readFileSync(join(root, '.github/ai/impact-paths.json'), 'utf8')));
const plan = (level = 'minor', council = false) => `<!-- ai-director-plan:v1 -->\n<!-- ai-director-data:{"level":"${level}","impact":"low","risk":"medium","council":${council},"decision":"plan"} -->`;
const base = { number: 7, labels: ['ai:planned', 'ai:semver-minor'], directorPlan: plan(), openPrs: [], closedPrs: [] };

test('mode: NEW, RECOVERY, FIX and every reason to stay out', () => {
  assert.equal(decideMode(base).mode, 'NEW');
  assert.equal(decideMode({ ...base, closedPrs: [{ number: 3 }] }).mode, 'RECOVERY');
  const ours = { number: 9, head: branchFor(7) };
  assert.deepEqual(decideMode({ ...base, labels: ['ai:changes-required'], openPrs: [ours] }), { mode: 'FIX', pr: ours });
  const skip = (over) => decideMode({ ...base, ...over }).reason;
  assert.match(skip({ labels: ['ai:planned', 'ai:blocked'] }), /ai:blocked/);
  assert.match(skip({ labels: ['ai:planned', 'ai:semver-major'] }), /ai:semver-major/);
  assert.match(skip({ labels: ['ai:pr-ready'] }), /neither/);
  assert.match(skip({ directorPlan: null }), /no Director plan/);
  assert.match(skip({ directorPlan: plan('major') }), /major/);
  assert.match(skip({ labels: ['ai:planned', 'ai:council-required'] }), /without ai:council-approved/);
  assert.equal(decideMode({ ...base, labels: ['ai:planned', 'ai:council-required', 'ai:council-approved'] }).mode, 'NEW');
  assert.match(skip({ openPrs: [{ number: 1209, head: 'claude/x' }] }), /open PR #1209 already works on it/);
  assert.match(skip({ openPrs: [ours] }), /PR #9 already implements it/);
  assert.match(skip({ labels: ['ai:changes-required'] }), /no open ai\/issue-7 PR/);
  assert.match(skip({ labels: ['ai:changes-required'], openPrs: [ours], repairs: 3 }), /repair limit/);
});

test('the agent answer is validated: bad title or decision falls back, text is sanitized', () => {
  const { result, dropped } = validateResult({
    decision: 'ship-it', title: 'Fixed stuff!!', summary: ['ping @octocat <!-- x -->'],
    productImpact: { 'vnext-docs': { needed: true, handoff: 'docs/integration/vnext-docs/x.md' }, 'vnext-forge': { needed: 'yes' } },
    meta: { status: 'whatever' }, conditions: [{ id: 'C-1', status: 'done' }, { status: 'done' }],
  });
  assert.equal(result.decision, 'needs-replan');
  assert.equal(result.title, '');
  assert.deepEqual(dropped, ['decision:ship-it', 'title:Fixed stuff!!']);
  assert.ok(!result.summary[0].includes('@octocat') && !result.summary[0].includes('<!--'));
  assert.equal(result.productImpact['vnext-docs'].needed, true);
  assert.equal(result.productImpact['vnext-forge'].needed, false);
  assert.equal(result.meta.status, 'not-needed');
  assert.equal(result.conditions.length, 1);
  assert.equal(validateResult({ decision: 'implemented', title: 'fix(cache): keep the newest build on a miss' }).result.title, 'fix(cache): keep the newest build on a miss');
});

test('the product list matches the PR template', () => {
  assert.deepEqual([...PRODUCTS].sort(), [...TEMPLATE_PRODUCTS].sort());
});

const patchOf = (path, lines) => [`diff --git a/${path} b/${path}`, `--- a/${path}`, `+++ b/${path}`, '@@ -1 +1 @@', ...lines].join('\n');

test('diff policy: forbidden paths, deleted or skipped tests, suppressions, packages, size', () => {
  const ok = [{ path: 'src/BBT.Workflow.Application/Caching/CacheSet.cs', status: 'M' }, { path: 'test/BBT.Workflow.Application.Tests/X.cs', status: 'A' }];
  assert.deepEqual(checkDiff(ok, patchOf('test/BBT.Workflow.Application.Tests/X.cs', ['+    [Fact]', '+    public void Y() {}'])), []);
  assert.deepEqual(checkDiff([], ''), ['the diff is empty']);
  for (const path of ['.github/workflows/x.yml', 'scripts/ai/lib/developer.mjs', '.claude/rules/x.md', 'AGENTS.md', 'Directory.Build.props', 'src/Directory.Packages.props', 'global.json', 'nuget.config', '.editorconfig', 'common.props', 'etc/docker/x.yml']) {
    assert.match(checkDiff([{ path, status: 'M' }], '').join(), /may not change this path/, path);
  }
  assert.match(checkDiff([{ path: 'test/A/B.cs', status: 'D' }], '').join(), /deletes a test file/);
  const bad = (line, file = 'src/A.cs') => checkDiff([{ path: file, status: 'M' }], patchOf(file, [line])).join();
  assert.match(bad('+#pragma warning disable CS1234'), /pragma/);
  assert.match(bad('+[SuppressMessage("x", "y")]'), /SuppressMessage/);
  assert.match(bad('+    <NoWarn>CS1591</NoWarn>', 'src/A.csproj'), /NoWarn/);
  assert.match(bad('+    <PackageReference Include="Evil" Version="1.0" />', 'src/A.csproj'), /package reference/);
  assert.match(bad('+    [Fact(Skip = "flaky")]', 'test/A.cs'), /skips a test/);
  assert.match(bad('-    [Fact]', 'test/A.cs'), /removes 1 more test/);
  assert.match(checkDiff([{ path: 'src/a.png', status: 'A' }], 'GIT binary patch').join(), /binary/);
  const many = Array.from({ length: 81 }, (_, i) => ({ path: `src/F${i}.cs`, status: 'M' }));
  assert.match(checkDiff(many, '').join(), /81 files/);
});

test('milestone, PR labels and issue transitions', () => {
  assert.equal(pickMilestone(['backlog', 'v0.1.0', 'v0.0.105', 'v0.0.99']), 'v0.0.99');
  assert.equal(pickMilestone(['backlog']), null);
  assert.deepEqual(prLabels(['bug', 'ai:planned', 'ai:semver-minor', 'ai:impact-high', 'ai:risk-high', 'ai:council-required', 'ai:council-approved', 'ai:triaged']),
    ['ai:semver-minor', 'ai:impact-high', 'ai:risk-high', 'ai:council-required', 'ai:council-approved']);
  assert.deepEqual(developerLabels('pushed', ['ai:planned', 'ai:council-approved']), { add: ['ai:pr-ready'], remove: ['ai:planned'] });
  assert.deepEqual(developerLabels('pushed', ['ai:changes-required', 'ai:fixing']), { add: ['ai:pr-ready'], remove: ['ai:changes-required', 'ai:fixing'] });
  assert.deepEqual(developerLabels('needs-replan', ['ai:planned']), { add: ['ai:replan'], remove: ['ai:planned'] });
  assert.deepEqual(developerLabels('rejected', ['ai:planned']), { add: ['ai:blocked'], remove: ['ai:planned'] });
  assert.deepEqual(developerLabels('failed', ['ai:planned']), { add: [], remove: [] });
  for (const o of ['pushed', 'needs-replan', 'rejected', 'no-change']) for (const l of developerLabels(o, []).add) assert.ok(allLabels.has(l), l);
});

test('the council row is carried into the sessions log once', () => {
  const row = '| 2026-11-10 | [issue-7-council](https://github.com/o/r/issues/7#issuecomment-1) | T | High | `APPROVED` (CI council) | A | — |';
  const body = `x\n<details><summary>Decision log row</summary>\n\n\`\`\`markdown\n${row}\n\`\`\`\n\n</details>`;
  assert.equal(extractCouncilRow(body), row);
  const readme = readFileSync(join(root, 'docs/agent-council/sessions/README.md'), 'utf8');
  const next = insertLogRow(readme, row);
  assert.ok(next.includes(`${row}\n<!-- newest at the bottom`));
  assert.equal(insertLogRow(next, row), null);
});

test('the PR body passes the PR contract, AI decision record included', () => {
  const { result } = validateResult({
    decision: 'implemented', title: 'fix(cache): keep the newest build on a miss',
    summary: ['Keeps the newest build.'], vnextScope: 'component cache; behaviour changes on a miss only',
    changes: ['src/BBT.Workflow.Application/Caching/CacheSet.cs — x'], testPlan: ['run CacheSet tests'],
    e2eSuites: ['ComponentVersionLab'], meta: { status: 'not-needed', reason: 'internal fix, no contract change' },
    conditions: [{ id: 'C-1', status: 'done', evidence: 'x' }], confidence: 'medium',
  }, { mode: 'NEW' });
  const labels = ['ai:semver-patch', 'ai:impact-high', 'ai:risk-high', 'ai:council-required', 'ai:council-approved'];
  const body = renderPrBody({
    issue: 1192, result, plan: { council: true }, labels, councilUrl: 'https://github.com/o/r/issues/1192#issuecomment-2',
    planUrl: 'https://github.com/o/r/issues/1192#issuecomment-1', branch: branchFor(1192),
    runner: { build: { status: 'run', detail: 'passed' }, tests: { status: 'run', detail: 'Domain 10/0' } }, repairs: 0, maxRepairs: 3,
    e2eReason: 'the e2e job (pr-e2e.yml) is not built yet; run the suites locally',
  });
  const findings = checkPr({
    title: result.title, body, labels, milestone: { title: 'v0.0.105', state: 'open' },
    changedFiles: ['src/BBT.Workflow.Application/Caching/CacheSet.cs'], impactRules: rules, readFile: () => null,
  });
  assert.deepEqual(findings.filter((f) => f.severity !== 'warning').map((f) => `${f.id}: ${f.message}`), []);
  assert.match(body, /Closes #1192/);
  assert.match(updateRepairs(body, 2, 3), /- Repair attempts: 2\/3/);
});

test('the status comment is sticky and lists violations', () => {
  const body = renderDeveloperComment({ issue: 7, mode: 'NEW', outcome: 'rejected', violations: ['.github/x: the pipeline may not change this path'], runMode: 'dry-run', labels: { add: ['ai:blocked'], remove: ['ai:planned'] } });
  assert.ok(body.startsWith(DEV_MARKER));
  assert.match(body, /nothing was pushed/);
  assert.match(body, /would add `ai:blocked`/);
  assert.ok(isDeveloperComment({ user: { login: 'github-actions[bot]' }, body }));
});

test('a patch carrying a secret is never pushed', () => {
  const p = (line) => patchOf('test/A.cs', [line]);
  assert.deepEqual(findSecrets(p('+var x = 1;'), ['cursor-key-123456']), []);
  assert.match(findSecrets(p('+// cursor-key-123456'), ['cursor-key-123456']).join(), /a secret this job holds/);
  assert.match(findSecrets(p('+var t = "ghs_abcdefghijklmnopqrstuvwxyz012345";')).join(), /token-shaped/);
  assert.deepEqual(findSecrets(p('-var t = "ghs_abcdefghijklmnopqrstuvwxyz012345";')), []);
});
