// PR body parser and renderer. This file is the single owner of the PR-body format:
// the CI checker parses with it and the TRX-to-evidence tool renders with it.
// Dependency-free: Node built-ins only.

export const PRODUCTS = Object.freeze([
  'vnext-schema',
  'vnext-forge',
  'vnext-helm-charts',
  'vnext-docs',
  'vnext-workflow-cli',
  'vnext-runtime',
  'vnext-template',
  'vnext-integration-test',
  'vnext-example',
  'vnext-ai-toolkit',
]);

const COMMENT_RE = /<!--[\s\S]*?-->/g;

function normalize(text) {
  return String(text ?? '').replace(/\r\n?/g, '\n');
}

function stripComments(text) {
  return normalize(text).replace(COMMENT_RE, '');
}

/**
 * Splits a PR body into H2 sections. Keys are lower-cased, trimmed headings; values have
 * HTML comments removed and are trimmed. Content before the first H2 is ignored.
 */
export function parseSections(body) {
  const sections = new Map();
  const lines = stripComments(body).split('\n');
  let key = null;
  let buffer = [];
  const flush = () => {
    if (key !== null) sections.set(key, buffer.join('\n').trim());
  };
  for (const line of lines) {
    const heading = /^##\s+(.+?)\s*$/.exec(line);
    if (heading) {
      flush();
      key = heading[1].trim().toLowerCase();
      buffer = [];
    } else {
      buffer.push(line);
    }
  }
  flush();
  return sections;
}

const PLACEHOLDER_RE = /^<[^<>]*>$/;

/**
 * True when a section has no real content: empty, only comments, empty list bullets,
 * or lines whose only value is a `<...>` placeholder (optionally behind a label or backticks).
 */
export function isBlank(text) {
  if (text === undefined || text === null) return true;
  for (const raw of stripComments(text).split('\n')) {
    let line = raw.trim();
    if (!line) continue;
    line = line.replace(/^[-*]\s*(\[[ xX]\]\s*)?/, '').trim();
    if (!line) continue;
    const labelled = /^[A-Za-z][A-Za-z0-9 _-]*:\s*(.*)$/.exec(line);
    if (labelled) line = labelled[1].trim();
    line = line.replace(/^`+|`+$/g, '').trim();
    if (!line || PLACEHOLDER_RE.test(line)) continue;
    return false;
  }
  return true;
}

function unwrap(value) {
  return value.trim().replace(/^`+|`+$/g, '').trim();
}

function unwrapWhole(value) {
  const v = value.trim();
  const m = /^`([^`]*)`$/.exec(v);
  return m ? m[1].trim() : v;
}

const FIELD_RE = /^\s*[-*]\s*(status|runtime|base url|result|reds|reason)\s*:\s*(.*?)\s*$/i;

function readFields(text) {
  const fields = {};
  for (const line of stripComments(text).split('\n')) {
    const m = FIELD_RE.exec(line);
    if (!m) continue;
    const key = m[1].toLowerCase();
    // Runtime and base URL are always code-formatted; free text only loses a wrapping span.
    fields[key] = key === 'runtime' || key === 'base url' ? unwrap(m[2]) : unwrapWhole(m[2]);
  }
  return fields;
}

function tableRows(text) {
  const rows = [];
  for (const line of stripComments(text).split('\n')) {
    const t = line.trim();
    if (!t.startsWith('|')) continue;
    const inner = t.replace(/^\|/, '').replace(/\|$/, '');
    if (/^[\s|:-]+$/.test(inner)) continue;
    rows.push(inner.split('|').map((c) => c.trim()));
  }
  return rows;
}

const RESULT_RE = /^(\d+)\s*\/\s*(\d+)\s+passed\s*,\s*(\d+)\s+failed\s*,\s*(\d+)\s+skipped$/i;

/** Parses the E2E evidence block (see renderEvidence for the format). */
export function parseEvidence(text) {
  const fields = readFields(text);
  const status = (fields.status ?? '').toLowerCase();
  const out = { status: status === 'run' || status === 'not-run' ? status : null, suites: [] };
  if (fields.reason) out.reason = fields.reason;
  if (fields.runtime) out.runtimeSha = fields.runtime;
  if (fields['base url']) out.baseUrl = fields['base url'];
  const result = RESULT_RE.exec(fields.result ?? '');
  if (result) {
    out.totals = {
      passed: Number(result[1]),
      failed: Number(result[3]),
      skipped: Number(result[4]),
      total: Number(result[2]),
    };
  }
  for (const cells of tableRows(text)) {
    if (cells.length < 4 || cells[0].toLowerCase() === 'suite') continue;
    const [name, passed, failed, skipped] = cells;
    if (![passed, failed, skipped].every((n) => /^\d+$/.test(n))) continue;
    out.suites.push({ name, passed: Number(passed), failed: Number(failed), skipped: Number(skipped) });
  }
  if (fields.reds) out.reds = fields.reds;
  return out;
}

/** Renders evidence in the format parseEvidence reads; parse(render(x)) round-trips. */
export function renderEvidence(e) {
  const lines = [`- Status: ${e.status}`];
  if (e.status === 'not-run') {
    if (e.reason) lines.push(`- Reason: ${e.reason}`);
    return lines.join('\n');
  }
  if (e.runtimeSha) lines.push(`- Runtime: \`${e.runtimeSha}\``);
  if (e.baseUrl) lines.push(`- Base URL: \`${e.baseUrl}\``);
  if (e.totals) {
    const t = e.totals;
    lines.push(`- Result: ${t.passed}/${t.total} passed, ${t.failed} failed, ${t.skipped} skipped`);
  }
  if (e.suites && e.suites.length > 0) {
    lines.push('', '| Suite | Passed | Failed | Skipped |', '|---|---|---|---|');
    for (const s of e.suites) lines.push(`| ${s.name} | ${s.passed} | ${s.failed} | ${s.skipped} |`);
  }
  if (e.reds) lines.push('', `- Reds: ${e.reds}`);
  return lines.join('\n');
}

/** Parses the `| Product | Needed | Handoff |` table. Rows keep table order; no product validation. */
export function parseProductImpact(text) {
  const rows = [];
  for (const cells of tableRows(text)) {
    if (cells.length < 2 || cells[0].toLowerCase() === 'product') continue;
    const needed = (cells[1] ?? '').toLowerCase();
    rows.push({
      product: cells[0].toLowerCase(),
      needed: needed === 'yes' || needed === 'no' ? needed : null,
      handoff: cells.slice(2).join('|').trim(),
    });
  }
  return rows;
}

/** Parses the vnext-meta impact block: `- Status: updated|not-needed` and optional `- Reason:`. */
export function parseMetaImpact(text) {
  const fields = readFields(text);
  const status = (fields.status ?? '').toLowerCase();
  const out = { status: status === 'updated' || status === 'not-needed' ? status : null };
  if (fields.reason) out.reason = fields.reason;
  return out;
}
