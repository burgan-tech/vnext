import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { checkPr } from '../../governance/lib/pr-contract.mjs';
import { PRODUCTS as TEMPLATE_PRODUCTS } from '../../governance/lib/pr-body.mjs';
import { compileImpactRules } from '../../governance/lib/ai-record.mjs';
import {
  PRODUCTS, DEV_MARKER, branchFor, decideMode, validateResult, checkDiff, pickMilestone, prLabels, developerLabels,
  extractCouncilRow, insertLogRow, findSecrets, parsePatch, parseNameStatusZ, renderPrBody, updateRepairs, renderDeveloperComment, isDeveloperComment,
  checkRestoreSafety, runnerWarning, testVerdict,
} from '../lib/developer.mjs';

test('testVerdict: Docker-only failures are named, anything else stays failed', () => {
  const dockerFail = (name) => `  Failed ${name} [1 ms]\n  Error Message:\n   Class fixture threw\n---- DotNet.Testcontainers.Builders.DockerUnavailableException : Docker is either not running\n  Stack Trace:\n`;
  const realFail = '  Failed A.B.C [3 ms]\n  Error Message:\n   Shouldly.ShouldAssertException : 1 should be 2\n';
  const summary = (n, p) => `\nFailed!  - Failed:   ${n}, Passed:   ${p}, Skipped:     0, Total:   ${n + p}, Duration: 2 s - X.dll (net10.0)\n`;
  assert.equal(testVerdict('Domain', 0, ''), 'Domain passed');
  const docker = `  Passed A.Ok [1 ms]\n${dockerFail('A.One')}${dockerFail('A.Two(x: 1)')}${summary(2, 5)}`;
  const v = testVerdict('Infrastructure', 1, docker);
  assert.match(v, /^Infrastructure passed except 2 Docker-dependent tests/);
  assert.deepEqual(runnerWarning({ build: { detail: 'passed' }, tests: { detail: `Domain passed, ${v}` } }), []);
  assert.equal(testVerdict('Infrastructure', 1, `${dockerFail('A.One')}${realFail}${summary(2, 5)}`), 'Infrastructure failed');
  assert.equal(testVerdict('Infrastructure', 1, `${dockerFail('A.One')}${summary(3, 5)}`), 'Infrastructure failed', 'summary count must match the parsed blocks');
  assert.equal(testVerdict('Infrastructure', 1, 'error MSB1009: Project file does not exist.'), 'Infrastructure failed');
  assert.equal(testVerdict('Infrastructure', 134, ''), 'Infrastructure failed');
});

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const taxonomy = JSON.parse(readFileSync(join(root, '.github/triage/labels.json'), 'utf8'));
const allLabels = new Set(Object.entries(taxonomy).filter(([k, v]) => k !== 'defaults' && Array.isArray(v)).flatMap(([, v]) => v.map((l) => l.name)));
const rules = compileImpactRules(JSON.parse(readFileSync(join(root, '.github/ai/impact-paths.json'), 'utf8')));
const plan = (level = 'minor', council = false) => `<!-- ai-director-plan:v1 -->\n<!-- ai-director-data:{"level":"${level}","impact":"low","risk":"medium","council":${council},"decision":"plan"} -->`;
const base = { number: 7, labels: ['ai:planned', 'ai:semver-minor'], directorPlan: plan(), openPrs: [], closedPrs: [] };

