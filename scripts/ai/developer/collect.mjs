#!/usr/bin/env node
// Collects one issue for the Developer and decides the run's mode before the agent (lib decideMode).
//   --issue <n>          the issue (issues: labeled ai:planned / ai:changes-required, manual run)
//   --out <dir>          default .ai/in; writes <n>.json (the agent's only input) unless the mode is skip
//   --repo owner/name    default $GITHUB_REPOSITORY
//   --max-repairs <n>    default $AI_MAX_REPAIRS or 3
// Runs twice: in the agent job, and again in the write job right before pushing, so a label that
// changed while the agent worked (ai:blocked, a new open PR) still stops the write.
// Prints `<n>:<mode>`; on Actions sets `mode`, `ref` (what the agent works on) and `branch`.
import { mkdirSync, writeFileSync, appendFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';
import { toolPath } from '../lib/tools.mjs';
import { isPlanComment } from '../lib/director.mjs';
import { isCouncilComment } from '../lib/council.mjs';
import { isGateComment, readGateData } from '../lib/gate.mjs';
import { isTriageComment } from '../../triage/lib/triage.mjs';
import { branchFor, decideMode, isDeveloperComment, pickMilestone } from '../lib/developer.mjs';

const args = Object.fromEntries(
  process.argv.slice(2).reduce((acc, a, i, all) => {
    if (a.startsWith('--')) acc.push([a.slice(2), all[i + 1]]);
    return acc;
  }, []),
);
const repo = args.repo ?? process.env.GITHUB_REPOSITORY;
const outDir = args.out ?? '.ai/in';
const maxRepairs = Number(args['max-repairs'] ?? process.env.AI_MAX_REPAIRS ?? 3);
if (!repo || !args.issue) {
  console.error('usage: collect.mjs --issue <n> [--out dir] [--repo owner/name]');
  process.exit(2);
}
const n = Number(args.issue);
const GH = toolPath('gh');
const gh = (path) => JSON.parse(execFileSync(GH, ['api', '--paginate', '--slurp', path], { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 })).flat();
const ghOne = (path) => JSON.parse(execFileSync(GH, ['api', path], { encoding: 'utf8' }));
const pipeline = (c) => isPlanComment(c) || isCouncilComment(c) || isTriageComment(c) || isGateComment(c) || isDeveloperComment(c);
const output = (o) => process.env.GITHUB_OUTPUT && appendFileSync(process.env.GITHUB_OUTPUT, Object.entries(o).map(([k, v]) => `${k}=${v}\n`).join(''));

const issue = ghOne(`repos/${repo}/issues/${n}`);
if (issue.pull_request || issue.state !== 'open') {
  console.log(`${n}:skip (not an open issue)`);
  output({ mode: 'skip', ref: '', branch: '' });
  process.exit(0);
}
const labels = issue.labels.map((l) => l.name);
const comments = gh(`repos/${repo}/issues/${n}/comments?per_page=100`);
const plan = comments.filter(isPlanComment).at(-1) ?? null;
const council = comments.filter(isCouncilComment).at(-1) ?? null;

// PRs that reference the issue, plus the pipeline's own branch even if it never referenced it.
const prs = new Map();
for (const e of gh(`repos/${repo}/issues/${n}/timeline?per_page=100`)) {
  const src = e.event === 'cross-referenced' ? e.source?.issue : null;
  if (src?.pull_request && src.repository?.full_name === repo) prs.set(src.number, src.number);
}
const [owner] = repo.split('/');
for (const p of gh(`repos/${repo}/pulls?state=all&head=${owner}:${branchFor(n)}&per_page=100`)) prs.set(p.number, p.number);
const details = [...prs.keys()].map((num) => ghOne(`repos/${repo}/pulls/${num}`));
const openPrs = details.filter((p) => p.state === 'open').map((p) => ({ number: p.number, head: p.head.ref, headSha: p.head.sha, title: p.title, author: p.user?.login }));
const closedPrs = details.filter((p) => p.state === 'closed' && !p.merged_at).map((p) => ({ number: p.number, head: p.head.ref, title: p.title }));
const ours = openPrs.find((p) => p.head === branchFor(n));
const prComments = ours ? gh(`repos/${repo}/issues/${ours.number}/comments?per_page=100`) : [];
const gate = prComments.filter(isGateComment).at(-1) ?? null;
const repairs = readGateData(gate?.body)?.repairShas?.length ?? 0;

const decision = decideMode({ number: n, labels, directorPlan: plan?.body ?? null, openPrs, closedPrs, repairs, maxRepairs });
console.log(`${n}:${decision.mode}${decision.reason ? ` (${decision.reason})` : ''}`);
if (decision.mode === 'skip') {
  output({ mode: 'skip', ref: '', branch: '', reason: decision.reason });
  process.exit(0);
}

const milestones = gh(`repos/${repo}/milestones?state=open&per_page=100`).map((m) => m.title);
const input = {
  number: n,
  mode: decision.mode,
  title: issue.title,
  body: issue.body ?? '',
  labels,
  comments: comments.filter((c) => !pipeline(c)).slice(-20).map((c) => ({ author: c.user?.login, body: c.body ?? '' })),
  milestone: pickMilestone(milestones),
  directorPlan: plan.body,
  directorPlanUrl: plan.html_url,
  council: council?.body ?? null,
  councilUrl: council?.html_url ?? null,
  closedPrs,
  repairs,
  maxRepairs,
};
if (decision.mode === 'FIX') {
  const reviews = gh(`repos/${repo}/pulls/${ours.number}/reviews?per_page=100`).filter((r) => r.body).map((r) => ({ author: r.user?.login, state: r.state, body: r.body }));
  const inline = gh(`repos/${repo}/pulls/${ours.number}/comments?per_page=100`).map((c) => ({ author: c.user?.login, path: c.path, line: c.line, body: c.body }));
  input.pr = { number: ours.number, branch: ours.head, headSha: ours.headSha };
  input.gate = gate?.body ?? null;
  input.reviewComments = [...reviews, ...inline].slice(-40);
}
mkdirSync(outDir, { recursive: true });
writeFileSync(join(outDir, `${n}.json`), JSON.stringify(input, null, 2));
output({ mode: decision.mode, ref: decision.mode === 'FIX' ? ours.headSha : '', branch: branchFor(n) });
