// Guards for PRs made by the AI engineering pipeline (docs/governance/ai-engineering-pipeline.md §3, §7.3, §9).
// Pure and dependency-free: the caller passes the PR, its parsed sections and the impact path rules.
import { isBlank } from './pr-body.mjs';

const SECTION = 'ai decision record';
const CHANGE_LEVELS = ['ai:semver-patch', 'ai:semver-minor', 'ai:semver-major'];
const IMPACTS = ['ai:impact-low', 'ai:impact-high'];
const REQUIRED_CHECK_ROWS = ['build', 'unit tests', 'e2e'];

/** A PR is an AI PR when it carries any `ai:` label; the pipeline's trusted writer sets them. */
export const isAiPr = (labels) => (labels ?? []).some((l) => l.startsWith('ai:'));

/** Compiles the rule list of `.github/ai/impact-paths.json`. */
export function compileImpactRules(json) {
  return (json?.rules ?? []).map((r) => ({ id: r.id, reason: r.reason, re: new RegExp(r.pattern) }));
}

/** Each high-impact rule that a changed path matches, with the first matching paths. */
export function matchImpactPaths(changedFiles, rules) {
  const hits = [];
  for (const rule of rules) {
    const paths = (changedFiles ?? []).filter((f) => rule.re.test(f));
    if (paths.length) hits.push({ id: rule.id, reason: rule.reason, paths });
  }
  return hits;
}

const field = (text, name) => new RegExp(String.raw`^\s*[-*]?\s*${name}\s*:\s*(.+)$`, 'im').exec(text)?.[1].trim() ?? null;

function subsection(text, heading) {
  const lines = text.split('\n');
  const start = lines.findIndex((l) => new RegExp(String.raw`^###\s+${heading}\s*$`, 'i').test(l));
  if (start < 0) return null;
  const rest = lines.slice(start + 1);
  const end = rest.findIndex((l) => /^###\s/.test(l));
  return (end < 0 ? rest : rest.slice(0, end)).join('\n').trim();
}

/** Lower-cased first cells of a markdown table, header and separator excluded. */
function tableRows(text) {
  return text
    .split('\n')
    .filter((l) => /^\s*\|/.test(l) && !/^\s*\|\s*:?-{3,}/.test(l))
    .slice(1)
    .map((l) => l.split('|')[1]?.trim().toLowerCase() ?? '');
}

/**
 * Checks an AI PR. `add(id, message, severity?)` records a finding.
 * @param {{labels?:string[], changedFiles?:string[]}} pr
 * @param {Map<string,string>} sections  parsed H2 sections of the body
 * @param {{id:string, reason:string, re:RegExp}[]} impactRules
 */
export function checkAiRecord(pr, sections, impactRules, add) {
  const labels = pr.labels ?? [];
  const one = (group, what) => {
    const found = group.filter((l) => labels.includes(l));
    if (found.length !== 1) add('PR-AI-LABELS', `An AI PR carries exactly one ${what} label (${group.join(', ')}); found ${found.length ? found.join(', ') : 'none'}.`);
    return found.length === 1 ? found[0] : null;
  };
  const level = one(CHANGE_LEVELS, 'change-level');
  const impact = one(IMPACTS, 'impact');

  if (level === 'ai:semver-major') {
    add('PR-AI-GUARD', 'A major (breaking) change is never implemented by the AI pipeline; a human takes it over (ai:blocked).');
  }

  if (impact === 'ai:impact-low') {
    const hits = matchImpactPaths(pr.changedFiles, impactRules);
    if (hits.length) {
      const list = hits.map((h) => `${h.id} — ${h.reason} (${h.paths.slice(0, 3).join(', ')}${h.paths.length > 3 ? ', …' : ''})`).join('; ');
      add('PR-AI-IMPACT', `Declared ai:impact-low, but the diff touches high-impact paths: ${list}. Re-classify as ai:impact-high; the council runs if a trigger fires.`);
    }
  }

  const councilRequired = labels.includes('ai:council-required');
  if (councilRequired && !labels.includes('ai:council-approved')) {
    add('PR-AI-COUNCIL', 'ai:council-required is set but the PR has no ai:council-approved label; development needs an APPROVED council verdict first.');
  }

  const text = sections.get(SECTION);
  if (text === undefined || isBlank(text)) {
    add('PR-AI-RECORD', 'An AI PR needs a filled "## AI decision record" section (template: docs/governance/ai-engineering-pipeline.md §9).');
    return;
  }

  if (!/#\d+/.test(field(text, 'Issue') ?? '')) add('PR-AI-RECORD', 'AI decision record: the `Issue:` line must reference the issue as `#<n>`.');

  const classification = field(text, 'Classification') ?? '';
  const bodyLevel = /semver-(patch|minor|major)/i.exec(classification)?.[1]?.toLowerCase();
  const bodyImpact = /impact-(low|high)/i.exec(classification)?.[1]?.toLowerCase();
  if (!bodyLevel || !bodyImpact) {
    add('PR-AI-RECORD', 'AI decision record: the `Classification:` line must state `semver-<patch|minor|major>` and `impact-<low|high>`.');
  } else if ((level && level !== `ai:semver-${bodyLevel}`) || (impact && impact !== `ai:impact-${bodyImpact}`)) {
    add('PR-AI-RECORD', `AI decision record: the Classification line (semver-${bodyLevel}, impact-${bodyImpact}) disagrees with the PR labels (${[level, impact].filter(Boolean).join(', ')}).`);
  }

  const council = field(text, 'Council') ?? '';
  if (!council || /^<.*>$/.test(council)) {
    add('PR-AI-RECORD', 'AI decision record: the `Council:` line must say "not required — <reason>" or give the verdict and session link.');
  } else if (councilRequired && !(/\bAPPROVED\b/.test(council) && /(https?:\/\/|docs\/agent-council\/|ai-docs\/agent-council\/)/.test(council))) {
    add('PR-AI-RECORD', 'AI decision record: with ai:council-required the `Council:` line must state the APPROVED verdict and link the council session.');
  }

  const checks = subsection(text, 'Done / skipped');
  if (checks === null) {
    add('PR-AI-RECORD', 'AI decision record: the "### Done / skipped" table is missing.');
  } else {
    const rows = tableRows(checks);
    const missing = REQUIRED_CHECK_ROWS.filter((r) => !rows.some((row) => row.startsWith(r)));
    if (missing.length) add('PR-AI-RECORD', `AI decision record: the Done / skipped table needs rows for ${missing.join(', ')}.`);
  }

  const local = subsection(text, 'Test it locally');
  if (local === null || isBlank(local)) {
    add('PR-AI-RECORD', 'AI decision record: the "### Test it locally" steps are missing; a reviewer must be able to pull and test the branch.');
  }
}
