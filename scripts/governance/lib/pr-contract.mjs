// PR contract rules. Pure: the only I/O is the injected `readFile`.
// Dependency-free: Node built-ins only.
import {
  PRODUCTS,
  parseSections,
  isBlank,
  parseEvidence,
  parseProductImpact,
  parseMetaImpact,
} from './pr-body.mjs';
import { META_FILES, isFragmentPath } from './meta-lint.mjs';

/** @typedef {{id:string, severity:'error'|'warning', file:string, message:string}} Finding */

const FULL_TYPES = ['feat', 'fix', 'perf', 'refactor'];
const LIGHT_TYPES = ['test', 'docs', 'chore', 'ci', 'style', 'revert'];
const GITHUB_REVERT_RE = /^Revert ".+"$/; // GitHub's auto title for a revert PR; light tier, no length cap
const TITLE_RE = new RegExp(`^(${[...FULL_TYPES, ...LIGHT_TYPES].join('|')})(\\([^()\\s]+\\))?(!)?: \\S.*$`);
const RELEASE_RE = /^v\d+\.\d+\.\d+$/;
const MILESTONE_RE = /^v\d+\.\d+\.\d+$/;
const RELEASE_LINE_RE = /^release-v\d+\.\d+$/;
const BACKPORT_RE = /\bBackport of #(\d+)\b/;
const FORWARD_PORT_LABEL = 'needs-forward-port';
const MIN_REASON = 10;

const REQUIRED_FULL = ['summary', 'vnext scope', 'e2e evidence', 'product impact', 'vnext-meta impact'];

