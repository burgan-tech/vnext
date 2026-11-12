#!/usr/bin/env node
// Collects the PRs the Quality Gate looks at and their CI evidence; the agent's only input.
//   --pr <n>                       one PR (manual run)
//   --sha <sha> [--pr-list "1 2"]  workflow_run: the run's PR numbers, else PRs whose HEAD is <sha>
//   --sweep [--limit 2]            open PRs into a reviewed base whose evidence changed
//   --force                        review even when the evidence fingerprint is unchanged
//   --out <dir> default .ai/in     --config <path> default .github/ai/gate.json
//   --impact-rules <path>          default .github/ai/impact-paths.json
// Per PR writes <n>.json (snapshot + evaluated evidence + decision) and <n>.diff. Decisions:
//   unchanged     same fingerprint as the last gate comment: nothing to do
//   inconclusive  required evidence missing or running: comment, no agent
//   council       AI PR declared low impact but the diff touches a high-impact path: no agent
//   review        evidence complete: the agent reviews the diff
// Outputs (Actions): `prs` (everything but unchanged) and `agent` (review only).
// Sonar is read with $SONAR_TOKEN when set; without it a failed Sonar gate stays inconclusive.
import { mkdirSync, writeFileSync, appendFileSync, readFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';
import { toolPath } from '../lib/tools.mjs';
import { isGateComment, readGateData, isReviewedBase, evaluateEvidence, fingerprint } from '../lib/gate.mjs';
import { isPlanComment } from '../lib/director.mjs';
import { compileImpactRules, matchImpactPaths } from '../../governance/lib/ai-record.mjs';

const args = Object.fromEntries(
  process.argv.slice(2).reduce((acc, a, i, all) => {
    if (a === '--sweep' || a === '--force') acc.push([a.slice(2), true]);
    else if (a.startsWith('--')) acc.push([a.slice(2), all[i + 1]]);
    return acc;
  }, []),
);
const repo = args.repo ?? process.env.GITHUB_REPOSITORY;
const outDir = args.out ?? '.ai/in';
const config = JSON.parse(readFileSync(args.config ?? '.github/ai/gate.json', 'utf8'));
const rules = compileImpactRules(JSON.parse(readFileSync(args['impact-rules'] ?? '.github/ai/impact-paths.json', 'utf8')));
if (!repo || (!args.pr && !args.sha && !args.sweep)) {
  console.error('usage: collect.mjs --pr <n> | --sha <sha> [--pr-list "1 2"] | --sweep [--limit 2] [--force]');
  process.exit(2);
}

const GH = toolPath('gh');
const run = (a, opts = {}) => execFileSync(GH, a, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024, ...opts });
const gh = (path) => JSON.parse(run(['api', '--paginate', '--slurp', path])).flat();
const ghOne = (path) => JSON.parse(run(['api', path]));
const MAX_DIFF = 400_000;

async function readSonar(pr) {
  const token = process.env.SONAR_TOKEN;
  if (!token) return null;
  const headers = { Authorization: `Basic ${Buffer.from(`${token}:`).toString('base64')}` };
  const get = async (path) => {
    const res = await fetch(`https://sonarcloud.io/api/${path}`, { headers });
    if (!res.ok) throw new Error(`sonar ${res.status}`);
    return res.json();
  };
  try {
    const key = encodeURIComponent(config.sonar.projectKey);
    const gate = await get(`qualitygates/project_status?projectKey=${key}&pullRequest=${pr}`);
    const issues = await get(`issues/search?componentKeys=${key}&pullRequest=${pr}&resolved=false&types=BUG,VULNERABILITY&ps=50`);
    return {
      status: gate.projectStatus?.status,
      conditions: (gate.projectStatus?.conditions ?? []).map((c) => ({ metricKey: c.metricKey, status: c.status, actualValue: c.actualValue, errorThreshold: c.errorThreshold })),
      issues: (issues.issues ?? []).map((i) => ({ key: i.key, type: i.type, rule: i.rule, component: String(i.component).split(':').pop(), line: i.line, message: i.message })),
    };
  } catch (e) {
    console.log(`::warning::#${pr}: Sonar not readable (${e.message})`);
    return null;
  }
}

function contractFindings(checks) {
  const run = checks.find((c) => c.name === 'pr-contract' && c.status === 'completed');
  if (!run) return [];
  return gh(`repos/${repo}/check-runs/${run.id}/annotations?per_page=100`)
    .filter((a) => /^(PR|META)-[A-Z0-9-]+$/.test(a.title ?? ''))
    .map((a) => ({ id: a.title, level: a.annotation_level, message: a.message }));
}

