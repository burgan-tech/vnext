import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { checkPr } from '../lib/pr-contract.mjs';
import { compileImpactRules, matchImpactPaths, isAiPr } from '../lib/ai-record.mjs';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const rules = compileImpactRules(JSON.parse(readFileSync(join(root, '.github/ai/impact-paths.json'), 'utf8')));

const RECORD = `## Summary
- Fix the thing.

## AI decision record
- Issue: #1234 · Director plan: https://github.com/burgan-tech/vnext/issues/1234#issuecomment-1
- Classification: semver-patch · impact-low · risk-low
  - Why: no high-impact criterion matched
- Council: not required — patch with low impact
- Repair attempts: 0/3

### Done / skipped
| Check | Status | Note |
|---|---|---|
| Build (runner) | run | |
| Unit tests (runner) | run | Domain.Tests |
| Unit tests (CI) | see checks | |
| E2E (vnext-example) | not-run | no core process touched |

### Test it locally
    git fetch origin fix/x && git switch fix/x
    dotnet test test/BBT.Workflow.Domain.Tests
`;

// A light-tier title keeps the base contract out of the way; the AI rules apply on every tier.
function pr(over = {}) {
  return {
    title: 'test(domain): cover the thing',
    body: RECORD,
    labels: ['ai:pr-ready', 'ai:semver-patch', 'ai:impact-low', 'ai:risk-low'],
    changedFiles: ['test/BBT.Workflow.Domain.Tests/ThingTests.cs'],
    commitShas: [],
    impactRules: rules,
    readFile: () => null,
    ...over,
  };
}
const ids = (findings) => findings.map((f) => f.id);
const swap = (from, to) => RECORD.replace(from, to);

test('a complete AI decision record on a low-impact PR has no findings', () => {
  assert.deepEqual(checkPr(pr()), []);
});

test('a PR without ai: labels is not checked by the AI rules', () => {
  assert.equal(isAiPr(['bug', 'area:pipeline']), false);
  assert.deepEqual(checkPr(pr({ labels: [], body: '## Summary\n- x\n' })), []);
});

test('an AI PR without the record section fails', () => {
  assert.deepEqual(ids(checkPr(pr({ body: '## Summary\n- x\n' }))), ['PR-AI-RECORD']);
});

test('exactly one change-level and one impact label are required', () => {
  const f = checkPr(pr({ labels: ['ai:pr-ready', 'ai:semver-patch', 'ai:semver-minor'] }));
  assert.deepEqual(ids(f).filter((i) => i === 'PR-AI-LABELS').length, 2);
});

test('a major change is refused', () => {
  const f = checkPr(pr({ labels: ['ai:pr-ready', 'ai:semver-major', 'ai:impact-high'], body: swap('semver-patch · impact-low', 'semver-major · impact-high') }));
  assert.ok(ids(f).includes('PR-AI-GUARD'));
});

test('a low-impact claim that touches a high-impact path fails and names the rule', () => {
  const f = checkPr(pr({ changedFiles: ['src/BBT.Workflow.Infrastructure/Migrations/20261110_AddX.cs', 'README.md'] }));
  assert.deepEqual(ids(f), ['PR-AI-IMPACT']);
  assert.match(f[0].message, /migration/);
});

test('a high-impact PR may touch high-impact paths', () => {
  const body = swap('impact-low · risk-low', 'impact-high · risk-high');
  const f = checkPr(pr({ body, labels: ['ai:pr-ready', 'ai:semver-patch', 'ai:impact-high'], changedFiles: ['src/BBT.Workflow.Infrastructure/Migrations/x.cs'] }));
  assert.deepEqual(f, []);
});

test('the Classification line must agree with the labels', () => {
  const f = checkPr(pr({ body: swap('semver-patch', 'semver-minor') }));
  assert.deepEqual(ids(f), ['PR-AI-RECORD']);
  assert.match(f[0].message, /disagrees/);
});

test('council-required needs the approved label and an APPROVED verdict with a session link', () => {
  const labels = ['ai:pr-ready', 'ai:semver-minor', 'ai:impact-high', 'ai:council-required'];
  const body = swap('semver-patch · impact-low', 'semver-minor · impact-high');
  const missing = checkPr(pr({ labels, body }));
  assert.ok(ids(missing).includes('PR-AI-COUNCIL'));
  assert.ok(ids(missing).includes('PR-AI-RECORD'));

  const approved = body.replace('Council: not required — patch with low impact', 'Council: APPROVED — https://github.com/burgan-tech/vnext/blob/master/docs/agent-council/sessions/README.md#row-42; dissent recorded');
  assert.deepEqual(checkPr(pr({ labels: [...labels, 'ai:council-approved'], body: approved })), []);
});

test('the Done / skipped table needs build, unit-test and e2e rows', () => {
  const f = checkPr(pr({ body: swap('| E2E (vnext-example) | not-run | no core process touched |\n', '') }));
  assert.deepEqual(ids(f), ['PR-AI-RECORD']);
  assert.match(f[0].message, /e2e/);
});

test('the local test steps are required', () => {
  const body = RECORD.slice(0, RECORD.indexOf('### Test it locally'));
  assert.deepEqual(ids(checkPr(pr({ body }))), ['PR-AI-RECORD']);
});

test('every impact rule matches representative paths', () => {
  const samples = {
    migration: 'src/BBT.Workflow.Infrastructure/Migrations/20260101_X.cs',
    authorization: 'src/BBT.Workflow.Domain/Definitions/Authorization/RoleGrant.cs',
    security: 'src/BBT.Workflow.Infrastructure/Encryption/Protector.cs',
    'script-sandbox': 'modules/BBT.Workflow.Modules.Scripting/BBT/Workflow/Scripting/Host.cs',
    'public-api': 'orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Controllers/InstancesController.cs',
    'api-contract': 'src/BBT.Workflow.Application/Functions/Contracts/StateDto.cs',
    'event-contract': 'src/BBT.Workflow.Events.Contracts/Instances/Events/X.cs',
    'task-contract': 'src/BBT.Workflow.Tasks.Abstractions/ITask.cs',
    pipeline: 'src/BBT.Workflow.Domain/Execution/Transitions/Pipeline/LifecycleOrder.cs',
    'state-function-shape': 'src/BBT.Workflow.Application/Instances/Caching/StateFunctionCache.cs',
    'dapr-components': 'etc/orchestration/dapr/components/statestore.yaml',
    'ci-governance': '.github/workflows/pr-tests.yml',
  };
  assert.deepEqual(rules.map((r) => r.id).sort(), Object.keys(samples).sort());
  for (const [id, path] of Object.entries(samples)) {
    assert.deepEqual(matchImpactPaths([path], rules).map((h) => h.id), [id], path);
  }
  assert.deepEqual(matchImpactPaths(['src/BBT.Workflow.Domain/Instances/Instance.cs', 'test/BBT.Workflow.Infrastructure.Tests/Authorization/X.cs', 'docs/README.md'], rules), []);
});

test('every impact rule still matches a file in the repository', () => {
  const files = execFileSync('git', ['ls-files'], { cwd: root, encoding: 'utf8' }).split('\n').filter(Boolean);
  for (const rule of rules) assert.ok(files.some((f) => rule.re.test(f)), `${rule.id} matches no tracked file; the path moved?`);
});
