// Brings a patch line's vnext-meta (e.g. release-v1.1 after 1.1.9) back into master's, which may already
// carry the next minor. Dependency-free: Node built-ins only. Process: docs/governance/release-lines.md.
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { META_FILES, RELEASED_FRAGMENTS, compareSemver, isSemver, readReleasedFragments } from './meta-lint.mjs';

/** Lifecycle fields where the lower release wins when both sides carry the same entry. */
const VERSION_KEYS = new Set(['since', 'deprecatedSince', 'enforcedSince', 'fixedIn', 'removedAt']);
const LIST_FILES = ['deprecations.json', 'migrations.json', 'known-issues.json'];
/** Registry, performance and security metadata are not fragment-able, so a patch line never adds to them. */
const NOT_SYNCED = new Set(['component-registry.json', 'performance-profiles.json', 'security-policy.json']);
const SYNCED_FILES = META_FILES.filter((file) => !NOT_SYNCED.has(file));

const isObject = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
const serialize = (doc) => JSON.stringify(doc, null, 2) + '\n';

function readJson(metaDir, file) {
  let text;
  try {
    text = readFileSync(join(metaDir, file), 'utf8');
  } catch (e) {
    throw new Error(`${file} cannot be read from ${metaDir}: ${e.message}`);
  }
  try {
    return JSON.parse(text);
  } catch (e) {
    throw new Error(`${file} in ${metaDir} is not valid JSON: ${e.message}`);
  }
}

function readSide(metaDir, label) {
  const docs = Object.fromEntries(SYNCED_FILES.map((file) => [file, readJson(metaDir, file)]));
  const fail = (file, what) => {
    throw new Error(`${label} ${file} has the wrong shape: expected ${what}.`);
  };
  if (!isObject(docs['version-manifest.json']) || !isObject(docs['version-manifest.json'].versions)) fail('version-manifest.json', 'a `versions` object');
  if (!isObject(docs['features.json'])) fail('features.json', 'an object');
  for (const file of LIST_FILES) {
    if (!isObject(docs[file]) || !Array.isArray(docs[file].items)) fail(file, 'an `items` array');
  }
  const { record, problem } = readReleasedFragments(metaDir);
  if (problem) throw new Error(`${label} ${RELEASED_FRAGMENTS}: ${problem.message}`);
  return { docs, record };
}

/** Lower release wins; a release beats null; anything else keeps the target's value. */
function lowerVersion(target, source) {
  if (isSemver(target) && isSemver(source)) return compareSemver(source, target) < 0 ? source : target;
  if (target === null && isSemver(source)) return source;
  return target;
}

const SEMVER_TOKEN = /\b\d+\.\d+\.\d+\b/g;

/**
 * `affectedVersions` ranges with the same shape (equal once every version is a placeholder) take the lower
 * version at each position, so `>=0.1.0` and `>=0.0.101` become `>=0.0.101`; other shapes keep the target's.
 */
function lowerRange(target, source) {
  if (typeof target !== 'string' || typeof source !== 'string') return target;
  if (target.replace(SEMVER_TOKEN, '#') !== source.replace(SEMVER_TOKEN, '#')) return target;
  const theirs = source.match(SEMVER_TOKEN) ?? [];
  let i = 0;
  return target.replace(SEMVER_TOKEN, (mine) => lowerVersion(mine, theirs[i++]));
}

/**
 * Returns a copy of `target` where every lifecycle field (at any depth) takes the lower release of the two
 * sides, and `affectedVersions` the lower range (see lowerRange). Every other field keeps the target's value.
 */
function mergeVersions(target, source) {
  if (Array.isArray(target)) {
    return Array.isArray(source) ? target.map((t, i) => mergeVersions(t, source[i])) : target;
  }
  if (!isObject(target) || !isObject(source)) return target;
  return Object.fromEntries(
    Object.entries(target).map(([k, v]) => {
      if (!Object.hasOwn(source, k)) return [k, v];
      if (VERSION_KEYS.has(k)) return [k, lowerVersion(v, source[k])];
      if (k === 'affectedVersions') return [k, lowerRange(v, source[k])];
      return [k, mergeVersions(v, source[k])];
    }),
  );
}

/** Inserts `[key, value]` before the first semver key lower than `key`, keeping a descending object order. */
function insertDescending(obj, key, value) {
  const entries = Object.entries(obj);
  const at = entries.findIndex(([k]) => isSemver(k) && compareSemver(k, key) < 0);
  entries.splice(at < 0 ? entries.length : at, 0, [key, value]);
  return Object.fromEntries(entries);
}

