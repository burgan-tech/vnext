// Release stamp for the vnext-meta package: consumes `unreleased/*.json` fragments, writes the real version
// into the shipped files and the release manifest. Dependency-free: Node built-ins only.
import { existsSync, readFileSync, unlinkSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { FRAGMENT_DIR, NEXT, compareSemver as compare, RELEASED_FRAGMENTS, readFragments, readReleasedFragments, releasedAs } from './meta-lint.mjs';

const SEMVER = /^(\d+)\.(\d+)\.(\d+)$/;
const DATE = /^\d{4}-\d{2}-\d{2}$/;
const PLACEHOLDER_KEYS = new Set(['since', 'deprecatedSince', 'enforcedSince', 'fixedIn', 'removedAt']);
const LISTS = [
  ['migrations', 'migrations.json'],
  ['deprecations', 'deprecations.json'],
  ['knownIssues', 'known-issues.json'],
];

export const releaseNotesUrl = (version) =>
  `https://burgan-tech.github.io/vnext-docs/blog/release-v${version.replaceAll('.', '-')}`;

const maxSemver = (list) => list.reduce((a, b) => (a === undefined || compare(b, a) > 0 ? b : a), undefined);
const isObject = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);

/** Deep copy replacing the `next` placeholder with the version (see vnext-meta/unreleased/README.md). */
function substitute(node, version, key) {
  if (Array.isArray(node)) return node.map((c) => substitute(c, version, key));
  if (isObject(node)) return Object.fromEntries(Object.entries(node).map(([k, v]) => [k, substitute(v, version, k)]));
  if (typeof node === 'string') {
    if (PLACEHOLDER_KEYS.has(key) && node === NEXT) return version;
    if (key === 'affectedVersions') return node.replace(/\bnext\b/g, version);
  }
  return node;
}

const readJson = (metaDir, file) => JSON.parse(readFileSync(join(metaDir, file), 'utf8'));
const serialize = (doc) => JSON.stringify(doc, null, 2) + '\n';

/**
 * @param {{ metaDir: string, version: string, date: string, schemaVersion?: string }} opts
 * @returns {{ status: 'stamped'|'already-stamped', schemaVersion: string, consumed: string[], leftovers: string[],
 *   notes: { slug: string, summary: string, breaking: boolean, docs: string[] }[] }}
 *   `consumed` fragments are merged and recorded in released-fragments.json under `version`; `leftovers`
 *   (byte-identical copies of fragments an earlier release consumed) are only deleted.
 */
