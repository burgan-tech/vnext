#!/usr/bin/env node
// Collects the issues the Architecture Council judges and writes each one as the agent's only input.
//   --issue <n>               one issue (issues: labeled ai:council-required, manual run)
//   --sweep [--limit 2]       open issues with ai:council-required whose input changed
//   --force                   judge again even when the input is unchanged or already approved
//   --out <dir>               default .ai/in; writes <n>.json per issue
//   --repo owner/name         default $GITHUB_REPOSITORY
// The sweep exists because the Director cannot re-trigger the council: when `ai:council-required`
// is already on the issue, adding it again raises no `labeled` event.
// Prints `<n>:<decision>`; on Actions sets `issues` (all written) and `agent` (decision `council`).
import { mkdirSync, writeFileSync, appendFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';
import { isPlanComment } from '../lib/director.mjs';
import { isCouncilComment, councilFingerprint, preDecide } from '../lib/council.mjs';
import { isGateComment } from '../lib/gate.mjs';
import { isTriageComment } from '../../triage/lib/triage.mjs';
import { toolPath } from '../lib/tools.mjs';

const args = Object.fromEntries(
  process.argv.slice(2).reduce((acc, a, i, all) => {
    if (a === '--sweep' || a === '--force') acc.push([a.slice(2), true]);
    else if (a.startsWith('--')) acc.push([a.slice(2), all[i + 1]]);
    return acc;
  }, []),
);
const repo = args.repo ?? process.env.GITHUB_REPOSITORY;
const outDir = args.out ?? '.ai/in';
const limit = Number(args.limit ?? 2);
if (!repo || (!args.issue && !args.sweep)) {
  console.error('usage: collect.mjs --issue <n> | --sweep [--limit 2] [--force] [--out dir] [--repo owner/name]');
  process.exit(2);
}

const GH = toolPath('gh');
const gh = (path) => JSON.parse(execFileSync(GH, ['api', '--paginate', '--slurp', path], { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 })).flat();
const ghOne = (path) => JSON.parse(execFileSync(GH, ['api', path], { encoding: 'utf8' }));
const labelsOf = (issue) => (issue.labels ?? []).map((l) => (typeof l === 'string' ? l : l.name));
const eligible = (issue) => !issue.pull_request && issue.state === 'open' && labelsOf(issue).includes('ai:council-required');
const pipelineComment = (c) => isPlanComment(c) || isCouncilComment(c) || isTriageComment(c) || isGateComment(c);

/** PRs that reference the issue; an open one carries its changed files (the Quality Gate's COUNCIL case). */
function linkedPrs(number) {
  const seen = new Map();
  for (const e of gh(`repos/${repo}/issues/${number}/timeline?per_page=100`)) {
    const src = e.event === 'cross-referenced' ? e.source?.issue : null;
    if (!src?.pull_request || src.repository?.full_name !== repo) continue;
    seen.set(src.number, { number: src.number, title: src.title, state: src.state, merged: Boolean(src.pull_request.merged_at) });
  }
  for (const pr of seen.values()) {
    if (pr.state === 'open') pr.changedFiles = gh(`repos/${repo}/pulls/${pr.number}/files?per_page=100`).map((f) => f.filename).slice(0, 200);
  }
  return [...seen.values()];
}

function toInput(issue) {
  const comments = gh(`repos/${repo}/issues/${issue.number}/comments?per_page=100`);
  const human = comments.filter((c) => !pipelineComment(c)).slice(-20).map((c) => ({ author: c.user?.login, body: c.body ?? '' }));
  const input = {
    number: issue.number,
    title: issue.title,
    body: issue.body ?? '',
    author: issue.user?.login,
    labels: labelsOf(issue),
    comments: human,
    directorPlan: comments.filter(isPlanComment).at(-1)?.body ?? null,
    previousCouncil: comments.filter(isCouncilComment).at(-1)?.body ?? null,
  };
  input.fp = councilFingerprint(input);
  return input;
}

let candidates;
if (args.issue) {
  const issue = ghOne(`repos/${repo}/issues/${args.issue}`);
  candidates = eligible(issue) ? [issue] : [];
  if (!candidates.length) console.log(`${args.issue}:not-eligible (open issue with ai:council-required required)`);
} else {
  candidates = gh(`repos/${repo}/issues?state=open&labels=ai:council-required&per_page=100`).filter(eligible).sort((a, b) => a.number - b.number);
}

mkdirSync(outDir, { recursive: true });
const all = [];
const agent = [];
for (const issue of candidates) {
  if (args.sweep && all.length >= limit) break;
  const input = toInput(issue);
  const decision = preDecide(input, { force: Boolean(args.force) });
  console.log(`${issue.number}:${decision}`);
  // Left alone: nothing to write. `no-plan` gets a comment once (its fingerprint dedupes it).
  if (['blocked', 'approved', 'unchanged'].includes(decision)) continue;
  input.preDecision = decision;
  if (decision === 'council') input.linkedPrs = linkedPrs(issue.number);
  writeFileSync(join(outDir, `${issue.number}.json`), JSON.stringify(input, null, 2));
  all.push(issue.number);
  if (decision === 'council') agent.push(issue.number);
}
if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT, `issues=${all.join(' ')}\nagent=${agent.join(' ')}\n`);
