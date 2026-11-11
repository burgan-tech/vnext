#!/usr/bin/env node
// Prints the sandboxed runner's verdict for one test project (lib testVerdict): `<P> passed`,
// `<P> passed except N Docker-dependent tests (...)` or `<P> failed`.
//   test-verdict.mjs <project> <exit-code> <log-file>
import { readFileSync } from 'node:fs';
import { testVerdict } from '../lib/developer.mjs';

const [project, code, file] = process.argv.slice(2);
if (!/^[A-Z][A-Za-z]*$/.test(project ?? '') || !/^\d+$/.test(code ?? '') || !file) {
  console.error('usage: test-verdict.mjs <project> <exit-code> <log-file>');
  process.exit(2);
}
let log = '';
try { log = readFileSync(file, 'utf8'); } catch { /* a missing log is a failed run */ }
console.log(testVerdict(project, code, log));