export function stampMeta({ metaDir, version, date, schemaVersion }) {
  if (typeof version !== 'string' || !SEMVER.test(version)) throw new Error(`version must look like X.Y.Z (got ${JSON.stringify(version)}).`);
  if (typeof date !== 'string' || !DATE.test(date)) throw new Error(`date must look like YYYY-MM-DD (got ${JSON.stringify(date)}).`);
  if (schemaVersion !== undefined && (typeof schemaVersion !== 'string' || !SEMVER.test(schemaVersion))) {
    throw new Error(`schemaVersion must look like X.Y.Z (got ${JSON.stringify(schemaVersion)}).`);
  }

  const manifest = readJson(metaDir, 'version-manifest.json');
  const rows = manifest.versions ?? {};
  if (Object.hasOwn(rows, version)) {
    return { status: 'already-stamped', schemaVersion: rows[version].schemaVersion, consumed: [], leftovers: [], notes: [] };
  }
  const newest = maxSemver(Object.keys(rows).filter((k) => SEMVER.test(k)));
  if (newest !== undefined && compare(version, newest) < 0) {
    throw new Error(`version ${version} is lower than the newest release ${newest} and is not in the manifest.`);
  }

  const { record, problem } = readReleasedFragments(metaDir);
  if (problem) throw new Error(`${RELEASED_FRAGMENTS}: ${problem.message}`);
  // A byte-identical copy of a fragment an earlier release consumed (the back-merge keeps it) is a leftover:
  // deleted, never merged again. A released slug with new content must be renamed.
  const fragments = [];
  const leftovers = [];
  for (const f of readFragments(metaDir)) {
    const released = releasedAs(record, f.slug, f.hash);
    if (released?.leftover) leftovers.push(f);
    else if (released) throw new Error(`Fragment ${f.file}: slug already used by release ${released.version}; rename the fragment.`);
    else fragments.push(f);
  }
  for (const f of fragments) {
    if (f.doc === null) throw new Error(`Fragment ${f.file} cannot be parsed: ${f.error}`);
    if (!isObject(f.doc)) throw new Error(`Fragment ${f.file} must be a JSON object.`);
  }

  const fragSchemas = fragments.map((f) => f.doc.schemaVersion).filter((s) => typeof s === 'string' && SEMVER.test(s));
  const prevSchema = newest !== undefined ? rows[newest].schemaVersion : undefined;
  const effectiveSchema = schemaVersion ?? maxSemver([...(prevSchema ? [prevSchema] : []), ...fragSchemas]);
  if (!effectiveSchema) throw new Error('Cannot determine schemaVersion; pass it explicitly.');

  const features = readJson(metaDir, 'features.json');
  const lists = Object.fromEntries(LISTS.map(([key, file]) => [key, readJson(metaDir, file)]));
  features.runtimeVersion = version;
  const notes = [];

  for (const { slug, file, doc } of fragments) {
    for (const [group, entries] of Object.entries(doc.features ?? {})) {
      features[group] ??= {};
      for (const [key, entry] of Object.entries(entries)) {
        if (Object.hasOwn(features[group], key)) throw new Error(`Feature ${group}.${key} from ${file} would collide with an existing key.`);
        features[group][key] = substitute(entry, version);
      }
    }
    for (const [key] of LISTS) {
      const items = lists[key].items;
      for (const entry of doc[key] ?? []) {
        if (items.some((i) => i.id === entry.id)) throw new Error(`Id ${entry.id} from ${file} would collide with an existing ${key} entry.`);
        items.push(substitute(entry, version));
      }
    }
    for (const id of doc.fixes?.knownIssues ?? []) {
      const issue = lists.knownIssues.items.find((i) => i.id === id);
      if (!issue) throw new Error(`fixes.knownIssues in ${file} names unknown known issue ${id}.`);
      issue.fixedIn = version;
    }
    for (const id of doc.fixes?.deprecationsRemoved ?? []) {
      const dep = lists.deprecations.items.find((i) => i.id === id);
      if (!dep) throw new Error(`fixes.deprecationsRemoved in ${file} names unknown deprecation ${id}.`);
      dep.removedAt = version;
    }
    notes.push({ slug, ...doc.releaseNote });
  }

  const nextManifest = {
    ...manifest,
    versions: { [version]: { schemaVersion: effectiveSchema, releasedAt: date, releaseNotes: releaseNotesUrl(version) }, ...rows },
  };

  // All validation done; write only files whose content changes, then delete the consumed fragments.
  const outputs = [
    ['version-manifest.json', nextManifest],
    ['features.json', features],
    ...LISTS.map(([key, file]) => [file, lists[key]]),
  ];
  if (fragments.length > 0) {
    const { [version]: current, ...older } = record;
    const recorded = { ...current, ...Object.fromEntries(fragments.map((f) => [f.slug, f.hash])) };
    outputs.push([RELEASED_FRAGMENTS, { [version]: recorded, ...older }]);
  }
  for (const [file, doc] of outputs) {
    const text = serialize(doc);
    const path = join(metaDir, file);
    if (!existsSync(path) || readFileSync(path, 'utf8') !== text) writeFileSync(path, text);
  }
  for (const f of [...fragments, ...leftovers]) unlinkSync(join(metaDir, FRAGMENT_DIR, f.file));

  return {
    status: 'stamped',
    schemaVersion: effectiveSchema,
    consumed: fragments.map((f) => f.slug),
    leftovers: leftovers.map((f) => f.slug),
    notes,
  };
}
