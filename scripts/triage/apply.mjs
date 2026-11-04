#!/usr/bin/env node
// Applies the agent's results. For every <n>.json in --in that has a matching <n>.json result
// in --results: validate, upsert the sticky triage comment, and in `apply` mode add labels and
// set the issue type, then fill what is still empty from the taxonomy's `defaults`: milestone, org
// issue fields (Priority, Effort) and the org project with its fields. `dry-run` (default) writes
// the comment only.
//   --in <dir>            default .triage/in (issue snapshots from collect.mjs)
//   --results <dir>       default .triage/out (the agent's stdout: `agent -p --output-format json` or bare JSON)
//   --mode dry-run|apply
//   --print               print the plan, write nothing to GitHub (local testing)
//   --taxonomy <path>     default .github/triage/labels.json (CI passes the copy staged before the agent ran)
//   --repo owner/name     default $GITHUB_REPOSITORY;  --sha <commit> for file links
// Secrets in $TRIAGE_REDACT (newline-separated) are redacted from every comment.
// $TRIAGE_APP_TOKEN (GitHub App installation token) writes the org issue fields and the project;
// without it those two are skipped. Comments, labels, type and milestone use $GH_TOKEN.
import { readFileSync, readdirSync, existsSync, appendFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';
import { extractResult, validateResult, renderComment, planActions, planDefaults, isTriageComment } from './lib/triage.mjs';

const args = Object.fromEntries(
  process.argv.slice(2).reduce((acc, a, i, all) => {
    if (a === '--print') acc.push(['print', true]);
    else if (a.startsWith('--')) acc.push([a.slice(2), all[i + 1]]);
    return acc;
  }, []),
);
const repo = args.repo ?? process.env.GITHUB_REPOSITORY;
const inDir = args.in ?? '.triage/in';
const resultsDir = args.results ?? '.triage/out';
const mode = args.mode ?? 'dry-run';
const sha = args.sha ?? process.env.GITHUB_SHA;
if (!['dry-run', 'apply'].includes(mode)) {
  console.error(`--mode must be dry-run or apply, got ${mode}`);
  process.exit(2);
}

const taxonomy = JSON.parse(readFileSync(args.taxonomy ?? '.github/triage/labels.json', 'utf8'));
const secrets = (process.env.TRIAGE_REDACT ?? '').split('\n').map((s) => s.trim()).filter(Boolean);
const gh = (ghArgs, input) => execFileSync('gh', ghArgs, { encoding: 'utf8', input });
const appToken = process.env.TRIAGE_APP_TOKEN;
const ghApp = (ghArgs, input) => execFileSync('gh', ghArgs, { encoding: 'utf8', input, env: { ...process.env, GH_TOKEN: appToken } });
const graphql = (query, variables) => JSON.parse(ghApp(['api', 'graphql', '--input', '-'], JSON.stringify({ query, variables }))).data;
const owner = repo?.split('/')[0];
const defaults = taxonomy.defaults ?? {};
// Only committed files count: the agent can create files, it cannot add them to the index.
const tracked = new Set(execFileSync('git', ['ls-files', '-z'], { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 }).split('\0'));

function upsertComment(number, body) {
  const comments = JSON.parse(gh(['api', '--paginate', '--slurp', `repos/${repo}/issues/${number}/comments?per_page=100`])).flat();
  const existing = comments.find(isTriageComment);
  const payload = JSON.stringify({ body });
  if (existing) gh(['api', '-X', 'PATCH', `repos/${repo}/issues/comments/${existing.id}`, '--input', '-'], payload);
  else gh(['api', '-X', 'POST', `repos/${repo}/issues/${number}/comments`, '--input', '-'], payload);
}

// Looked up once, on first use.
const lazy = (load) => {
  let value;
  return () => (value ??= load());
};
const milestones = lazy(() =>
  new Map(JSON.parse(gh(['api', '--paginate', '--slurp', `repos/${repo}/milestones?state=open&per_page=100`])).flat().map((m) => [m.title, m.number])),
);
const issueFieldDefs = lazy(() => new Map(JSON.parse(ghApp(['api', `orgs/${owner}/issue-fields`])).map((f) => [f.name, f])));
const project = lazy(() => {
  const nodes = graphql(
    `query($owner: String!, $title: String!) { organization(login: $owner) { projectsV2(first: 20, query: $title) { nodes {
       id title fields(first: 50) { nodes { ... on ProjectV2SingleSelectField { id name options { id name } } } } } } } }`,
    { owner, title: defaults.project.title },
  ).organization.projectsV2.nodes;
  const found = nodes.find((p) => p.title === defaults.project.title);
  if (!found) throw new Error(`project "${defaults.project.title}" not found`);
  return found;
});

/** Sets the empty org issue fields; returns the names written. */
function applyIssueFields(number, fields) {
  const current = JSON.parse(ghApp(['api', `repos/${repo}/issues/${number}/issue-field-values`]));
  const values = [];
  const written = [];
  for (const { name, value } of fields) {
    if (current.some((v) => v.issue_field_name === name)) continue;
    const def = issueFieldDefs().get(name);
    const option = def?.options?.find((o) => o.name === value);
    if (!option) throw new Error(`issue field ${name} has no option ${value}`);
    values.push({ field_id: def.id, value: option.name });
    written.push(`${name}=${value}`);
  }
  if (values.length) ghApp(['api', '-X', 'POST', `repos/${repo}/issues/${number}/issue-field-values`, '--input', '-'], JSON.stringify({ issue_field_values: values }));
  return written;
}

/**
 * Adds the issue to the project unless a person already put it in another project, then fills
 * the project's single-select fields that are still empty. Returns what was written.
 */
function applyProject(issue) {
  const p = project();
  const items = graphql(
    `query($id: ID!) { node(id: $id) { ... on Issue { projectItems(first: 20) { nodes { id project { id } } } } } }`,
    { id: issue.nodeId },
  ).node.projectItems.nodes;
  let item = items.find((i) => i.project.id === p.id);
  if (!item && items.length) return [];
  const written = [];
  if (!item) {
    item = graphql(
      `mutation($project: ID!, $content: ID!) { addProjectV2ItemById(input: { projectId: $project, contentId: $content }) { item { id } } }`,
      { project: p.id, content: issue.nodeId },
    ).addProjectV2ItemById.item;
    written.push(p.title);
  }
  for (const [name, value] of Object.entries(defaults.project.fields ?? {})) {
    const field = p.fields.nodes.find((f) => f?.name === name);
    const option = field?.options?.find((o) => o.name === value);
    if (!option) throw new Error(`project field ${name} has no option ${value}`);
    const set = graphql(
      `query($id: ID!, $name: String!) { node(id: $id) { ... on ProjectV2Item { fieldValueByName(name: $name) { ... on ProjectV2ItemFieldSingleSelectValue { optionId } } } } }`,
      { id: item.id, name },
    ).node.fieldValueByName;
    if (set?.optionId) continue;
    graphql(
      `mutation($project: ID!, $item: ID!, $field: ID!, $option: String!) {
         updateProjectV2ItemFieldValue(input: { projectId: $project, itemId: $item, fieldId: $field, value: { singleSelectOptionId: $option } }) { projectV2Item { id } } }`,
      { project: p.id, item: item.id, field: field.id, option: option.id },
    );
    written.push(`${name}=${value}`);
  }
  return written;
}

/** Each default is independent: one failing (missing permission, renamed option) never blocks the rest. */
function applyDefaults(issue, plan) {
  const done = [];
  const attempt = (what, fn) => {
    try {
      const out = fn();
      if (out?.length) done.push(...out);
    } catch (e) {
      console.log(`::warning::#${issue.number}: ${what} not set (${e.message.split('\n')[0]})`);
    }
  };
  if (plan.milestone) {
    attempt('milestone', () => {
      const number = milestones().get(plan.milestone);
      if (!number) throw new Error(`no open milestone "${plan.milestone}"`);
      gh(['api', '-X', 'PATCH', `repos/${repo}/issues/${issue.number}`, '-F', `milestone=${number}`]);
      return [`milestone=${plan.milestone}`];
    });
  }
  if (!appToken) {
    if (plan.fields.length || defaults.project) console.log(`::notice::#${issue.number}: TRIAGE_APP_TOKEN not set; issue fields and project skipped`);
    return done;
  }
  if (plan.fields.length) attempt('issue fields', () => applyIssueFields(issue.number, plan.fields));
  if (defaults.project && issue.nodeId) attempt('project', () => applyProject(issue));
  return done;
}

let failures = 0;
const summary = ['| Issue | Type | Labels added | Defaults set | Dropped |', '|---|---|---|---|---|'];
for (const file of existsSync(inDir) ? readdirSync(inDir).filter((f) => f.endsWith('.json')) : []) {
  const issue = JSON.parse(readFileSync(join(inDir, file), 'utf8'));
  const resultPath = join(resultsDir, file);
  if (!existsSync(resultPath)) {
    console.log(`::warning::#${issue.number}: the agent wrote no result`);
    failures++;
    continue;
  }
  let raw;
  try {
    raw = extractResult(readFileSync(resultPath, 'utf8'));
  } catch (e) {
    console.log(`::warning::#${issue.number}: no usable result (${e.message})`);
    failures++;
    continue;
  }
  const { result, dropped } = validateResult(raw, taxonomy, { fileExists: (p) => tracked.has(p), secrets });
  const body = renderComment(result, { mode, repo, sha });
  const { addLabels, setType } = planActions(result, issue, mode, taxonomy);
  const plan = planDefaults(result, issue, mode, defaults);

  if (args.print) {
    const planned = [plan.milestone && `milestone=${plan.milestone}`, ...plan.fields.map((f) => `${f.name}=${f.value}`)].filter(Boolean);
    console.log(`----- #${issue.number} -----\n${body}\nlabels: ${addLabels.join(', ') || '-'} | type: ${setType ?? '-'} | defaults: ${planned.join(', ') || '-'} | dropped: ${dropped.join(', ') || '-'}`);
    continue;
  }
  try {
    upsertComment(issue.number, body);
    if (addLabels.length) gh(['issue', 'edit', String(issue.number), '--repo', repo, '--add-label', addLabels.join(',')]);
    if (setType) gh(['api', '-X', 'PATCH', `repos/${repo}/issues/${issue.number}`, '-f', `type=${setType}`]);
  } catch (e) {
    console.log(`::warning::#${issue.number}: GitHub write failed (${e.message.split('\n')[0]})`);
    failures++;
    summary.push(`| #${issue.number} | — | write failed | — | ${dropped.join(', ') || '—'} |`);
    continue;
  }
  const set = mode === 'apply' ? applyDefaults(issue, plan) : [];
  summary.push(`| #${issue.number} | ${setType ?? '—'} | ${addLabels.join(', ') || '—'} | ${set.join(', ') || '—'} | ${dropped.join(', ') || '—'} |`);
}

if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, `### Issue triage (${mode})\n\n${summary.join('\n')}\n`);
if (failures) console.log(`${failures} issue(s) not triaged; see warnings.`);