function linkedIssue(pr) {
  let number = null;
  try {
    const q = `query($o:String!,$r:String!,$n:Int!){repository(owner:$o,name:$r){pullRequest(number:$n){closingIssuesReferences(first:1){nodes{number}}}}}`;
    const [o, r] = repo.split('/');
    const res = JSON.parse(run(['api', 'graphql', '-f', `query=${q}`, '-f', `o=${o}`, '-f', `r=${r}`, '-F', `n=${pr.number}`]));
    number = res.data.repository.pullRequest.closingIssuesReferences.nodes[0]?.number ?? null;
  } catch {
    // fall back to the body below
  }
  number ??= Number(/^\s*[-*]?\s*Issue:\s*#(\d+)/im.exec(pr.body ?? '')?.[1] ?? /\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s+#(\d+)/i.exec(pr.body ?? '')?.[1] ?? 0) || null;
  if (!number) return null;
  const issue = ghOne(`repos/${repo}/issues/${number}`);
  const plan = gh(`repos/${repo}/issues/${number}/comments?per_page=100`).filter(isPlanComment).at(-1)?.body ?? null;
  return { number, title: issue.title, state: issue.state, labels: issue.labels.map((l) => l.name), directorPlan: plan };
}

async function snapshot(number) {
  const pr = ghOne(`repos/${repo}/pulls/${number}`);
  if (pr.state !== 'open' || !isReviewedBase(pr.base.ref, config.baseBranches)) return null;
  const sha = pr.head.sha;
  const files = gh(`repos/${repo}/pulls/${number}/files?per_page=100`).map((f) => f.filename);
  const checks = gh(`repos/${repo}/commits/${sha}/check-runs?per_page=100`).flatMap((p) => p.check_runs ?? p);
  const statuses = ghOne(`repos/${repo}/commits/${sha}/status`).statuses ?? [];
  const sonar = await readSonar(number);
  const contract = contractFindings(checks);
  const issue = linkedIssue(pr);
  const prLabels = pr.labels.map((l) => l.name);
  const isAiPr = prLabels.some((l) => l.startsWith('ai:')) || (issue?.labels ?? []).some((l) => l.startsWith('ai:') && l !== 'ai:triaged');
  const impactHits = isAiPr && prLabels.includes('ai:impact-low') ? matchImpactPaths(files, rules) : [];
  const previous = gh(`repos/${repo}/issues/${number}/comments?per_page=100`).filter(isGateComment).map((c) => readGateData(c.body)).filter(Boolean).at(-1) ?? null;

  const slim = checks.map((c) => ({ id: c.id, name: c.name, status: c.status, conclusion: c.conclusion, started_at: c.started_at }));
  const slimStatuses = statuses.map((s) => ({ context: s.context, state: s.state }));
  const fp = fingerprint({ sha, checks: slim, statuses: slimStatuses, sonar, contract, labels: [...prLabels, ...(issue?.labels ?? [])] });
  const evaluation = evaluateEvidence({ checks: slim, statuses: slimStatuses, sonar, contract, isAiPr }, config);

  let decision;
  if (previous?.fp === fp && !args.force) decision = 'unchanged';
  else if (!evaluation.complete) decision = 'inconclusive';
  else if (impactHits.length) decision = 'council';
  else decision = 'review';

  let diff = '';
  if (decision === 'review') {
    diff = run(['api', '-H', 'Accept: application/vnd.github.diff', `repos/${repo}/pulls/${number}`]);
    if (diff.length > MAX_DIFF) diff = `${diff.slice(0, MAX_DIFF)}\n… diff truncated at ${MAX_DIFF} bytes; read the changed files directly.\n`;
  }
  const riskLabel = [...prLabels, ...(issue?.labels ?? [])].find((l) => l.startsWith('ai:risk-'));
  return {
    snapshot: {
      pr: { number, title: pr.title, body: pr.body ?? '', author: pr.user?.login, base: pr.base.ref, headSha: sha, draft: pr.draft, labels: prLabels, changedFiles: files },
      issue,
      evidence: { rows: evaluation.rows, blocking: evaluation.blocking, warnings: evaluation.warnings, pending: evaluation.pending },
      raw: { checks: slim, statuses: slimStatuses, sonar, contract },
      isAiPr,
      impactHits,
      previous,
      fp,
      risk: riskLabel ? riskLabel.slice('ai:risk-'.length) : null,
      decision,
    },
    diff,
  };
}

function prsForSha(sha) {
  if (args['pr-list']) return args['pr-list'].split(/\s+/).filter(Boolean).map(Number);
  return gh(`repos/${repo}/commits/${sha}/pulls`).filter((p) => p.state === 'open').map((p) => p.number);
}

let numbers;
if (args.pr) numbers = [Number(args.pr)];
else if (args.sha) numbers = prsForSha(args.sha);
else {
  numbers = gh(`repos/${repo}/pulls?state=open&per_page=100&sort=updated&direction=desc`)
    .filter((p) => isReviewedBase(p.base.ref, config.baseBranches))
    .map((p) => p.number);
}

mkdirSync(outDir, { recursive: true });
const limit = Number(args.limit ?? 2);
const prs = [];
const agent = [];
for (const n of numbers) {
  if (args.sweep && prs.length >= limit) break;
  const s = await snapshot(n);
  if (!s) continue;
  console.log(`${n}:${s.snapshot.decision}`);
  if (s.snapshot.decision === 'unchanged') continue;
  writeFileSync(join(outDir, `${n}.json`), JSON.stringify(s.snapshot, null, 2));
  if (s.diff) writeFileSync(join(outDir, `${n}.diff`), s.diff);
  prs.push(n);
  if (s.snapshot.decision === 'review') agent.push(n);
}
if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT, `prs=${prs.join(' ')}\nagent=${agent.join(' ')}\n`);
