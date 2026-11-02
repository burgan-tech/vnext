#!/usr/bin/env node
// Collects the issues to triage and writes each one as the agent's only input.
//   --issue <n>               one issue (issues: opened/edited)
//   --sweep [--limit 20]      open issues that are neither `ai:triaged` nor carry a bot triage comment
//   --out <dir>               default .triage/in; writes <n>.json per issue
//   --repo owner/name         default $GITHUB_REPOSITORY
// Prints the selected numbers space-separated and, on Actions, sets the `issues` output.
import { mkdirSync, writeFileSync, appendFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';
import { needsTriage, isTriageComment, TRIAGED_LABEL } from './lib/triage.mjs';

const args = Object.fromEntries(
  process.argv.slice(2).reduce((acc, a, i, all) => {
    if (a === '--sweep') acc.push(['sweep', true]);
    else if (a.startsWith('--')) acc.push([a.slice(2), all[i + 1]]);
    return acc;
  }, []),
);
const repo = args.repo ?? process.env.GITHUB_REPOSITORY;
const outDir = args.out ?? '.triage/in';
const limit = Number(args.limit ?? 20);
if (!repo || (!args.issue && !args.sweep)) {
  console.error('usage: collect.mjs --issue <n> | --sweep [--limit 20] [--out dir] [--repo owner/name]');
  process.exit(2);
}

const gh = (path) => JSON.parse(execFileSync('gh', ['api', '--paginate', '--slurp', path], { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 })).flat();
const ghOne = (path) => JSON.parse(execFileSync('gh', ['api', path], { encoding: 'utf8' }));

function toInput(issue, comments) {
  return {
    number: issue.number,
    title: issue.title,
    body: issue.body ?? '',
    author: issue.user?.login,
    labels: (issue.labels ?? []).map((l) => (typeof l === 'string' ? l : l.name)),
    type: issue.type?.name ?? null,
    comments: comments
      .filter((c) => !isTriageComment(c))
      .slice(-20)
      .map((c) => ({ author: c.user?.login, body: c.body ?? '' })),
  };
}

const selected = [];
if (args.issue) {
  const issue = ghOne(`repos/${repo}/issues/${args.issue}`);
  if (!issue.pull_request && issue.state === 'open') selected.push([issue, gh(`repos/${repo}/issues/${issue.number}/comments?per_page=100`)]);
} else {
  const open = gh(`repos/${repo}/issues?state=open&per_page=100`)
    .filter((i) => !i.pull_request && !(i.labels ?? []).some((l) => l.name === TRIAGED_LABEL));
  for (const issue of open) {
    if (selected.length >= limit) break;
    const comments = gh(`repos/${repo}/issues/${issue.number}/comments?per_page=100`);
    const labels = issue.labels.map((l) => l.name);
    if (needsTriage({ ...issue, labels }, comments.some(isTriageComment))) selected.push([issue, comments]);
  }
}

mkdirSync(outDir, { recursive: true });
for (const [issue, comments] of selected) {
  writeFileSync(join(outDir, `${issue.number}.json`), JSON.stringify(toInput(issue, comments), null, 2));
}
const numbers = selected.map(([i]) => i.number).join(' ');
console.log(numbers);
if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT, `issues=${numbers}\n`);
