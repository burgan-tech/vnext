// Issue-triage core. The agent only answers with a JSON result; everything that reaches GitHub
// goes through extractResult -> validateResult -> renderComment / planActions here, so the issue text the agent
// read (untrusted: the repo is public) can never pick a label outside the taxonomy, ping a user
// or carry a secret into a comment.
// Dependency-free: Node built-ins only.

export const MARKER = '<!-- vnext-issue-triage -->';
export const BOT_LOGIN = 'github-actions[bot]';
export const TRIAGED_LABEL = 'ai:triaged';
export const NEEDS_INFO_LABEL = 'needs-info';
// P0 is a maintainer decision: the agent may suggest it, never apply it.
export const MAINTAINER_ONLY_PRIORITIES = Object.freeze(['priority:P0']);

const MAX_AREAS = 3;
const MAX_FILES = 8;
const MAX_LIST = 8;
const MAX_TEXT = 1200;
const MAX_ITEM = 300;
const CONFIDENCE = new Set(['low', 'medium', 'high']);

const SECRET_RES = [
  /sk-ant-[A-Za-z0-9_-]{8,}/g,
  /gh[pousr]_[A-Za-z0-9]{20,}/g,
  /github_pat_[A-Za-z0-9_]{20,}/g,
  /eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}/g,
];

/**
 * Pulls the result object out of the agent's stdout: the `agent -p --output-format json` envelope
 * ({ type: 'result', result: '<final answer text>' }) or bare text; the answer may be fenced or
 * carry prose around the object. Throws when no JSON object can be parsed.
 */
export function extractResult(stdout) {
  let text = String(stdout ?? '').trim();
  let envelope;
  try {
    envelope = JSON.parse(text);
  } catch {
    // not an envelope: treat stdout as the answer text
  }
  if (envelope?.type === 'result') {
    if (envelope.is_error || typeof envelope.result !== 'string') throw new Error('the agent reported an error');
    text = envelope.result;
  } else if (envelope && typeof envelope === 'object') {
    return envelope;
  }
  const start = text.indexOf('{');
  const end = text.lastIndexOf('}');
  if (start < 0 || end <= start) throw new Error('no JSON object in the agent output');
  return JSON.parse(text.slice(start, end + 1));
}

