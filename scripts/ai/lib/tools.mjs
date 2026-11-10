// Absolute paths of the CLI tools the AI pipeline writers call. Resolved from fixed system
// directories, never from PATH: the agent ran in the same job and a writable PATH entry would let
// a planted `gh` or `git` receive the GitHub token (Sonar javascript:S4036).
import { existsSync } from 'node:fs';
import { join } from 'node:path';

const DIRS = Object.freeze(['/usr/bin', '/usr/local/bin', '/bin', '/opt/homebrew/bin']);

/** Absolute path of `name` in the first fixed directory that has it; throws when none does. */
export function toolPath(name, dirs = DIRS) {
  for (const dir of dirs) {
    const candidate = join(dir, name);
    if (existsSync(candidate)) return candidate;
  }
  throw new Error(`${name} not found in ${dirs.join(', ')}`);
}
