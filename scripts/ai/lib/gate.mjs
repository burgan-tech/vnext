// Quality Gate core (docs/governance/ai-engineering-pipeline.md §8). The CI evidence is evaluated
// here, deterministically; the agent only reviews the diff and can only ADD blocking findings.
// Every GitHub write is decided by evaluateEvidence -> validateReview -> decideVerdict ->
// planGateLabels / renderGateComment. Dependency-free: Node built-ins only.
import { createHash } from 'node:crypto';
import { sanitizeText } from '../../triage/lib/triage.mjs';

export const GATE_MARKER = '<!-- ai-quality-gate:v1 -->';
const DATA_RE = /<!-- ai-quality-gate-data:(\{[^\n]*?\}) -->/;
const BOT_LOGINS = Object.freeze(['github-actions[bot]']);
const OK = new Set(['success', 'neutral']);
const FAILED = new Set(['failure', 'cancelled', 'timed_out', 'action_required', 'startup_failure', 'stale']);
const GATE_STATES = Object.freeze(['ai:pr-ready', 'ai:reviewing', 'ai:fixing', 'ai:changes-required', 'ai:human-approval']);
const MAX_FINDINGS = 15;

export const isGateComment = (c) => BOT_LOGINS.includes(c?.user?.login) && String(c?.body ?? '').includes(GATE_MARKER);

export function readGateData(body) {
  const m = DATA_RE.exec(String(body ?? ''));
  if (!m) return null;
  try {
    return JSON.parse(m[1]);
  } catch {
    return null;
  }
}

/** A base branch the gate reviews: an exact name or a `prefix*` pattern from gate.json. */
export function isReviewedBase(base, patterns) {
  return patterns.some((p) => (p.endsWith('*') ? base.startsWith(p.slice(0, -1)) : base === p));
}

/** The latest run per check name (re-runs leave older runs behind). */
function latestByName(runs) {
  const by = new Map();
  for (const r of runs ?? []) {
    const prev = by.get(r.name);
    if (!prev || String(r.started_at ?? '') > String(prev.started_at ?? '')) by.set(r.name, r);
  }
  return by;
}

function sonarVerdict(sonar, coverageMetrics) {
  if (!sonar) return null;
  const failed = (sonar.conditions ?? []).filter((c) => c.status === 'ERROR');
  const blockingIssues = (sonar.issues ?? []).filter((i) => i.type === 'BUG' || i.type === 'VULNERABILITY');
  const coverageOnly = failed.length > 0 && failed.every((c) => coverageMetrics.includes(c.metricKey));
  return { failed, blockingIssues, coverageOnly, status: sonar.status };
}

/**
 * Evaluates the CI evidence for the HEAD commit against gate.json.
 * Returns `complete` (false while anything required is missing or running), `blocking`,
 * `warnings`, and one `rows` entry per evidence item for the comment.
 */
export function evaluateEvidence({ checks, statuses, sonar, contract, isAiPr }, config) {
  const runs = latestByName(checks);
  const blocking = [];
  const warnings = [];
  const pending = [];
  const rows = [];
  const sv = sonarVerdict(sonar, config.sonar?.coverageMetrics ?? []);

  // A check that has not reported is only missing once nothing else is running for this SHA: until then
  // it is most likely produced by a run that is still queued (Sonar reports after `Build and analyze`).
  const ciRunning = [...runs.values()].some((r) => r.status !== 'completed') || (statuses ?? []).some((s) => s.state === 'pending');
  const notReported = ciRunning ? 'not reported yet — other CI for this SHA is still running' : 'not reported for this SHA — push a commit or re-run CI to produce it';
  for (const spec of config.checks) {
    const run = runs.get(spec.name);
    if (!run || run.status !== 'completed') {
      const state = run ? run.status : 'not reported';
      if (spec.required) pending.push(`${spec.name}: ${run ? state : notReported}`);
      rows.push({ name: spec.name, kind: spec.kind, result: state });
      continue;
    }
    const c = run.conclusion;
    let result = c;
    if (OK.has(c) || (c === 'skipped' && spec.skippedOk)) {
      result = c === 'skipped' ? 'skipped (counts as passed)' : 'passed';
    } else if (spec.kind === 'sonar' && sv?.coverageOnly && sv.blockingIssues.length === 0) {
      result = 'failed on new-code coverage only';
      warnings.push('COVERAGE_WARNING: the Sonar gate failed only on new-code coverage');
    } else if (spec.kind === 'sonar' && FAILED.has(c) && !sv) {
      result = 'failed (conditions not readable)';
      pending.push('Sonar: the gate failed but its conditions could not be read, so coverage-only cannot be told apart');
    } else if (spec.kind === 'sonar' && FAILED.has(c) && (sv.failed.length || sv.blockingIssues.length)) {
      result = 'failed';
      // The concrete conditions and issues are added below; no generic line on top of them.
    } else if (FAILED.has(c) || c === 'skipped') {
      result = 'failed';
      (spec.required ? blocking : warnings).push(`${spec.name} ${c}${spec.kind === 'tests' ? ' — the unit-test suite must be green (docs/testing/unit-tests.md)' : ''}`);
    }
    rows.push({ name: spec.name, kind: spec.kind, result });
  }

  for (const spec of config.statuses ?? []) {
    const s = (statuses ?? []).find((x) => x.context === spec.context);
    const state = s?.state ?? 'not reported';
    if (!s || state === 'pending') {
      if (spec.required) pending.push(`${spec.context}: ${state}`);
    } else if (state !== 'success') {
      (spec.required ? blocking : warnings).push(`${spec.context}: ${state}`);
    }
    rows.push({ name: spec.context, kind: spec.kind, result: state === 'success' ? 'passed' : state });
  }

  if (sv) {
    for (const i of sv.blockingIssues.slice(0, 10)) blocking.push(`Sonar ${i.type.toLowerCase()} ${i.rule} at ${i.component}:${i.line ?? '?'} — ${i.message}`);
    for (const c of sv.failed.filter((x) => !(config.sonar?.coverageMetrics ?? []).includes(x.metricKey))) {
      blocking.push(`Sonar gate condition ${c.metricKey} = ${c.actualValue} (threshold ${c.errorThreshold})`);
    }
  }

  for (const f of contract ?? []) {
    const aiRule = f.id.startsWith('PR-AI-');
    if (isAiPr && aiRule) blocking.push(`PR contract ${f.id}: ${f.message}`);
    else warnings.push(`PR contract ${f.id}: ${f.message}`);
  }

  return { complete: pending.length === 0, pending, blocking: [...new Set(blocking)], warnings: [...new Set(warnings)], rows };
}

