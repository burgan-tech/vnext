import { test } from 'node:test';
import assert from 'node:assert/strict';
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { lintMeta, fragmentHash } from '../lib/meta-lint.mjs';
import { stampMeta, releaseNotesUrl } from '../lib/meta-stamp.mjs';

// The CLI must behave the same locally and under GitHub Actions.
const { GITHUB_ACTIONS: _a, GITHUB_STEP_SUMMARY: _s, ...localEnv } = process.env;
const here = dirname(fileURLToPath(import.meta.url));
const BASE = join(here, 'fixtures', 'meta-base');
const CLI = join(here, '..', 'meta-stamp.mjs');

function setup(fragments = {}) {
  const dir = mkdtempSync(join(tmpdir(), 'meta-stamp-'));
  cpSync(BASE, dir, { recursive: true });
  const metaDir = join(dir, 'meta');
  const repoRoot = join(dir, 'repo');
  mkdirSync(join(metaDir, 'unreleased'), { recursive: true });
  for (const [name, doc] of Object.entries(fragments)) {
    writeFileSync(join(metaDir, 'unreleased', name), typeof doc === 'string' ? doc : JSON.stringify(doc, null, 2));
  }
  return { dir, metaDir, repoRoot };
}

const snapshot = (metaDir) => {
  const out = {};
  for (const f of readdirSync(metaDir)) if (f.endsWith('.json')) out[f] = readFileSync(join(metaDir, f), 'utf8');
  out.fragments = existsSync(join(metaDir, 'unreleased')) ? readdirSync(join(metaDir, 'unreleased')).sort() : [];
  return out;
};
const readJson = (metaDir, f) => JSON.parse(readFileSync(join(metaDir, f), 'utf8'));
const note = (summary = 'A change.') => ({ summary, breaking: false, docs: [] });

const full = {
  releaseNote: { summary: 'Everything.', breaking: true, docs: ['https://example.com/d'] },
  schemaVersion: '0.0.57',
  features: { engine: { newThing: { since: 'next', status: 'stable' } }, fresh: { g: { since: 'next' } } },
  migrations: [{ id: 'm2', since: 'next' }],
  deprecations: [{ id: 'd2', deprecatedSince: 'next', removedAt: null }],
  knownIssues: [{ id: 'k2', affectedVersions: '>=next', fixedIn: null }],
  fixes: { knownIssues: ['k1'], deprecationsRemoved: ['d1'] },
};

test('no fragments still writes the manifest row', () => {
  const { metaDir, repoRoot } = setup();
  const r = stampMeta({ metaDir, version: '0.0.101', date: '2026-10-20' });
  assert.equal(r.status, 'stamped');
  const versions = readJson(metaDir, 'version-manifest.json').versions;
  assert.equal(Object.keys(versions)[0], '0.0.101');
  assert.deepEqual(versions['0.0.101'], {
    schemaVersion: '0.0.56',
    releasedAt: '2026-10-20',
    releaseNotes: 'https://burgan-tech.github.io/vnext-docs/blog/release-v0-0-101',
  });
  assert.equal(readJson(metaDir, 'features.json').runtimeVersion, '0.0.101');
  assert.deepEqual(lintMeta({ metaDir, repoRoot }), []);
});

test('releaseNotesUrl replaces dots with dashes', () => {
  assert.equal(releaseNotesUrl('1.2.3'), 'https://burgan-tech.github.io/vnext-docs/blog/release-v1-2-3');
});

test('fragments are merged with the real version', () => {
  const { metaDir, repoRoot } = setup({ 'all.json': full });
  const r = stampMeta({ metaDir, version: '0.0.101', date: '2026-10-20' });
  assert.deepEqual(r.consumed, ['all']);
  assert.deepEqual(r.notes[0], { ...full.releaseNote, slug: 'all' });
  assert.equal(existsSync(join(metaDir, 'unreleased', 'all.json')), false);
  const features = readJson(metaDir, 'features.json');
  assert.equal(features.engine.newThing.since, '0.0.101');
  assert.equal(features.fresh.g.since, '0.0.101');
  assert.deepEqual(Object.keys(features.engine), ['x', 'newThing']);
  assert.equal(readJson(metaDir, 'migrations.json').items[1].since, '0.0.101');
  assert.equal(readJson(metaDir, 'deprecations.json').items[1].deprecatedSince, '0.0.101');
  assert.equal(readJson(metaDir, 'deprecations.json').items[0].removedAt, '0.0.101');
  const kn = readJson(metaDir, 'known-issues.json').items;
  assert.equal(kn[0].fixedIn, '0.0.101');
  assert.equal(kn[1].affectedVersions, '>=0.0.101');
  assert.deepEqual(lintMeta({ metaDir, repoRoot }), []);
});

