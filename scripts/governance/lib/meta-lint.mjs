// Deterministic lint for the vnext-meta package. Dependency-free: Node built-ins only.
import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { createRequire } from 'node:module';

/**
 * @typedef {Object} Finding
 * @property {string} id        Rule id, e.g. `META-SINCE`.
 * @property {'error'|'warning'} severity
 * @property {string} file      File the finding is about (relative to the meta dir, or a source path).
 * @property {string} message   One-sentence English explanation.
 */

const SEMVER = /^(\d+)\.(\d+)\.(\d+)$/;
const DATE = /^\d{4}-\d{2}-\d{2}$/;
const SINCE_KEYS = new Set(['since', 'deprecatedSince', 'enforcedSince', 'fixedIn']);

/** Directory (inside the meta dir) holding version-less release fragments. */
export const FRAGMENT_DIR = 'unreleased';
/** Placeholder a fragment carries wherever the release version belongs; the release stamp replaces it. */
export const NEXT = 'next';

const SLUG = /^[a-z0-9][a-z0-9-]*$/;
const NEXT_TOKEN = /\bnext\b/;
const FRAGMENT_KEYS = new Set(['releaseNote', 'schemaVersion', 'features', 'migrations', 'deprecations', 'knownIssues', 'fixes']);
const FRAGMENT_LISTS = [
  ['deprecations', 'deprecations.json'],
  ['migrations', 'migrations.json'],
  ['knownIssues', 'known-issues.json'],
];
const FIXES_KEYS = new Set(['knownIssues', 'deprecationsRemoved']);

export const META_FILES = [
  'version-manifest.json',
  'features.json',
  'component-registry.json',
  'deprecations.json',
  'migrations.json',
  'known-issues.json',
  'performance-profiles.json',
  'security-policy.json',
];

const TASK_TYPES = 'src/BBT.Workflow.Execution.Abstractions/TaskTypes.cs';
const TASK_ENUMS = 'src/BBT.Workflow.Domain/Definitions/Tasks/TaskEnums.cs';
const FUNCTION_CONSTS = 'src/BBT.Workflow.Domain/Definitions/Functions/FunctionTypeConst.cs';
const EXTENSION_ENUMS = 'src/BBT.Workflow.Domain/Definitions/Extensions/ExtensionEnums.cs';

const isSemver = (v) => typeof v === 'string' && SEMVER.test(v);
const parts = (v) => v.match(SEMVER).slice(1).map(Number);

function compare(a, b) {
  const pa = parts(a);
  const pb = parts(b);
  for (let i = 0; i < 3; i++) if (pa[i] !== pb[i]) return pa[i] - pb[i];
  return 0;
}

const camel = (name) => name.charAt(0).toLowerCase() + name.slice(1);

