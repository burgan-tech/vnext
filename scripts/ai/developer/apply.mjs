#!/usr/bin/env node
// The Developer's trusted writer — runs in a separate job on a fresh runner, from the default-branch
// checkout, after the agent job finished. It is the only step that commits, pushes, opens a PR or
// writes labels. For the issue in --in (re-collected in this job, so a label that changed meanwhile
// still stops it):
//   1. reads the agent job's artifact: patch.diff, files.tsv (git diff --name-status), result.json,
//      base.txt (the commit the agent worked on), runner.json (its sandboxed build/test, informational);
//   2. validates the result, applies the patch on base and runs the diff policy and the secret scan on
//      what git applied (not on the agent job's own listing) — any violation: nothing is pushed;
//   3. adds the council decision-log row (NEW / RECOVERY with an approved
//      council), commits, pushes `ai/issue-<n>` with the App token, opens the draft PR (NEW /
//      RECOVERY) or updates it (FIX), and labels the PR with the issue's classification;
//   4. apply mode only: moves the issue's state label. One sticky status comment on the issue.
//   --in <dir>  --artifact <dir>  --mode dry-run|apply  --repo owner/name  --print (no writes)
// $GH_TOKEN comments; $AI_APP_TOKEN pushes, PRs and labels (so CI and the next role start);
// $AI_REDACT secrets to redact and to refuse in the patch.
import { readFileSync, writeFileSync, existsSync, readdirSync, appendFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';
import { extractResult } from '../../triage/lib/triage.mjs';
import { toolPath } from '../lib/tools.mjs';
import { readPlanData } from '../lib/director.mjs';
import {
  branchFor, validateResult, checkDiff, findSecrets, prLabels, developerLabels, extractCouncilRow, insertLogRow,
  renderPrBody, updateRepairs, renderDeveloperComment, isDeveloperComment,
} from '../lib/developer.mjs';

const args = Object.fromEntries(
  process.argv.slice(2).reduce((acc, a, i, all) => {
    if (a === '--print') acc.push(['print', true]);
    else if (a.startsWith('--')) acc.push([a.slice(2), all[i + 1]]);
    return acc;
  }, []),
);
const repo = args.repo ?? process.env.GITHUB_REPOSITORY;
const mode = args.mode ?? 'dry-run';
const inDir = args.in ?? '.ai/in';
const art = args.artifact ?? '.ai/artifact';
if (!['dry-run', 'apply'].includes(mode) || !repo) {
  console.error('usage: apply.mjs --in <dir> --artifact <dir> --mode dry-run|apply [--repo owner/name] [--print]');
  process.exit(2);
}
const secrets = (process.env.AI_REDACT ?? '').split('\n').map((s) => s.trim()).filter(Boolean);
const appToken = process.env.AI_APP_TOKEN;
const GH = toolPath('gh');
const GIT = toolPath('git');
const gh = (a, input) => execFileSync(GH, a, { encoding: 'utf8', input, maxBuffer: 64 * 1024 * 1024 });
const ghApp = (a, input) => {
  if (!appToken) throw new Error('AI_APP_TOKEN not set');
  return execFileSync(GH, a, { encoding: 'utf8', input, env: { ...process.env, GH_TOKEN: appToken } });
};
const git = (a, opts = {}) => execFileSync(GIT, a, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024, ...opts });
const E2E_REASON = 'the e2e job (pr-e2e.yml, rollout step 5) is not built yet; run the listed suites locally';
const summary = ['| Issue | Mode | Outcome | PR | Notes |', '|---|---|---|---|---|'];

const files = readdirSync(inDir).filter((f) => f.endsWith('.json'));
if (!files.length) {
  console.log('No eligible issue after re-reading its labels; nothing is written.');
  process.exit(0);
}
const input = JSON.parse(readFileSync(join(inDir, files[0]), 'utf8'));
const n = input.number;
const branch = branchFor(n);
const read = (f) => (existsSync(join(art, f)) ? readFileSync(join(art, f), 'utf8') : null);

