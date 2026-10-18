import { test } from 'node:test';
import assert from 'node:assert/strict';
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { lintMeta } from '../lib/meta-lint.mjs';
import { stampMeta } from '../lib/meta-stamp.mjs';
import { syncMeta } from '../lib/meta-sync.mjs';

// The CLI must behave the same locally and under GitHub Actions.
const { GITHUB_ACTIONS: _a, GITHUB_STEP_SUMMARY: _s, ...localEnv } = process.env;
const here = dirname(fileURLToPath(import.meta.url));
const BASE = join(here, 'fixtures', 'meta-base');
const CLI = join(here, '..', 'meta-sync.mjs');

/** A temp copy of the base fixture holding the given fragments (raw text, so both sides share the bytes). */
function setup(fragments = {}) {
  const dir = mkdtempSync(join(tmpdir(), 'meta-sync-'));
  cpSync(BASE, dir, { recursive: true });
  const metaDir = join(dir, 'meta');
  mkdirSync(join(metaDir, 'unreleased'), { recursive: true });
  for (const [name, doc] of Object.entries(fragments)) {
    writeFileSync(join(metaDir, 'unreleased', name), typeof doc === 'string' ? doc : JSON.stringify(doc, null, 2));
  }
  return { metaDir, repoRoot: join(dir, 'repo') };
}

const snapshot = (metaDir) => {
  const out = {};
  for (const f of readdirSync(metaDir)) if (f.endsWith('.json')) out[f] = readFileSync(join(metaDir, f), 'utf8');
  out.fragments = existsSync(join(metaDir, 'unreleased')) ? readdirSync(join(metaDir, 'unreleased')).sort() : [];
  return out;
};
const readJson = (metaDir, f) => JSON.parse(readFileSync(join(metaDir, f), 'utf8'));
const note = (summary) => ({ summary, breaking: false, docs: [] });

const featureFragment = { releaseNote: note('New engine thing.'), features: { engine: { newThing: { since: 'next' } } } };
const patchFragment = {
  releaseNote: note('Fix k1 and deprecate d2.'),
  deprecations: [{ id: 'd2', deprecatedSince: 'next', removedAt: null }],
  fixes: { knownIssues: ['k1'] },
};
const sharedFragment = {
  releaseNote: note('Fix shipped on both lines.'),
  features: { engine: { shared: { since: 'next', details: { enforcedSince: 'next' } } } },
  deprecations: [{ id: 'd2', deprecatedSince: 'next', removedAt: null }],
  fixes: { knownIssues: ['k1'] },
};
const text = (doc) => JSON.stringify(doc, null, 2);

test('sync brings a patch release to a master that moved on', () => {
  const target = setup({ 'engine-thing.json': featureFragment });
  stampMeta({ metaDir: target.metaDir, version: '0.1.0', date: '2026-10-10' });
  const source = setup({ 'fix-k1.json': patchFragment });
  stampMeta({ metaDir: source.metaDir, version: '0.0.101', date: '2026-10-20' });

  const r = syncMeta({ sourceDir: source.metaDir, targetDir: target.metaDir });
  assert.equal(r.status, 'synced');
  assert.deepEqual(r.addedVersions, ['0.0.101']);
  assert.deepEqual(r.changed.sort(), ['deprecations.json', 'known-issues.json', 'released-fragments.json', 'version-manifest.json']);

  const t = target.metaDir;
  assert.deepEqual(Object.keys(readJson(t, 'version-manifest.json').versions), ['0.1.0', '0.0.101', '0.0.100', '0.0.99']);
  assert.equal(readJson(t, 'version-manifest.json').versions['0.0.101'].releasedAt, '2026-10-20');
  const features = readJson(t, 'features.json');
  assert.equal(features.runtimeVersion, '0.1.0');
  assert.equal(features.engine.newThing.since, '0.1.0');
  assert.deepEqual(readJson(t, 'deprecations.json').items.map((d) => [d.id, d.deprecatedSince]), [['d1', '0.0.99'], ['d2', '0.0.101']]);
  assert.equal(readJson(t, 'known-issues.json').items.find((k) => k.id === 'k1').fixedIn, '0.0.101');
  assert.deepEqual(Object.keys(readJson(t, 'released-fragments.json')), ['0.1.0', '0.0.101']);
  assert.deepEqual(lintMeta({ metaDir: t, repoRoot: target.repoRoot }), []);
});

