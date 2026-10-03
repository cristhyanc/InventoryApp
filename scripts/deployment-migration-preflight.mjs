// Production deployment migration preflight (issue #343).
//
// Production stores its data in SQLite on the App Service, and the GitHub runner cannot read
// that database's applied-migration history. The preflight therefore derives the *expected*
// pending migrations from the repository: the EF Core migrations present in the release commit
// minus those present in the baseline commit (the backend production currently runs). It never
// connects to production and never applies anything; production startup still applies pending
// migrations itself (issue #201).
//
// Usage: node scripts/deployment-migration-preflight.mjs --release <sha> --baseline <sha>
// Prints one JSON object; exits non-zero (fail closed) when the expected state cannot be
// established or the release would run older code against a newer schema.

import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const FULL_SHA = /^[0-9a-f]{40}$/;

// Matches an EF Core migration source file anywhere under backend/, so the scan keeps working
// when the migrations folder moves between projects. Designer files and the model snapshot are
// not migrations.
const MIGRATION_FILE = /^backend\/(?:.+\/)?Migrations\/(\d{14}_[A-Za-z0-9_]+)\.cs$/;

export function migrationIdsFromPaths(paths) {
  const ids = new Set();
  for (const path of paths) {
    if (path.endsWith('.Designer.cs')) {
      continue;
    }
    const match = MIGRATION_FILE.exec(path);
    if (match) {
      ids.add(match[1]);
    }
  }
  return [...ids].sort();
}

export function compareMigrations(releaseIds, baselineIds) {
  if (releaseIds.length === 0) {
    throw new Error('No EF Core migrations were found in the release commit; refusing to guess the migration state.');
  }
  if (baselineIds.length === 0) {
    throw new Error('No EF Core migrations were found in the baseline commit; refusing to guess the migration state.');
  }

  const baseline = new Set(baselineIds);
  const release = new Set(releaseIds);
  const expectedPending = releaseIds.filter((id) => !baseline.has(id));
  const missingFromRelease = baselineIds.filter((id) => !release.has(id));

  return { expectedPending, missingFromRelease };
}

export function evaluatePreflight({ releaseSha, baselineSha, releaseIds, baselineIds }) {
  const { expectedPending, missingFromRelease } = compareMigrations(releaseIds, baselineIds);
  return {
    releaseSha,
    baselineSha,
    releaseMigrationCount: releaseIds.length,
    expectedPending,
    missingFromRelease,
    startupWillApplyMigrations: expectedPending.length > 0,
    // Older code over a newer schema: production has migrations this release does not know.
    // Startup would not roll them back, so this is never deployed automatically.
    ok: missingFromRelease.length === 0,
  };
}

function listTree(sha) {
  if (!FULL_SHA.test(sha)) {
    throw new Error(`Not a full 40-character commit SHA: ${sha}`);
  }
  const output = execFileSync('git', ['ls-tree', '-r', '--name-only', sha, '--', 'backend'], {
    encoding: 'utf8',
    maxBuffer: 64 * 1024 * 1024,
  });
  return output.split('\n').filter(Boolean);
}

export function parseArguments(argv) {
  const options = {};
  for (let index = 0; index < argv.length; index += 1) {
    const flag = argv[index];
    if (flag !== '--release' && flag !== '--baseline') {
      throw new Error(`Unrecognised argument '${flag}'. Usage: --release <sha> --baseline <sha>`);
    }
    const value = argv[index + 1];
    if (!value || !FULL_SHA.test(value)) {
      throw new Error(`${flag} needs a full 40-character commit SHA.`);
    }
    options[flag.slice(2)] = value;
    index += 1;
  }
  if (!options.release || !options.baseline) {
    throw new Error('Both --release and --baseline are required.');
  }
  return options;
}

function main() {
  const { release, baseline } = parseArguments(process.argv.slice(2));
  const result = evaluatePreflight({
    releaseSha: release,
    baselineSha: baseline,
    releaseIds: migrationIdsFromPaths(listTree(release)),
    baselineIds: migrationIdsFromPaths(listTree(baseline)),
  });
  process.stdout.write(`${JSON.stringify(result)}\n`);
  if (!result.ok) {
    process.stderr.write(
      `The baseline ${baseline} contains migrations the release ${release} does not: ${result.missingFromRelease.join(', ')}. `
        + 'Deploying it would run older code against a newer production schema, and startup never rolls a migration back. '
        + 'Recovery is a human decision (see docs/automation.md, Deploy Production).\n',
    );
    process.exit(1);
  }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  try {
    main();
  } catch (error) {
    process.stderr.write(`${error.message}\n`);
    process.exit(1);
  }
}
