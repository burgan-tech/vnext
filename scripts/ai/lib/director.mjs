// Engineering Director core (docs/governance/ai-engineering-pipeline.md §3–§5). The agent only
// returns JSON; everything that reaches GitHub goes through preDecide -> validatePlan ->
// classify -> planLabels / renderPlanComment here, so issue text (untrusted: public repo) can never
// pick a label, skip the council or carry a secret into a comment.
// Dependency-free: Node built-ins only.
import { sanitizeText } from '../../triage/lib/triage.mjs';

export const PLAN_MARKER = '<!-- ai-director-plan:v1 -->';
export const BOT_LOGINS = Object.freeze(['github-actions[bot]']);
const DATA_RE = /<!-- ai-director-data:(\{[^\n]*?\}) -->/;

export const WORKFLOW_STATES = Object.freeze([
  'ai:ready', 'ai:replan', 'ai:planned', 'ai:developing', 'ai:pr-ready', 'ai:reviewing',
  'ai:changes-required', 'ai:fixing', 'ai:human-approval', 'ai:blocked', 'ai:done',
]);
const CLASS_LABELS = Object.freeze([
  'ai:semver-patch', 'ai:semver-minor', 'ai:semver-major', 'ai:impact-low', 'ai:impact-high',
  'ai:risk-low', 'ai:risk-medium', 'ai:risk-high', 'ai:risk-critical',
]);
export const CRITERIA = Object.freeze([
  'auth-security', 'migration', 'contract', 'core-process', 'distributed-consistency', 'companion-contract', 'ci-governance',
]);
/** High-impact criteria that call the council (§3). `core-process` alone is high impact without one. */
export const COUNCIL_CRITERIA = Object.freeze(['auth-security', 'migration', 'contract', 'distributed-consistency', 'companion-contract']);

const LEVELS = new Set(['patch', 'minor', 'major']);
const DECISIONS = new Set(['plan', 'resolved', 'needs-info']);
const CONFIDENCE = new Set(['low', 'medium', 'high']);
const MAX_TEXT = 1500;
const MAX_ITEM = 400;
const MAX_LIST = 12;

export const isPlanComment = (c) => BOT_LOGINS.includes(c?.user?.login) && String(c?.body ?? '').includes(PLAN_MARKER);

/** Classification stored in an earlier plan comment by renderPlanComment, or null. */
export function readPlanData(body) {
  const m = DATA_RE.exec(String(body ?? ''));
  if (!m) return null;
  try {
    const d = JSON.parse(m[1]);
    return LEVELS.has(d.level) && ['low', 'high'].includes(d.impact) ? d : null;
  } catch {
    return null;
  }
}

/**
 * Decides before the agent runs (routine states A, C, D): an open linked PR -> `active-pr`;
 * a still-valid plan and no `ai:replan` -> `reuse`; otherwise the agent plans (`plan`).
 * Closed, unmerged PRs are history and never stop planning.
 */
export function preDecide(issue) {
  if ((issue.linkedPrs ?? []).some((p) => p.state === 'open')) return 'active-pr';
  const replan = (issue.labels ?? []).includes('ai:replan');
  if (!replan && (issue.previousPlans ?? []).some((b) => readPlanData(b))) return 'reuse';
  return 'plan';
}

const list = (value, secrets, max = MAX_LIST) =>
  (Array.isArray(value) ? value : []).map((v) => sanitizeText(v, MAX_ITEM, secrets)).filter(Boolean).slice(0, max);

