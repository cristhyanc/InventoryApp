/**
 * The Jest test environment `supplier-preset-dialog.timezone.spec.ts` runs in: an ordinary jsdom
 * environment whose time zone is fixed to one behind UTC (issue #1109).
 *
 * **Why an environment rather than two lines in the spec.** Jest hands the code inside a test file a
 * *copy* of `process.env`, so assigning `process.env.TZ` from a spec reaches nothing: the zone V8
 * resolves dates in is the worker process's, and it is settled before the sandbox exists. An
 * environment's constructor runs in that worker, outside the sandbox, which is the one place the
 * value can still be set - and the spec's first assertion checks that it took effect, so a mocked
 * zone that stopped working fails the suite rather than quietly passing it.
 *
 * It is `.cjs` on purpose: Jest loads an environment itself, and this extension is outside the
 * `transform` pattern, so the file is required as the plain CommonJS module it is rather than
 * compiled as application source. Nothing imports it and no Angular build sees it.
 */
const { TestEnvironment } = require('jest-environment-jsdom');

/**
 * Eight hours behind UTC, so midnight UTC - which is what `new Date('2026-11-09')` is - falls on the
 * previous day here. Any date-only value read through a `Date` therefore names the day before.
 */
const TIME_ZONE = 'America/Los_Angeles';

class FixedTimeZoneEnvironment extends TestEnvironment {
  constructor(config, context) {
    const previousTimeZone = process.env.TZ;
    process.env.TZ = TIME_ZONE;
    super(config, context);
    this.previousTimeZone = previousTimeZone;
  }

  /**
   * A Jest worker runs many test files one after another, so the zone is put back: every other
   * suite has to see the machine's own zone whichever worker happens to pick it up next.
   */
  async teardown() {
    if (this.previousTimeZone === undefined) {
      delete process.env.TZ;
    } else {
      process.env.TZ = this.previousTimeZone;
    }

    await super.teardown();
  }
}

module.exports = FixedTimeZoneEnvironment;