test('placeholders are replaced at any depth', () => {
  const { metaDir } = setup({
    'deep.json': { releaseNote: note(), features: { engine: { f: { since: 'next', nested: { enforcedSince: 'next', keep: 'next' } } } } },
  });
  stampMeta({ metaDir, version: '0.0.101', date: '2026-10-20' });
  const f = readJson(metaDir, 'features.json').engine.f;
  assert.equal(f.nested.enforcedSince, '0.0.101');
  assert.equal(f.nested.keep, 'next');
});

test('schemaVersion is the highest of the previous row and the fragments', () => {
  const frags = {
    'a.json': { releaseNote: note(), schemaVersion: '0.0.57' },
    'b.json': { releaseNote: note(), schemaVersion: '0.0.58' },
  };
  const one = setup(frags);
  const r = stampMeta({ metaDir: one.metaDir, version: '0.0.101', date: '2026-10-20' });
  assert.equal(r.schemaVersion, '0.0.58');
  const two = setup(frags);
  const r2 = stampMeta({ metaDir: two.metaDir, version: '0.0.101', date: '2026-10-20', schemaVersion: '0.0.60' });
  assert.equal(r2.schemaVersion, '0.0.60');
  assert.equal(readJson(two.metaDir, 'version-manifest.json').versions['0.0.101'].schemaVersion, '0.0.60');
});

test('stamp twice is a no-op', () => {
  const { metaDir } = setup({ 'all.json': full });
  stampMeta({ metaDir, version: '0.0.101', date: '2026-10-20' });
  const after = snapshot(metaDir);
  const r = stampMeta({ metaDir, version: '0.0.101', date: '2026-10-21' });
  assert.equal(r.status, 'already-stamped');
  assert.deepEqual(r.consumed, []);
  assert.deepEqual(snapshot(metaDir), after);
});

test('version already released leaves fragments alone', () => {
  const { metaDir } = setup({ 'all.json': full });
  const before = snapshot(metaDir);
  const r = stampMeta({ metaDir, version: '0.0.100', date: '2026-10-20' });
  assert.equal(r.status, 'already-stamped');
  assert.equal(r.schemaVersion, '0.0.56');
  assert.deepEqual(snapshot(metaDir), before);
  assert.ok(existsSync(join(metaDir, 'unreleased', 'all.json')));
});

test('older version than the newest release throws', () => {
  const { metaDir } = setup();
  assert.throws(() => stampMeta({ metaDir, version: '0.0.98', date: '2026-10-20' }), /lower than/);
});

test('malformed inputs throw', () => {
  const { metaDir } = setup();
  assert.throws(() => stampMeta({ metaDir, version: '0.0.101-rc1', date: '2026-10-20' }), /version/);
  assert.throws(() => stampMeta({ metaDir, version: '0.0.101', date: '20-10-2026' }), /date/);
  assert.throws(() => stampMeta({ metaDir, version: '0.0.101', date: '2026-10-20', schemaVersion: 'x' }), /schemaVersion/);
});

test('an unparseable fragment throws and writes nothing', () => {
  const { metaDir } = setup({ 'a.json': { releaseNote: note() }, 'bad.json': '{ nope' });
  const before = snapshot(metaDir);
  assert.throws(() => stampMeta({ metaDir, version: '0.0.101', date: '2026-10-20' }), /bad/);
  assert.deepEqual(snapshot(metaDir), before);
});

test('a colliding feature key throws and writes nothing', () => {
  const { metaDir } = setup({ 'a.json': { releaseNote: note(), features: { engine: { x: { since: 'next' } } } } });
  const before = snapshot(metaDir);
  assert.throws(() => stampMeta({ metaDir, version: '0.0.101', date: '2026-10-20' }), /collide|exists/);
  assert.deepEqual(snapshot(metaDir), before);
});

