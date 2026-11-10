// Architecture Council core (docs/governance/ai-engineering-pipeline.md §3). The agent only returns
// JSON; everything that reaches GitHub goes through preDecide -> validateCouncil -> decideCouncil ->
// councilLabels / renderCouncilComment here, so issue text (untrusted: public repo) can never pick
// the verdict's labels, approve a breaking change or carry a secret into a comment.
// Dependency-free: Node built-ins only.
import { createHash } from 'node:crypto';
import { sanitizeText } from '../../triage/lib/triage.mjs';
import { BOT_LOGINS, WORKFLOW_STATES, readPlanData } from './director.mjs';

export const COUNCIL_MARKER = '<!-- ai-council:v1 -->';
const DATA_RE = /<!-- ai-council-data:(\{[^\n]*?\}) -->/;

export const VERDICTS = Object.freeze([
  'APPROVED', 'APPROVE_WITH_CONDITIONS', 'EXPERIMENT_REQUIRED', 'CLARIFICATION_REQUIRED', 'BLOCKED', 'REJECTED',
]);
const VOTES = new Set(['approve', 'conditions', 'experiment', 'clarify', 'block', 'reject']);
const CONFIDENCE = new Set(['low', 'medium', 'high']);
const RESOLUTIONS = new Set(['accepted', 'countered', 'open']);
const CORE_ROLES = Object.freeze(['Solution Architect', 'Pragmatic Engineer', 'Test & Evidence']);
const MAX_TEXT = 1500;
const MAX_ITEM = 500;
const MAX_LIST = 12;
const MAX_CELL = 300;

export const isCouncilComment = (c) => BOT_LOGINS.includes(c?.user?.login) && String(c?.body ?? '').includes(COUNCIL_MARKER);

/** `{ verdict, fp }` stored in an earlier council comment, or null. */
export function readCouncilData(body) {
  const m = DATA_RE.exec(String(body ?? ''));
  if (!m) return null;
  try {
    const d = JSON.parse(m[1]);
    return typeof d.fp === 'string' ? d : null;
  } catch {
    return null;
  }
}

/**
 * What the council judged: the issue text, the Director plan, the human comments and the
 * classification labels. The council's own label writes do not change it, so an unchanged proposal
 * is never judged twice.
 */
export function councilFingerprint({ title, body, directorPlan, comments, labels }) {
  const cls = (labels ?? []).filter((l) => /^ai:(semver|impact)-/.test(l)).sort();
  const text = JSON.stringify([title ?? '', body ?? '', directorPlan ?? '', (comments ?? []).map((c) => c.body ?? ''), cls]);
  return createHash('sha256').update(text).digest('hex').slice(0, 16);
}

/**
 * Decides before the agent runs: a blocked or already approved issue is left alone; an issue
 * without a Director plan cannot be judged (`no-plan`, comment only); an unchanged proposal is
 * skipped unless forced; otherwise the agent runs (`council`).
 */
export function preDecide(input, { force = false } = {}) {
  const labels = input.labels ?? [];
  if (labels.includes('ai:blocked')) return 'blocked';
  if (labels.includes('ai:council-approved') && !force) return 'approved';
  if (!force && input.previousCouncil && readCouncilData(input.previousCouncil)?.fp === input.fp) return 'unchanged';
  if (!input.directorPlan) return 'no-plan';
  return 'council';
}

const text = (v, secrets, max = MAX_TEXT) => sanitizeText(v, max, secrets);
const list = (value, secrets, max = MAX_LIST) =>
  (Array.isArray(value) ? value : []).map((v) => sanitizeText(v, MAX_ITEM, secrets)).filter(Boolean).slice(0, max);
const objects = (value, map, max = MAX_LIST) => (Array.isArray(value) ? value : []).map(map).filter(Boolean).slice(0, max);