/** Removes secrets, user/team mentions and HTML comments (the marker) from agent text. */
export function sanitizeText(value, max = MAX_TEXT, secrets = []) {
  let text = String(value ?? '').replace(/\r\n?/g, '\n').trim();
  for (const secret of secrets) {
    if (secret && secret.length >= 8) text = text.split(secret).join('[redacted]');
  }
  for (const re of SECRET_RES) text = text.replace(re, '[redacted]');
  text = text
    .replace(/<!--|-->/g, '')
    .replace(/(^|[^\w`])@(?=[A-Za-z0-9])/g, '$1@​');
  return text.length > max ? `${text.slice(0, max - 1)}…` : text;
}

const sanitizeList = (value, secrets) =>
  (Array.isArray(value) ? value : [])
    .map((v) => sanitizeText(v, MAX_ITEM, secrets))
    .filter(Boolean)
    .slice(0, MAX_LIST);

/**
 * Validates an agent result against the taxonomy. Unknown labels are dropped (and reported in
 * `dropped`), affected files must exist (`fileExists`), all text is sanitized.
 */
export function validateResult(raw, taxonomy, { fileExists = () => true, secrets = [] } = {}) {
  const dropped = [];
  const kinds = new Map(taxonomy.kinds.map((k) => [k.name, k]));
  const areas = new Set(taxonomy.areas.map((a) => a.name));
  const priorities = new Set(taxonomy.priorities.map((p) => p.name));

  const kind = kinds.has(raw?.kind) ? raw.kind : null;
  if (!kind) dropped.push(`kind:${raw?.kind}`);

  const pickedAreas = [];
  for (const a of Array.isArray(raw?.areas) ? raw.areas : []) {
    if (areas.has(a) && !pickedAreas.includes(a)) pickedAreas.push(a);
    else dropped.push(`area:${a}`);
  }

  const priority = priorities.has(raw?.priority) ? raw.priority : null;
  if (raw?.priority && !priority) dropped.push(`priority:${raw.priority}`);

  const affectedFiles = [];
  for (const f of Array.isArray(raw?.affectedFiles) ? raw.affectedFiles : []) {
    const path = String(f?.path ?? '').replace(/^\.?\//, '');
    if (!path || path.includes('..') || !fileExists(path)) {
      dropped.push(`file:${path}`);
      continue;
    }
    const line = Number.isInteger(f?.line) && f.line > 0 ? f.line : null;
    affectedFiles.push({ path, line, why: sanitizeText(f?.why, MAX_ITEM, secrets) });
    if (affectedFiles.length === MAX_FILES) break;
  }

  return {
    result: {
      kind,
      type: kind ? kinds.get(kind).type : null,
      areas: pickedAreas.slice(0, MAX_AREAS),
      priority,
      hotfixCandidate: raw?.hotfixCandidate === true,
      needsInfo: raw?.needsInfo === true,
      questions: sanitizeList(raw?.questions, secrets),
      summary: sanitizeText(raw?.summary, MAX_TEXT, secrets),
      reproduction: sanitizeList(raw?.reproduction, secrets),
      affectedFiles,
      suspectedCause: sanitizeText(raw?.suspectedCause, MAX_TEXT, secrets),
      confidence: CONFIDENCE.has(raw?.confidence) ? raw.confidence : 'low',
      integrationTestRequired: raw?.integrationTestRequired === true,
      integrationTestReason: sanitizeText(raw?.integrationTestReason, MAX_ITEM, secrets),
    },
    dropped,
  };
}

/** Renders the sticky triage comment. `mode` is shown so a dry run is recognizable. */
export function renderComment(result, { mode, repo, sha }) {
  const fileLink = (f) => {
    const anchor = f.line ? `#L${f.line}` : '';
    const label = f.line ? `${f.path}:${f.line}` : f.path;
    const url = repo && sha ? `https://github.com/${repo}/blob/${sha}/${f.path}${anchor}` : null;
    return url ? `[\`${label}\`](${url})` : `\`${label}\``;
  };
  const suggested = [result.kind, ...result.areas, result.priority].filter(Boolean);
  const out = [
    MARKER,
    `### 🤖 AI triage${mode === 'dry-run' ? ' (dry run — labels not applied)' : ''}`,
    '',
    `**Summary.** ${result.summary || '_No summary._'}`,
    '',
    '| | |',
    '|---|---|',
    `| Type | ${result.type ?? '—'} |`,
    `| Suggested labels | ${suggested.length ? suggested.map((l) => `\`${l}\``).join(' ') : '—'} |`,
    `| Hotfix candidate | ${result.hotfixCandidate ? 'yes — maintainer decides' : 'no'} |`,
    `| Integration test | ${result.integrationTestRequired ? 'required' : 'not required'}${result.integrationTestReason ? ` — ${result.integrationTestReason}` : ''} |`,
    `| Confidence | ${result.confidence} |`,
  ];
  if (result.reproduction.length) {
    out.push('', '**Reproduction**', ...result.reproduction.map((s, i) => `${i + 1}. ${s}`));
  }
  if (result.affectedFiles.length) {
    out.push('', '**Likely affected code**', ...result.affectedFiles.map((f) => `- ${fileLink(f)}${f.why ? ` — ${f.why}` : ''}`));
  }
  if (result.suspectedCause) out.push('', `**Suspected cause.** ${result.suspectedCause}`);
  if (result.needsInfo && result.questions.length) {
    out.push('', '**Missing information**', ...result.questions.map((q) => `- [ ] ${q}`));
  }
  if (MAINTAINER_ONLY_PRIORITIES.includes(result.priority)) {
    out.push('', `> ⚠️ Suggested \`${result.priority}\` — a maintainer must confirm it; the agent does not apply it.`);
  }
  out.push('', '<sub>Generated from the issue text and a read-only scan of the code. Edit the issue to re-run.</sub>');
  return out.join('\n');
}

/**
 * Decides the GitHub writes. Never removes a label and never overrides a human decision:
 * a kind/priority/type that is already set wins over the agent's suggestion.
 */
export function planActions(result, issue, mode, taxonomy) {
  const present = new Set(issue.labels ?? []);
  const kindNames = new Set(taxonomy.kinds.map((k) => k.name));
  const hasKind = [...present].some((l) => kindNames.has(l));
  const hasPriority = [...present].some((l) => l.startsWith('priority:'));

  const addLabels = [];
  if (result.kind && !hasKind) addLabels.push(result.kind);
  for (const a of result.areas) if (!present.has(a)) addLabels.push(a);
  if (result.priority && !hasPriority && !MAINTAINER_ONLY_PRIORITIES.includes(result.priority)) addLabels.push(result.priority);
  if (result.needsInfo && !present.has(NEEDS_INFO_LABEL)) addLabels.push(NEEDS_INFO_LABEL);
  if (!present.has(TRIAGED_LABEL)) addLabels.push(TRIAGED_LABEL);

  const setType = result.type && !issue.type ? result.type : null;
  return mode === 'apply' ? { addLabels, setType } : { addLabels: [], setType: null };
}

/** Sweep selection: open issues that are neither triaged nor already carry a bot triage comment. */
export function needsTriage(issue, botCommented) {
  if (issue.pull_request) return false;
  if ((issue.labels ?? []).includes(TRIAGED_LABEL)) return false;
  return !botCommented;
}

export const isTriageComment = (c) => c?.user?.login === BOT_LOGIN && String(c?.body ?? '').includes(MARKER);
