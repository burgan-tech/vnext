#!/usr/bin/env node
// Creates or updates the labels in .github/triage/labels.json. Never deletes a label.
//   [--apply]            without it, prints what would change
//   [--repo owner/name]  default: the current gh repo
import { readFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';

const argv = process.argv.slice(2);
const apply = argv.includes('--apply');
const repoIdx = argv.indexOf('--repo');
const repoArgs = repoIdx >= 0 ? ['--repo', argv[repoIdx + 1]] : [];

const taxonomy = JSON.parse(readFileSync('.github/triage/labels.json', 'utf8'));
const wanted = [...taxonomy.kinds, ...taxonomy.areas, ...taxonomy.priorities, ...taxonomy.status];
const current = new Map(
  JSON.parse(execFileSync('gh', ['label', 'list', '--limit', '500', '--json', 'name,color,description', ...repoArgs], { encoding: 'utf8' }))
    .map((l) => [l.name, l]),
);

let changes = 0;
for (const label of wanted) {
  const have = current.get(label.name);
  const same = have && have.color.toLowerCase() === label.color.toLowerCase() && (have.description ?? '') === label.description;
  if (same) continue;
  changes++;
  console.log(`${have ? 'update' : 'create'} ${label.name}`);
  if (apply) {
    execFileSync('gh', ['label', 'create', label.name, '--color', label.color, '--description', label.description, '--force', ...repoArgs], { stdio: 'inherit' });
  }
}
console.log(changes ? `${changes} label(s) ${apply ? 'written' : 'would change; re-run with --apply'}` : 'labels already in sync');