/** Validates and sanitizes the agent's answer. Unknown values fall back to the strictest choice. */
export function validateCouncil(raw, { secrets = [] } = {}) {
  const dropped = [];
  const verdict = VERDICTS.includes(raw?.verdict) ? raw.verdict : 'CLARIFICATION_REQUIRED';
  if (verdict !== raw?.verdict) dropped.push(`verdict:${raw?.verdict}`);
  const result = {
    verdict,
    summary: text(raw?.summary, secrets),
    problem: text(raw?.problem, secrets),
    constraints: list(raw?.constraints, secrets),
    facts: list(raw?.facts, secrets),
    assumptions: list(raw?.assumptions, secrets),
    council: objects(raw?.council, (r) => {
      const role = text(r?.role, secrets, 60);
      if (!role) return null;
      return { role, why: text(r?.why, secrets, MAX_ITEM), position: text(r?.position, secrets), vote: VOTES.has(r?.vote) ? r.vote : 'clarify' };
    }, 8),
    objections: objects(raw?.objections, (o) => {
      const claim = text(o?.claim, secrets, MAX_ITEM);
      if (!claim) return null;
      return { by: text(o?.by, secrets, 60), against: text(o?.against, secrets, 80), claim, evidence: text(o?.evidence, secrets, MAX_ITEM), resolution: RESOLUTIONS.has(o?.resolution) ? o.resolution : 'open' };
    }),
    approaches: objects(raw?.approaches, (a) => {
      const name = text(a?.name, secrets, 80);
      if (!name) return null;
      return { name, summary: text(a?.summary, secrets, MAX_ITEM), tradeoffs: text(a?.tradeoffs, secrets, MAX_ITEM) };
    }, 5),
    recommended: text(raw?.recommended, secrets),
    securityPerformance: text(raw?.securityPerformance, secrets),
    rollout: text(raw?.rollout, secrets),
    tests: list(raw?.tests, secrets),
    risks: list(raw?.risks, secrets),
    conditions: objects(raw?.conditions, (c, i) => {
      const t = text(c?.text, secrets, MAX_ITEM);
      if (!t) return null;
      return { id: text(c?.id, secrets, 12) || `C-${i + 1}`, text: t, owner: c?.owner === 'developer' ? 'developer' : 'human' };
    }),
    humanDecisions: list(raw?.humanDecisions, secrets),
    dissent: list(raw?.dissent, secrets),
    confidence: CONFIDENCE.has(raw?.confidence) ? raw.confidence : 'low',
  };
  return { result, dropped };
}

/**
 * Final verdict. The agent can make it stricter, never looser: an approval with a human condition
 * or decision becomes APPROVE_WITH_CONDITIONS; a low-confidence approval, or one missing a core
 * role, becomes CLARIFICATION_REQUIRED; a major (breaking) change is BLOCKED. No result at all is
 * INCONCLUSIVE (nothing changes). Returns the verdict, why it differs from the agent's, and
 * whether development may start.
 */
export function decideCouncil(result, { labels = [], directorPlan = null } = {}) {
  if (!result) return { verdict: 'INCONCLUSIVE', reasons: ['the council produced no usable result'], develop: false };
  let verdict = result.verdict;
  const reasons = [];
  const plan = readPlanData(directorPlan);
  const major = labels.includes('ai:semver-major') || plan?.level === 'major';
  if (major && verdict !== 'REJECTED') {
    if (verdict !== 'BLOCKED') reasons.push(`${verdict} → BLOCKED: a major (breaking) change is never developed by AI`);
    verdict = 'BLOCKED';
  }
  if (verdict === 'APPROVED') {
    const missing = CORE_ROLES.filter((r) => !result.council.some((m) => m.role.toLowerCase().startsWith(r.toLowerCase())));
    if (result.confidence === 'low') {
      verdict = 'CLARIFICATION_REQUIRED';
      reasons.push('APPROVED → CLARIFICATION_REQUIRED: low confidence is never an approval');
    } else if (missing.length) {
      verdict = 'CLARIFICATION_REQUIRED';
      reasons.push(`APPROVED → CLARIFICATION_REQUIRED: core role(s) missing: ${missing.join(', ')}`);
    } else if (result.conditions.some((c) => c.owner === 'human') || result.humanDecisions.length) {
      verdict = 'APPROVE_WITH_CONDITIONS';
      reasons.push('APPROVED → APPROVE_WITH_CONDITIONS: a condition or decision needs a human');
    }
  }
  return { verdict, reasons, develop: verdict === 'APPROVED' };
}

/**
 * Label writes for the issue. APPROVED → `ai:council-approved` + `ai:planned` (the Developer
 * starts); any other verdict → `ai:blocked` (a human decides); INCONCLUSIVE → nothing.
 * `ai:council-required` stays: the Developer requires it together with `ai:council-approved`.
 */
export function councilLabels(verdict, issueLabels) {
  const present = new Set(issueLabels ?? []);
  if (verdict === 'INCONCLUSIVE') return { add: [], remove: [], next: null };
  const want = verdict === 'APPROVED' ? ['ai:council-approved', 'ai:planned'] : ['ai:blocked'];
  const next = want.at(-1);
  const remove = [...present].filter((l) => !want.includes(l) && (WORKFLOW_STATES.includes(l) || l === 'ai:council-approved'));
  return { add: want.filter((l) => !present.has(l)), remove, next };
}

const cell = (v) => {
  const t = String(v ?? '').replace(/\s+/g, ' ').replaceAll('|', '/').trim();
  return t.length > MAX_CELL ? `${t.slice(0, MAX_CELL - 1)}…` : t || '—';
};

