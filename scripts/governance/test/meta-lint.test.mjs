import { test } from 'node:test';
import assert from 'node:assert/strict';
import { cpSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { lintMeta } from '../lib/meta-lint.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const BASE = join(here, 'fixtures', 'meta-base');
const REAL_ROOT = join(here, '..', '..', '..');

/** Copies the base fixture, applies `mutate(editJson, editText)`, and lints it. */
function lint(mutate = () => {}, milestone) {
  const dir = mkdtempSync(join(tmpdir(), 'meta-lint-'));
  cpSync(BASE, dir, { recursive: true });
  const metaDir = join(dir, 'meta');
  const repoRoot = join(dir, 'repo');
  const editJson = (file, fn) => {
    const p = join(metaDir, file);
    const doc = JSON.parse(readFileSync(p, 'utf8'));
    writeFileSync(p, JSON.stringify(fn(doc) ?? doc, null, 2));
  };
  const editText = (path, fn) => {
    const p = join(dir, path);
    writeFileSync(p, fn(readFileSync(p, 'utf8')));
  };
  const frag = (name, doc) => {
    mkdirSync(join(metaDir, 'unreleased'), { recursive: true });
    writeFileSync(join(metaDir, 'unreleased', name), typeof doc === 'string' ? doc : JSON.stringify(doc, null, 2));
  };
  mutate(editJson, editText, frag);
  return lintMeta({ metaDir, repoRoot, milestone });
}

const ids = (findings) => findings.map((f) => f.id);

test('base fixture is clean', () => {
  assert.deepEqual(lint(), []);
});

test('META-PARSE: invalid JSON', () => {
  const f = lint((_, text) => text('meta/features.json', () => '{ nope'));
  assert.ok(ids(f).includes('META-PARSE'));
});

test('META-PARSE: index.js cannot be required', () => {
  const f = lint((_, text) => text('meta/index.js', () => 'module.exports = require("./missing.json");'));
  assert.ok(f.some((x) => x.id === 'META-PARSE' && x.file === 'index.js'));
});

test('META-SHAPE: wrong root shapes', () => {
  const f = lint((json) => {
    json('version-manifest.json', () => ({ versions: [] }));
    json('features.json', () => ({ runtimeVersion: 100 }));
    json('component-registry.json', () => ({ tasks: [] }));
    json('deprecations.json', () => ({ items: {} }));
  });
  assert.equal(f.filter((x) => x.id === 'META-SHAPE').length, 4);
});

test('META-MANIFEST-ORDER: ascending keys', () => {
  const f = lint((json) =>
    json('version-manifest.json', (d) => ({
      versions: { '0.0.99': d.versions['0.0.99'], '0.0.100': d.versions['0.0.100'] },
    })),
  );
  assert.ok(ids(f).includes('META-MANIFEST-ORDER'));
});

test('META-MANIFEST-ORDER: non-semver key and bad date', () => {
  const a = lint((json) => json('version-manifest.json', (d) => ({ versions: { latest: d.versions['0.0.100'] } })));
  assert.ok(ids(a).includes('META-MANIFEST-ORDER'));
  const b = lint((json) => json('version-manifest.json', (d) => { d.versions['0.0.99'].releasedAt = '15/10/2026'; }));
  assert.ok(ids(b).includes('META-MANIFEST-ORDER'));
});

test('META-MANIFEST-ORDER: releasedAt increasing down the list', () => {
  const f = lint((json) => json('version-manifest.json', (d) => { d.versions['0.0.99'].releasedAt = '2026-11-01'; }));
  assert.ok(ids(f).includes('META-MANIFEST-ORDER'));
});

test('META-MANIFEST-ENTRY: bad schemaVersion and releaseNotes', () => {
  const f = lint((json) =>
    json('version-manifest.json', (d) => {
      d.versions['0.0.100'].schemaVersion = 'x';
      d.versions['0.0.99'].releaseNotes = 'http://insecure';
    }),
  );
  assert.equal(f.filter((x) => x.id === 'META-MANIFEST-ENTRY').length, 2);
});

test('META-RUNTIME-VERSION: differs from newest manifest key', () => {
  const f = lint((json) => json('features.json', (d) => { d.runtimeVersion = '0.0.99'; }));
  assert.deepEqual(ids(f), ['META-RUNTIME-VERSION']);
});

test('META-SINCE: non-semver value', () => {
  const f = lint((json) => json('migrations.json', (d) => { d.items[0].since = 'soon'; }));
  assert.deepEqual(ids(f), ['META-SINCE']);
});

test('META-SINCE: released version missing from manifest', () => {
  const f = lint((json) => json('migrations.json', (d) => { d.items[0].since = '0.0.50'; }));
  assert.deepEqual(ids(f), ['META-SINCE']);
});

test('META-SINCE: nested since is checked too', () => {
  const f = lint((json) => json('features.json', (d) => { d.engine.x.since = '0.0.7'; }));
  assert.deepEqual(ids(f), ['META-SINCE']);
});

test('META-SINCE: removedAt only needs null or semver', () => {
  assert.deepEqual(lint((json) => json('deprecations.json', (d) => { d.items[0].removedAt = '0.0.7'; })), []);
  const f = lint((json) => json('deprecations.json', (d) => { d.items[0].removedAt = 'later'; }));
  assert.deepEqual(ids(f), ['META-SINCE']);
});

test('META-SINCE: since equal to milestone passes', () => {
  const edit = (json) => json('migrations.json', (d) => { d.items[0].since = '0.0.101'; });
  assert.deepEqual(lint(edit, 'v0.0.101'), []);
});

test('META-SINCE: since ahead of milestone fails', () => {
  const edit = (json) => json('migrations.json', (d) => { d.items[0].since = '0.0.102'; });
  const f = lint(edit, 'v0.0.101');
  assert.deepEqual(f.map((x) => [x.id, x.severity]), [['META-SINCE', 'error']]);
});

test('META-SINCE: ahead of release without a milestone is only a warning', () => {
  const f = lint((json) => json('migrations.json', (d) => { d.items[0].since = '0.0.101'; }));
  assert.deepEqual(f.map((x) => [x.id, x.severity]), [['META-SINCE', 'warning']]);
});

test('META-REGISTRY-KEY: unknown keys and duplicates', () => {
  const f = lint((json) =>
    json('component-registry.json', (d) => {
      d.tasks.push({ key: 'ghost' }, { key: 'http' });
      d.functions.push({ key: 'nope' });
      d.extensions.push({ key: 'Global' });
    }),
  );
  const msgs = f.filter((x) => x.id === 'META-REGISTRY-KEY').map((x) => x.message).join('\n');
  assert.match(msgs, /"ghost"/);
  assert.match(msgs, /Duplicate tasks key "http"/);
  assert.match(msgs, /"nope"/);
  assert.match(msgs, /extensions key "Global"/);
});

test('META-REGISTRY-KEY: enum-only task (fanout) and camelCase extensions are accepted', () => {
  const f = lint((json) => json('component-registry.json', (d) => { d.extensions.push({ key: 'global' }); }));
  assert.deepEqual(f, []);
});

test('META-ID-UNIQUE: duplicate ids', () => {
  const f = lint((json) => {
    json('deprecations.json', (d) => { d.items.push({ ...d.items[0] }); });
    json('known-issues.json', (d) => { d.items.push({ ...d.items[0] }); });
  });
  assert.deepEqual(ids(f), ['META-ID-UNIQUE', 'META-ID-UNIQUE']);
});

const NOTE = { summary: 'A change.', breaking: false, docs: [] };
const validFragment = () => ({
  releaseNote: NOTE,
  schemaVersion: '0.0.57',
  features: { engine: { newThing: { since: 'next', status: 'stable' } } },
  migrations: [{ id: 'm-new', since: 'next' }],
  deprecations: [{ id: 'd-new', deprecatedSince: 'next', removedAt: null }],
  knownIssues: [{ id: 'k-new', affectedVersions: '>=next', fixedIn: null }],
  fixes: { knownIssues: ['k1'], deprecationsRemoved: ['d1'] },
});

test('fragment: valid fragment is clean', () => {
  assert.deepEqual(lint((_, __, frag) => frag('a.json', validFragment())), []);
});

test('fragment: releaseNote-only fragment is clean', () => {
  assert.deepEqual(lint((_, __, frag) => frag('a.json', { releaseNote: NOTE })), []);
});

test('fragment: missing releaseNote', () => {
  const f = lint((_, __, frag) => frag('a.json', { migrations: [] }));
  assert.deepEqual(ids(f), ['META-FRAGMENT']);
  assert.equal(f[0].file, 'unreleased/a.json');
});

test('fragment: unknown top-level key', () => {
  const f = lint((_, __, frag) => frag('a.json', { releaseNote: NOTE, knownIssue: [] }));
  assert.deepEqual(ids(f), ['META-FRAGMENT']);
});

test('fragment: since is a version, not next', () => {
  const f = lint((_, __, frag) => frag('a.json', { releaseNote: NOTE, migrations: [{ id: 'm-new', since: '0.0.101' }] }));
  assert.deepEqual(ids(f), ['META-FRAGMENT']);
});

test('fragment: invalid JSON', () => {
  assert.deepEqual(ids(lint((_, __, frag) => frag('a.json', '{ nope'))), ['META-FRAGMENT']);
});

test('fragment: bad slug', () => {
  assert.deepEqual(ids(lint((_, __, frag) => frag('My_Frag.json', { releaseNote: NOTE }))), ['META-FRAGMENT']);
});

test('fragment: feature key already in features.json', () => {
  const f = lint((_, __, frag) => frag('a.json', { releaseNote: NOTE, features: { engine: { x: { since: 'next' } } } }));
  assert.deepEqual(ids(f), ['META-FRAGMENT']);
});

test('fragment: feature key already in another fragment', () => {
  const doc = { releaseNote: NOTE, features: { engine: { y: { since: 'next' } } } };
  const f = lint((_, __, frag) => {
    frag('a.json', doc);
    frag('b.json', doc);
  });
  assert.deepEqual(ids(f), ['META-FRAGMENT']);
});

test('fragment: id repeats a stamped id', () => {
  const f = lint((_, __, frag) => frag('a.json', { releaseNote: NOTE, migrations: [{ id: 'm1', since: 'next' }] }));
  assert.deepEqual(ids(f), ['META-ID-UNIQUE']);
});

test('fragment: id repeats across two fragments', () => {
  const doc = { releaseNote: NOTE, migrations: [{ id: 'm-new', since: 'next' }] };
  const f = lint((_, __, frag) => {
    frag('a.json', doc);
    frag('b.json', doc);
  });
  assert.deepEqual(ids(f), ['META-ID-UNIQUE']);
});

test('fragment: fixes an unknown or already fixed known issue', () => {
  const unknown = lint((_, __, frag) => frag('a.json', { releaseNote: NOTE, fixes: { knownIssues: ['nope'] } }));
  assert.deepEqual(ids(unknown), ['META-FRAGMENT']);
  const fixed = lint((json, _, frag) => {
    json('known-issues.json', (d) => { d.items[0].fixedIn = '0.0.100'; });
    frag('a.json', { releaseNote: NOTE, fixes: { knownIssues: ['k1'] } });
  });
  assert.deepEqual(ids(fixed), ['META-FRAGMENT']);
});

test('fragment: fixes an unknown or already removed deprecation', () => {
  const unknown = lint((_, __, frag) => frag('a.json', { releaseNote: NOTE, fixes: { deprecationsRemoved: ['nope'] } }));
  assert.deepEqual(ids(unknown), ['META-FRAGMENT']);
  const removed = lint((json, _, frag) => {
    json('deprecations.json', (d) => { d.items[0].removedAt = '0.0.100'; });
    frag('a.json', { releaseNote: NOTE, fixes: { deprecationsRemoved: ['d1'] } });
  });
  assert.deepEqual(ids(removed), ['META-FRAGMENT']);
});

test('fragment: a new known issue cannot be fixed in the same release', () => {
  const f = lint((_, __, frag) =>
    frag('a.json', { releaseNote: NOTE, knownIssues: [{ id: 'k-new', affectedVersions: '>=next', fixedIn: null }], fixes: { knownIssues: ['k-new'] } }),
  );
  assert.deepEqual(ids(f), ['META-FRAGMENT']);
});

test('fragment: unknown key inside fixes, bad releaseNote, new-entry lifecycle values', () => {
  const bad = (doc) => ids(lint((_, __, frag) => frag('a.json', { releaseNote: NOTE, ...doc })));
  assert.deepEqual(bad({ fixes: { knownIssue: [] } }), ['META-FRAGMENT']);
  assert.deepEqual(bad({ releaseNote: { summary: '', breaking: false, docs: [] } }), ['META-FRAGMENT']);
  assert.deepEqual(bad({ releaseNote: { summary: 'x', breaking: 'no', docs: [] } }), ['META-FRAGMENT']);
  assert.deepEqual(bad({ releaseNote: { summary: 'x', breaking: false, docs: ['http://a'] } }), ['META-FRAGMENT']);
  assert.deepEqual(bad({ schemaVersion: 'x' }), ['META-FRAGMENT']);
  assert.deepEqual(bad({ features: { engine: { y: { status: 'stable' } } } }), ['META-FRAGMENT']);
  assert.deepEqual(bad({ features: { engine: [] } }), ['META-FRAGMENT']);
  assert.deepEqual(bad({ migrations: [{ since: 'next' }] }), ['META-FRAGMENT']);
  assert.deepEqual(bad({ deprecations: [{ id: 'd-new', deprecatedSince: 'next', removedAt: '0.0.1' }] }), ['META-FRAGMENT']);
  assert.deepEqual(bad({ knownIssues: [{ id: 'k-new', affectedVersions: '>=0.0.1', fixedIn: null }] }), ['META-FRAGMENT']);
  assert.deepEqual(bad({ knownIssues: [{ id: 'k-new', affectedVersions: '>=next', fixedIn: '0.0.1' }] }), ['META-FRAGMENT']);
});

test('next outside unreleased is an error', () => {
  const f = lint((json) => json('migrations.json', (d) => { d.items[0].since = 'next'; }));
  assert.deepEqual(ids(f), ['META-SINCE']);
  assert.equal(f[0].severity, 'error');
  assert.match(f[0].message, /unreleased/);
});

test('README.md in unreleased is ignored', () => {
  assert.deepEqual(lint((_, __, frag) => frag('README.md', '# notes')), []);
});

// CI lints the same live data in the contract step, under PR_CONTRACT_MODE control; here it would bypass warn mode.
test('real repo meta is clean', { skip: process.env.GITHUB_ACTIONS === 'true' }, () => {
  const f = lintMeta({ metaDir: join(REAL_ROOT, 'vnext-meta'), repoRoot: REAL_ROOT });
  assert.deepEqual(f.filter((x) => x.severity === 'error'), []);
});