function constValues(text) {
  return new Set([...text.matchAll(/public const string \w+ = "([^"]+)"/g)].map((m) => m[1]));
}

function enumMembers(text, enumName) {
  const body = text.match(new RegExp(`enum\\s+${enumName}\\b[^{]*\\{([^}]*)\\}`));
  if (!body) return [];
  const noComments = body[1].replace(/\/\/.*$/gm, '').replace(/\/\*[\s\S]*?\*\//g, '');
  return [...noComments.matchAll(/\b([A-Za-z_]\w*)\s*(?:=\s*[^,]+)?(?:,|$)/g)].map((m) => m[1]);
}

function readSource(repoRoot, rel, findings) {
  const path = join(repoRoot, rel);
  if (!existsSync(path)) {
    findings.push({ id: 'META-REGISTRY-KEY', severity: 'error', file: rel, message: 'Source file needed to verify registry keys is missing.' });
    return null;
  }
  return readFileSync(path, 'utf8');
}

/**
 * Reads every `<metaDir>/unreleased/*.json` fragment, sorted by file name. A missing directory yields `[]`;
 * non-`.json` files (the README) are skipped; an unparseable file yields `doc: null` and an `error`.
 * @param {string} metaDir
 * @returns {{ slug: string, file: string, doc: object|null, error?: string }[]}
 */
export function readFragments(metaDir) {
  const dir = join(metaDir, FRAGMENT_DIR);
  if (!existsSync(dir)) return [];
  return readdirSync(dir)
    .filter((name) => name.endsWith('.json'))
    .sort()
    .map((file) => {
      const slug = file.slice(0, -'.json'.length);
      try {
        return { slug, file, doc: JSON.parse(readFileSync(join(dir, file), 'utf8')) };
      } catch (e) {
        return { slug, file, doc: null, error: e.message };
      }
    });
}

/** Walks a parsed JSON value, calling visit(key, value, path) for every property. */
function walk(node, visit, path = '') {
  if (Array.isArray(node)) {
    node.forEach((child, i) => walk(child, visit, `${path}[${i}]`));
  } else if (node && typeof node === 'object') {
    for (const [k, v] of Object.entries(node)) {
      visit(k, v, path ? `${path}.${k}` : k);
      walk(v, visit, path ? `${path}.${k}` : k);
    }
  }
}

/**
 * @param {{ metaDir: string, repoRoot: string, milestone?: string }} opts
 * @returns {Finding[]}
 */
export function lintMeta({ metaDir, repoRoot, milestone }) {
  /** @type {Finding[]} */
  const findings = [];
  const add = (id, file, message, severity = 'error') => findings.push({ id, severity, file, message });
  const ms = milestone ? milestone.replace(/^v/, '') : null;

  // META-PARSE
  const docs = {};
  for (const file of META_FILES) {
    const path = join(metaDir, file);
    if (!existsSync(path)) {
      add('META-PARSE', file, 'File is missing from the meta directory.');
      continue;
    }
    try {
      docs[file] = JSON.parse(readFileSync(path, 'utf8'));
    } catch (e) {
      add('META-PARSE', file, `File is not valid JSON: ${e.message}`);
    }
  }
  try {
    createRequire(import.meta.url)(join(metaDir, 'index.js'));
  } catch (e) {
    add('META-PARSE', 'index.js', `index.js cannot be required: ${e.message}`);
  }

  // META-SHAPE
  const isObj = (v) => v && typeof v === 'object' && !Array.isArray(v);
  const shape = (file, ok, what) => {
    if (docs[file] !== undefined && !ok) add('META-SHAPE', file, `Root shape is wrong: expected ${what}.`);
  };
  const manifest = docs['version-manifest.json'];
  const features = docs['features.json'];
  const registry = docs['component-registry.json'];
  shape('version-manifest.json', isObj(manifest) && isObj(manifest.versions), 'a `versions` object');
  shape('features.json', isObj(features) && typeof features.runtimeVersion === 'string', 'a `runtimeVersion` string');
  shape(
    'component-registry.json',
    isObj(registry) && ['tasks', 'functions', 'extensions'].every((k) => Array.isArray(registry[k])),
    '`tasks`, `functions` and `extensions` arrays',
  );
  for (const file of ['deprecations.json', 'migrations.json', 'known-issues.json']) {
    shape(file, isObj(docs[file]) && Array.isArray(docs[file].items), 'an `items` array');
  }

  // META-MANIFEST-ORDER / META-MANIFEST-ENTRY
  let manifestKeys = [];
  let newest = null;
  if (isObj(manifest) && isObj(manifest.versions)) {
    manifestKeys = Object.keys(manifest.versions);
    const f = 'version-manifest.json';
    let prevKey = null;
    let prevDate = null;
    for (const key of manifestKeys) {
      const entry = manifest.versions[key];
      if (!isSemver(key)) {
        add('META-MANIFEST-ORDER', f, `Manifest key "${key}" is not a semver version.`);
      } else if (prevKey !== null && isSemver(prevKey) && compare(prevKey, key) <= 0) {
        add('META-MANIFEST-ORDER', f, `Manifest key "${key}" is not strictly below the previous key "${prevKey}".`);
      }
      const date = entry && entry.releasedAt;
      if (typeof date !== 'string' || !DATE.test(date)) {
        add('META-MANIFEST-ORDER', f, `"${key}" has a releasedAt that is not YYYY-MM-DD.`);
      } else {
        if (prevDate !== null && date > prevDate) {
          add('META-MANIFEST-ORDER', f, `"${key}" is released after the previous (newer) entry; releasedAt must not increase down the list.`);
        }
        prevDate = date;
      }
      prevKey = key;
      if (!entry || !isSemver(entry.schemaVersion)) {
        add('META-MANIFEST-ENTRY', f, `"${key}" has no semver schemaVersion.`);
      }
      if (!entry || typeof entry.releaseNotes !== 'string' || !entry.releaseNotes.startsWith('https://')) {
        add('META-MANIFEST-ENTRY', f, `"${key}" has no https:// releaseNotes link.`);
      }
    }
    const valid = manifestKeys.filter(isSemver);
    newest = valid.length ? valid.reduce((a, b) => (compare(a, b) >= 0 ? a : b)) : null;
  }

  // META-RUNTIME-VERSION
  if (newest && isObj(features) && typeof features.runtimeVersion === 'string' && features.runtimeVersion !== newest) {
    add('META-RUNTIME-VERSION', 'features.json', `runtimeVersion "${features.runtimeVersion}" does not equal the newest manifest key "${newest}".`);
  }

  // META-SINCE
  if (newest) {
    const manifestSet = new Set(manifestKeys);
    for (const [file, doc] of Object.entries(docs)) {
      if (file === 'version-manifest.json') continue;
      walk(doc, (key, value, path) => {
        if (value === NEXT && (key === 'removedAt' || SINCE_KEYS.has(key))) {
          add('META-SINCE', file, `${path} is "next", which is only valid in vnext-meta/${FRAGMENT_DIR}/, not in a stamped file.`);
          return;
        }
        if (key === 'removedAt') {
          if (value !== null && !isSemver(value)) add('META-SINCE', file, `${path} must be null or a semver version.`);
          return;
        }
        if (!SINCE_KEYS.has(key)) return;
        if (value === null) return;
        if (!isSemver(value)) {
          add('META-SINCE', file, `${path} must be null or a semver version, got ${JSON.stringify(value)}.`);
        } else if (compare(value, newest) <= 0) {
          if (!manifestSet.has(value)) add('META-SINCE', file, `${path} "${value}" is not a release in version-manifest.json.`);
        } else if (ms === null) {
          add('META-SINCE', file, `${path} "${value}" is ahead of the newest release ${newest}; pass --milestone to confirm it.`, 'warning');
        } else if (value !== ms) {
          add('META-SINCE', file, `${path} "${value}" is ahead of the newest release ${newest} and does not equal the milestone ${ms}.`);
        }
      });
    }
  }

  // META-REGISTRY-KEY
  if (isObj(registry) && ['tasks', 'functions', 'extensions'].every((k) => Array.isArray(registry[k]))) {
    const f = 'component-registry.json';
    const taskConsts = readSource(repoRoot, TASK_TYPES, findings);
    const taskEnums = readSource(repoRoot, TASK_ENUMS, findings);
    const fnConsts = readSource(repoRoot, FUNCTION_CONSTS, findings);
    const extEnums = readSource(repoRoot, EXTENSION_ENUMS, findings);
    const validTasks = new Set([
      ...(taskConsts ? constValues(taskConsts) : []),
      ...(taskEnums ? enumMembers(taskEnums, 'TaskType').map((m) => m.toLowerCase()) : []),
    ]);
    const validFns = fnConsts ? constValues(fnConsts) : new Set();
    const validExts = new Set(extEnums ? enumMembers(extEnums, 'ExtensionType').map(camel) : []);
    const check = (kind, valid, src, available) => {
      const seen = new Set();
      for (const item of registry[kind]) {
        const key = item && item.key;
        if (seen.has(key)) add('META-REGISTRY-KEY', f, `Duplicate ${kind} key "${key}".`);
        seen.add(key);
        if (available && !valid.has(key)) add('META-REGISTRY-KEY', f, `${kind} key "${key}" does not exist in code (${src}).`);
      }
    };
    check('tasks', validTasks, `${TASK_TYPES} or ${TASK_ENUMS}`, taskConsts !== null && taskEnums !== null);
    check('functions', validFns, FUNCTION_CONSTS, fnConsts !== null);
    check('extensions', validExts, EXTENSION_ENUMS, extEnums !== null);
  }

  // META-ID-UNIQUE
  for (const file of ['deprecations.json', 'migrations.json', 'known-issues.json']) {
    const doc = docs[file];
    if (!isObj(doc) || !Array.isArray(doc.items)) continue;
    const seen = new Set();
    for (const item of doc.items) {
      const id = item && item.id;
      if (seen.has(id)) add('META-ID-UNIQUE', file, `Duplicate id "${id}".`);
      seen.add(id);
    }
  }

  lintFragments(readFragments(metaDir), docs, add);

  return findings;
}

/**
 * Fragment rules: `META-FRAGMENT` for format errors, `META-ID-UNIQUE` for ids repeated across stamped files
 * and fragments. The format itself is documented in vnext-meta/unreleased/README.md.
 */
function lintFragments(fragments, docs, add) {
  const isObj = (v) => v && typeof v === 'object' && !Array.isArray(v);
  const stampedItems = (file) => (isObj(docs[file]) && Array.isArray(docs[file].items) ? docs[file].items : []);
  const stampedFeatures = isObj(docs['features.json']) ? docs['features.json'] : {};
  const seenFeatures = new Set();
  const seenIds = {};
  for (const [, file] of FRAGMENT_LISTS) seenIds[file] = new Set(stampedItems(file).map((i) => i && i.id));
  const knownIssues = stampedItems('known-issues.json');
  const deprecations = stampedItems('deprecations.json');

  for (const { slug, file: name, doc, error } of fragments) {
    const file = `${FRAGMENT_DIR}/${name}`;
    const bad = (message) => add('META-FRAGMENT', file, message);
    if (!SLUG.test(slug)) bad('Fragment file name must match ^[a-z0-9][a-z0-9-]*$.');
    if (doc === null) {
      bad(`Fragment is not valid JSON: ${error}`);
      continue;
    }
    if (!isObj(doc)) {
      bad('Fragment root must be a JSON object.');
      continue;
    }
    for (const key of Object.keys(doc)) if (!FRAGMENT_KEYS.has(key)) bad(`Unknown top-level key "${key}".`);

    const note = doc.releaseNote;
    if (!isObj(note)) {
      bad('releaseNote is required and must be an object.');
    } else {
      if (typeof note.summary !== 'string' || note.summary.trim() === '' || note.summary.length > 200) {
        bad('releaseNote.summary must be a non-empty string of at most 200 characters.');
      }
      if (typeof note.breaking !== 'boolean') bad('releaseNote.breaking must be a boolean.');
      if (!Array.isArray(note.docs) || !note.docs.every((d) => typeof d === 'string' && d.startsWith('https://'))) {
        bad('releaseNote.docs must be an array of https:// strings.');
      }
    }
    if (doc.schemaVersion !== undefined && !isSemver(doc.schemaVersion)) bad('schemaVersion must be a semver version.');

    // Any lifecycle value inside a new entry must be the placeholder (or null where "open" is meant).
    const checkLifecycle = (entry, where, handled = []) => {
      walk(entry, (key, value, path) => {
        if (handled.includes(path)) return; // top-level keys the caller reports itself
        if (key === 'removedAt' || key === 'fixedIn') {
          if (value !== null) bad(`${where}.${path} must be null in a new entry; a fix is declared under "fixes".`);
        } else if (SINCE_KEYS.has(key) && value !== NEXT && value !== null) {
          bad(`${where}.${path} must be "next" or null in a fragment, got ${JSON.stringify(value)}.`);
        }
      });
    };

    if (doc.features !== undefined) {
      if (!isObj(doc.features)) {
        bad('features must be an object of group -> key -> entry.');
      } else {
        for (const [group, keys] of Object.entries(doc.features)) {
          if (!isObj(keys)) {
            bad(`features.${group} must be an object of key -> entry.`);
            continue;
          }
          for (const [key, entry] of Object.entries(keys)) {
            const where = `features.${group}.${key}`;
            if (!isObj(entry)) {
              bad(`${where} must be an object.`);
              continue;
            }
            if (entry.since !== NEXT) bad(`${where}.since must be "next".`);
            checkLifecycle(entry, where, ['since']);
            const id = `${group}.${key}`;
            if ((isObj(stampedFeatures[group]) && key in stampedFeatures[group]) || seenFeatures.has(id)) {
              bad(`${where} already exists in features.json or another fragment.`);
            }
            seenFeatures.add(id);
          }
        }
      }
    }

    for (const [section, target] of FRAGMENT_LISTS) {
      const list = doc[section];
      if (list === undefined) continue;
      if (!Array.isArray(list) || !list.every((e) => isObj(e) && typeof e.id === 'string' && e.id !== '')) {
        bad(`${section} must be an array of objects that each have a string id.`);
        continue;
      }
      list.forEach((entry, i) => {
        const where = `${section}[${i}]`;
        const handled = { migrations: ['since'], deprecations: ['deprecatedSince', 'removedAt'], knownIssues: ['fixedIn'] };
        checkLifecycle(entry, where, handled[section]);
        if (section === 'migrations' && entry.since !== NEXT) bad(`${where}.since must be "next".`);
        if (section === 'deprecations') {
          if (entry.deprecatedSince !== NEXT) bad(`${where}.deprecatedSince must be "next".`);
          if (entry.removedAt !== null) bad(`${where}.removedAt must be null.`);
        }
        if (section === 'knownIssues') {
          if (typeof entry.affectedVersions !== 'string' || !NEXT_TOKEN.test(entry.affectedVersions)) {
            bad(`${where}.affectedVersions must be a string containing "next".`);
          }
          if (entry.fixedIn !== null) bad(`${where}.fixedIn must be null.`);
        }
        if (seenIds[target].has(entry.id)) add('META-ID-UNIQUE', file, `Duplicate id "${entry.id}" (already in ${target} or another fragment).`);
        seenIds[target].add(entry.id);
      });
    }

    if (doc.fixes !== undefined) {
      if (!isObj(doc.fixes)) {
        bad('fixes must be an object.');
      } else {
        for (const key of Object.keys(doc.fixes)) if (!FIXES_KEYS.has(key)) bad(`Unknown key "${key}" inside fixes.`);
        const ids = (key) => {
          const v = doc.fixes[key];
          if (v === undefined) return [];
          if (!Array.isArray(v) || !v.every((x) => typeof x === 'string')) {
            bad(`fixes.${key} must be an array of string ids.`);
            return [];
          }
          return v;
        };
        for (const id of ids('knownIssues')) {
          if (!knownIssues.some((k) => k && k.id === id && k.fixedIn === null)) {
            bad(`fixes.knownIssues "${id}" is not an open known issue in known-issues.json.`);
          }
        }
        for (const id of ids('deprecationsRemoved')) {
          if (!deprecations.some((d) => d && d.id === id && d.removedAt === null)) {
            bad(`fixes.deprecationsRemoved "${id}" is not a deprecation with removedAt null in deprecations.json.`);
          }
        }
      }
    }
  }
}
