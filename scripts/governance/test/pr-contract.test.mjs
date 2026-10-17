import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { checkPr, formatAnnotation } from '../lib/pr-contract.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const VALID = readFileSync(join(here, 'fixtures', 'pr', 'valid-feat.md'), 'utf8');
const SHA = '3f2a9c1e5d0000000000000000000000000000aa';

const HANDOFF_DOC = (over = {}) => {
  const fm = { product: 'vnext-schema', milestone: 'v0.0.101', status: 'pending', ...over };
  return `---\n${Object.entries(fm).map(([k, v]) => `${k}: ${v}`).join('\n')}\n---\n# Handoff\n`;
};

function pr(over = {}) {
  return {
    title: 'feat(governance): add PR contract checker',
    body: VALID,
    milestone: { title: 'v0.0.101', state: 'open' },
    changedFiles: ['scripts/governance/pr-contract.mjs'],
    commitShas: [SHA],
    commitsCapped: false,
    readFile: () => null,
    ...over,
  };
}
const ids = (findings) => findings.map((f) => f.id);
const swap = (from, to) => VALID.replace(from, to);

test('valid full-contract PR has no findings', () => {
  assert.deepEqual(checkPr(pr()), []);
});

test('checks a local draft body', () => {
  // a draft carries milestone-as-title (open) and git-derived files/commits
  const draft = pr({ milestone: { title: 'v0.0.101', state: 'open' }, changedFiles: ['a.md'], commitShas: [SHA] });
  assert.deepEqual(checkPr(draft), []);
  // without git info the sha cannot be proven; only a capped list downgrades it
  assert.deepEqual(ids(checkPr(pr({ commitShas: [] }))), ['PR-E2E-RUN']);
});

