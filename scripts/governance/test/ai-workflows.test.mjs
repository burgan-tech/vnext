// Guard for the AI pipeline workflows (docs/governance/ai-engineering-pipeline.md §7.1, §13): every
// GitHub App token they mint is down-scoped to the permissions the trusted writer needs and never
// carries `workflows` (or admin rights), so even a leaked token cannot change CI.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const dir = join(dirname(fileURLToPath(import.meta.url)), '..', '..', '..', '.github', 'workflows');
const ALLOWED = new Set(['contents', 'issues', 'pull-requests', 'metadata', 'checks', 'statuses', 'actions']);
const FORBIDDEN = ['workflows', 'administration', 'secrets', 'environments', 'members', 'organization-administration'];

/** The `with:` block of each create-github-app-token step, as key -> value. */
export function appTokenSteps(yaml) {
  const lines = yaml.split('\n');
  const steps = [];
  lines.forEach((line, i) => {
    if (!/uses:\s*actions\/create-github-app-token@/.test(line)) return;
    const indent = line.search(/\S/);
    const withAt = lines.findIndex((l, j) => j > i && /^\s*with:\s*$/.test(l));
    const inputs = {};
    if (withAt > i) {
      const withIndent = lines[withAt].search(/\S/);
      for (let j = withAt + 1; j < lines.length; j++) {
        const l = lines[j];
        if (l.trim() === '') continue;
        const ind = l.search(/\S/);
        if (ind <= withIndent || ind < indent) break;
        const kv = /^\s*([\w-]+):\s*(.*)$/.exec(l);
        if (kv && ind === withIndent + 2) inputs[kv[1]] = kv[2].trim();
      }
    }
    steps.push({ line: i + 1, inputs });
  });
  return steps;
}

const aiWorkflows = readdirSync(dir).filter((f) => /^ai-.*\.ya?ml$/.test(f));

test('the parser finds a token step and its permission inputs', () => {
  const steps = appTokenSteps(`steps:
  - id: app
    uses: actions/create-github-app-token@v1
    with:
      app-id: x
      permission-issues: write
      permission-workflows: write
  - run: echo`);
  assert.equal(steps.length, 1);
  assert.deepEqual(Object.keys(steps[0].inputs), ['app-id', 'permission-issues', 'permission-workflows']);
});

for (const file of aiWorkflows) {
  test(`${file}: every App token is down-scoped and never gets workflows`, () => {
    for (const step of appTokenSteps(readFileSync(join(dir, file), 'utf8'))) {
      const perms = Object.keys(step.inputs).filter((k) => k.startsWith('permission-')).map((k) => k.slice('permission-'.length));
      assert.ok(perms.length > 0, `${file}:${step.line} mints an App token without permission-* inputs (it would get every App permission)`);
      for (const p of perms) {
        assert.ok(!FORBIDDEN.includes(p), `${file}:${step.line} requests permission-${p}`);
        assert.ok(ALLOWED.has(p), `${file}:${step.line} requests permission-${p}, outside the allowed set`);
      }
    }
  });
}
