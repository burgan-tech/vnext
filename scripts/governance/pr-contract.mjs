#!/usr/bin/env node
// CLI for the PR contract. Input sources:
//   --event <path>                     GitHub `pull_request` event JSON (files/commits via `gh api`)
//   --pr <n> [--repo owner/name]       replay a real PR via `gh pr view`
//   --body-file <path> --title <t> [--milestone vX.Y.Z] [--files-from-git <base>]   local draft
//   --mode warn|enforce                warn (default) prints errors as warnings and exits 0
import { readFileSync, appendFileSync, existsSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { checkPr } from './lib/pr-contract.mjs';
import { lintMeta } from './lib/meta-lint.mjs';

const COMMITS_CAP = 250;

function parseArgs(argv) {
  const out = {};
  for (let i = 0; i < argv.length; i++) {
    if (!argv[i].startsWith('--')) throw new Error(`Unexpected argument: ${argv[i]}`);
    const key = argv[i].slice(2);
    const value = argv[i + 1];
    if (value === undefined || value.startsWith('--')) throw new Error(`--${key} needs a value`);
    out[key] = value;
    i++;
  }
  return out;
}

const run = (cmd, args) => execFileSync(cmd, args, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
const lines = (text) => text.split('\n').map((l) => l.trim()).filter(Boolean);

function fromEvent(path) {
  const event = JSON.parse(readFileSync(path, 'utf8'));
  const p = event.pull_request;
  if (!p) throw new Error('Event has no pull_request payload.');
  const repo = event.repository?.full_name;
  const base = `repos/${repo}/pulls/${p.number}`;
  const changedFiles = lines(run('gh', ['api', `${base}/files`, '--paginate', '--jq', '.[].filename']));
  const commitShas = lines(run('gh', ['api', `${base}/commits`, '--paginate', '--jq', '.[].sha']));
  return {
    title: p.title ?? '',
    body: p.body ?? '',
    milestone: p.milestone ? { title: p.milestone.title, state: p.milestone.state } : null,
    changedFiles,
    commitShas,
    commitsCapped: commitShas.length >= COMMITS_CAP,
    draft: Boolean(p.draft),
  };
}

function fromReplay(n, repo) {
  const args = ['pr', 'view', n, '--json', 'title,body,milestone,files,commits'];
  if (repo) args.push('--repo', repo);
  const p = JSON.parse(run('gh', args));
  const commitShas = (p.commits ?? []).map((c) => c.oid);
  return {
    title: p.title ?? '',
    body: p.body ?? '',
    // gh does not report milestone state; a PR that carries one is judged by its title.
    milestone: p.milestone ? { title: p.milestone.title, state: p.milestone.state ?? 'open' } : null,
    changedFiles: (p.files ?? []).map((f) => f.path),
    commitShas,
    commitsCapped: commitShas.length >= COMMITS_CAP,
  };
}

function fromDraft(args) {
  let changedFiles = [];
  let commitShas = [];
  if (args['files-from-git']) {
    const base = args['files-from-git'];
    changedFiles = lines(run('git', ['diff', '--name-only', `${base}...HEAD`]));
    commitShas = lines(run('git', ['rev-list', `${base}..HEAD`]));
  }
  return {
    title: args.title ?? '',
    body: readFileSync(args['body-file'], 'utf8'),
    milestone: args.milestone ? { title: args.milestone, state: 'open' } : null,
    changedFiles,
    commitShas,
    commitsCapped: false,
  };
}

function main() {
  const args = parseArgs(process.argv.slice(2));
  const mode = args.mode ?? 'warn';
  if (!['warn', 'enforce'].includes(mode)) throw new Error('--mode must be warn or enforce');

  let pr;
  if (args.event) pr = fromEvent(args.event);
  else if (args.pr) pr = fromReplay(args.pr, args.repo);
  else if (args['body-file']) pr = fromDraft(args);
  else throw new Error('Provide one of --event, --pr or --body-file.');

  const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
  const readFile = (p) => {
    const full = resolve(repoRoot, p);
    return existsSync(full) ? readFileSync(full, 'utf8') : null;
  };

  const findings = checkPr({ ...pr, readFile });
  const milestone = pr.milestone && /^v\d+\.\d+\.\d+$/.test(pr.milestone.title) ? pr.milestone.title : undefined;
  findings.push(...lintMeta({ metaDir: resolve(repoRoot, 'vnext-meta'), repoRoot, milestone }));

  const effective = findings.map((f) => (mode === 'warn' && f.severity === 'error' ? { ...f, severity: 'warning' } : f));
  const inActions = process.env.GITHUB_ACTIONS === 'true';
  for (const f of effective) {
    if (inActions) console.log(`::${f.severity} title=${f.id}::${f.file}: ${f.message}`.replace(/\r?\n/g, '%0A'));
    else console.log(`${f.severity.toUpperCase()} ${f.id} ${f.file}: ${f.message}`);
  }
  if (!inActions && effective.length === 0) console.log('PR contract: no findings.');

  if (process.env.GITHUB_STEP_SUMMARY) {
    const esc = (s) => String(s).replace(/\|/g, '\\|').replace(/\r?\n/g, ' ');
    const rows = effective.map((f) => `| ${f.severity} | ${f.id} | ${esc(f.file)} | ${esc(f.message)} |`);
    const table = effective.length
      ? ['| Severity | Id | File | Message |', '|---|---|---|---|', ...rows].join('\n')
      : 'PR contract: no findings.';
    appendFileSync(process.env.GITHUB_STEP_SUMMARY, `## PR contract (${mode})\n\n${table}\n`);
  }

  process.exit(mode === 'enforce' && effective.some((f) => f.severity === 'error') ? 1 : 0);
}

try {
  main();
} catch (err) {
  console.error(`pr-contract: ${err.message}`);
  process.exit(2);
}
