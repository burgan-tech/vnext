import { test } from 'node:test';
import assert from 'node:assert/strict';
import { cpSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
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
  mutate(editJson, editText);
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

// CI lints the same live data in the contract step, under PR_CONTRACT_MODE control; here it would bypass warn mode.
test('real repo meta is clean', { skip: process.env.GITHUB_ACTIONS === 'true' }, () => {
  const f = lintMeta({ metaDir: join(REAL_ROOT, 'vnext-meta'), repoRoot: REAL_ROOT });
  assert.deepEqual(f.filter((x) => x.severity === 'error'), []);
});