function upsertIssueComment(body) {
  const comments = JSON.parse(gh(['api', '--paginate', '--slurp', `repos/${repo}/issues/${n}/comments?per_page=100`])).flat();
  const existing = comments.filter(isDeveloperComment).at(-1);
  const payload = JSON.stringify({ body });
  if (existing) gh(['api', '-X', 'PATCH', `repos/${repo}/issues/comments/${existing.id}`, '--input', '-'], payload);
  else gh(['api', '-X', 'POST', `repos/${repo}/issues/${n}/comments`, '--input', '-'], payload);
}

function editLabels(number, add, remove) {
  if (!add.length && !remove.length) return;
  const edit = ['issue', 'edit', String(number), '--repo', repo];
  if (add.length) edit.push('--add-label', add.join(','));
  if (remove.length) edit.push('--remove-label', remove.join(','));
  ghApp(edit);
}

const auth = () => `AUTHORIZATION: basic ${Buffer.from(`x-access-token:${appToken}`).toString('base64')}`;
const remote = `https://github.com/${repo}.git`;
const parseStatus = (text) => text.split('\n').filter(Boolean).map((l) => {
  const [status, path] = l.split('\t');
  return { status, path };
});

/**
 * Applies the patch on `base` in the index and returns what git itself says changed: the file
 * list and the diff the policy is checked on. Nothing from the agent job's own listing is trusted.
 */
function stage(base, patch) {
  git(['-c', `http.extraheader=${auth()}`, 'fetch', '--no-tags', '--depth', '50', remote, base]);
  git(['switch', '--force-create', branch, base]);
  writeFileSync(join(art, '.apply.diff'), patch);
  git(['apply', '--index', '--whitespace=nowarn', join(art, '.apply.diff')]);
  return {
    files: parseStatus(git(['diff', '--cached', '--no-renames', '--name-status', base])),
    diff: git(['diff', '--cached', '--no-renames', '--binary', base]),
  };
}

/** Commits what `stage` applied (plus the council log row) and pushes `branch`; returns the new HEAD SHA. */
function commitAndPush(result, council) {
  if (council) {
    const path = 'docs/agent-council/sessions/README.md';
    const next = insertLogRow(readFileSync(path, 'utf8'), council);
    if (next) {
      writeFileSync(path, next);
      git(['add', path]);
    }
  }
  const message = `${result.title}\n\n${result.commitMessage || result.summary.join('\n')}\n\nRefs #${n}`;
  git(['-c', 'user.name=vnext-ai-developer', '-c', 'user.email=ai-developer@users.noreply.github.com', 'commit', '--quiet', '-F', '-'], { input: message });
  // NEW / RECOVERY replace a stale branch left by a closed attempt; FIX only fast-forwards the PR branch.
  const force = result.mode === 'FIX' ? [] : ['--force'];
  git(['-c', `http.extraheader=${auth()}`, 'push', '--quiet', ...force, remote, `HEAD:refs/heads/${branch}`]);
  return git(['rev-parse', 'HEAD']).trim();
}