test('output keeps two-space JSON with a trailing newline', () => {
  const { metaDir } = setup({ 'all.json': full });
  stampMeta({ metaDir, version: '0.0.101', date: '2026-10-20' });
  for (const f of ['version-manifest.json', 'features.json', 'migrations.json', 'deprecations.json', 'known-issues.json']) {
    const text = readFileSync(join(metaDir, f), 'utf8');
    assert.equal(text, JSON.stringify(JSON.parse(text), null, 2) + '\n', f);
  }
});

const fragBytes = (metaDir, name) => readFileSync(join(metaDir, 'unreleased', name));
const writeRecord = (metaDir, doc) => writeFileSync(join(metaDir, 'released-fragments.json'), JSON.stringify(doc, null, 2) + '\n');

test('stamp records every consumed fragment hash under the new version, newest first', () => {
  const { metaDir } = setup({ 'all.json': full, 'b.json': { releaseNote: note() } });
  const hashes = { all: fragmentHash(fragBytes(metaDir, 'all.json')), b: fragmentHash(fragBytes(metaDir, 'b.json')) };
  writeRecord(metaDir, { '0.0.100': { older: 'ab'.repeat(32) } });
  const r = stampMeta({ metaDir, version: '0.0.101', date: '2026-10-20' });
  assert.deepEqual(r.leftovers, []);
  const text = readFileSync(join(metaDir, 'released-fragments.json'), 'utf8');
  assert.equal(text, JSON.stringify(JSON.parse(text), null, 2) + '\n');
  const record = JSON.parse(text);
  assert.deepEqual(Object.keys(record), ['0.0.101', '0.0.100']);
  assert.deepEqual(record['0.0.101'], hashes);
});

test('a byte-identical leftover is deleted, not merged and not in the notes', () => {
  const { metaDir, repoRoot } = setup({ 'all.json': full, 'b.json': { releaseNote: note('B.') } });
  writeRecord(metaDir, { '0.0.100': { all: fragmentHash(fragBytes(metaDir, 'all.json')) } });
  const r = stampMeta({ metaDir, version: '0.0.101', date: '2026-10-20' });
  assert.equal(r.status, 'stamped');
  assert.deepEqual(r.leftovers, ['all']);
  assert.deepEqual(r.consumed, ['b']);
  assert.deepEqual(r.notes.map((n) => n.slug), ['b']);
  assert.deepEqual(readdirSync(join(metaDir, 'unreleased')).sort(), ['.gitkeep']);
  assert.equal(readJson(metaDir, 'features.json').engine.newThing, undefined);
  assert.equal(readJson(metaDir, 'migrations.json').items.length, 1);
  assert.deepEqual(Object.keys(readJson(metaDir, 'released-fragments.json')['0.0.101']), ['b']);
  assert.deepEqual(lintMeta({ metaDir, repoRoot }), []);
});

test('a reused slug with different content throws and writes nothing', () => {
  const { metaDir } = setup({ 'all.json': full });
  writeRecord(metaDir, { '0.0.100': { all: 'ab'.repeat(32) } });
  const before = snapshot(metaDir);
  assert.throws(() => stampMeta({ metaDir, version: '0.0.101', date: '2026-10-20' }), /all.*0\.0\.100.*rename|rename/);
  assert.deepEqual(snapshot(metaDir), before);
});

test('already-stamped leaves leftovers and the record untouched', () => {
  const { metaDir } = setup({ 'all.json': full });
  writeRecord(metaDir, { '0.0.100': { all: fragmentHash(fragBytes(metaDir, 'all.json')) } });
  const before = snapshot(metaDir);
  const r = stampMeta({ metaDir, version: '0.0.100', date: '2026-10-20' });
  assert.equal(r.status, 'already-stamped');
  assert.deepEqual(r.leftovers, []);
  assert.deepEqual(snapshot(metaDir), before);
});

