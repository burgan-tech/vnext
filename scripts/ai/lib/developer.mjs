// Autonomous Developer core (docs/governance/ai-engineering-pipeline.md §2, §9, §13). The agent edits
// the working tree and returns JSON; everything that reaches GitHub goes through decideMode ->
// validateResult -> checkDiff -> renderPrBody / developerLabels here, in a job the agent never ran in.
// Dependency-free: Node built-ins only.
import { sanitizeText } from '../../triage/lib/triage.mjs';
import { BOT_LOGINS, readPlanData } from './director.mjs';

export const DEV_MARKER = '<!-- ai-developer:v1 -->';
export const branchFor = (n) => `ai/issue-${n}`;
export const isDeveloperComment = (c) => BOT_LOGINS.includes(c?.user?.login) && String(c?.body ?? '').includes(DEV_MARKER);

export const PRODUCTS = Object.freeze([
  'vnext-schema', 'vnext-forge', 'vnext-helm-charts', 'vnext-docs', 'vnext-workflow-cli',
  'vnext-runtime', 'vnext-template', 'vnext-integration-test', 'vnext-example', 'vnext-ai-toolkit',
]);
const STOP_LABELS = Object.freeze(['ai:blocked', 'ai:done', 'ai:human-approval', 'ai:semver-major']);
const STATES_DONE_BY_PUSH = Object.freeze(['ai:ready', 'ai:replan', 'ai:planned', 'ai:developing', 'ai:changes-required', 'ai:fixing', 'ai:reviewing']);
const PR_LABELS = /^ai:(semver|impact|risk)-|^ai:council-(required|approved)$/;
const TITLE_RE = /^(feat|fix|perf|refactor|test|docs|chore)(\([a-z0-9-]+\))?: \S.{0,70}$/;
const DECISIONS = new Set(['implemented', 'needs-replan', 'no-change']);
const CONFIDENCE = new Set(['low', 'medium', 'high']);
const MAX_TEXT = 2000;
const MAX_ITEM = 1200;
const MAX_LIST = 15;

/**
 * Which run this is, decided before the agent: `NEW`, `RECOVERY` (earlier unmerged attempts), `FIX`
 * (the pipeline's own open PR with `ai:changes-required`), or `skip` with the reason. Another open
 * PR for the issue (a human's, or an older routine's) is never competed with.
 */
