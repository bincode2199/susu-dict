import { spawnSync } from 'node:child_process';

// The VM launcher supplies both Path and PATH. Legacy MSBuild rejects duplicate
// case-insensitive environment keys. Normalize only this child process's block.
const env = {};
const keys = new Map();
for (const [key, value] of Object.entries(process.env)) {
  const folded = key.toUpperCase();
  if (!keys.has(folded)) {
    keys.set(folded, key);
    env[key] = value;
  }
}
const [command, ...args] = process.argv.slice(2);
if (!command) throw new Error('Usage: node tools/run-clean-env.mjs executable [arguments]');
const result = spawnSync(command, args, { env, stdio: 'inherit', windowsHide: true });
if (result.error) throw result.error;
process.exit(result.status ?? 1);
