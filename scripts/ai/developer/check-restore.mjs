#!/usr/bin/env node
// FIX mode only: before the PR branch is restored (with network, no token), checks what the branch
// changed against the default branch for anything `dotnet restore` would evaluate (lib
// checkRestoreSafety). Humans may push to `ai/issue-<n>`, so the branch is not only the agent's own
// policy-checked diff. Run from the staged default-branch copy, inside the checked-out target.
//   check-restore.mjs <base-sha> <head-sha>
// Exit 0 = safe to restore; 1 = violations (printed); 2 = usage or git error.
import { execFileSync } from 'node:child_process';
import { toolPath } from '../lib/tools.mjs';
import { checkRestoreSafety, parseNameStatusZ } from '../lib/developer.mjs';

const [base, head] = process.argv.slice(2);
if (!/^[0-9a-f]{40}$/.test(base ?? '') || !/^[0-9a-f]{40}$/.test(head ?? '')) {
  console.error('usage: check-restore.mjs <base-sha> <head-sha>');
  process.exit(2);
}
const GIT = toolPath('git');
const git = (a) => execFileSync(GIT, ['-c', 'core.quotepath=false', '-c', 'core.hooksPath=/dev/null', '-c', 'core.fsmonitor=', ...a], {
  encoding: 'utf8', maxBuffer: 256 * 1024 * 1024, env: { ...process.env, GIT_CONFIG_GLOBAL: '/dev/null', GIT_CONFIG_NOSYSTEM: '1' },
});

let files, patch;
try {
  const mergeBase = git(['merge-base', base, head]).trim();
  files = parseNameStatusZ(git(['diff', '--no-renames', '--no-ext-diff', '--no-textconv', '--name-status', '-z', mergeBase, head]));
  patch = git(['diff', '--no-renames', '--no-ext-diff', '--no-textconv', '--binary', mergeBase, head]);
} catch (e) {
  console.error(`check-restore: ${e.message.split('\n')[0]}`);
  process.exit(2);
}
const v = checkRestoreSafety(files, patch);
if (v.length) {
  console.log(`::error::the PR branch changes what restore evaluates — not restored, nothing runs:\n${v.map((x) => `- ${x}`).join('\n')}`);
  process.exit(1);
}
console.log(`check-restore: ${files.length} changed file(s), nothing restore would evaluate differently`);
