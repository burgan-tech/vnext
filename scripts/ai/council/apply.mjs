#!/usr/bin/env node
// Applies the Architecture Council — the only step that writes to GitHub. For each <n>.json in --in:
//   preDecision `council`  validate the agent's <n>.json in --results, decide the final verdict
//                          (stricter, never looser than the agent's), upsert the sticky council
//                          comment with the decision-log row; in apply mode set the next state.
//   preDecision `no-plan`  no Director plan to judge: comment only, labels unchanged.
// dry-run (default) writes the comment only, showing the labels it would set.
//   --in <dir> default .ai/in   --results <dir> default .ai/out   --mode dry-run|apply   --print
//   --repo owner/name default $GITHUB_REPOSITORY   --date YYYY-MM-DD default today (UTC)
// $AI_REDACT: secrets to redact. $AI_APP_TOKEN: GitHub App token for label writes (so the label
// event starts the Developer). Comments use $GH_TOKEN.
import { readFileSync, readdirSync, existsSync, appendFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';
import { extractResult } from '../../triage/lib/triage.mjs';
import { toolPath } from '../lib/tools.mjs';
import { readPlanData } from '../lib/director.mjs';
import { isCouncilComment, validateCouncil, decideCouncil, councilLabels, councilLogRow, renderCouncilComment } from '../lib/council.mjs';

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
const date = args.date ?? new Date().toISOString().slice(0, 10);
if (!['dry-run', 'apply'].includes(mode)) {
  console.error(`--mode must be dry-run or apply, got ${mode}`);
  process.exit(2);
}
const secrets = (process.env.AI_REDACT ?? '').split('\n').map((s) => s.trim()).filter(Boolean);
const appToken = process.env.AI_APP_TOKEN;
const GH = toolPath('gh');
const gh = (a, input) => execFileSync(GH, a, { encoding: 'utf8', input });
const ghApp = (a) => execFileSync(GH, a, { encoding: 'utf8', env: { ...process.env, GH_TOKEN: appToken } });

/** Upserts the sticky comment and returns its URL (the decision-log row links to it). */
function upsertComment(number, render) {
  const comments = JSON.parse(gh(['api', '--paginate', '--slurp', `repos/${repo}/issues/${number}/comments?per_page=100`])).flat();
  const existing = comments.filter(isCouncilComment).at(-1);
  if (existing) {
    gh(['api', '-X', 'PATCH', `repos/${repo}/issues/comments/${existing.id}`, '--input', '-'], JSON.stringify({ body: render(existing.html_url) }));
    return existing.html_url;
  }
  // A new comment's URL is only known after the POST: write it, then patch the row's link in.
  const created = JSON.parse(gh(['api', '-X', 'POST', `repos/${repo}/issues/${number}/comments`, '--input', '-'], JSON.stringify({ body: render(null) })));
  gh(['api', '-X', 'PATCH', `repos/${repo}/issues/comments/${created.id}`, '--input', '-'], JSON.stringify({ body: render(created.html_url) }));
  return created.html_url;
}

function editLabels(number, labels) {
  if (mode !== 'apply' || (!labels.add.length && !labels.remove.length)) return;
  if (!appToken) throw new Error('AI_APP_TOKEN not set; labels not written (the next role would not start)');
  const edit = ['issue', 'edit', String(number), '--repo', repo];
  if (labels.add.length) edit.push('--add-label', labels.add.join(','));
  if (labels.remove.length) edit.push('--remove-label', labels.remove.join(','));
  ghApp(edit);
}

let failures = 0;
const summary = ['| Issue | Decision | Verdict | Labels | Notes |', '|---|---|---|---|---|'];
for (const file of existsSync(inDir) ? readdirSync(inDir).filter((f) => f.endsWith('.json')) : []) {
  const input = JSON.parse(readFileSync(join(inDir, file), 'utf8'));
  const n = input.number;
  try {
    let result = null;
    let dropped = [];
    let note;
    if (input.preDecision === 'council') {
      const resultPath = join(resultsDir, file);
      if (existsSync(resultPath)) {
        try {
          ({ result, dropped } = validateCouncil(extractResult(readFileSync(resultPath, 'utf8')), { secrets }));
        } catch (e) {
          console.log(`::warning::#${n}: no usable council result (${e.message})`);
        }
      }
    } else {
      note = 'There is no Director plan on this issue to judge. Add `ai:ready` so the Director plans it; the hourly sweep runs the council once the plan is there.';
    }
    const decision = decideCouncil(result, { labels: input.labels, directorPlan: input.directorPlan });
    const labels = councilLabels(decision.verdict, input.labels);
    const risk = readPlanData(input.directorPlan)?.risk ?? input.labels.find((l) => l.startsWith('ai:risk-'))?.slice('ai:risk-'.length);
    const render = (url) => renderCouncilComment({
      // A failed agent run stores no fingerprint, so the sweep retries it; a judged or no-plan input does.
      issue: n, result, decision, labels, mode, fp: result || note ? input.fp : null, note,
      logRow: result ? councilLogRow(result, decision, { date, issue: n, title: input.title, url: url ?? `https://github.com/${repo}/issues/${n}`, risk }) : null,
    });
    const labelText = `${mode === 'dry-run' ? 'would ' : ''}+${labels.add.join(' +') || '—'} −${labels.remove.join(' −') || '—'}`;
    if (args.print) {
      console.log(`----- #${n} -----\n${render(null)}\nlabels: ${labelText} | dropped: ${dropped.join(', ') || '-'}`);
      continue;
    }
    upsertComment(n, render);
    editLabels(n, labels);
    summary.push(`| #${n} | ${input.preDecision} | ${decision.verdict} | ${labelText} | ${dropped.length ? `dropped: ${dropped.join(', ')}` : '—'} |`);
  } catch (e) {
    failures++;
    console.log(`::warning::#${n}: ${e.message.split('\n')[0]}`);
    summary.push(`| #${n} | ${input.preDecision} | failed | — | ${e.message.split('\n')[0].replaceAll('|', '/')} |`);
  }
}

if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, `### Architecture Council (${mode})\n\n${summary.join('\n')}\n`);
else if (!args.print) console.log(summary.join('\n'));
if (failures) process.exitCode = 1;