let outcome = 'failed';
let reason = '';
let violations = [];
let result = null;
let prUrl = null;
try {
  const base = (read('base.txt') ?? '').trim();
  if (!/^[0-9a-f]{40}$/.test(base)) throw new Error('the agent job left no base commit');
  if (input.mode === 'FIX' && input.pr.headSha !== base) throw new Error(`the PR moved while the agent worked (${base.slice(0, 7)} → ${input.pr.headSha.slice(0, 7)}); the next run retries`);
  const raw = read('result.json');
  if (!raw) throw new Error('the agent wrote no result');
  ({ result } = validateResult(extractResult(raw), { secrets, mode: input.mode }));

  if (result.decision === 'needs-replan') {
    outcome = 'needs-replan';
    reason = 'the agent could not implement the plan as written';
  } else if (result.decision === 'no-change') {
    outcome = 'no-change';
    reason = `the agent found nothing to change${result.notes ? `: ${result.notes}` : ''}`;
  } else {
    const patch = read('patch.diff') ?? '';
    const listed = parseStatus(read('files.tsv') ?? '');
    // --print checks the artifact as it is; a real run checks what git applied, and refuses when
    // the two disagree.
    const applied = args.print ? { files: listed, diff: patch } : stage(base, patch);
    violations = [...checkDiff(applied.files, applied.diff), ...findSecrets(applied.diff, secrets)];
    const key = (fs) => fs.map((f) => `${f.status[0]} ${f.path}`).sort().join('\n');
    if (key(listed) !== key(applied.files)) violations.push('the applied change differs from the file list the agent job recorded');
    if (!result.title) violations.push('no valid Conventional Commits title (type(scope): subject, at most 72 characters)');
    if (violations.length) {
      outcome = 'rejected';
      reason = 'the diff breaks the pipeline policy (§13)';
    } else if (args.print) {
      outcome = 'pushed';
      reason = 'print only — nothing pushed';
    } else {
      const councilRow = input.mode !== 'FIX' && input.labels.includes('ai:council-approved') ? extractCouncilRow(input.council) : null;
      const sha = commitAndPush(result, councilRow);
      const runner = JSON.parse(read('runner.json') ?? '{}');
      for (const k of Object.keys(runner)) runner[k].detail = `${runner[k].detail ?? ''} (sandboxed runner, informational)`.trim();
      const labels = prLabels(input.labels);
      if (input.mode === 'FIX') {
        const pr = JSON.parse(gh(['api', `repos/${repo}/pulls/${input.pr.number}`]));
        ghApp(['api', '-X', 'PATCH', `repos/${repo}/pulls/${input.pr.number}`, '--input', '-'], JSON.stringify({ body: updateRepairs(pr.body, input.repairs, input.maxRepairs) }));
        ghApp(['api', '-X', 'POST', `repos/${repo}/issues/${input.pr.number}/comments`, '--input', '-'], JSON.stringify({ body: `🛠️ **AI Developer — repair ${input.repairs}/${input.maxRepairs}** at \`${sha.slice(0, 12)}\`\n\n${result.summary.map((s) => `- ${s}`).join('\n')}` }));
        prUrl = pr.html_url;
      } else {
        const body = renderPrBody({
          issue: n, result, plan: readPlanData(input.directorPlan), labels: input.labels, councilUrl: input.councilUrl,
          planUrl: input.directorPlanUrl, branch, runner, repairs: 0, maxRepairs: input.maxRepairs, e2eReason: E2E_REASON,
        });
        const create = ['pr', 'create', '--repo', repo, '--draft', '--base', 'master', '--head', branch, '--title', result.title, '--body-file', '-'];
        if (input.milestone) create.push('--milestone', input.milestone);
        prUrl = ghApp(create, body).trim().split('\n').at(-1);
      }
      const prNumber = Number(/\/pull\/(\d+)/.exec(prUrl)?.[1]);
      if (labels.length && prNumber) editLabels(prNumber, labels, []);
      outcome = 'pushed';
      reason = `pushed \`${sha.slice(0, 12)}\` to \`${branch}\``;
    }
  }
} catch (e) {
  outcome = 'failed';
  reason = e.message.split('\n')[0];
  console.log(`::warning::#${n}: ${reason}`);
}

const labels = developerLabels(outcome, input.labels);
const comment = renderDeveloperComment({ issue: n, mode: input.mode, outcome, reason, prUrl, violations, result, runMode: mode, labels });
if (args.print) {
  console.log(comment);
} else {
  try {
    upsertIssueComment(comment);
    if (mode === 'apply') editLabels(n, labels.add, labels.remove);
  } catch (e) {
    console.log(`::warning::#${n}: status not written (${e.message.split('\n')[0]})`);
    process.exitCode = 1;
  }
}
summary.push(`| #${n} | ${input.mode} | ${outcome} | ${prUrl ?? '—'} | ${(reason || '—').replaceAll('|', '/')} |`);
if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, `### AI Developer (${mode})\n\n${summary.join('\n')}\n${violations.length ? `\n${violations.map((v) => `- ${v}`).join('\n')}\n` : ''}`);
else console.log(summary.join('\n'));
if (outcome === 'failed') process.exitCode = 1;