/** The row for docs/agent-council/sessions/README.md. The session link is the council comment. */
export function councilLogRow(result, decision, { date, issue, title, url, risk }) {
  const conditions = result.conditions.map((c) => `${c.id} (${c.owner}) ${c.text}`).join('; ');
  return `| ${date} | [issue-${issue}-council](${url}) | ${cell(title)} | ${cell(risk ? risk[0].toUpperCase() + risk.slice(1) : 'High')} | \`${decision.verdict}\` (CI council) | ${cell(result.recommended || result.summary)} | ${cell(conditions)} |`;
}

const bullets = (items) => items.map((i) => `- ${i}`);
const section = (title, lines) => (lines.length ? ['', `**${title}**`, ...lines] : []);
const VERDICT_ICON = { APPROVED: '✅', APPROVE_WITH_CONDITIONS: '🟡', EXPERIMENT_REQUIRED: '🧪', CLARIFICATION_REQUIRED: '❓', BLOCKED: '⛔', REJECTED: '❌', INCONCLUSIVE: '⏳' };

/** The sticky council comment. Carries the verdict and the input fingerprint for idempotency. */
export function renderCouncilComment(ctx) {
  const { issue, result, decision, labels, mode, fp, logRow, note } = ctx;
  const data = { verdict: decision.verdict, fp };
  const labelText = `${mode === 'dry-run' ? 'would ' : ''}add ${labels.add.map((l) => `\`${l}\``).join(' ') || '—'}; ${mode === 'dry-run' ? 'would ' : ''}remove ${labels.remove.map((l) => `\`${l}\``).join(' ') || '—'}`;
  const out = [
    COUNCIL_MARKER,
    `<!-- ai-council-data:${JSON.stringify(data)} -->`,
    `### 🏛️ Architecture Council — ${VERDICT_ICON[decision.verdict] ?? ''} \`${decision.verdict}\`${mode === 'dry-run' ? ' (dry run — labels not applied)' : ''}`,
    '',
  ];
  if (!result) {
    out.push(note ?? 'The council did not produce a usable result; nothing changed.');
    out.push('', `<sub>Issue #${issue} · labels unchanged. Re-run the AI Council workflow from the Actions tab; an hourly sweep retries once the input changes.</sub>`);
    return out.join('\n');
  }
  out.push(
    `**Summary.** ${result.summary || '_No summary._'}`,
    '',
    '| | |',
    '|---|---|',
    `| Verdict | \`${decision.verdict}\`${decision.reasons.length ? ` — the agent said \`${result.verdict}\`; ${decision.reasons.join('; ')}` : ''} |`,
    `| Development | ${decision.develop ? 'may start (the Developer picks up `ai:planned`)' : 'does not start — a human decides'} |`,
    `| Council | ${result.council.map((m) => `${m.role} (${m.vote})`).join(' · ') || '—'} |`,
    `| Next state | ${labels.next ? `\`${labels.next}\`` : '—'} |`,
    `| Labels | ${labelText} |`,
    `| Confidence | ${result.confidence} |`,
  );
  out.push(
    ...section('Problem', result.problem ? [result.problem] : []),
    ...section('Design constraints', bullets(result.constraints)),
    ...section('Verified facts', bullets(result.facts)),
    ...section('Assumptions', bullets(result.assumptions)),
    ...section('Positions', result.council.map((m) => `- **${m.role}** (${m.vote})${m.why ? ` — _${m.why}_` : ''}: ${m.position}`)),
    ...section('Objections', result.objections.map((o) => `- ${o.by} → ${o.against}: ${o.claim}${o.evidence ? ` _(evidence: ${o.evidence})_` : ''} — ${o.resolution}`)),
    ...section('Approaches', result.approaches.map((a) => `- **${a.name}** — ${a.summary}${a.tradeoffs ? ` _Trade-offs:_ ${a.tradeoffs}` : ''}`)),
    ...section('Recommended', result.recommended ? [result.recommended] : []),
    ...section('Security and performance', result.securityPerformance ? [result.securityPerformance] : []),
    ...section('Rollout and rollback', result.rollout ? [result.rollout] : []),
    ...section('Tests', bullets(result.tests)),
    ...section('Risks', bullets(result.risks)),
    ...section('Conditions', result.conditions.map((c) => `- [ ] **${c.id}** (${c.owner}) ${c.text}`)),
    ...section('Decisions for a human', result.humanDecisions.map((d) => `- [ ] ${d}`)),
    ...section('Dissent', bullets(result.dissent)),
  );
  if (logRow) out.push('', '<details><summary>Decision log row (<code>docs/agent-council/sessions/README.md</code>)</summary>', '', '```markdown', logRow, '```', '', '</details>');
  out.push('', `<sub>Issue #${issue} · advisory: a council verdict never authorizes a merge or deploy. \`APPROVED\` lets the Developer start; the Developer's PR adds the log row. Add \`ai:blocked\` to stop the pipeline.</sub>`);
  return out.join('\n');
}
