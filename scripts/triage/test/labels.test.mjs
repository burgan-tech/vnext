import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { taxonomyLabels } from '../sync-labels.mjs';

const taxonomy = JSON.parse(readFileSync(fileURLToPath(new URL('../../../.github/triage/labels.json', import.meta.url)), 'utf8'));
const labels = taxonomyLabels(taxonomy);
const names = new Set(labels.map((l) => l.name));

test('sync reads every label group, not only the original four', () => {
  for (const group of ['kinds', 'areas', 'priorities', 'status', 'changeLevel', 'impact', 'risk', 'council', 'deprecated']) {
    assert.ok(Array.isArray(taxonomy[group]) && taxonomy[group].length > 0, group);
    for (const l of taxonomy[group]) assert.ok(names.has(l.name), `${group}: ${l.name}`);
  }
  assert.equal(labels.length, Object.entries(taxonomy).filter(([k, v]) => k !== 'defaults' && Array.isArray(v)).reduce((n, [, v]) => n + v.length, 0));
});

test('every label has a unique name, a 6-digit hex colour and a description GitHub accepts', () => {
  assert.equal(names.size, labels.length, 'duplicate label name');
  for (const l of labels) {
    assert.match(l.color, /^[0-9a-f]{6}$/i, l.name);
    assert.ok(l.description && l.description.length <= 100, `${l.name}: description missing or over 100 chars`);
  }
});

test('the pipeline label groups match docs/governance/ai-engineering-pipeline.md §4', () => {
  const group = (g) => taxonomy[g].map((l) => l.name);
  assert.deepEqual(group('status').filter((n) => n.startsWith('ai:')), [
    'ai:triaged', 'ai:ready', 'ai:replan', 'ai:planned', 'ai:developing', 'ai:pr-ready', 'ai:reviewing',
    'ai:changes-required', 'ai:fixing', 'ai:human-approval', 'ai:blocked', 'ai:done',
  ]);
  assert.deepEqual(group('changeLevel'), ['ai:semver-patch', 'ai:semver-minor', 'ai:semver-major']);
  assert.deepEqual(group('impact'), ['ai:impact-low', 'ai:impact-high']);
  assert.deepEqual(group('risk'), ['ai:risk-low', 'ai:risk-medium', 'ai:risk-high', 'ai:risk-critical']);
  assert.deepEqual(group('council'), ['ai:council-required', 'ai:council-approved']);
});

test('a deprecated label names a live replacement and says so in its description', () => {
  const live = new Set(labels.filter((l) => !taxonomy.deprecated.includes(l)).map((l) => l.name));
  for (const d of taxonomy.deprecated) {
    assert.ok(live.has(d.replacedBy), `${d.name} -> ${d.replacedBy}`);
    assert.match(d.description, new RegExp(`^Deprecated: use ${d.replacedBy}$`), d.name);
  }
});