test('mode: NEW, RECOVERY, FIX and every reason to stay out', () => {
  assert.equal(decideMode(base).mode, 'NEW');
  assert.equal(decideMode({ ...base, closedPrs: [{ number: 3 }] }).mode, 'RECOVERY');
  const ours = { number: 9, head: branchFor(7) };
  assert.deepEqual(decideMode({ ...base, labels: ['ai:changes-required'], openPrs: [ours] }), { mode: 'FIX', pr: ours });
  const skip = (over) => decideMode({ ...base, ...over }).reason;
  assert.match(skip({ labels: ['ai:planned', 'ai:blocked'] }), /ai:blocked/);
  assert.match(skip({ labels: ['ai:planned', 'ai:semver-major'] }), /ai:semver-major/);
  assert.match(skip({ labels: ['ai:pr-ready'] }), /neither/);
  assert.match(skip({ directorPlan: null }), /no Director plan/);
  assert.match(skip({ directorPlan: plan('major') }), /major/);
  assert.match(skip({ labels: ['ai:planned', 'ai:council-required'] }), /without ai:council-approved/);
  assert.equal(decideMode({ ...base, labels: ['ai:planned', 'ai:council-required', 'ai:council-approved'] }).mode, 'NEW');
  assert.match(skip({ openPrs: [{ number: 1209, head: 'claude/x' }] }), /open PR #1209 already works on it/);
  assert.match(skip({ openPrs: [ours] }), /PR #9 already implements it/);
  assert.match(skip({ labels: ['ai:changes-required'] }), /no open ai\/issue-7 PR/);
  // collect.mjs prefixes a fork's branch with its repository, so a fork's ai/issue-7 is never the FIX target.
  assert.match(skip({ labels: ['ai:changes-required'], openPrs: [{ number: 9, head: 'someone/vnext:ai/issue-7' }] }), /no open ai\/issue-7 PR/);
  assert.match(skip({ labels: ['ai:changes-required'], openPrs: [ours], repairs: 3 }), /repair limit/);
});

test('the agent answer is validated: bad title or decision falls back, text is sanitized', () => {
  const { result, dropped } = validateResult({
    decision: 'ship-it', title: 'Fixed stuff!!', summary: ['ping @octocat <!-- x -->'],
    productImpact: { 'vnext-docs': { needed: true, handoff: 'docs/integration/vnext-docs/x.md' }, 'vnext-forge': { needed: 'yes' } },
    meta: { status: 'whatever' }, conditions: [{ id: 'C-1', status: 'done' }, { status: 'done' }],
  });
  assert.equal(result.decision, 'needs-replan');
  assert.equal(result.title, '');
  assert.deepEqual(dropped, ['decision:ship-it', 'title:Fixed stuff!!']);
  assert.ok(!result.summary[0].includes('@octocat') && !result.summary[0].includes('<!--'));
  assert.equal(result.productImpact['vnext-docs'].needed, true);
  assert.equal(result.productImpact['vnext-forge'].needed, false);
  assert.equal(result.meta.status, 'not-needed');
  assert.equal(result.conditions.length, 1);
  assert.equal(validateResult({ decision: 'implemented', title: 'fix(cache): keep the newest build on a miss' }).result.title, 'fix(cache): keep the newest build on a miss');
});

test('the product list matches the PR template', () => {
  assert.deepEqual([...PRODUCTS].sort(), [...TEMPLATE_PRODUCTS].sort());
});

const patchOf = (path, lines) => [`diff --git a/${path} b/${path}`, `--- a/${path}`, `+++ b/${path}`, '@@ -1 +1 @@', ...lines].join('\n');

test('diff policy: forbidden paths, deleted or skipped tests, suppressions, packages, size', () => {
  const ok = [{ path: 'src/BBT.Workflow.Application/Caching/CacheSet.cs', status: 'M' }, { path: 'test/BBT.Workflow.Application.Tests/X.cs', status: 'A' }];
  const okPatch = `${patchOf('src/BBT.Workflow.Application/Caching/CacheSet.cs', ['+// x'])}\n${patchOf('test/BBT.Workflow.Application.Tests/X.cs', ['+    [Fact]', '+    public void Y() {}'])}`;
  assert.deepEqual(checkDiff(ok, okPatch), []);
  assert.deepEqual(checkDiff([], ''), ['the diff is empty']);
  for (const path of ['.github/workflows/x.yml', 'scripts/ai/lib/developer.mjs', '.claude/rules/x.md', 'AGENTS.md', 'Directory.Build.props', 'global.json', 'nuget.config', '.editorconfig', 'common.props', 'etc/docker/x.yml', '.gitattributes', '.mcp.json', 'BBT.Workflow.slnx', '.config/dotnet-tools.json']) {
    assert.match(checkDiff([{ path, status: 'M' }], '').join(), /outside the folders the pipeline writes/, path);
  }
  for (const path of ['src/Directory.Packages.props', 'src/A/Directory.Build.props', 'docs/governance/ai-engineering-pipeline.md', 'docs/agent-council/sessions/README.md', 'src/A/.config/dotnet-tools.json', 'src/A/Dockerfile', 'test/A/.editorconfig']) {
    assert.match(checkDiff([{ path, status: 'M' }], '').join(), /may not change this path/, path);
  }
  assert.match(checkDiff([{ path: 'test/A/B.cs', status: 'D' }], '').join(), /deletes a test file/);
  const bad = (line, file = 'src/A.cs') => checkDiff([{ path: file, status: 'M' }], patchOf(file, [line])).join();
  assert.match(bad('+#pragma warning disable CS1234'), /pragma/);
  assert.match(bad('+[SuppressMessage("x", "y")]'), /SuppressMessage/);
  assert.match(bad('+    <NoWarn>CS1591</NoWarn>', 'src/A.csproj'), /NoWarn/);
  assert.match(bad('+    <PackageReference Include="Evil" Version="1.0" />', 'src/A.csproj'), /package reference/);
  assert.match(bad('+    [Fact(Skip = "flaky")]', 'test/A.cs'), /skips or disables a test/);
  assert.match(bad('+#if false', 'test/A.cs'), /skips or disables a test/);
  assert.match(bad('-    [Fact]', 'test/A.cs'), /removes or comments out 1 test/);
  assert.match(checkDiff([{ path: 'src/a.png', status: 'A' }], 'diff --git a/src/a.png b/src/a.png\nnew file mode 100644\nGIT binary patch\nliteral 4').join(), /binary/);
  const many = Array.from({ length: 81 }, (_, i) => ({ path: `src/F${i}.cs`, status: 'M' }));
  assert.match(checkDiff(many, '').join(), /81 files/);
});

test('milestone, PR labels and issue transitions', () => {
  assert.equal(pickMilestone(['backlog', 'v0.1.0', 'v0.0.105', 'v0.0.99']), 'v0.0.99');
  assert.equal(pickMilestone(['backlog']), null);
  assert.deepEqual(prLabels(['bug', 'ai:planned', 'ai:semver-minor', 'ai:impact-high', 'ai:risk-high', 'ai:council-required', 'ai:council-approved', 'ai:triaged']),
    ['ai:semver-minor', 'ai:impact-high', 'ai:risk-high', 'ai:council-required', 'ai:council-approved']);
  assert.deepEqual(developerLabels('pushed', ['ai:planned', 'ai:council-approved']), { add: ['ai:pr-ready'], remove: ['ai:planned'] });
  assert.deepEqual(developerLabels('pushed', ['ai:changes-required', 'ai:fixing']), { add: ['ai:pr-ready'], remove: ['ai:changes-required', 'ai:fixing'] });
  assert.deepEqual(developerLabels('needs-replan', ['ai:planned']), { add: ['ai:replan'], remove: ['ai:planned'] });
  assert.deepEqual(developerLabels('rejected', ['ai:planned']), { add: ['ai:blocked'], remove: ['ai:planned'] });
  assert.deepEqual(developerLabels('failed', ['ai:planned']), { add: [], remove: [] });
  for (const o of ['pushed', 'needs-replan', 'rejected', 'no-change']) for (const l of developerLabels(o, []).add) assert.ok(allLabels.has(l), l);
});

test('the council row is carried into the sessions log once', () => {
  const row = '| 2026-11-10 | [issue-7-council](https://github.com/o/r/issues/7#issuecomment-1) | T | High | `APPROVED` (CI council) | A | — |';
  const body = `x\n<details><summary>Decision log row</summary>\n\n\`\`\`markdown\n${row}\n\`\`\`\n\n</details>`;
  assert.equal(extractCouncilRow(body), row);
  const readme = readFileSync(join(root, 'docs/agent-council/sessions/README.md'), 'utf8');
  const next = insertLogRow(readme, row);
  assert.ok(next.includes(`${row}\n<!-- newest at the bottom`));
  assert.equal(insertLogRow(next, row), null);
});

test('the PR body passes the PR contract, AI decision record included', () => {
  const { result } = validateResult({
    decision: 'implemented', title: 'fix(cache): keep the newest build on a miss',
    summary: ['Keeps the newest build.'], vnextScope: 'component cache; behaviour changes on a miss only',
    changes: ['src/BBT.Workflow.Application/Caching/CacheSet.cs — x'], testPlan: ['run CacheSet tests'],
    e2eSuites: ['ComponentVersionLab'], meta: { status: 'not-needed', reason: 'internal fix, no contract change' },
    conditions: [{ id: 'C-1', status: 'done', evidence: 'x' }], confidence: 'medium',
  }, { mode: 'NEW' });
  const labels = ['ai:semver-patch', 'ai:impact-high', 'ai:risk-high', 'ai:council-required', 'ai:council-approved'];
  const body = renderPrBody({
    issue: 1192, result, plan: { council: true }, labels, councilUrl: 'https://github.com/o/r/issues/1192#issuecomment-2',
    planUrl: 'https://github.com/o/r/issues/1192#issuecomment-1', branch: branchFor(1192),
    runner: { build: { status: 'run', detail: 'passed' }, tests: { status: 'run', detail: 'Domain passed, Application passed, Infrastructure passed' } }, repairs: 0, maxRepairs: 3,
    e2eReason: 'the e2e job (pr-e2e.yml) is not built yet; run the suites locally',
  });
  const findings = checkPr({
    title: result.title, body, labels, milestone: { title: 'v0.0.105', state: 'open' },
    changedFiles: ['src/BBT.Workflow.Application/Caching/CacheSet.cs'], impactRules: rules, readFile: () => null,
  });
  assert.deepEqual(findings.filter((f) => f.severity !== 'warning').map((f) => `${f.id}: ${f.message}`), []);
  assert.match(body, /Closes #1192/);
  assert.doesNotMatch(body, /Not verified on the runner/);
  assert.match(updateRepairs(body, 2, 3), /- Repair attempts: 2\/3/);

  // A failed runner build opens the body with a warning, and the body still passes the contract.
  const failed = renderPrBody({
    issue: 1192, result, plan: { council: true }, labels, councilUrl: 'https://github.com/o/r/issues/1192#issuecomment-2',
    planUrl: 'https://github.com/o/r/issues/1192#issuecomment-1', branch: branchFor(1192),
    runner: { build: { status: 'run', detail: 'failed (sandboxed runner, informational)' }, tests: { status: 'not-run', detail: 'build failed' } }, repairs: 0, maxRepairs: 3,
    e2eReason: 'the e2e job (pr-e2e.yml) is not built yet; run the suites locally',
  });
  assert.match(failed.split('\n')[0], /^> \[!WARNING\]/);
  assert.match(failed, /Not verified on the runner:\*\* the build did not pass \(failed\)/);
  const f2 = checkPr({
    title: result.title, body: failed, labels, milestone: { title: 'v0.0.105', state: 'open' },
    changedFiles: ['src/BBT.Workflow.Application/Caching/CacheSet.cs'], impactRules: rules, readFile: () => null,
  });
  assert.deepEqual(f2.filter((f) => f.severity !== 'warning').map((f) => `${f.id}: ${f.message}`), []);
});

test('runner warning: build, failed test projects, nothing run', () => {
  const ok = { build: { detail: 'passed (sandboxed runner, informational)' }, tests: { detail: 'Domain passed, Application passed, Infrastructure passed (sandboxed runner, informational)' } };
  assert.deepEqual(runnerWarning(ok), []);
  assert.match(runnerWarning({ build: { detail: 'passed' }, tests: { detail: 'Domain passed, Application failed, Infrastructure failed' } }).join(), /unit tests failed in Application, Infrastructure/);
  assert.match(runnerWarning({}).join(), /the build was not run/);
  assert.match(runnerWarning({ build: { detail: 'passed' }, tests: { detail: 'build failed' } }).join(), /unit tests were not run/);
});

test('the status comment is sticky and lists violations', () => {
  const body = renderDeveloperComment({ issue: 7, mode: 'NEW', outcome: 'rejected', violations: ['.github/x: the pipeline may not change this path'], runMode: 'dry-run', labels: { add: ['ai:blocked'], remove: ['ai:planned'] } });
  assert.ok(body.startsWith(DEV_MARKER));
  assert.match(body, /nothing was pushed/);
  assert.match(body, /would add `ai:blocked`/);
  assert.ok(isDeveloperComment({ user: { login: 'github-actions[bot]' }, body }));
});

test('a patch carrying a secret is never pushed', () => {
  const p = (line) => patchOf('test/A.cs', [line]);
  assert.deepEqual(findSecrets(p('+var x = 1;'), ['cursor-key-123456']), []);
  assert.match(findSecrets(p('+// cursor-key-123456'), ['cursor-key-123456']).join(), /a secret this job holds/);
  assert.match(findSecrets(p('+var t = "ghs_abcdefghijklmnopqrstuvwxyz012345";')).join(), /token-shaped/);
  assert.deepEqual(findSecrets(p('-var t = "ghs_abcdefghijklmnopqrstuvwxyz012345";')), []);
});

test('a content line that starts with +++ or --- is content, never a file header', () => {
  // Review finding on #1214: an added line `++ x` shows up in the patch as `+++ x`.
  const patch = [
    'diff --git a/docs/a.md b/docs/a.md', '--- a/docs/a.md', '+++ b/docs/a.md', '@@ -1,0 +1,2 @@',
    '+++ ghp_abcdefghijklmnopqrstuvwxyz012345', '+--- not a header',
    'diff --git a/test/A.cs b/test/A.cs', '--- a/test/A.cs', '+++ b/test/A.cs', '@@ -1,0 +1,2 @@',
    '+++ x', '+    [Fact(Skip = "flaky")]',
  ].join('\n');
  assert.deepEqual(parsePatch(patch).map((f) => [f.path, f.added.length]), [['docs/a.md', 2], ['test/A.cs', 2]]);
  assert.match(findSecrets(patch).join(), /docs\/a\.md: contains a token-shaped string/);
  const files = [{ path: 'docs/a.md', status: 'M' }, { path: 'test/A.cs', status: 'M' }];
  assert.match(checkDiff(files, patch).join(), /test\/A\.cs: skips or disables a test/);
  assert.match(checkDiff([files[0]], patch).join(), /test\/A\.cs: in the patch but not in the file list/);
  assert.match(checkDiff([files[0]], patch).join(), /the patch has 2 file\(s\), the file list 1/);
});

test('project files may not add targets, imports or analyzer changes', () => {
  const csproj = (line) => checkDiff([{ path: 'src/A/A.csproj', status: 'M' }], patchOf('src/A/A.csproj', [line])).join();
  assert.match(csproj('+  <Target Name="X" BeforeTargets="Build"><Exec Command="curl x" /></Target>'), /build target/);
  assert.match(csproj('+  <Import Project="../../evil.props" />'), /build target/);
  assert.match(csproj('+    <RunAnalyzers>false</RunAnalyzers>'), /analyzer or warning/);
  assert.match(csproj('+    <WarningsNotAsErrors>CS8600</WarningsNotAsErrors>'), /analyzer or warning/);
  assert.match(csproj('+<Project Sdk="Evil.Sdk/1.0.0">'), /SDK other than Microsoft\.NET\.Sdk/);
  assert.match(csproj("+<Project Sdk='Microsoft.NET.Sdk;Evil.Sdk'>"), /SDK other than/);
  assert.match(csproj('+  <Sdk Name="Evil.Sdk" Version="1.0.0" />'), /<Sdk> element/);
  for (const ok of ['+<Project Sdk="Microsoft.NET.Sdk">', '+<Project Sdk="Microsoft.NET.Sdk.Web">', '+<Project Sdk="Microsoft.NET.Sdk.Worker">']) assert.deepEqual(csproj(ok), '', ok);
  // The same text in a C# file is just text.
  assert.deepEqual(checkDiff([{ path: 'src/A/B.cs', status: 'M' }], patchOf('src/A/B.cs', ['+// <Target Name="X" />'])), []);
});

test('FIX restore check: restore configuration, packages and build rules on the whole branch', () => {
  assert.deepEqual(checkRestoreSafety([{ path: 'src/A/B.cs', status: 'M' }, { path: '.github/workflows/x.yml', status: 'M' }], patchOf('src/A/B.cs', ['+var x = 1;'])), []);
  for (const path of ['nuget.config', 'src/NuGet.Config', 'global.json', 'Directory.Build.props', 'src/Directory.Build.targets', 'Directory.Packages.props', '.config/dotnet-tools.json', 'src/A/x.rsp']) {
    assert.match(checkRestoreSafety([{ path, status: 'M' }], '').join(), /changes restore configuration/, path);
  }
  const proj = (line) => checkRestoreSafety([{ path: 'src/A/A.csproj', status: 'M' }], patchOf('src/A/A.csproj', [line])).join();
  assert.match(proj('+  <PackageReference Include="Evil" Version="1.0.0" />'), /package reference/);
  assert.match(proj('+  <Import Project="x.targets" />'), /build target/);
  assert.match(proj('+<Project Sdk="Evil.Sdk">'), /SDK other than/);
  assert.equal(proj('+  <Compile Include="X.cs" />'), 'src/A/A.csproj: changes which files are compiled or packed');
  assert.equal(proj('+    <Description>x</Description>'), '');
});

test('a path git has to quote is refused and never skips the scan', () => {
  // Second review finding on #1214: `.github/é.yml` is printed as `".github/\\303\\251.yml"`.
  const quoted = ['diff --git "a/src/\\303\\251.cs" "b/src/\\303\\251.cs"', '--- "a/src/\\303\\251.cs"', '+++ "b/src/\\303\\251.cs"', '@@ -1,0 +1 @@', '+#pragma warning disable CS1'].join('\n');
  const v = checkDiff([{ path: 'src/é.cs', status: 'M' }], quoted).join();
  assert.match(v, /characters outside/);
  assert.match(v, /unparseable file header/);
  assert.match(findSecrets(quoted).join(), /not scanned/);
  assert.match(checkDiff([{ path: '.github/é.yml', status: 'A' }], '').join(), /characters outside/);
  assert.deepEqual(parseNameStatusZ('M\0src/a.cs\0A\0src/é.cs\0'), [{ status: 'M', path: 'src/a.cs' }, { status: 'A', path: 'src/é.cs' }]);
});

test('commenting out a test, or paying for it with a test in another file, is refused', () => {
  const swap = (lines) => checkDiff([{ path: 'test/A.cs', status: 'M' }], patchOf('test/A.cs', lines)).join();
  assert.match(swap(['-    [Fact]', '+    // [Fact]']), /removes or comments out 1 test/);
  const two = [
    'diff --git a/test/A.cs b/test/A.cs', '--- a/test/A.cs', '+++ b/test/A.cs', '@@ -1 +0,0 @@', '-    [Fact]',
    'diff --git a/test/B.cs b/test/B.cs', '--- a/test/B.cs', '+++ b/test/B.cs', '@@ -0,0 +1 @@', '+    [Fact]',
  ].join('\n');
  assert.match(checkDiff([{ path: 'test/A.cs', status: 'M' }, { path: 'test/B.cs', status: 'M' }], two).join(), /test\/A\.cs: removes or comments out 1 test/);
});

test('build events, compile removal, removed analyzer settings, suppressions and symlinks are refused', () => {
  const csproj = (lines) => checkDiff([{ path: 'test/A/A.csproj', status: 'M' }], patchOf('test/A/A.csproj', lines)).join();
  assert.match(csproj(['+    <PreBuildEvent>curl x | sh</PreBuildEvent>']), /build target, task, event/);
  assert.match(csproj(['+    <Compile Remove="**/*Tests.cs" />']), /which files are compiled/);
  assert.match(csproj(['-    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>']), /analyzer or warning settings/);
  assert.match(csproj(['-    <Nullable>enable</Nullable>']), /analyzer or warning settings/);
  const cs = (line) => checkDiff([{ path: 'src/A.cs', status: 'M' }], patchOf('src/A.cs', [line])).join();
  assert.match(cs('+[System.Diagnostics.CodeAnalysis.SuppressMessage("x", "y")]'), /SuppressMessage/);
  assert.match(cs('+#nullable disable'), /nullable disable/);
  assert.match(cs('+var x = 1; // NOSONAR'), /NOSONAR/);
  assert.match(cs('+[ExcludeFromCodeCoverage]'), /ExcludeFromCodeCoverage/);
  assert.match(checkDiff([{ path: 'src/A.csproj', status: 'M' }], patchOf('src/A.csproj', ['+  <NoWarn Condition="x">CS1</NoWarn>'])).join(), /NoWarn/);
  const link = ['diff --git a/src/link b/src/link', 'new file mode 120000', 'index 0000000..1', '--- /dev/null', '+++ b/src/link', '@@ -0,0 +1 @@', '+../.github/workflows'].join('\n');
  assert.match(checkDiff([{ path: 'src/link', status: 'A' }], link).join(), /not a regular file \(mode 120000\)/);
  const exec = ['diff --git a/src/a.sh b/src/a.sh', 'old mode 100644', 'new mode 100755'].join('\n');
  assert.match(checkDiff([{ path: 'src/a.sh', status: 'M' }], exec).join(), /changes the file mode/);
  const added = ['diff --git a/src/B.cs b/src/B.cs', 'new file mode 100644', 'index 0000000..1', '--- /dev/null', '+++ b/src/B.cs', '@@ -0,0 +1 @@', '+class B {}'].join('\n');
  assert.deepEqual(checkDiff([{ path: 'src/B.cs', status: 'A' }], added), []);
});

// The writer itself, in --print mode (no GitHub, no git): the controls that stop a push.
import { mkdtempSync, writeFileSync as writeFile, mkdirSync as mkdir } from 'node:fs';
import { tmpdir } from 'node:os';
import { spawnSync } from 'node:child_process';

function runApply({ mode = 'NEW', exit = '0', base = 'a'.repeat(40), headSha, files, patch, result }) {
  const dir = mkdtempSync(join(tmpdir(), 'ai-dev-'));
  mkdir(join(dir, 'in'));
  mkdir(join(dir, 'art'));
  const input = { number: 7, mode, title: 't', labels: ['ai:planned', 'ai:semver-patch', 'ai:impact-low'], directorPlan: plan('patch'), milestone: 'v0.0.105', repairs: 0, maxRepairs: 3 };
  if (mode === 'FIX') input.pr = { number: 9, branch: 'ai/issue-7', headSha: headSha ?? base };
  writeFile(join(dir, 'in', '7.json'), JSON.stringify(input));
  writeFile(join(dir, 'art', 'agent.exit'), exit);
  writeFile(join(dir, 'art', 'base.txt'), base);
  writeFile(join(dir, 'art', 'files.z'), files);
  writeFile(join(dir, 'art', 'patch.diff'), patch);
  writeFile(join(dir, 'art', 'result.json'), JSON.stringify({ decision: 'implemented', title: 'fix(cache): keep the newest build', summary: ['x'], ...result }));
  const r = spawnSync(process.execPath, [join(root, 'scripts/ai/developer/apply.mjs'), '--in', join(dir, 'in'), '--artifact', join(dir, 'art'), '--print'], {
    encoding: 'utf8', env: { ...process.env, GITHUB_REPOSITORY: 'o/r', GITHUB_STEP_SUMMARY: '' },
  });
  return `${r.stdout}${r.stderr}`;
}
const goodPatch = patchOf('src/A.cs', ['+// fix']);

test('writer: a clean result passes; an unfinished agent, a list mismatch or a moved PR never push', () => {
  assert.match(runApply({ files: 'M\0src/A.cs\0', patch: goodPatch }), /\| #7 \| NEW \| pushed \|.*print only/);
  assert.match(runApply({ exit: '124', files: 'M\0src/A.cs\0', patch: goodPatch }), /failed .*the agent did not finish \(exit 124\)/);
  const two = `${goodPatch}\n${patchOf('src/B.cs', ['+// b'])}`;
  assert.match(runApply({ files: 'M\0src/A.cs\0', patch: two }), /rejected[\s\S]*the patch has 2 file\(s\), the file list 1/);
  assert.match(runApply({ mode: 'FIX', headSha: 'b'.repeat(40), files: 'M\0src/A.cs\0', patch: goodPatch }), /the PR moved while the agent worked/);
  assert.match(runApply({ files: 'M\0src/A.cs\0', patch: goodPatch, result: { title: 'did stuff' } }), /no valid Conventional Commits title/);
});
