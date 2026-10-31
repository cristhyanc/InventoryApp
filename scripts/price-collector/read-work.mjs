// Reads a prepared snapshot. No API call, bearer token or second run issuance is possible here.
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

try {
  if (process.argv.length !== 2 || !process.env.PRICE_COLLECTOR_STATE_DIR) throw new Error();
  const work = JSON.parse(readFileSync(join(process.env.PRICE_COLLECTOR_STATE_DIR, 'work.json'), 'utf8'));
  if (!Number.isInteger(work.runId) || !Array.isArray(work.searches)) throw new Error();
  process.stdout.write(`${JSON.stringify(work)}\n`);
} catch {
  process.stderr.write('The prepared work snapshot is unavailable. Stop this collector run.\n');
  process.exitCode = 1;
}
