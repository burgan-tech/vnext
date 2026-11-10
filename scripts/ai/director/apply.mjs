#!/usr/bin/env node
// Applies the Director's results — the only step that writes to GitHub.
// For each <n>.json in --in:
//   preDecision `plan`       validate the agent's <n>.json in --results, classify (path rules can
//                            only raise impact), upsert the sticky plan comment; in apply mode
//                            set the classification labels and the next state.
//   preDecision `reuse`      an earlier plan is still valid: apply mode restores its labels; no comment.
//   preDecision `active-pr`  an open PR exists: nothing is planned; reported in the summary.
// dry-run (default) writes the comment only, showing the labels it would set.
//   --in <dir>  default .ai/in        --results <dir>  default .ai/out
//   --mode dry-run|apply              --print  print the plan, write nothing (local testing)
//   --impact-rules <path>  default .github/ai/impact-paths.json (CI passes the copy staged before the agent ran)
//   --repo owner/name      default $GITHUB_REPOSITORY;  --sha <commit> for file links
// Secrets in $AI_REDACT (newline-separated) are redacted from every comment. Labels are written
// with $AI_APP_TOKEN (GitHub App, so the label event starts the next role); comments use $GH_TOKEN.
import { readFileSync, readdirSync, existsSync, appendFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';
import { extractResult } from '../../triage/lib/triage.mjs';
import { compileImpactRules } from '../../governance/lib/ai-record.mjs';
import { isPlanComment, readPlanData, validatePlan, classify, planLabels, renderPlanComment } from '../lib/director.mjs';

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
const sha = args.sha ?? process.env.GITHUB_SHA;
if (!['dry-run', 'apply'].includes(mode)) {
  console.error(`--mode must be dry-run or apply, got ${mode}`);
  process.exit(2);
}

const rules = compileImpactRules(JSON.parse(readFileSync(args['impact-rules'] ?? '.github/ai/impact-paths.json', 'utf8')));
const secrets = (process.env.AI_REDACT ?? '').split('\n').map((s) => s.trim()).filter(Boolean);
const appToken = process.env.AI_APP_TOKEN;
const gh = (ghArgs, input) => execFileSync('gh', ghArgs, { encoding: 'utf8', input });
const ghApp = (ghArgs) => execFileSync('gh', ghArgs, { encoding: 'utf8', env: { ...process.env, GH_TOKEN: appToken } });
// Only committed files count as existing: the agent can create files, not add them to the index.
const tracked = new Set(execFileSync('git', ['ls-files', '-z'], { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 }).split('\0'));

function upsertComment(number, body) {
  const comments = JSON.parse(gh(['api', '--paginate', '--slurp', `repos/${repo}/issues/${number}/comments?per_page=100`])).flat();
  const existing = comments.filter(isPlanComment).at(-1);
  const payload = JSON.stringify({ body });
  if (existing) gh(['api', '-X', 'PATCH', `repos/${repo}/issues/comments/${existing.id}`, '--input', '-'], payload);
  else gh(['api', '-X', 'POST', `repos/${repo}/issues/${number}/comments`, '--input', '-'], payload);
}

function writeLabels(number, labels) {
  if (mode !== 'apply' || (!labels.add.length && !labels.remove.length)) return;
  if (!appToken) throw new Error('AI_APP_TOKEN not set; labels not written (the next role would not start)');
  const edit = ['issue', 'edit', String(number), '--repo', repo];
  if (labels.add.length) edit.push('--add-label', labels.add.join(','));
  if (labels.remove.length) edit.push('--remove-label', labels.remove.join(','));
  ghApp(edit);
}

let failures = 0;
const summary = ['| Issue | Decision | Classification | Next state | Labels | Notes |', '|---|---|---|---|---|---|'];
const row = (n, d, c, next, l, notes) => summary.push(`| #${n} | ${d} | ${c} | ${next} | ${l} | ${notes} |`);

for (const file of existsSync(inDir) ? readdirSync(inDir).filter((f) => f.endsWith('.json')) : []) {
  const issue = JSON.parse(readFileSync(join(inDir, file), 'utf8'));
  const n = issue.number;
  try {
    if (issue.preDecision === 'active-pr') {
      const open = issue.linkedPrs.filter((p) => p.state === 'open').map((p) => `#${p.number}`).join(', ');
      row(n, 'active-pr', '—', '—', '—', `open PR ${open}; not re-planned`);
      continue;
    }
    if (issue.preDecision === 'reuse') {
      const data = issue.previousPlans.map(readPlanData).find(Boolean);
      const cls = { level: data.level, impact: data.impact, risk: data.risk, council: data.council, councilBy: data.council ? ['earlier plan'] : [], blocked: data.decision === 'plan' ? [] : [`earlier plan decided ${data.decision}`], criteria: [] };
      const labels = planLabels(cls, issue.labels);
      if (!args.print) writeLabels(n, labels);
      row(n, 'reuse', `${data.level}/${data.impact}/${data.risk}`, labels.next, `${mode === 'dry-run' ? 'would ' : ''}+${labels.add.join(' +') || '—'} −${labels.remove.join(' −') || '—'}`, 'earlier plan still valid');
      continue;
    }

    const resultPath = join(resultsDir, file);
    if (!existsSync(resultPath)) throw new Error('the agent wrote no result');
    const { result, dropped } = validatePlan(extractResult(readFileSync(resultPath, 'utf8')), { fileExists: (p) => tracked.has(p), secrets });
    const cls = classify(result, issue.labels, rules);
    const labels = planLabels(cls, issue.labels);
    const body = renderPlanComment(result, cls, labels, { mode, repo, sha, issue: n });
    if (args.print) {
      console.log(`----- #${n} -----\n${body}\nadd: ${labels.add.join(', ') || '-'} | remove: ${labels.remove.join(', ') || '-'} | dropped: ${dropped.join(', ') || '-'}`);
      continue;
    }
    upsertComment(n, body);
    writeLabels(n, labels);
    row(n, result.decision, `${cls.level}/${cls.impact}/${cls.risk}`, labels.next, `${mode === 'dry-run' ? 'would ' : ''}+${labels.add.join(' +') || '—'} −${labels.remove.join(' −') || '—'}`, dropped.length ? `dropped: ${dropped.join(', ')}` : '—');
  } catch (e) {
    failures++;
    console.log(`::warning::#${n}: ${e.message.split('\n')[0]}`);
    row(n, 'failed', '—', '—', '—', e.message.split('\n')[0].replace(/\|/g, '/'));
  }
}

if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, `### Engineering Director (${mode})\n\n${summary.join('\n')}\n`);
else if (!args.print) console.log(summary.join('\n'));
if (failures) {
  console.log(`${failures} issue(s) not handled; see warnings.`);
  process.exitCode = 1;
}
