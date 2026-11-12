#!/usr/bin/env node
// Creates or updates the labels in .github/triage/labels.json. Never deletes a label.
//   [--apply]            without it, prints what would change
//   [--repo owner/name]  default: the current gh repo
// Every top-level array of labels.json is a label group (kinds, areas, status, risk, …); a new
// group is picked up without a change here. `defaults` is configuration, not labels.
import { readFileSync, realpathSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

/** Every label of the taxonomy, in file order. */
export function taxonomyLabels(taxonomy) {
  return Object.entries(taxonomy)
    .filter(([key, value]) => key !== 'defaults' && Array.isArray(value))
    .flatMap(([, labels]) => labels);
}

function main() {
  const argv = process.argv.slice(2);
  const apply = argv.includes('--apply');
  const repoIdx = argv.indexOf('--repo');
  const repoArgs = repoIdx >= 0 ? ['--repo', argv[repoIdx + 1]] : [];

  const taxonomy = JSON.parse(readFileSync('.github/triage/labels.json', 'utf8'));
  const current = new Map(
    JSON.parse(execFileSync('gh', ['label', 'list', '--limit', '500', '--json', 'name,color,description', ...repoArgs], { encoding: 'utf8' }))
      .map((l) => [l.name, l]),
  );

  let changes = 0;
  for (const label of taxonomyLabels(taxonomy)) {
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
}

if (process.argv[1] && realpathSync(process.argv[1]) === fileURLToPath(import.meta.url)) main();
