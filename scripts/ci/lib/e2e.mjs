// Pure helpers for the PR E2E job (.github/workflows/pr-e2e.yml, pr-e2e-report.yml).
// Policy: docs/governance/ai-engineering-pipeline.md §10.2.

export const E2E_MARKER = '<!-- pr-e2e:v1 -->';
const SUITE = /^[A-Z][A-Za-z0-9]{1,63}$/;
const AI_BRANCH = /^ai\/issue-\d+$/;
const TRIGGER_LABELS = new Set(['e2e', 'e2e:full']);

/**
 * Suites a PR asks for. A human writes `E2E suites: Smoke, ChainBusy` on its own line; the AI
 * Developer writes them in the Done / skipped row (`| E2E (vnext-example) | … | suites: A, B — … |`).
 * Anything that is not a suite-shaped name is dropped; order is kept, duplicates removed.
 */
export function parseSuites(body) {
  const text = String(body ?? '');
  const human = /^E2E suites:\s*(.+)$/im.exec(text)?.[1];
  const ai = /^\|\s*E2E \(vnext-example\)\s*\|[^|\n]*\|\s*suites:\s*([^|\n—]+)/im.exec(text)?.[1];
  const names = `${human ?? ''},${ai ?? ''}`.split(/[\s,]+/).filter((s) => SUITE.test(s));
  return [...new Set(names)].slice(0, 40);
}

/**
 * Whether the job runs and which suites (empty list = the full set).
 * schedule → full; workflow_dispatch → its `suites` input or full;
 * pull_request → label `e2e:full` → full; an AI branch (`ai/issue-<n>`) → the listed suites, else
 * Smoke; label `e2e` → the listed suites, else full; anything else is skipped. A `labeled` event only counts for the two
 * trigger labels, so the pipeline's own `ai:*` labels never restart a 30-minute run.
 */
export function selectRun({ eventName, action, label, headRef, labels = [], body, inputSuites }) {
  if (eventName === 'schedule') return { run: true, suites: [], why: 'nightly full set on the default branch' };
  if (eventName === 'workflow_dispatch') {
    const suites = parseSuites(`E2E suites: ${inputSuites ?? ''}`);
    return { run: true, suites, why: 'manual dispatch' };
  }
  if (eventName !== 'pull_request') return { run: false, suites: [], why: `event ${eventName} is not handled` };
  if (action === 'labeled' && !TRIGGER_LABELS.has(label)) return { run: false, suites: [], why: `label ${label} does not trigger e2e` };
  if (labels.includes('e2e:full')) return { run: true, suites: [], why: 'label e2e:full' };
  const ai = AI_BRANCH.test(headRef ?? '');
  if (!ai && !labels.includes('e2e')) return { run: false, suites: [], why: 'not an AI PR and no e2e / e2e:full label' };
  const suites = parseSuites(body);
  // An AI PR that lists no suite changed no runtime behaviour (developer.md): the smoke test still
  // proves the PR's runtime starts and publishes the domain. A human asking with `e2e` gets all.
  if (ai && !suites.length) return { run: true, suites: ['Smoke'], why: 'AI PR, no suite listed — smoke only' };
  return { run: true, suites, why: ai ? 'AI PR' : 'label e2e' };
}

/**
 * The `dotnet test --filter` for the suites, checked against the suite folders that exist in the
 * vnext-example checkout (`Smoke` is a class one level up, `Tests/SmokeTests.cs`). Empty = no filter.
 * An unknown name never narrows the run: the full set runs and `note` says which names were unknown
 * (the AI Developer cannot see vnext-example, so it may guess a name).
 */
export function buildFilter(suites, available) {
  const known = new Set(available);
  const unknown = suites.filter((s) => !known.has(s));
  if (unknown.length) return { filter: '', note: `unknown suite(s) ${unknown.join(', ')} — full set run instead` };
  return { filter: suites.map((s) => `FullyQualifiedName~Core.IntegrationTests.Tests.${s}`).join('|'), note: '' };
}

/** The Reds line for e2e-evidence.mjs: names the failed tests, never explains them away. */
export function redsLine(failedNames, runUrl) {
  const names = failedNames.slice(0, 10).join('; ');
  const more = failedNames.length > 10 ? ` and ${failedNames.length - 10} more` : '';
  return `${failedNames.length} failed in CI (${names}${more}); not triaged — read the TRX and the host logs of ${runUrl}`;
}

/** Failed test names (`className.method`) from TRX XML. */
export function failedTests(xml) {
  const out = [];
  for (const m of String(xml).matchAll(/<UnitTestResult\b([^>]*?)\/?>/g)) {
    if (!/\boutcome="Failed"/.test(m[1])) continue;
    const name = /\btestName="([^"]*)"/.exec(m[1])?.[1];
    if (name) out.push(name.replace(/^Core\.IntegrationTests\.Tests\./, ''));
  }
  return out;
}

/**
 * The PR body with the run's evidence: the `## E2E evidence` section's content replaced by
 * `evidence`, and the Done / skipped `E2E (vnext-example)` row set to the result. Returns null
 * when the body has no `## E2E evidence` section (a human PR body is never restructured).
 */
export function applyEvidence(body, evidence, rowDetail) {
  const text = String(body ?? '');
  const head = /^## E2E evidence[^\n]*\n/m.exec(text);
  if (!head) return null;
  const start = head.index + head[0].length;
  const next = text.slice(start).search(/^## /m);
  const end = next === -1 ? text.length : start + next;
  let out = `${text.slice(0, start)}${evidence.trim()}\n\n${text.slice(end)}`;
  out = out.replace(/^\|\s*E2E \(vnext-example\)\s*\|[^\n]*$/m, `| E2E (vnext-example) | run | ${rowDetail.replace(/\|/g, '/')} |`);
  return out;
}