test('end to end: a back-merged leftover self-heals on the next release', () => {
  const f1 = { releaseNote: note('F1.'), features: { engine: { f1: { since: 'next' } } }, migrations: [{ id: 'm-f1', since: 'next' }] };
  const f2 = { releaseNote: note('F2.'), migrations: [{ id: 'm-f2', since: 'next' }] };
  const { metaDir, repoRoot } = setup({ 'f1.json': f1 });
  const f1Bytes = fragBytes(metaDir, 'f1.json');
  stampMeta({ metaDir, version: '0.0.101', date: '2026-10-20' });
  assert.equal(existsSync(join(metaDir, 'unreleased', 'f1.json')), false);

  // The back-merge keeps master's copy of f1 byte-identical; a new PR adds f2.
  writeFileSync(join(metaDir, 'unreleased', 'f1.json'), f1Bytes);
  writeFileSync(join(metaDir, 'unreleased', 'f2.json'), JSON.stringify(f2, null, 2));
  const findings = lintMeta({ metaDir, repoRoot });
  assert.deepEqual(findings.map((x) => [x.id, x.severity, x.file]), [['META-FRAGMENT-RELEASED', 'warning', 'unreleased/f1.json']]);

  const r = stampMeta({ metaDir, version: '0.0.102', date: '2026-10-21' });
  assert.deepEqual([r.consumed, r.leftovers], [['f2'], ['f1']]);
  assert.deepEqual(readdirSync(join(metaDir, 'unreleased')).sort(), ['.gitkeep']);
  assert.equal(readJson(metaDir, 'features.json').engine.f1.since, '0.0.101');
  assert.deepEqual(readJson(metaDir, 'migrations.json').items.map((i) => [i.id, i.since]), [['m1', '0.0.100'], ['m-f1', '0.0.101'], ['m-f2', '0.0.102']]);
  assert.deepEqual(Object.keys(readJson(metaDir, 'released-fragments.json')), ['0.0.102', '0.0.101']);
  assert.deepEqual(lintMeta({ metaDir, repoRoot }), []);
});

test('CLI prints leftovers, also under --dry-run', () => {
  const { metaDir } = setup({ 'all.json': full });
  writeRecord(metaDir, { '0.0.100': { all: fragmentHash(fragBytes(metaDir, 'all.json')) } });
  const r = spawnSync(process.execPath, [CLI, '--version', '0.0.101', '--date', '2026-10-20', '--meta-dir', metaDir, '--dry-run'], { encoding: 'utf8', env: localEnv });
  assert.equal(r.status, 0, r.stderr);
  assert.match(r.stdout, /leftover fragments \(1\): all/);
});

test('CLI writes no --notes-out when the version is already stamped', () => {
  const { dir, metaDir } = setup({ 'all.json': full });
  const out = join(dir, 'notes.json');
  const r = spawnSync(process.execPath, [CLI, '--version', '0.0.100', '--date', '2026-10-20', '--meta-dir', metaDir, '--notes-out', out], { encoding: 'utf8', env: localEnv });
  assert.equal(r.status, 0, r.stderr);
  assert.match(r.stdout, /already in the manifest/);
  assert.equal(existsSync(out), false);
});

test('CLI --dry-run does not touch the meta dir', () => {
  const { metaDir } = setup({ 'all.json': full });
  const before = snapshot(metaDir);
  const r = spawnSync(process.execPath, [CLI, '--version', '0.0.101', '--date', '2026-10-20', '--meta-dir', metaDir, '--dry-run'], { encoding: 'utf8', env: localEnv });
  assert.equal(r.status, 0, r.stderr);
  assert.match(r.stdout, /0\.0\.101/);
  assert.match(r.stdout, /all/);
  assert.deepEqual(snapshot(metaDir), before);
});

test('CLI stamps and writes --notes-out; bad version exits 1', () => {
  const { dir, metaDir } = setup({ 'all.json': full });
  const out = join(dir, 'notes.json');
  const r = spawnSync(process.execPath, [CLI, '--version', '0.0.101', '--date', '2026-10-20', '--meta-dir', metaDir, '--notes-out', out], { encoding: 'utf8', env: localEnv });
  assert.equal(r.status, 0, r.stderr);
  const notes = JSON.parse(readFileSync(out, 'utf8'));
  assert.deepEqual([notes.version, notes.schemaVersion, notes.date], ['0.0.101', '0.0.57', '2026-10-20']);
  assert.equal(notes.notes[0].slug, 'all');
  const bad = spawnSync(process.execPath, [CLI, '--version', 'nope', '--meta-dir', metaDir], { encoding: 'utf8', env: localEnv });
  assert.equal(bad.status, 1);
});