const CASES = [
  ['PR-TITLE', { title: 'Add the thing' }],
  ['PR-TITLE', { title: `feat: ${'x'.repeat(80)}` }],
  ['PR-MILESTONE', { milestone: null }],
  ['PR-MILESTONE', { milestone: { title: 'v0.0.101', state: 'closed' } }],
  ['PR-MILESTONE', { milestone: { title: 'next', state: 'open' } }],
  ['PR-SECTION', { body: swap(/## vNext scope\n[^\n]*\n/.exec(VALID)[0], '## vNext scope\n<!-- fill -->\n') }],
  ['PR-SECTION', { body: swap('## vnext-meta impact', '## Something else') }],
  ['PR-E2E-STATUS', { body: swap('- Status: run', '- Status: maybe') }],
  ['PR-E2E-STATUS', { body: swap(/- Status: run[\s\S]*?\n\n## Product/.exec(VALID)[0], '- Status: not-run\n- Reason: too short\n\n## Product') }],
  ['PR-E2E-RUN', { body: swap('`3f2a9c1e5d`', '`deadbeef12`') }],
  ['PR-E2E-RUN', { body: swap('- Result: 10/10 passed, 0 failed, 0 skipped\n', '') }],
  ['PR-E2E-RUN', { body: swap('| Core.IntegrationTests | 10 | 0 | 0 |\n', '') }],
  ['PR-E2E-RUN', { body: swap('10/10 passed, 0 failed', '9/10 passed, 1 failed').replace('| 10 | 0 | 0 |', '| 9 | 1 | 0 |') }],
  ['PR-PRODUCT-ROWS', { body: swap('| vnext-forge | no | |\n', '') }],
  ['PR-PRODUCT-ROWS', { body: swap('| vnext-forge | no | |', '| vnext-forge | maybe | |') }],
  ['PR-HANDOFF', { body: swap('| vnext-forge | no | |', '| vnext-forge | yes | |') }],
  ['PR-HANDOFF', { body: swap('| vnext-forge | no | |', '| vnext-forge | yes | docs/integration/vnext-forge/x.md |') }],
  ['PR-META', { changedFiles: ['vnext-meta/features.json'] }],
  ['PR-META', { body: swap('- Status: not-needed\n- Reason: No runtime contract changed in this PR.', '- Status: updated') }],
  ['PR-META', { body: swap('- Reason: No runtime contract changed in this PR.', '') }],
];
for (const [id, over] of CASES) {
  test(`${id} fires: ${JSON.stringify(over).slice(0, 60)}`, () => {
    const f = checkPr(pr(over));
    assert.ok(ids(f).includes(id), `expected ${id}, got ${JSON.stringify(f)}`);
    assert.ok(f.every((x) => x.id.startsWith('PR-') && x.message));
  });
}

test('release title is exempt', () => {
  assert.deepEqual(checkPr(pr({ title: 'v0.0.100', milestone: null, body: '', changedFiles: ['vnext-meta/a.json'] })), []);
});

test('chore needs only summary', () => {
  const body = '## Summary\n- bump\n';
  assert.deepEqual(checkPr(pr({ title: 'chore: bump deps', body, milestone: null, changedFiles: ['vnext-meta/a.json'] })), []);
  assert.deepEqual(ids(checkPr(pr({ title: 'docs: fix', body: '', milestone: null }))), ['PR-SECTION']);
});

test('accepts short uppercase sha prefix', () => {
  assert.deepEqual(checkPr(pr({ body: swap('`3f2a9c1e5d`', '`3F2A9C1`') })), []);
});

test('rejects 6-char sha', () => {
  assert.ok(ids(checkPr(pr({ body: swap('`3f2a9c1e5d`', '`3f2a9c`') }))).includes('PR-E2E-RUN'));
});

test('downgrades missing sha when commit list is capped', () => {
  const f = checkPr(pr({ body: swap('`3f2a9c1e5d`', '`deadbeef12`'), commitsCapped: true }));
  assert.deepEqual(f.map((x) => [x.id, x.severity]), [['PR-E2E-RUN', 'warning']]);
});

test('meta files changed but not-needed fails', () => {
  const f = checkPr(pr({ changedFiles: ['vnext-meta/known-issues.json'] }));
  assert.deepEqual(ids(f), ['PR-META']);
  assert.equal(f[0].severity, 'error');
});

test('meta updated passes when a meta file changed', () => {
  const body = swap('- Status: not-needed\n- Reason: No runtime contract changed in this PR.', '- Status: updated');
  assert.deepEqual(checkPr(pr({ body, changedFiles: ['vnext-meta/features.json'] })), []);
});

test('not-run with a real reason passes', () => {
  const body = swap(/- Status: run[\s\S]*?\n\n## Product/.exec(VALID)[0], '- Status: not-run\n- Reason: Governance scripts only, no runtime.\n\n## Product');
  assert.deepEqual(checkPr(pr({ body })), []);
});

const handoffBody = (cell) => swap('| vnext-schema | no | |', `| vnext-schema | yes | ${cell} |`);
const DOC = 'docs/integration/vnext-schema/new-field.md';

test('handoff doc path accepted plain, in backticks, or as a markdown link', () => {
  for (const cell of [DOC, `\`${DOC}\``, `[doc](${DOC})`]) {
    const f = checkPr(pr({ body: handoffBody(cell), changedFiles: [DOC], readFile: (p) => (p === DOC ? HANDOFF_DOC() : null) }));
    assert.deepEqual(f, [], cell);
  }
});

test('handoff doc not changed in PR fails PR-HANDOFF', () => {
  const f = checkPr(pr({ body: handoffBody(DOC), readFile: () => HANDOFF_DOC() }));
  assert.deepEqual(ids(f), ['PR-HANDOFF']);
});

test('handoff PR/issue URL accepted', () => {
  assert.deepEqual(checkPr(pr({ body: handoffBody('https://github.com/burgan-tech/vnext-schema/issues/12') })), []);
  assert.deepEqual(ids(checkPr(pr({ body: handoffBody('https://github.com/burgan-tech/vnext-forge/issues/12') }))), ['PR-HANDOFF']);
});

test('missing handoff doc file fails PR-HANDOFF-DOC', () => {
  const f = checkPr(pr({ body: handoffBody(DOC), changedFiles: [DOC] }));
  assert.deepEqual(ids(f), ['PR-HANDOFF-DOC']);
});

test('handoff doc frontmatter is checked', () => {
  for (const over of [{ product: 'vnext-forge' }, { milestone: 'v9.9.9' }, { status: 'wip' }]) {
    const f = checkPr(pr({ body: handoffBody(DOC), changedFiles: [DOC], readFile: () => HANDOFF_DOC(over) }));
    assert.deepEqual(ids(f), ['PR-HANDOFF-DOC'], JSON.stringify(over));
  }
  const f = checkPr(pr({ body: handoffBody(DOC), changedFiles: [DOC], readFile: () => '# no frontmatter' }));
  assert.deepEqual(ids(f), ['PR-HANDOFF-DOC']);
});

test('CRLF body is accepted', () => {
  assert.deepEqual(checkPr(pr({ body: VALID.replace(/\n/g, '\r\n') })), []);
});

test('CLI smoke: --body-file draft in enforce mode', () => {
  const dir = mkdtempSync(join(tmpdir(), 'prc-'));
  const file = join(dir, 'body.md');
  const cli = join(here, '..', 'pr-contract.mjs');
  const hermetic = ['--meta-dir', join(here, 'fixtures', 'meta-base', 'meta'), '--repo-root', join(here, 'fixtures', 'meta-base', 'repo')];
  writeFileSync(file, VALID.replace('3f2a9c1e5d', 'abcdef1234'));
  const bad = spawnSync('node', [cli, '--body-file', file, '--title', 'feat(x): y', '--milestone', 'v0.0.101', '--mode', 'enforce', ...hermetic], { encoding: 'utf8' });
  assert.equal(bad.status, 1, bad.stdout + bad.stderr);
  assert.match(bad.stdout, /PR-E2E-RUN/);
  const warn = spawnSync('node', [cli, '--body-file', file, '--title', 'feat(x): y', '--milestone', 'v0.0.101', '--mode', 'warn', ...hermetic], { encoding: 'utf8' });
  assert.equal(warn.status, 0, warn.stdout + warn.stderr);
  assert.match(warn.stdout, /WARNING PR-E2E-RUN/);
  const rel = spawnSync('node', [cli, '--body-file', file, '--title', 'v0.0.100', '--mode', 'enforce', ...hermetic], { encoding: 'utf8' });
  assert.equal(rel.status, 0, rel.stdout + rel.stderr);
  assert.doesNotMatch(rel.stdout, /PR-/);
});

test('annotation encoding follows workflow-command rules', () => {
  const line = formatAnnotation({ id: 'PR-X', severity: 'error', file: 'a:b,c.md', message: 'has %0A and 100% and\rlone\nnewline' });
  assert.equal(line, '::error title=PR-X,file=a%3Ab%2Cc.md::has %250A and 100%25 and%0Dlone%0Anewline');
  assert.equal(line.split('\n').length, 1);
});

test('CLI rejects option-looking values for path/ref arguments', () => {
  const cli = join(here, '..', 'pr-contract.mjs');
  for (const flag of ['--pr', '--files-from-git', '--body-file', '--event']) {
    const r = spawnSync('node', [cli, flag, '-oops'], { encoding: 'utf8' });
    assert.equal(r.status, 2, flag + r.stderr);
    assert.match(r.stderr, /must not start with "-"/);
  }
});
