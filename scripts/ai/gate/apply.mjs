#!/usr/bin/env node
// Applies the Quality Gate — the only step that writes to GitHub. For each <n>.json in --in:
// decide the verdict from the evaluated evidence plus the agent's review (decision `review`),
// upsert the one gate comment on the PR, and for a pipeline PR in apply mode move the linked
// issue's state (and, on COUNCIL, raise the PR's impact label). Human PRs get the comment only.
//   --in <dir> default .ai/in   --results <dir> default .ai/out   --mode dry-run|apply   --print
//   --config <path> default .github/ai/gate.json   --repo owner/name
// $AI_REDACT: secrets to redact. $AI_APP_TOKEN: GitHub App token for label writes (so the label
// event starts the Developer). Comments use $GH_TOKEN. $AI_MAX_REPAIRS overrides gate.json.
import { readFileSync, readdirSync, existsSync, appendFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';
import { extractResult } from '../../triage/lib/triage.mjs';
import { toolPath } from '../lib/tools.mjs';
import { isGateComment, validateReview, decideVerdict, nextRepairs, planGateLabels, renderGateComment } from '../lib/gate.mjs';

const args = Object.fromEntries(
  process.argv.slice(2).reduce((acc, a, i, all) => {
    if (a === '--print') acc.push(['print', true]);
    else if (a.startsWith('--')) acc.push([a.slice(2), all[i + 1]]);
    return acc;
  }, []),
);
const repo = args.repo ?? process.env.GITHUB_REPOSITORY;
const inDir = args.in ?? '.ai/in';
const resultsDir = args.results ?? '.ai/out';
const mode = args.mode ?? 'dry-run';
if (!['dry-run', 'apply'].includes(mode)) {
  console.error(`--mode must be dry-run or apply, got ${mode}`);
  process.exit(2);
}
const config = JSON.parse(readFileSync(args.config ?? '.github/ai/gate.json', 'utf8'));
const maxRepairs = Number(process.env.AI_MAX_REPAIRS || config.maxRepairs || 3);
const secrets = (process.env.AI_REDACT ?? '').split('\n').map((s) => s.trim()).filter(Boolean);
const appToken = process.env.AI_APP_TOKEN;
const GH = toolPath('gh');
const gh = (a, input) => execFileSync(GH, a, { encoding: 'utf8', input });
const ghApp = (a) => execFileSync(GH, a, { encoding: 'utf8', env: { ...process.env, GH_TOKEN: appToken } });

function upsertComment(number, body) {
  const comments = JSON.parse(gh(['api', '--paginate', '--slurp', `repos/${repo}/issues/${number}/comments?per_page=100`])).flat();
  const existing = comments.filter(isGateComment).at(-1);
  const payload = JSON.stringify({ body });
  if (existing) gh(['api', '-X', 'PATCH', `repos/${repo}/issues/comments/${existing.id}`, '--input', '-'], payload);
  else gh(['api', '-X', 'POST', `repos/${repo}/issues/${number}/comments`, '--input', '-'], payload);
}

function editLabels(number, add, remove) {
  if (!add.length && !remove.length) return;
  if (!appToken) throw new Error('AI_APP_TOKEN not set; labels not written (the next role would not start)');
  const edit = ['issue', 'edit', String(number), '--repo', repo];
  if (add.length) edit.push('--add-label', add.join(','));
  if (remove.length) edit.push('--remove-label', remove.join(','));
  ghApp(edit);
}

let failures = 0;
const summary = ['| PR | Decision | Verdict | Issue | Labels | Notes |', '|---|---|---|---|---|---|'];
for (const file of existsSync(inDir) ? readdirSync(inDir).filter((f) => f.endsWith('.json')) : []) {
  const s = JSON.parse(readFileSync(join(inDir, file), 'utf8'));
  const n = s.pr.number;
  try {
    let review = null;
    let dropped = [];
    if (s.decision === 'review') {
      const resultPath = join(resultsDir, file);
      if (existsSync(resultPath)) {
        try {
          ({ review, dropped } = validateReview(extractResult(readFileSync(resultPath, 'utf8')), { changedFiles: s.pr.changedFiles, secrets }));
        } catch (e) {
          console.log(`::warning::#${n}: no usable review (${e.message})`);
        }
      }
    }
    const evaluation = { complete: s.evidence.pending.length === 0, ...s.evidence };
    const { verdict, reasons } = decideVerdict(evaluation, review, { impactHits: s.impactHits, isAiPr: s.isAiPr });
    const repairShas = nextRepairs(s.previous, verdict, s.pr.headSha);
    const labels = planGateLabels(verdict, s.issue?.labels, { isAiPr: s.isAiPr && Boolean(s.issue), attempts: repairShas.length, maxRepairs });
    const body = renderGateComment({
      pr: n, sha: s.pr.headSha, verdict, reasons, evaluation, review, labels, mode, attempts: repairShas.length, maxRepairs,
      isAiPr: s.isAiPr, fp: s.fp, repairShas, risk: s.risk,
    });
    const labelText = `${mode === 'dry-run' ? 'would ' : ''}+${labels.add.join(' +') || '—'} −${labels.remove.join(' −') || '—'}`;
    if (args.print) {
      console.log(`----- PR #${n} -----\n${body}\nissue labels: ${labelText} | dropped: ${dropped.join(', ') || '-'}`);
      continue;
    }
    upsertComment(n, body);
    if (mode === 'apply' && s.issue) {
      editLabels(s.issue.number, labels.add, labels.remove);
      if (verdict === 'COUNCIL') editLabels(n, ['ai:impact-high'], s.pr.labels.includes('ai:impact-low') ? ['ai:impact-low'] : []);
    }
    summary.push(`| #${n} | ${s.decision} | ${verdict} | ${s.issue ? `#${s.issue.number}` : '—'} | ${labelText} | ${dropped.length ? `dropped: ${dropped.join(', ')}` : '—'} |`);
  } catch (e) {
    failures++;
    console.log(`::warning::#${n}: ${e.message.split('\n')[0]}`);
    summary.push(`| #${n} | ${s.decision} | failed | — | — | ${e.message.split('\n')[0].replaceAll('|', '/')} |`);
  }
}

if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, `### AI Quality Gate (${mode})\n\n${summary.join('\n')}\n`);
else if (!args.print) console.log(summary.join('\n'));
if (failures) process.exitCode = 1;
