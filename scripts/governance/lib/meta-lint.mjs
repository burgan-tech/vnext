// Deterministic lint for the vnext-meta package. Dependency-free: Node built-ins only.
import { readFileSync, existsSync } from 'node:fs';
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

const FILES = [
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
  for (const file of FILES) {
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

  return findings;
}