/** Validates and sanitizes the agent's answer. Unknown values fall back to the safest choice. */
export function validatePlan(raw, { fileExists = () => true, secrets = [] } = {}) {
  const dropped = [];
  const decision = DECISIONS.has(raw?.decision) ? raw.decision : 'needs-info';
  if (decision !== raw?.decision) dropped.push(`decision:${raw?.decision}`);
  const c = raw?.classification ?? {};
  const level = LEVELS.has(c.level) ? c.level : 'major';
  if (level !== c.level) dropped.push(`level:${c.level}`);
  const impact = c.impact === 'low' ? 'low' : 'high';
  const criteria = [...new Set((Array.isArray(c.criteria) ? c.criteria : []).filter((x) => CRITERIA.includes(x)))];
  const p = raw?.plan ?? {};
  const components = [];
  for (const comp of Array.isArray(p.components) ? p.components : []) {
    const given = String(comp?.path ?? '').trim();
    const path = given.replace(/^\.\//, '');
    if (!path || path.startsWith('/') || path.includes('..') || !fileExists(path)) {
      // A new file the plan creates does not exist yet: keep it, marked, unless it escapes the tree.
      if (path && !path.includes('..') && !path.startsWith('/')) components.push({ path, why: sanitizeText(comp?.why, MAX_ITEM, secrets), isNew: true });
      else dropped.push(`file:${path}`);
    } else {
      components.push({ path, why: sanitizeText(comp?.why, MAX_ITEM, secrets), isNew: false });
    }
    if (components.length === MAX_LIST) break;
  }
  return {
    result: {
      decision,
      summary: sanitizeText(raw?.summary, MAX_TEXT, secrets),
      classification: { level, impact, criteria, why: sanitizeText(c.why, MAX_TEXT, secrets) },
      plan: {
        problem: sanitizeText(p.problem, MAX_TEXT, secrets),
        rootCause: sanitizeText(p.rootCause, MAX_TEXT, secrets),
        components,
        strategy: list(p.strategy, secrets),
        acceptance: list(p.acceptance, secrets),
        tests: list(p.tests, secrets),
        e2eSuites: list(p.e2eSuites, secrets),
        securityCompatibility: sanitizeText(p.securityCompatibility, MAX_TEXT, secrets),
        companionProducts: (Array.isArray(p.companionProducts) ? p.companionProducts : [])
          .map((x) => ({ product: sanitizeText(x?.product, 60, secrets), why: sanitizeText(x?.why, MAX_ITEM, secrets) }))
          .filter((x) => x.product)
          .slice(0, MAX_LIST),
        dependencies: list(p.dependencies, secrets),
        definitionOfDone: list(p.definitionOfDone, secrets),
      },
      resolvedEvidence: sanitizeText(raw?.resolvedEvidence, MAX_TEXT, secrets),
      questions: list(raw?.questions, secrets),
      confidence: CONFIDENCE.has(raw?.confidence) ? raw.confidence : 'low',
    },
    dropped,
  };
}

/**
 * Final classification: the agent's claim, raised (never lowered) by the path rules matched
 * against the planned components. Returns the level/impact/risk, the criteria with their source,
 * whether the council runs and whether AI work is blocked, with reasons.
 */
export function classify(result, issueLabels, impactRules) {
  const { level } = result.classification;
  const criteria = new Map(result.classification.criteria.map((c) => [c, 'agent']));
  for (const rule of impactRules) {
    const hit = result.plan.components.find((comp) => rule.re.test(comp.path));
    if (hit && rule.criterion && !criteria.has(rule.criterion)) criteria.set(rule.criterion, `path rule ${rule.id}: ${hit.path}`);
  }
  const impact = result.classification.impact === 'high' || criteria.size > 0 ? 'high' : 'low';
  const risk = level === 'major' ? 'critical' : impact === 'high' ? 'high' : level === 'minor' ? 'medium' : 'low';

  const blocked = [];
  if (level === 'major') blocked.push('major (breaking) change — never implemented by AI');
  if (criteria.has('ci-governance')) blocked.push('needs a change under .github/ — the AI pipeline may not make it');
  if (result.decision === 'resolved') blocked.push('looks already fixed — a human confirms and closes');
  if (result.decision === 'needs-info') blocked.push('needs information from the reporter');

  const councilBy = [...criteria.keys()].filter((c) => COUNCIL_CRITERIA.includes(c));
  if ((issueLabels ?? []).includes('ai:council-required')) councilBy.push('ai:council-required set by a maintainer');
  return { level, impact, risk, criteria: [...criteria].map(([id, source]) => ({ id, source })), council: councilBy.length > 0, councilBy, blocked };
}

/**
 * Label writes for the issue. The next state is one of ai:blocked, ai:council-required (council
 * first) or ai:planned. A human-set ai:blocked survives unless the human asked for ai:replan.
 */
export function planLabels(cls, issueLabels) {
  const present = new Set(issueLabels ?? []);
  const humanBlocked = present.has('ai:blocked') && !present.has('ai:replan');
  const want = new Set([`ai:semver-${cls.level}`, `ai:impact-${cls.impact}`, `ai:risk-${cls.risk}`]);
  let next;
  if (humanBlocked || cls.blocked.length) next = 'ai:blocked';
  else if (cls.council) next = 'ai:council-required';
  else next = 'ai:planned';
  want.add(next);
  if (cls.blocked.some((b) => b.startsWith('needs information'))) want.add('needs-info');

  const remove = [...present].filter(
    (l) => !want.has(l) && (CLASS_LABELS.includes(l) || (WORKFLOW_STATES.includes(l) && !(humanBlocked && l === 'ai:blocked'))),
  );
  // ai:council-required is a request, not one of the states above, so it is never removed here.
  const add = [...want].filter((l) => !present.has(l));
  return { add, remove, next };
}

const bullets = (items) => items.map((i) => `- ${i}`);
const numbered = (items) => items.map((i, n) => `${n + 1}. ${i}`);

/** The sticky plan comment. Carries a machine-readable classification line for `reuse`. */
export function renderPlanComment(result, cls, labels, { mode, repo, sha, issue }) {
  const link = (c) => {
    const url = repo && sha && !c.isNew ? `https://github.com/${repo}/blob/${sha}/${c.path}` : null;
    const name = `\`${c.path}\`${c.isNew ? ' (new)' : ''}`;
    return url ? `[${name}](${url})` : name;
  };
  const p = result.plan;
  const data = { level: cls.level, impact: cls.impact, risk: cls.risk, council: cls.council, decision: result.decision };
  const out = [
    PLAN_MARKER,
    `<!-- ai-director-data:${JSON.stringify(data)} -->`,
    `### 🧭 Director plan${mode === 'dry-run' ? ' (dry run — labels not applied)' : ''}`,
    '',
    `**Summary.** ${result.summary || '_No summary._'}`,
    '',
    '| | |',
    '|---|---|',
    `| Decision | ${result.decision} |`,
    `| Classification | semver-${cls.level} · impact-${cls.impact} · risk-${cls.risk} |`,
    `| High-impact criteria | ${cls.criteria.length ? cls.criteria.map((c) => `\`${c.id}\` (${c.source})`).join('<br>') : 'none'} |`,
    `| Council | ${cls.council ? `required — ${cls.councilBy.join(', ')}` : 'not required'} |`,
    `| Next state | \`${labels.next}\`${cls.blocked.length ? ` — ${cls.blocked.join('; ')}` : ''} |`,
    `| Labels | ${mode === 'dry-run' ? 'would ' : ''}add ${labels.add.map((l) => `\`${l}\``).join(' ') || '—'}; ${mode === 'dry-run' ? 'would ' : ''}remove ${labels.remove.map((l) => `\`${l}\``).join(' ') || '—'} |`,
    `| Confidence | ${result.confidence} |`,
  ];
  if (result.classification.why) out.push('', `**Why this classification.** ${result.classification.why}`);
  if (result.decision === 'resolved') out.push('', `**Evidence it is already fixed.** ${result.resolvedEvidence || '_none given_'}`);
  if (result.questions.length) out.push('', '**Questions for the reporter**', ...result.questions.map((q) => `- [ ] ${q}`));
  if (result.decision === 'plan') {
    const section = (title, lines) => (lines.length ? ['', `**${title}**`, ...lines] : []);
    out.push(
      ...section('Problem', p.problem ? [p.problem] : []),
      ...section('Root-cause hypothesis', p.rootCause ? [p.rootCause] : []),
      ...section('Components', p.components.map((c) => `- ${link(c)}${c.why ? ` — ${c.why}` : ''}`)),
      ...section('Implementation strategy', numbered(p.strategy)),
      ...section('Acceptance criteria', p.acceptance.map((a) => `- [ ] ${a}`)),
      ...section('Tests', bullets(p.tests)),
      ...section('E2E suites (vnext-example)', bullets(p.e2eSuites)),
      ...section('Security and compatibility', p.securityCompatibility ? [p.securityCompatibility] : []),
      ...section('Companion products', p.companionProducts.map((c) => `- ${c.product}${c.why ? ` — ${c.why}` : ''}`)),
      ...section('Dependencies', bullets(p.dependencies)),
      ...section('Definition of done', p.definitionOfDone.map((d) => `- [ ] ${d}`)),
    );
  }
  out.push('', `<sub>Issue #${issue} · generated from the issue text and a read-only scan of the code. Add \`ai:replan\` to plan again; add \`ai:blocked\` to stop the pipeline.</sub>`);
  return out.join('\n');
}
