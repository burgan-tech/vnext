#!/usr/bin/env node
// CLI over lib/e2e.mjs for .github/workflows/pr-e2e.yml and pr-e2e-report.yml.
//   e2e.mjs select                                → reads GITHUB_EVENT_PATH (+ E2E_INPUT_SUITES); prints run/suites/why lines for GITHUB_OUTPUT
//   e2e.mjs filter <tests-dir> <suites>           → prints the dotnet test filter ('' = full set); a note on stderr for unknown suites
//   e2e.mjs reds <run-url> <trx>...               → prints the Reds line, or nothing when no test failed
//   e2e.mjs apply-body <body> <evidence> <detail> → prints the new PR body; exit 3 when it has no E2E evidence section
import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { selectRun, buildFilter, redsLine, failedTests, applyEvidence } from './lib/e2e.mjs';

const [cmd, ...args] = process.argv.slice(2);
const read = (f) => readFileSync(f, 'utf8');

if (cmd === 'select') {
  const ev = JSON.parse(read(process.env.GITHUB_EVENT_PATH));
  const pr = ev.pull_request ?? {};
  const r = selectRun({
    eventName: process.env.GITHUB_EVENT_NAME,
    action: ev.action,
    label: ev.label?.name,
    headRef: pr.head?.ref,
    labels: (pr.labels ?? []).map((l) => l.name),
    body: pr.body,
    inputSuites: process.env.E2E_INPUT_SUITES,
  });
  console.log(`run=${r.run}\nsuites=${r.suites.join(',')}\nwhy=${r.why.replace(/[\r\n]/g, ' ')}`);
} else if (cmd === 'filter') {
  const [dir, list] = args;
  const available = readdirSync(dir, { withFileTypes: true }).filter((d) => d.isDirectory()).map((d) => d.name);
  if (existsSync(join(dir, 'SmokeTests.cs'))) available.push('Smoke');
  const r = buildFilter((list ?? '').split(',').filter(Boolean), available);
  if (r.note) console.error(r.note);
  console.log(r.filter);
} else if (cmd === 'reds') {
  const [url, ...trx] = args;
  const failed = trx.flatMap((f) => failedTests(read(f)));
  if (failed.length) console.log(redsLine(failed, url));
} else if (cmd === 'apply-body') {
  const [body, evidence, detail] = args;
  const out = applyEvidence(read(body), read(evidence), detail ?? '');
  if (out === null) process.exit(3);
  process.stdout.write(out);
} else {
  console.error('usage: e2e.mjs select | filter <tests-dir> <suites> | reds <run-url> <trx>... | apply-body <body> <evidence> <detail>');
  process.exit(2);
}
