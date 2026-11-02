#!/usr/bin/env node
// Applies the agent's results. For every <n>.json in --in that has a matching <n>.json result
// in --results: validate, upsert the sticky triage comment, and in `apply` mode add labels and
// set the issue type. `dry-run` (default) writes the comment only.
//   --in <dir>            default .triage/in (issue snapshots from collect.mjs)
//   --results <dir>       default .triage/out (the agent's stdout: `agent -p --output-format json` or bare JSON)
//   --mode dry-run|apply
//   --print               print the plan, write nothing to GitHub (local testing)
//   --taxonomy <path>     default .github/triage/labels.json (CI passes the copy staged before the agent ran)
//   --repo owner/name     default $GITHUB_REPOSITORY;  --sha <commit> for file links
// Secrets in $TRIAGE_REDACT (newline-separated) are redacted from every comment.
import { readFileSync, readdirSync, existsSync, appendFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';
import { extractResult, validateResult, renderComment, planActions, isTriageComment } from './lib/triage.mjs';

const args = Object.fromEntries(
  process.argv.slice(2).reduce((acc, a, i, all) => {
    if (a === '--print') acc.push(['print', true]);
    else if (a.startsWith('--')) acc.push([a.slice(2), all[i + 1]]);
    return acc;
  }, []),
);
const repo = args.repo ?? process.env.GITHUB_REPOSITORY;
const inDir = args.in ?? '.triage/in';
const resultsDir = args.results ?? '.triage/out';
const mode = args.mode ?? 'dry-run';
const sha = args.sha ?? process.env.GITHUB_SHA;
if (!['dry-run', 'apply'].includes(mode)) {
  console.error(`--mode must be dry-run or apply, got ${mode}`);
  process.exit(2);
}

const taxonomy = JSON.parse(readFileSync(args.taxonomy ?? '.github/triage/labels.json', 'utf8'));
const secrets = (process.env.TRIAGE_REDACT ?? '').split('\n').map((s) => s.trim()).filter(Boolean);
const gh = (ghArgs, input) => execFileSync('gh', ghArgs, { encoding: 'utf8', input });
// Only committed files count: the agent can create files, it cannot add them to the index.
const tracked = new Set(execFileSync('git', ['ls-files', '-z'], { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 }).split('\0'));

function upsertComment(number, body) {
  const comments = JSON.parse(gh(['api', '--paginate', '--slurp', `repos/${repo}/issues/${number}/comments?per_page=100`])).flat();
  const existing = comments.find(isTriageComment);
  const payload = JSON.stringify({ body });
  if (existing) gh(['api', '-X', 'PATCH', `repos/${repo}/issues/comments/${existing.id}`, '--input', '-'], payload);
  else gh(['api', '-X', 'POST', `repos/${repo}/issues/${number}/comments`, '--input', '-'], payload);
}

let failures = 0;
const summary = ['| Issue | Type | Labels added | Dropped |', '|---|---|---|---|'];
for (const file of existsSync(inDir) ? readdirSync(inDir).filter((f) => f.endsWith('.json')) : []) {
  const issue = JSON.parse(readFileSync(join(inDir, file), 'utf8'));
  const resultPath = join(resultsDir, file);
  if (!existsSync(resultPath)) {
    console.log(`::warning::#${issue.number}: the agent wrote no result`);
    failures++;
    continue;
  }
  let raw;
  try {
    raw = extractResult(readFileSync(resultPath, 'utf8'));
  } catch (e) {
    console.log(`::warning::#${issue.number}: no usable result (${e.message})`);
    failures++;
    continue;
  }
  const { result, dropped } = validateResult(raw, taxonomy, { fileExists: (p) => tracked.has(p), secrets });
  const body = renderComment(result, { mode, repo, sha });
  const { addLabels, setType } = planActions(result, issue, mode, taxonomy);
  summary.push(`| #${issue.number} | ${setType ?? '—'} | ${addLabels.join(', ') || '—'} | ${dropped.join(', ') || '—'} |`);

  if (args.print) {
    console.log(`----- #${issue.number} -----\n${body}\nlabels: ${addLabels.join(', ') || '-'} | type: ${setType ?? '-'} | dropped: ${dropped.join(', ') || '-'}`);
    continue;
  }
  try {
    upsertComment(issue.number, body);
    if (addLabels.length) gh(['issue', 'edit', String(issue.number), '--repo', repo, '--add-label', addLabels.join(',')]);
    if (setType) gh(['api', '-X', 'PATCH', `repos/${repo}/issues/${issue.number}`, '-f', `type=${setType}`]);
  } catch (e) {
    console.log(`::warning::#${issue.number}: GitHub write failed (${e.message.split('\n')[0]})`);
    failures++;
  }
}

if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, `### Issue triage (${mode})\n\n${summary.join('\n')}\n`);
if (failures) console.log(`${failures} issue(s) not triaged; see warnings.`);
