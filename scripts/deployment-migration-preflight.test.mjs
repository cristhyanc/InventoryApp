import assert from 'node:assert/strict';
import test from 'node:test';
import {
  compareMigrations,
  evaluatePreflight,
  migrationIdsFromPaths,
  parseArguments,
} from './deployment-migration-preflight.mjs';

const RELEASE = 'a'.repeat(40);
const BASELINE = 'b'.repeat(40);

test('migration ids come from migration sources only, wherever the Migrations folder lives', () => {
  const ids = migrationIdsFromPaths([
    'backend/InventoryApi/Migrations/20260101000000_Initial.cs',
    'backend/InventoryApi/Migrations/20260101000000_Initial.Designer.cs',
    'backend/InventoryApi/Migrations/AppDbContextModelSnapshot.cs',
    'backend/Inventory.Infrastructure/Persistence/Migrations/20260202000000_Moved.cs',
    // The path the migrations actually live at since issue #307. Deploy Production derives the
    // migrations a release will apply from this scan alone, so a pattern that stopped matching the
    // real directory would report "no new migrations" for a release that has some.
    'backend/Inventory.Infrastructure/Migrations/20260303000000_Relocated.cs',
    'backend/Inventory.Infrastructure/Migrations/20260303000000_Relocated.Designer.cs',
    'backend/Inventory.Infrastructure/Migrations/AppDbContextModelSnapshot.cs',
    'backend/InventoryApi/Services/Migrations.cs',
    'backend/InventoryApi.Tests/Migrations/NotAMigrationTests.cs',
  ]);
  assert.deepEqual(ids, [
    '20260101000000_Initial',
    '20260202000000_Moved',
    '20260303000000_Relocated',
  ]);
});

test('expected pending migrations are the release migrations the baseline lacks', () => {
  const result = evaluatePreflight({
    releaseSha: RELEASE,
    baselineSha: BASELINE,
    releaseIds: ['20260101000000_A', '20260102000000_B', '20260103000000_C'],
    baselineIds: ['20260101000000_A'],
  });
  assert.deepEqual(result.expectedPending, ['20260102000000_B', '20260103000000_C']);
  assert.equal(result.startupWillApplyMigrations, true);
  assert.equal(result.ok, true);
});

test('a release with no new migrations reports none pending', () => {
  const result = evaluatePreflight({
    releaseSha: RELEASE,
    baselineSha: BASELINE,
    releaseIds: ['20260101000000_A'],
    baselineIds: ['20260101000000_A'],
  });
  assert.deepEqual(result.expectedPending, []);
  assert.equal(result.startupWillApplyMigrations, false);
  assert.equal(result.ok, true);
});

test('older code over a newer schema fails closed', () => {
  const result = evaluatePreflight({
    releaseSha: RELEASE,
    baselineSha: BASELINE,
    releaseIds: ['20260101000000_A'],
    baselineIds: ['20260101000000_A', '20260102000000_B'],
  });
  assert.deepEqual(result.missingFromRelease, ['20260102000000_B']);
  assert.equal(result.ok, false);
});

test('an empty migration scan fails closed instead of guessing', () => {
  assert.throws(() => compareMigrations([], ['20260101000000_A']), /release commit/);
  assert.throws(() => compareMigrations(['20260101000000_A'], []), /baseline commit/);
});

test('arguments require two full SHAs', () => {
  assert.deepEqual(parseArguments(['--release', RELEASE, '--baseline', BASELINE]), { release: RELEASE, baseline: BASELINE });
  assert.throws(() => parseArguments(['--release', RELEASE]), /required/);
  assert.throws(() => parseArguments(['--release', 'abc123', '--baseline', BASELINE]), /40-character/);
  assert.throws(() => parseArguments(['--release', RELEASE, '--baseline', BASELINE, '--apply']), /Unrecognised/);
});