/** Stable hash of the evidence the verdict depends on; an unchanged fingerprint means no re-review. */
export function fingerprint({ sha, checks, statuses, sonar, contract, labels }) {
  const runs = [...latestByName(checks).values()].map((r) => `${r.name}:${r.status}:${r.conclusion ?? ''}`).sort();
  const st = (statuses ?? []).map((s) => `${s.context}:${s.state}`).sort();
  const so = sonar ? [sonar.status, ...(sonar.conditions ?? []).map((c) => `${c.metricKey}:${c.status}`).sort(), ...(sonar.issues ?? []).map((i) => i.key).sort()] : ['no-sonar'];
  const ct = (contract ?? []).map((f) => f.id).sort();
  const lb = (labels ?? []).filter((l) => l.startsWith('ai:')).sort();
  return createHash('sha256').update(JSON.stringify({ sha, runs, st, so, ct, lb })).digest('hex').slice(0, 16);
}

/** Validates the agent's review; findings must point at a changed file. */
export function validateReview(raw, { changedFiles = [], secrets = [] } = {}) {
  const changed = new Set(changedFiles);
  const findings = [];
  const dropped = [];
  for (const f of Array.isArray(raw?.findings) ? raw.findings : []) {
    const file = String(f?.file ?? '').replace(/^\.\//, '');
    if (file && !changed.has(file)) {
      dropped.push(`finding:${file}`);
      continue;
    }
    findings.push({
      severity: f?.severity === 'blocking' ? 'blocking' : 'warning',
      title: sanitizeText(f?.title, 160, secrets),
      file,
      line: Number.isInteger(f?.line) && f.line > 0 ? f.line : null,
      detail: sanitizeText(f?.detail, 800, secrets),
      fix: sanitizeText(f?.fix, 600, secrets),
    });
    if (findings.length === MAX_FINDINGS) break;
  }
  const acceptance = (Array.isArray(raw?.acceptance) ? raw.acceptance : []).slice(0, 15).map((a) => ({
    criterion: sanitizeText(a?.criterion, 300, secrets),
    met: a?.met === true || a?.met === false ? a.met : 'unknown',
    evidence: sanitizeText(a?.evidence, 400, secrets),
  }));
  return {
    review: {
      summary: sanitizeText(raw?.summary, 1200, secrets),
      findings,
      acceptance,
      confidence: ['low', 'medium', 'high'].includes(raw?.confidence) ? raw.confidence : 'low',
    },
    dropped,
  };
}

/**
 * Verdict: INCONCLUSIVE while evidence is incomplete; COUNCIL when an AI PR declared low impact
 * but touches a high-impact path; CHANGES_REQUIRED on any blocking evidence, blocking finding or
 * unmet criterion; else PASS. A missing review (agent failed) is INCONCLUSIVE, never PASS.
 */
export function decideVerdict(evaluation, review, { impactHits = [], isAiPr = false } = {}) {
  if (!evaluation.complete) return { verdict: 'INCONCLUSIVE', reasons: evaluation.pending };
  if (isAiPr && impactHits.length) return { verdict: 'COUNCIL', reasons: impactHits.map((h) => `diff exceeds declared impact: ${h.id} (${h.paths.slice(0, 3).join(', ')})`) };
  if (!review) return { verdict: 'INCONCLUSIVE', reasons: ['the review agent returned no usable result'] };
  const reasons = [
    ...evaluation.blocking,
    ...review.findings.filter((f) => f.severity === 'blocking').map((f) => `${f.title}${f.file ? ` (${f.file}${f.line ? `:${f.line}` : ''})` : ''}`),
    ...review.acceptance.filter((a) => a.met === false).map((a) => `acceptance criterion not met: ${a.criterion}`),
  ];
  return reasons.length ? { verdict: 'CHANGES_REQUIRED', reasons } : { verdict: 'PASS', reasons: [] };
}

/** Repair bookkeeping: SHAs that got CHANGES_REQUIRED; the attempt count is their number. */
export function nextRepairs(previous, verdict, sha) {
  const shas = [...(previous?.repairShas ?? [])];
  if (verdict === 'CHANGES_REQUIRED' && !shas.includes(sha)) shas.push(sha);
  return shas.slice(-10);
}

/**
 * Issue labels for an AI PR. Human PRs get none. A human ai:blocked is never touched.
 * Over the repair limit, CHANGES_REQUIRED becomes ai:blocked.
 */
export function planGateLabels(verdict, issueLabels, { isAiPr, attempts, maxRepairs }) {
  const present = new Set(issueLabels ?? []);
  if (!isAiPr || present.has('ai:blocked') || verdict === 'INCONCLUSIVE') return { add: [], remove: [], next: null };
  let add;
  if (verdict === 'PASS') add = ['ai:human-approval'];
  else if (verdict === 'COUNCIL') add = ['ai:impact-high', 'ai:council-required'];
  else add = attempts > maxRepairs ? ['ai:blocked'] : ['ai:changes-required'];
  const removeSet = new Set(GATE_STATES.filter((l) => !add.includes(l)));
  if (verdict === 'COUNCIL') removeSet.add('ai:impact-low');
  return { add: add.filter((l) => !present.has(l)), remove: [...present].filter((l) => removeSet.has(l)), next: add[add.length - 1] };
}

const esc = (s) => String(s).replaceAll('|', String.raw`\|`);

export function renderGateComment(ctx) {
  const { pr, sha, verdict, reasons, evaluation, review, labels, mode, attempts, maxRepairs, isAiPr, fp, repairShas, risk } = ctx;
  const data = { sha, verdict, fp, repairShas };
  const icon = { PASS: '✅', CHANGES_REQUIRED: '❌', INCONCLUSIVE: '⏳', COUNCIL: '🏛️' }[verdict];
  const out = [
    GATE_MARKER,
    `<!-- ai-quality-gate-data:${JSON.stringify(data)} -->`,
    `### ${icon} AI Quality Gate — ${verdict}${mode === 'dry-run' ? ' (dry run — labels not applied)' : ''}`,
    '',
    '| | |',
    '|---|---|',
    `| PR | #${pr} |`,
    `| Reviewed SHA | \`${sha.slice(0, 12)}\` |`,
    `| Risk | ${risk ?? '—'} |`,
    `| Pipeline PR | ${isAiPr ? `yes — repair attempts ${attempts}/${maxRepairs}` : 'no — review only, no labels change'} |`,
  ];
  if (labels.add.length || labels.remove.length) {
    const verb = mode === 'dry-run' ? 'would ' : '';
    out.push(`| Issue labels | ${verb}add ${labels.add.map((l) => `\`${l}\``).join(' ') || '—'}; ${verb}remove ${labels.remove.map((l) => `\`${l}\``).join(' ') || '—'} |`);
  }
  out.push('', '**CI evidence** (read for this SHA; never inferred from one another)', '', '| Check | Kind | Result |', '|---|---|---|');
  for (const r of evaluation.rows) out.push(`| ${esc(r.name)} | ${r.kind} | ${esc(r.result)} |`);
  if (reasons.length) out.push('', `**${verdict === 'PASS' ? 'Notes' : 'Why'}**`, ...reasons.map((r) => `- ${esc(r)}`));
  if (evaluation.warnings.length) out.push('', '**Non-blocking warnings**', ...evaluation.warnings.map((w) => `- ${esc(w)}`));
  if (review) {
    if (review.summary) out.push('', `**Review.** ${review.summary} _(confidence: ${review.confidence})_`);
    if (review.acceptance.length) {
      const mark = (m) => ({ true: '✅', false: '❌' })[String(m)] ?? '❔';
      out.push('', '**Acceptance criteria**', ...review.acceptance.map((a) => `- ${mark(a.met)} ${a.criterion}${a.evidence ? ` — ${a.evidence}` : ''}`));
    }
    const blocking = review.findings.filter((f) => f.severity === 'blocking');
    const warns = review.findings.filter((f) => f.severity !== 'blocking');
    const fmt = (f) => `- **${f.title}**${f.file ? ` — \`${f.file}${f.line ? `:${f.line}` : ''}\`` : ''}${f.detail ? `: ${f.detail}` : ''}${f.fix ? `\n  - Fix: ${f.fix}` : ''}`;
    if (blocking.length) out.push('', '**Required developer actions**', ...blocking.map(fmt));
    if (warns.length) out.push('', '**Review warnings**', ...warns.map(fmt));
  }
  out.push('', `<sub>Human approval is always required to merge. The gate never merges, deploys or lowers a threshold.</sub>`);
  return out.join('\n');
}