test('sync after the next minor was stamped', () => {
  // The same fragment reached master (stamped 0.1.0) before the 0.0.101 sync landed: lower versions win.
  const target = setup({ 'shared.json': text(sharedFragment) });
  stampMeta({ metaDir: target.metaDir, version: '0.1.0', date: '2026-10-10' });
  const source = setup({ 'shared.json': text(sharedFragment) });
  stampMeta({ metaDir: source.metaDir, version: '0.0.101', date: '2026-10-20' });

  const r = syncMeta({ sourceDir: source.metaDir, targetDir: target.metaDir });
  assert.equal(r.status, 'synced');
  const t = target.metaDir;
  const shared = readJson(t, 'features.json').engine.shared;
  assert.equal(shared.since, '0.0.101');
  assert.equal(shared.details.enforcedSince, '0.0.101');
  assert.equal(readJson(t, 'deprecations.json').items.find((d) => d.id === 'd2').deprecatedSince, '0.0.101');
  assert.equal(readJson(t, 'deprecations.json').items.filter((d) => d.id === 'd2').length, 1);
  assert.equal(readJson(t, 'known-issues.json').items.find((k) => k.id === 'k1').fixedIn, '0.0.101');
  const record = readJson(t, 'released-fragments.json');
  assert.deepEqual(Object.keys(record), ['0.1.0', '0.0.101']);
  assert.equal(record['0.1.0'].shared, record['0.0.101'].shared);
  assert.equal(readJson(t, 'features.json').runtimeVersion, '0.1.0');
  assert.deepEqual(lintMeta({ metaDir: t, repoRoot: target.repoRoot }), []);
});

test('forward-ported fragment becomes a leftover', () => {
  // Emergency fix made on the release line; master already holds the cherry-picked, unstamped fragment.
  const target = setup({ 'fix-k1.json': text(patchFragment) });
  const source = setup({ 'fix-k1.json': text(patchFragment) });
  stampMeta({ metaDir: source.metaDir, version: '0.0.101', date: '2026-10-20' });

  syncMeta({ sourceDir: source.metaDir, targetDir: target.metaDir });
  const t = target.metaDir;
  const findings = lintMeta({ metaDir: t, repoRoot: target.repoRoot });
  assert.deepEqual(findings.map((f) => [f.id, f.severity, f.file]), [['META-FRAGMENT-RELEASED', 'warning', 'unreleased/fix-k1.json']]);

  const before = readJson(t, 'deprecations.json');
  const stamp = stampMeta({ metaDir: t, version: '0.1.0', date: '2026-10-25' });
  assert.deepEqual(stamp.consumed, []);
  assert.deepEqual(stamp.leftovers, ['fix-k1']);
  assert.equal(existsSync(join(t, 'unreleased', 'fix-k1.json')), false);
  assert.deepEqual(readJson(t, 'deprecations.json'), before);
  assert.deepEqual(lintMeta({ metaDir: t, repoRoot: target.repoRoot }), []);
});

test('sync is idempotent', () => {
  const target = setup({ 'engine-thing.json': featureFragment });
  stampMeta({ metaDir: target.metaDir, version: '0.1.0', date: '2026-10-10' });
  const source = setup({ 'fix-k1.json': patchFragment });
  stampMeta({ metaDir: source.metaDir, version: '0.0.101', date: '2026-10-20' });

  syncMeta({ sourceDir: source.metaDir, targetDir: target.metaDir });
  const once = snapshot(target.metaDir);
  const r = syncMeta({ sourceDir: source.metaDir, targetDir: target.metaDir });
  assert.deepEqual(r, { status: 'up-to-date', changed: [], addedVersions: [] });
  assert.deepEqual(snapshot(target.metaDir), once);
});

test('conflicting manifest row throws and writes nothing', () => {
  const target = setup();
  stampMeta({ metaDir: target.metaDir, version: '0.0.101', date: '2026-10-20' });
  const source = setup({ 'fix-k1.json': patchFragment });
  stampMeta({ metaDir: source.metaDir, version: '0.0.101', date: '2026-10-21' });
  const before = snapshot(target.metaDir);
  assert.throws(() => syncMeta({ sourceDir: source.metaDir, targetDir: target.metaDir }), /0\.0\.101/);
  assert.deepEqual(snapshot(target.metaDir), before);

  writeFileSync(join(source.metaDir, 'deprecations.json'), '{ nope');
  assert.throws(() => syncMeta({ sourceDir: source.metaDir, targetDir: target.metaDir }), /deprecations\.json/);
  assert.deepEqual(snapshot(target.metaDir), before);
});

test('CLI --dry-run leaves the target untouched', () => {
  const target = setup({ 'engine-thing.json': featureFragment });
  stampMeta({ metaDir: target.metaDir, version: '0.1.0', date: '2026-10-10' });
  const source = setup({ 'fix-k1.json': patchFragment });
  stampMeta({ metaDir: source.metaDir, version: '0.0.101', date: '2026-10-20' });
  const before = snapshot(target.metaDir);

  const dry = spawnSync('node', [CLI, '--source', source.metaDir, '--target', target.metaDir, '--dry-run'], { encoding: 'utf8', env: localEnv });
  assert.equal(dry.status, 0, dry.stderr);
  assert.match(dry.stdout, /synced/);
  assert.match(dry.stdout, /0\.0\.101/);
  assert.match(dry.stdout, /version-manifest\.json/);
  assert.deepEqual(snapshot(target.metaDir), before);

  const real = spawnSync('node', [CLI, '--source', source.metaDir, '--target', target.metaDir], { encoding: 'utf8', env: localEnv });
  assert.equal(real.status, 0, real.stderr);
  assert.ok(Object.hasOwn(readJson(target.metaDir, 'version-manifest.json').versions, '0.0.101'));

  const missing = spawnSync('node', [CLI, '--target', target.metaDir], { encoding: 'utf8', env: localEnv });
  assert.equal(missing.status, 1);
  assert.match(missing.stderr, /--source/);
});