export function decideMode({ number, labels = [], directorPlan = null, openPrs = [], closedPrs = [], repairs = 0, maxRepairs = 3 }) {
  const skip = (reason) => ({ mode: 'skip', reason });
  const stop = STOP_LABELS.find((l) => labels.includes(l));
  if (stop) return skip(`labelled ${stop}`);
  if (!labels.includes('ai:planned') && !labels.includes('ai:changes-required')) return skip('neither ai:planned nor ai:changes-required');
  const plan = readPlanData(directorPlan);
  if (!plan) return skip('no Director plan to implement');
  if (plan.level === 'major') return skip('a major (breaking) change is never implemented by AI');
  if (labels.includes('ai:council-required') && !labels.includes('ai:council-approved')) return skip('ai:council-required without ai:council-approved');
  const ours = openPrs.find((p) => p.head === branchFor(number));
  const others = openPrs.filter((p) => p !== ours);
  if (labels.includes('ai:changes-required')) {
    if (!ours) return skip(`ai:changes-required but no open ${branchFor(number)} PR to fix`);
    if (repairs >= maxRepairs) return skip(`repair limit reached (${repairs}/${maxRepairs})`);
    return { mode: 'FIX', pr: ours };
  }
  if (ours) return skip(`PR #${ours.number} already implements it`);
  if (others.length) return skip(`open PR ${others.map((p) => `#${p.number}`).join(', ')} already works on it`);
  return { mode: closedPrs.length ? 'RECOVERY' : 'NEW' };
}

const text = (v, secrets, max = MAX_TEXT) => sanitizeText(v, max, secrets);
const list = (v, secrets, max = MAX_LIST) => (Array.isArray(v) ? v : []).map((x) => sanitizeText(x, MAX_ITEM, secrets)).filter(Boolean).slice(0, max);

/** Validates the agent's answer. A bad title or decision falls back to the safest choice. */
export function validateResult(raw, { secrets = [], mode = 'NEW' } = {}) {
  const dropped = [];
  const decision = DECISIONS.has(raw?.decision) ? raw.decision : 'needs-replan';
  if (decision !== raw?.decision) dropped.push(`decision:${raw?.decision}`);
  let title = text(raw?.title, secrets, 120).replace(/\s+/g, ' ');
  if (!TITLE_RE.test(title)) {
    dropped.push(`title:${title}`);
    title = '';
  }
  const productImpact = {};
  for (const p of PRODUCTS) {
    const e = raw?.productImpact?.[p];
    productImpact[p] = { needed: e?.needed === true, handoff: text(e?.handoff, secrets, 300) };
  }
  const meta = raw?.meta?.status === 'updated' ? { status: 'updated', reason: '' } : { status: 'not-needed', reason: text(raw?.meta?.reason, secrets, 400) };
  return {
    result: {
      decision,
      mode,
      title,
      commitMessage: text(raw?.commitMessage, secrets),
      summary: list(raw?.summary, secrets, 6),
      vnextScope: text(raw?.vnextScope, secrets),
      changes: list(raw?.changes, secrets, 25),
      testPlan: list(raw?.testPlan, secrets),
      e2eSuites: list(raw?.e2eSuites, secrets),
      productImpact,
      meta,
      conditions: (Array.isArray(raw?.conditions) ? raw.conditions : []).slice(0, MAX_LIST).map((c) => ({
        id: text(c?.id, secrets, 12), status: c?.status === 'done' ? 'done' : 'not-done', evidence: text(c?.evidence, secrets, 400),
      })).filter((c) => c.id),
      verification: (Array.isArray(raw?.verification) ? raw.verification : []).slice(0, MAX_LIST).map((v) => ({
        command: text(v?.command, secrets, 200), result: text(v?.result, secrets, 200),
      })).filter((v) => v.command),
      notes: text(raw?.notes, secrets),
      expected: text(raw?.expected, secrets),
      questions: list(raw?.questions, secrets),
      confidence: CONFIDENCE.has(raw?.confidence) ? raw.confidence : 'low',
    },
    dropped,
  };
}

/** Paths the pipeline never changes (§13). */
export const FORBIDDEN_PATHS = Object.freeze([
  /^\.github\//, /^scripts\//, /^\.claude\//, /^\.cursor\//, /^(AGENTS|CLAUDE)\.md$/, /(^|\/)CLAUDE\.local\.md$/,
  /(^|\/)Directory\.(Build|Packages)\.(props|targets)$/, /^global\.json$/, /(^|\/)nuget\.config$/i, /^common\.props$/,
  /(^|\/)\.editorconfig$/, /\.globalconfig$/, /\.ruleset$/, /(^|\/)sonar-project\.properties$/, /\.runsettings$/,
  /(^|\/)coverlet\.json$/, /^etc\//, /^tools\//, /^init\//, /^ai-docs\//,
]);
const ADDED_FORBIDDEN = Object.freeze([
  [/#pragma\s+warning\s+disable/, 'adds `#pragma warning disable`'],
  [/\[\s*(assembly:\s*)?SuppressMessage\b/, 'adds `[SuppressMessage]`'],
  [/<NoWarn>/i, 'adds `<NoWarn>`'],
  [/<PackageReference\b/i, 'adds a package reference'],
  [/<TreatWarningsAsErrors>\s*false/i, 'turns warnings-as-errors off'],
]);
/** In MSBuild files, anything that runs code or changes analysis at build time. */
const BUILD_FILE = /\.(csproj|props|targets|proj)$/i;
const BUILD_FORBIDDEN = Object.freeze([
  [/<\s*(Target|Exec|Import|UsingTask|ProjectReference)\b/i, 'adds a build target, task, import or project reference'],
  [/<\s*(RunAnalyzers|RunAnalyzersDuringBuild|EnforceCodeStyleInBuild|AnalysisMode|AnalysisLevel|CodeAnalysisRuleSet|WarningsNotAsErrors|WarningLevel|Nullable|GenerateDocumentationFile)\b/i, 'changes analyzer or warning settings'],
]);
const TEST_ATTR = /\[\s*(Fact|Theory)\b/;
const MAX_FILES = 80;
const MAX_LINES = 4000;

/**
 * Parses a `git diff` by structure: a file starts at `diff --git`, its path comes from that line,
 * and only lines inside a hunk (after `@@`, within the hunk's line counts) are content. A content
 * line that happens to start with `+++` or `---` is content, never a header.
 * Returns [{ path, binary, added: string[], removed: string[] }].
 */
export function parsePatch(patch) {
  const out = [];
  let cur = null;
  let oldLeft = 0;
  let newLeft = 0;
  for (const line of String(patch ?? '').split('\n')) {
    if (oldLeft > 0 || newLeft > 0) {
      if (line.startsWith('+')) {
        cur.added.push(line.slice(1));
        newLeft--;
      } else if (line.startsWith('-')) {
        cur.removed.push(line.slice(1));
        oldLeft--;
      } else if (line.startsWith('\\')) {
        // "\ No newline at end of file"
      } else {
        oldLeft--;
        newLeft--;
      }
      continue;
    }
    const header = /^diff --git a\/(.+) b\/(.+)$/.exec(line);
    if (header) {
      cur = { path: header[2], from: header[1], binary: false, added: [], removed: [] };
      out.push(cur);
      continue;
    }
    if (!cur) continue;
    const hunk = /^@@ -\d+(?:,(\d+))? \+\d+(?:,(\d+))? @@/.exec(line);
    if (hunk) {
      oldLeft = hunk[1] === undefined ? 1 : Number(hunk[1]);
      newLeft = hunk[2] === undefined ? 1 : Number(hunk[2]);
    } else if (line.startsWith('GIT binary patch') || /^Binary files .* differ$/.test(line)) {
      cur.binary = true;
    }
  }
  return out;
}

/**
 * The diff policy. `files` = [{ path, status }] (`git diff --name-status`, status A/M/D/…, taken
 * from git in the write job), `patch` = the unified diff. Returns the violations; any one stops the push.
 */
export function checkDiff(files, patch) {
  const v = [];
  if (!files.length) return ['the diff is empty'];
  if (files.length > MAX_FILES) v.push(`touches ${files.length} files (limit ${MAX_FILES})`);
  for (const f of files) {
    if (f.path.includes('..') || f.path.startsWith('/')) v.push(`${f.path}: path escapes the repository`);
    const rule = FORBIDDEN_PATHS.find((re) => re.test(f.path));
    if (rule) v.push(`${f.path}: the pipeline may not change this path`);
    if (/^D/.test(f.status) && /^test\//.test(f.path)) v.push(`${f.path}: deletes a test file`);
  }
  const parsed = parsePatch(patch);
  const listed = new Set(files.map((f) => f.path));
  let changed = 0;
  let testsAdded = 0;
  let testsRemoved = 0;
  for (const f of parsed) {
    if (!listed.has(f.path)) v.push(`${f.path}: in the patch but not in the file list`);
    if (f.binary) v.push(`${f.path}: binary change`);
    changed += f.added.length + f.removed.length;
    for (const line of f.added) {
      for (const [re, what] of ADDED_FORBIDDEN) if (re.test(line)) v.push(`${f.path}: ${what}`);
      if (BUILD_FILE.test(f.path)) for (const [re, what] of BUILD_FORBIDDEN) if (re.test(line)) v.push(`${f.path}: ${what}`);
      if (/^test\//.test(f.path) && /\bSkip\s*=/.test(line)) v.push(`${f.path}: skips a test`);
      if (TEST_ATTR.test(line)) testsAdded++;
    }
    for (const line of f.removed) if (TEST_ATTR.test(line)) testsRemoved++;
  }
  if (changed > MAX_LINES) v.push(`changes ${changed} lines (limit ${MAX_LINES})`);
  if (testsRemoved > testsAdded) v.push(`removes ${testsRemoved - testsAdded} more test(s) than it adds`);
  return [...new Set(v)];
}

const SECRET_SHAPES = Object.freeze([
  /sk-ant-[A-Za-z0-9_-]{8,}/, /gh[pousr]_[A-Za-z0-9]{20,}/, /github_pat_[A-Za-z0-9_]{20,}/, /AKIA[0-9A-Z]{16}/,
  /-----BEGIN [A-Z ]*PRIVATE KEY-----/, /\bkey_[A-Za-z0-9]{32,}\b/,
]);

/**
 * Secrets in the added lines of the patch. Code the agent wrote runs in the build sandbox as the
 * runner user, so a test could try to write a token into the tree; such a diff is never pushed.
 * `secrets` are the exact values the job holds; the shapes catch the common token formats.
 */
export function findSecrets(patch, secrets = []) {
  const hits = new Set();
  for (const f of parsePatch(patch)) {
    for (const line of f.added) {
      if (secrets.some((x) => x && x.length >= 8 && line.includes(x))) hits.add(`${f.path}: contains a secret this job holds`);
      if (SECRET_SHAPES.some((re) => re.test(line))) hits.add(`${f.path}: contains a token-shaped string`);
    }
  }
  return [...hits];
}

/** The open milestone the PR goes into: the lowest `vX.Y.Z`. */
export function pickMilestone(titles) {
  const key = (t) => t.slice(1).split('.').map(Number);
  return (titles ?? []).filter((t) => /^v\d+\.\d+\.\d+$/.test(t)).sort((a, b) => {
    const [x, y] = [key(a), key(b)];
    return x[0] - y[0] || x[1] - y[1] || x[2] - y[2];
  })[0] ?? null;
}

/** The issue's classification labels, copied to the PR so the PR contract sees an AI PR. */
export const prLabels = (issueLabels) => (issueLabels ?? []).filter((l) => PR_LABELS.test(l));

/**
 * Issue label transition per outcome: `pushed` → `ai:pr-ready`; `needs-replan` → `ai:replan` (back
 * to the Director); `rejected` (diff policy) and `no-change` (already fixed) → `ai:blocked`;
 * `failed` (agent or infrastructure) → nothing, the next run retries.
 */
export function developerLabels(outcome, issueLabels) {
  const present = new Set(issueLabels ?? []);
  const want = { pushed: 'ai:pr-ready', 'needs-replan': 'ai:replan', rejected: 'ai:blocked', 'no-change': 'ai:blocked' }[outcome];
  if (!want) return { add: [], remove: [] };
  return { add: present.has(want) ? [] : [want], remove: STATES_DONE_BY_PUSH.filter((l) => l !== want && present.has(l)) };
}

/** The decision-log row the council comment carries, or null. */
export function extractCouncilRow(councilBody) {
  const m = /```markdown\n(\| \d{4}-\d{2}-\d{2} \|[^\n]+\|)\n```/.exec(String(councilBody ?? ''));
  return m ? m[1] : null;
}

/** Appends `row` to the sessions log above its closing comment; null when it is already there. */
export function insertLogRow(readme, row) {
  const link = /\[(issue-\d+-council)\]/.exec(row)?.[1];
  if (!link || readme.includes(`[${link}]`)) return null;
  const marker = '<!-- newest at the bottom; keep one line per session -->';
  if (!readme.includes(marker)) return null;
  return readme.replace(marker, `${row}\n${marker}`);
}

const yes = (b) => (b ? 'yes' : 'no');
const bullets = (items, fallback) => (items.length ? items.map((i) => `- ${i}`) : [`- ${fallback}`]);

/** The PR body: the template's sections plus the AI decision record (§9). */
export function renderPrBody(ctx) {
  const { issue, result, plan, labels, councilUrl, planUrl, branch, runner, repairs, maxRepairs, e2eReason } = ctx;
  const council = labels.includes('ai:council-approved') && councilUrl
    ? `APPROVED — ${councilUrl}${result.conditions.length ? ` · conditions: ${result.conditions.map((c) => `${c.id} ${c.status}`).join(', ')}` : ''}`
    : `not required — ${plan?.council ? 'see the Director plan' : 'no council criterion matched in the Director plan'}`;
  const why = labels.filter((l) => /^ai:(impact|risk)-/.test(l)).join(', ');
  const row = (name, r) => `| ${name} | ${r?.status ?? 'not-run'} | ${r?.detail ?? ''} |`;
  const handoffs = PRODUCTS.map((p) => `| ${p} | ${yes(result.productImpact[p].needed)} | ${result.productImpact[p].needed ? result.productImpact[p].handoff : ''} |`);
  const out = [
    '## Summary',
    ...bullets(result.summary, result.title),
    `- Implements #${issue}${result.mode === 'RECOVERY' ? ' (recovery: earlier attempts were closed unmerged)' : ''}.`,
    '',
    '## vNext scope',
    `- ${result.vnextScope || 'see Summary'}`,
    '',
    '## E2E evidence',
    '- Status: not-run',
    `- Reason: ${e2eReason}`,
    '',
    '## Product impact',
    '| Product | Needed | Handoff |',
    '|---|---|---|',
    ...handoffs,
    '',
    '## vnext-meta impact',
    `- Status: ${result.meta.status}`,
    ...(result.meta.status === 'not-needed' ? [`- Reason: ${result.meta.reason || 'no runtime behaviour or contract change to record'}`] : []),
    '',
    '## Changes',
    ...bullets(result.changes, 'see the diff'),
    '',
    '## Test Plan',
    ...(result.testPlan.length ? result.testPlan.map((t) => `- [ ] ${t}`) : ['- [ ] CI unit tests are green']),
    '',
    '## AI decision record',
    `- Issue: #${issue} · Director plan: ${planUrl ?? 'on the issue'}`,
    `- Classification: ${['semver', 'impact', 'risk'].map((g) => labels.find((l) => l.startsWith(`ai:${g}-`))?.slice(3) ?? `${g}-?`).join(' · ')}`,
    `  - Why: ${why ? `from the Director plan (${why})` : 'from the Director plan'}`,
    `- Council: ${council}`,
    `- Repair attempts: ${repairs}/${maxRepairs}`,
    `- Agent confidence: ${result.confidence}`,
    '',
    '### Done / skipped',
    '| Check | Status | Note |',
    '|---|---|---|',
    row('Build (runner)', runner?.build),
    row('Unit tests (runner)', runner?.tests),
    '| Unit tests (CI) | see checks | the `Unit Tests` check is authoritative |',
    `| E2E (vnext-example) | not-run | ${result.e2eSuites.length ? `suites: ${result.e2eSuites.join(', ')} — ` : ''}${e2eReason} |`,
    '| Observability verification | not-run | no infra in CI — must be done locally if the change is timing / trace related |',
    `| Companion PRs | ${PRODUCTS.some((p) => result.productImpact[p].needed) ? 'handoff' : 'none'} | ${PRODUCTS.filter((p) => result.productImpact[p].needed).join(', ') || '—'} |`,
    '',
    '### Test it locally',
    '```bash',
    `git fetch origin ${branch} && git switch ${branch}`,
    './scripts/setup-netstandard-ref.sh && dotnet build',
    'cd etc/docker && ./run-docker.sh up core',
    `cd ../vnext-example && dotnet test tests/Core.IntegrationTests${result.e2eSuites.length ? ` --filter "${result.e2eSuites.join('|')}"` : ''}`,
    '```',
    `- Expected behaviour: ${result.expected || 'see the acceptance criteria in the Director plan'}`,
  ];
  if (result.verification.length) out.push('', '<details><summary>What the agent ran</summary>', '', ...result.verification.map((v) => `- \`${v.command}\` → ${v.result}`), '', '</details>');
  if (result.notes) out.push('', '## Notes', result.notes);
  out.push('', `Closes #${issue}`, '', '🤖 Generated by the AI Developer workflow (Cursor CLI) — draft, a human reviews and merges.');
  return out.join('\n');
}

/** Rewrites the `Repair attempts:` line of an existing PR body. */
export const updateRepairs = (body, repairs, max) => String(body ?? '').replace(/^- Repair attempts: .*$/m, `- Repair attempts: ${repairs}/${max}`);

/** The sticky status comment on the issue. */
export function renderDeveloperComment({ issue, mode, outcome, reason, prUrl, violations = [], result, runMode, labels }) {
  const icon = { pushed: '🛠️', 'needs-replan': '↩️', rejected: '⛔', 'no-change': '✔️', failed: '⚠️' }[outcome] ?? 'ℹ️';
  const labelText = labels && (labels.add.length || labels.remove.length)
    ? `${runMode === 'dry-run' ? 'would ' : ''}add ${labels.add.map((l) => `\`${l}\``).join(' ') || '—'}; ${runMode === 'dry-run' ? 'would ' : ''}remove ${labels.remove.map((l) => `\`${l}\``).join(' ') || '—'}`
    : 'unchanged';
  const out = [
    DEV_MARKER,
    `### ${icon} AI Developer — ${mode} · ${outcome}${runMode === 'dry-run' ? ' (dry run — labels not applied)' : ''}`,
    '',
    `| | |`,
    '|---|---|',
    `| PR | ${prUrl ?? '—'} |`,
    `| Labels | ${labelText} |`,
  ];
  if (reason) out.push(`| Reason | ${reason} |`);
  if (result?.summary?.length) out.push('', ...result.summary.map((s) => `- ${s}`));
  if (violations.length) out.push('', '**Diff policy violations — nothing was pushed**', ...violations.map((x) => `- ${x}`));
  if (result?.questions?.length) out.push('', '**Questions for the Director**', ...result.questions.map((q) => `- [ ] ${q}`));
  out.push('', `<sub>Issue #${issue} · the PR is a draft; a human reviews and merges. Add \`ai:blocked\` to stop the pipeline.</sub>`);
  return out.join('\n');
}