function mergeManifest(target, source) {
  let versions = target.versions;
  const added = [];
  for (const [key, row] of Object.entries(source.versions)) {
    if (Object.hasOwn(versions, key)) {
      const mine = versions[key];
      if (mine?.schemaVersion !== row?.schemaVersion || mine?.releasedAt !== row?.releasedAt) {
        throw new Error(
          `version-manifest.json row ${key} differs between source (${JSON.stringify(row)}) and target (${JSON.stringify(mine)}).`,
        );
      }
      continue;
    }
    if (!isSemver(key)) throw new Error(`Source version-manifest.json key "${key}" is not a semver version.`);
    versions = insertDescending(versions, key, row);
    added.push(key);
  }
  return { doc: { ...target, versions }, added: added.sort((a, b) => compareSemver(b, a)) };
}

function mergeFeatures(target, source) {
  const out = mergeVersions(target, source);
  for (const [group, entries] of Object.entries(source)) {
    if (!isObject(entries)) continue;
    if (!isObject(out[group])) {
      if (out[group] === undefined) out[group] = { ...entries };
      continue;
    }
    for (const [key, entry] of Object.entries(entries)) if (!Object.hasOwn(out[group], key)) out[group][key] = entry;
  }
  const versions = [target.runtimeVersion, source.runtimeVersion].filter(isSemver);
  if (versions.length) out.runtimeVersion = versions.reduce((a, b) => (compareSemver(b, a) > 0 ? b : a));
  return out;
}

const hasId = (item) => isObject(item) && typeof item.id === 'string';

/** Items match by string `id`; an item without one is appended only when no identical item exists. */
function mergeList(target, source) {
  const items = target.items.map((item) => {
    const theirs = hasId(item) ? source.items.find((s) => hasId(s) && s.id === item.id) : undefined;
    return theirs ? mergeVersions(item, theirs) : item;
  });
  for (const entry of source.items) {
    const present = hasId(entry)
      ? items.some((i) => hasId(i) && i.id === entry.id)
      : items.some((i) => JSON.stringify(i) === JSON.stringify(entry));
    if (!present) items.push(entry);
  }
  return { ...target, items };
}

function mergeRecord(target, source) {
  let record = target;
  for (const [version, slugs] of Object.entries(source)) {
    if (!Object.hasOwn(record, version)) {
      record = insertDescending(record, version, slugs);
    } else {
      for (const [slug, hash] of Object.entries(slugs)) {
        if (Object.hasOwn(record[version], slug) && record[version][slug] !== hash) {
          throw new Error(`${RELEASED_FRAGMENTS}: fragment ${slug} of release ${version} has a different hash on source and target.`);
        }
      }
      const extra = Object.entries(slugs).filter(([slug]) => !Object.hasOwn(record[version], slug));
      if (extra.length) record = { ...record, [version]: { ...record[version], ...Object.fromEntries(extra) } };
    }
  }
  return record;
}

/**
 * Merges every release the source line has and the target lacks into the target meta directory: manifest
 * rows (semver-descending), new feature keys and list items (source order), lower-wins lifecycle versions for
 * entries both sides carry, `runtimeVersion` = the higher one, and the union of released-fragments.json.
 * Validates both sides fully before writing; only files whose content changes are written.
 * @param {{ sourceDir: string, targetDir: string }} opts
 * @returns {{ status: 'synced'|'up-to-date', changed: string[], addedVersions: string[] }}
 */
export function syncMeta({ sourceDir, targetDir }) {
  const source = readSide(sourceDir, 'Source');
  const target = readSide(targetDir, 'Target');

  const manifest = mergeManifest(target.docs['version-manifest.json'], source.docs['version-manifest.json']);
  const merged = {
    'version-manifest.json': manifest.doc,
    'features.json': mergeFeatures(target.docs['features.json'], source.docs['features.json']),
    ...Object.fromEntries(LIST_FILES.map((file) => [file, mergeList(target.docs[file], source.docs[file])])),
  };
  const record = mergeRecord(target.record, source.record);

  const changed = [];
  const writes = [];
  for (const [file, doc] of Object.entries(merged)) {
    if (JSON.stringify(doc) !== JSON.stringify(target.docs[file])) writes.push([file, doc]);
  }
  if (JSON.stringify(record) !== JSON.stringify(target.record)) {
    writes.push([RELEASED_FRAGMENTS, record]);
  }
  for (const [file, doc] of writes) {
    writeFileSync(join(targetDir, file), serialize(doc));
    changed.push(file);
  }
  return { status: changed.length ? 'synced' : 'up-to-date', changed, addedVersions: manifest.added };
}