/** Extracts `key: value` pairs from a leading `---` frontmatter block; null when absent. */
function parseFrontmatter(text) {
  const m = /^---\r?\n([\s\S]*?)\r?\n---(\r?\n|$)/.exec(String(text ?? ''));
  if (!m) return null;
  const out = {};
  for (const line of m[1].split(/\r?\n/)) {
    const kv = /^([A-Za-z0-9_-]+)\s*:\s*(.*?)\s*$/.exec(line);
    if (kv) out[kv[1]] = kv[2].replace(/^["']|["']$/g, '');
  }
  return out;
}

/** @returns {Finding[]} */
export function checkPr(pr) {
  const findings = [];
  const add = (id, message, severity = 'error', file = 'PR') => findings.push({ id, severity, file, message });

  const title = (pr.title ?? '').trim();
  if (RELEASE_RE.test(title)) return findings; // release merge PRs are exempt from everything

  const githubRevert = GITHUB_REVERT_RE.test(title);
  const tm = githubRevert ? null : TITLE_RE.exec(title);
  if (!githubRevert && (!tm || title.length > 72)) {
    add('PR-TITLE', 'Title must be Conventional Commits (`type(scope)!: subject`, at most 72 characters) or a release title `vX.Y.Z`.');
    return findings;
  }
  const light = githubRevert || LIGHT_TYPES.includes(tm[1]);
  const sections = parseSections(pr.body ?? '');

  const present = (key) => sections.has(key) && !isBlank(sections.get(key));
  const required = light ? ['summary'] : REQUIRED_FULL;
  for (const key of required) {
    if (!present(key)) add('PR-SECTION', `Section "${key}" is missing or still blank.`);
  }
  if (light) return findings;

  const milestoneTitle = pr.milestone?.title ?? '';
  if (!pr.milestone || pr.milestone.state !== 'open' || !MILESTONE_RE.test(milestoneTitle)) {
    add('PR-MILESTONE', 'Full-contract PRs need an open milestone named vX.Y.Z.');
  }

  const backport = BACKPORT_RE.exec(pr.body ?? '');
  if (RELEASE_LINE_RE.test(pr.baseRef ?? '') && !backport && !(pr.labels ?? []).includes(FORWARD_PORT_LABEL)) {
    add('PR-RELEASE-LINE', `A PR into ${pr.baseRef} must be a backport (the body starts with a "Backport of #<n>" line, as the backport bot writes it) or an emergency fix carrying the \`${FORWARD_PORT_LABEL}\` label. See docs/governance/release-lines.md.`);
  }

  checkEvidence(sections.get('e2e evidence') ?? '', pr, add, backport ? backport[1] : null); // a blank section also lacks a Status
  if (present('product impact')) checkProducts(sections.get('product impact'), pr, add);
  if (present('vnext-meta impact')) checkMeta(sections.get('vnext-meta impact'), pr, add);
  return findings;
}

function checkEvidence(text, pr, add, backportOf = null) {
  const ev = parseEvidence(text);
  if (!ev.status) {
    add('PR-E2E-STATUS', 'E2E evidence must declare `Status: run` or `Status: not-run`.');
    return;
  }
  if (ev.status === 'not-run') {
    if ((ev.reason ?? '').length < MIN_REASON || /^<.*>$/.test(ev.reason)) {
      add('PR-E2E-STATUS', `A not-run status needs a Reason of at least ${MIN_REASON} characters.`);
    }
    return;
  }
  const sha = ev.runtimeSha ?? '';
  if (!/^[0-9a-f]{7,40}$/i.test(sha)) {
    add('PR-E2E-RUN', 'A run block needs a Runtime commit SHA of at least 7 hex characters.');
  } else if (!(pr.commitShas ?? []).some((c) => c.toLowerCase().startsWith(sha.toLowerCase()))) {
    if (pr.commitsCapped) {
      add('PR-E2E-RUN', 'Runtime SHA not found, but the commit list is capped at 250 so it cannot be verified.', 'warning');
    } else if (backportOf) {
      add('PR-E2E-RUN', `Runtime SHA does not match any commit of this backport; the evidence comes from the original PR #${backportOf}.`, 'warning');
    } else {
      add('PR-E2E-RUN', 'Runtime SHA does not match any commit of this PR.');
    }
  }
  if (!ev.totals) add('PR-E2E-RUN', 'A run block needs a `Result: P/T passed, F failed, S skipped` line.');
  if (ev.suites.length === 0) add('PR-E2E-RUN', 'A run block needs at least one suite row.');
  const failed = ev.totals?.failed ?? ev.suites.reduce((n, s) => n + s.failed, 0);
  if (failed > 0 && !ev.reds) add('PR-E2E-RUN', 'A run with failed tests needs a Reds line explaining them.');
}

const HANDOFF_URL = (product) => new RegExp(`https://github\\.com/burgan-tech/${product}/(pull|issues)/\\d+`);
const HANDOFF_PATH = (product) => new RegExp(`docs/integration/${product}/[^\\s)\`|]+\\.md`);

function checkProducts(text, pr, add) {
  const rows = parseProductImpact(text);
  const byProduct = new Map();
  for (const r of rows) byProduct.set(r.product, [...(byProduct.get(r.product) ?? []), r]);
  const unknown = rows.filter((r) => !PRODUCTS.includes(r.product));
  const problems = [
    ...PRODUCTS.filter((p) => (byProduct.get(p) ?? []).length !== 1).map((p) => `${p}: expected exactly one row`),
    ...PRODUCTS.filter((p) => byProduct.get(p)?.length === 1 && !byProduct.get(p)[0].needed).map((p) => `${p}: Needed must be yes or no`),
    ...unknown.map((r) => `${r.product}: not a known product`),
  ];
  if (problems.length > 0) add('PR-PRODUCT-ROWS', `Product impact table is invalid (${problems.join('; ')}).`);

  for (const product of PRODUCTS) {
    const row = byProduct.get(product)?.[0];
    if (!row || row.needed !== 'yes') continue;
    const docPath = HANDOFF_PATH(product).exec(row.handoff)?.[0];
    if (docPath) {
      if (!(pr.changedFiles ?? []).includes(docPath)) {
        add('PR-HANDOFF', `Handoff doc for ${product} is not part of this PR's changed files.`, 'error', docPath);
        continue;
      }
      checkHandoffDoc(docPath, product, pr, add);
    } else if (!HANDOFF_URL(product).test(row.handoff)) {
      add('PR-HANDOFF', `A "yes" row for ${product} needs a changed docs/integration/${product}/*.md file or a PR/issue URL in burgan-tech/${product}.`);
    }
  }
}

function checkHandoffDoc(path, product, pr, add) {
  const content = pr.readFile(path);
  if (content === null || content === undefined) {
    add('PR-HANDOFF-DOC', 'Handoff doc could not be read.', 'error', path);
    return;
  }
  const fm = parseFrontmatter(content);
  if (!fm) {
    add('PR-HANDOFF-DOC', 'Handoff doc has no frontmatter.', 'error', path);
    return;
  }
  const bad = [];
  if (fm.product !== product) bad.push(`product must be ${product}`);
  if (!pr.milestone || fm.milestone !== pr.milestone.title) bad.push('milestone must equal the PR milestone');
  if (!['pending', 'in-progress', 'done'].includes(fm.status)) bad.push('status must be pending, in-progress or done');
  if (bad.length > 0) add('PR-HANDOFF-DOC', `Handoff doc frontmatter is invalid (${bad.join('; ')}).`, 'error', path);
}

/** A changed path counts as a vnext-meta change only if it is a release fragment or one of the shipped data files. */
function isMetaChange(f) {
  return isFragmentPath(f) || META_FILES.some((name) => f === `vnext-meta/${name}`);
}

function checkMeta(text, pr, add) {
  const meta = parseMetaImpact(text);
  const changedFile = (pr.changedFiles ?? []).find(isMetaChange);
  const changed = changedFile !== undefined;
  if (!meta.status) {
    add('PR-META', 'vnext-meta impact must declare `Status: updated` or `Status: not-needed`.');
  } else if (meta.status === 'updated' && !changed) {
    add('PR-META', 'Status is "updated" but no vnext-meta fragment or data file changed in this PR.');
  } else if (meta.status === 'not-needed') {
    if (changed) add('PR-META', `Status is "not-needed" but a vnext-meta fragment or data file changed in this PR (${changedFile}).`);
    else if ((meta.reason ?? '').length < MIN_REASON || /^<.*>$/.test(meta.reason)) {
      add('PR-META', `A not-needed status needs a Reason of at least ${MIN_REASON} characters.`);
    }
  }
}

const encodeData = (s) => String(s).replace(/%/g, '%25').replace(/\r/g, '%0D').replace(/\n/g, '%0A');
const encodeProp = (s) => encodeData(s).replace(/:/g, '%3A').replace(/,/g, '%2C');

/** Formats one finding as a GitHub workflow command (https://docs.github.com/actions/reference/workflow-commands-for-github-actions). */
export function formatAnnotation(f) {
  return `::${f.severity} title=${encodeProp(f.id)},file=${encodeProp(f.file)}::${encodeData(f.message)}`;
}
