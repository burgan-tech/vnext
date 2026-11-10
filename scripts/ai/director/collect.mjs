#!/usr/bin/env node
// Collects the issues the Director handles and writes each one as the agent's only input.
//   --issue <n>               one issue (issues: labeled ai:ready / ai:replan)
//   --sweep [--limit 3]       open issues labelled ai:replan first, then ai:ready
//   --out <dir>               default .ai/in; writes <n>.json per issue
//   --repo owner/name         default $GITHUB_REPOSITORY
// Prints `<n>:<decision>` per issue. On Actions it sets the outputs `issues` (all selected) and
// `agent` (only those whose pre-decision is `plan`: the agent runs for these alone).
import { mkdirSync, writeFileSync, appendFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';
import { isPlanComment, preDecide } from '../lib/director.mjs';
import { isTriageComment } from '../../triage/lib/triage.mjs';
import { toolPath } from '../lib/tools.mjs';

const args = Object.fromEntries(
  process.argv.slice(2).reduce((acc, a, i, all) => {
    if (a === '--sweep') acc.push(['sweep', true]);
    else if (a.startsWith('--')) acc.push([a.slice(2), all[i + 1]]);
    return acc;
  }, []),
);
const repo = args.repo ?? process.env.GITHUB_REPOSITORY;
const outDir = args.out ?? '.ai/in';
const limit = Number(args.limit ?? 3);
if (!repo || (!args.issue && !args.sweep)) {
  console.error('usage: collect.mjs --issue <n> | --sweep [--limit 3] [--out dir] [--repo owner/name]');
  process.exit(2);
}

const GH = toolPath('gh');
const gh = (path) => JSON.parse(execFileSync(GH, ['api', '--paginate', '--slurp', path], { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 })).flat();
const ghOne = (path) => JSON.parse(execFileSync(GH, ['api', path], { encoding: 'utf8' }));
const labelsOf = (issue) => (issue.labels ?? []).map((l) => (typeof l === 'string' ? l : l.name));
const eligible = (issue) => !issue.pull_request && issue.state === 'open' && labelsOf(issue).some((l) => l === 'ai:ready' || l === 'ai:replan');

/** PRs that reference the issue (timeline cross-references), with their real state. */
function linkedPrs(number) {
  const seen = new Map();
  for (const e of gh(`repos/${repo}/issues/${number}/timeline?per_page=100`)) {
    const src = e.event === 'cross-referenced' ? e.source?.issue : null;
    if (!src?.pull_request || src.repository?.full_name !== repo) continue;
    seen.set(src.number, { number: src.number, title: src.title, state: src.state, merged: Boolean(src.pull_request.merged_at) });
  }
  return [...seen.values()];
}

function toInput(issue) {
  const comments = gh(`repos/${repo}/issues/${issue.number}/comments?per_page=100`);
  const plans = comments.filter(isPlanComment).reverse().map((c) => c.body ?? '');
  return {
    number: issue.number,
    title: issue.title,
    body: issue.body ?? '',
    author: issue.user?.login,
    labels: labelsOf(issue),
    comments: comments
      .filter((c) => !isPlanComment(c) && !isTriageComment(c))
      .slice(-20)
      .map((c) => ({ author: c.user?.login, body: c.body ?? '' })),
    previousPlans: plans.slice(0, 3),
    linkedPrs: linkedPrs(issue.number),
  };
}

let candidates;
if (args.issue) {
  const issue = ghOne(`repos/${repo}/issues/${args.issue}`);
  candidates = eligible(issue) ? [issue] : [];
} else {
  const open = gh(`repos/${repo}/issues?state=open&per_page=100`).filter(eligible);
  const rank = (i) => (labelsOf(i).includes('ai:replan') ? 0 : 1);
  candidates = open.sort((a, b) => rank(a) - rank(b) || a.number - b.number).slice(0, limit);
}

mkdirSync(outDir, { recursive: true });
const all = [];
const agent = [];
for (const issue of candidates) {
  const input = toInput(issue);
  const decision = preDecide(input);
  input.preDecision = decision;
  writeFileSync(join(outDir, `${issue.number}.json`), JSON.stringify(input, null, 2));
  all.push(issue.number);
  if (decision === 'plan') agent.push(issue.number);
  console.log(`${issue.number}:${decision}`);
}
if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT, `issues=${all.join(' ')}\nagent=${agent.join(' ')}\n`);
